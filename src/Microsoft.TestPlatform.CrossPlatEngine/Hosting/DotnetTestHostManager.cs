// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.CrossPlatEngine.Hosting
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using Microsoft.Extensions.DependencyModel;
    using Microsoft.VisualStudio.TestPlatform.Common;
    using Microsoft.VisualStudio.TestPlatform.CrossPlatEngine.Helpers;
    using Microsoft.VisualStudio.TestPlatform.CrossPlatEngine.Helpers.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.CrossPlatEngine.Resources;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Host;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Utilities;
    using Microsoft.VisualStudio.TestPlatform.Utilities;
    using Microsoft.VisualStudio.TestPlatform.Utilities.Helpers;
    using Microsoft.VisualStudio.TestPlatform.Utilities.Helpers.Interfaces;

    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// A host manager for <c>dotnet</c> core runtime.
    /// </summary>
    /// <remarks>
    /// Note that some functionality of this entity overlaps with that of <see cref="DefaultTestHostManager"/>. That is
    /// intentional since we want to move this to a separate assembly (with some runtime extensibility discovery).
    /// </remarks>
    public class DotnetTestHostManager : ITestRuntimeProvider
    {
        private readonly IDotnetHostHelper dotnetHostHelper;

        private readonly IProcessHelper processHelper;

        private readonly IFileHelper fileHelper;

        private ITestHostLauncher testHostLauncher;

        private Process testHostProcess;

        private CancellationTokenSource hostLaunchCts;

        private StringBuilder testHostProcessStdError;

        private IMessageLogger messageLogger;

        private bool hostExitedEventRaised;

        private string hostPackageVersion = "15.0.0";

        /// <summary>
        /// Initializes a new instance of the <see cref="DotnetTestHostManager"/> class.
        /// </summary>
        public DotnetTestHostManager()
            : this(new ProcessHelper(), new FileHelper(), new DotnetHostHelper())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DotnetTestHostManager"/> class.
        /// </summary>
        /// <param name="processHelper">Process helper instance.</param>
        /// <param name="fileHelper">File helper instance.</param>
        /// <param name="dotnetHostHelper">File helper instance.</param>
        internal DotnetTestHostManager(
            IProcessHelper processHelper,
            IFileHelper fileHelper,
            IDotnetHostHelper dotnetHostHelper)
        {
            this.processHelper = processHelper;
            this.fileHelper = fileHelper;
            this.dotnetHostHelper = dotnetHostHelper;
            this.hostExitedEventRaised = false;
        }

        public event EventHandler<HostProviderEventArgs> HostLaunched;

        public event EventHandler<HostProviderEventArgs> HostExited;

        /// <summary>
        /// Gets a value indicating if the test host can be shared for multiple sources.
        /// </summary>
        /// <remarks>
        /// Dependency resolution for .net core projects are pivoted by the test project. Hence each test
        /// project must be launched in a separate test host process.
        /// </remarks>
        public bool Shared => false;

        internal bool IsVersionCheckRequired => !hostPackageVersion.StartsWith("15.0.0");

        protected int ErrorLength { get; set; } = 1000;

        protected int TimeOut { get; set; } = 10000;

        /// <summary>
        /// Callback on process exit
        /// </summary>
        private Action<Process> ExitCallBack => ((process) =>
        {
            var exitCode = 0;
            this.processHelper.TryGetExitCode(process, out exitCode);

            this.OnHostExited(new HostProviderEventArgs(this.testHostProcessStdError.ToString(), exitCode));
        });

        /// <summary>
        /// Callback to read from process error stream
        /// </summary>
        private Action<Process, string> ErrorReceivedCallback => (process, data) =>
        {
            var exitCode = 0;
            if (!string.IsNullOrEmpty(data))
            {
                // if incoming data stream is huge empty entire testError stream, & limit data stream to MaxCapacity
                if (data.Length > this.testHostProcessStdError.MaxCapacity)
                {
                    this.testHostProcessStdError.Clear();
                    data = data.Substring(data.Length - this.testHostProcessStdError.MaxCapacity);
                }

                // remove only what is required, from beginning of error stream
                else
                {
                    int required = data.Length + this.testHostProcessStdError.Length - this.testHostProcessStdError.MaxCapacity;
                    if (required > 0)
                    {
                        this.testHostProcessStdError.Remove(0, required);
                    }
                }

                this.testHostProcessStdError.Append(data);
            }

            if (this.processHelper.TryGetExitCode(process, out exitCode))
            {
                EqtTrace.Error("Test host exited with error: {0}", this.testHostProcessStdError);
                this.OnHostExited(new HostProviderEventArgs(this.testHostProcessStdError.ToString(), exitCode));
            }
        };

        /// <inheritdoc/>
        public void Initialize(IMessageLogger logger)
        {
            this.messageLogger = logger;
        }

        /// <inheritdoc/>
        public void SetCustomLauncher(ITestHostLauncher customLauncher)
        {
            this.testHostLauncher = customLauncher;
        }

        public async Task<int> LaunchTestHostAsync(TestProcessStartInfo testHostStartInfo)
        {
            return await Task.Run(() => this.LaunchHost(testHostStartInfo), this.GetCancellationTokenSource().Token);
        }

        /// <inheritdoc/>
        public virtual TestProcessStartInfo GetTestHostProcessStartInfo(
            IEnumerable<string> sources,
            IDictionary<string, string> environmentVariables,
            TestRunnerConnectionInfo connectionInfo)
        {
            var startInfo = new TestProcessStartInfo();

            var currentProcessPath = this.processHelper.GetCurrentProcessFileName();

            // This host manager can create process start info for dotnet core targets only.
            // If already running with the dotnet executable, use it; otherwise pick up the dotnet available on path.
            // Wrap the paths with quotes in case dotnet executable is installed on a path with whitespace.
            if (currentProcessPath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase)
                || currentProcessPath.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.FileName = currentProcessPath;
            }
            else
            {
                startInfo.FileName = this.dotnetHostHelper.GetDotnetHostFullPath();
            }

            EqtTrace.Verbose("DotnetTestHostmanager: Full path of dotnet.exe is {0}", startInfo.FileName);

            // .NET core host manager is not a shared host. It will expect a single test source to be provided.
            var args = "exec";
            var sourcePath = sources.Single();
            var sourceFile = Path.GetFileNameWithoutExtension(sourcePath);
            var sourceDirectory = Path.GetDirectoryName(sourcePath);

            // Probe for runtimeconfig and deps file for the test source
            var runtimeConfigPath = Path.Combine(sourceDirectory, string.Concat(sourceFile, ".runtimeconfig.json"));
            if (this.fileHelper.Exists(runtimeConfigPath))
            {
                string argsToAdd = " --runtimeconfig \"" + runtimeConfigPath + "\"";
                args += argsToAdd;
                EqtTrace.Verbose("DotnetTestHostmanager: Adding {0} in args", argsToAdd);
            }
            else
            {
                EqtTrace.Verbose("DotnetTestHostmanager: File {0}, doesnot exist", runtimeConfigPath);
            }

            // Use the deps.json for test source
            var depsFilePath = Path.Combine(sourceDirectory, string.Concat(sourceFile, ".deps.json"));
            if (this.fileHelper.Exists(depsFilePath))
            {
                string argsToAdd = " --depsfile \"" + depsFilePath + "\"";
                args += argsToAdd;
                EqtTrace.Verbose("DotnetTestHostmanager: Adding {0} in args", argsToAdd);
            }
            else
            {
                EqtTrace.Verbose("DotnetTestHostmanager: File {0}, doesnot exist", depsFilePath);
            }

            var runtimeConfigDevPath = Path.Combine(sourceDirectory, string.Concat(sourceFile, ".runtimeconfig.dev.json"));
            var testHostPath = this.GetTestHostPath(runtimeConfigDevPath, depsFilePath, sourceDirectory);

            if (this.fileHelper.Exists(testHostPath))
            {
                EqtTrace.Verbose("DotnetTestHostmanager: Full path of testhost.dll is {0}", testHostPath);
                args += " \"" + testHostPath + "\" " + connectionInfo.ToCommandLineOptions();
            }
            else
            {
                string message = string.Format(Resources.NoTestHostFileExist, sourcePath);
                EqtTrace.Verbose("DotnetTestHostmanager: " + message);
                throw new FileNotFoundException(message);
            }

            // Create a additional probing path args with Nuget.Client
            // args += "--additionalprobingpath xxx"
            // TODO this may be required in ASP.net, requires validation

            // Sample command line for the spawned test host
            // "D:\dd\gh\Microsoft\vstest\tools\dotnet\dotnet.exe" exec
            // --runtimeconfig G:\tmp\netcore-test\bin\Debug\netcoreapp1.0\netcore-test.runtimeconfig.json
            // --depsfile G:\tmp\netcore-test\bin\Debug\netcoreapp1.0\netcore-test.deps.json
            // --additionalprobingpath C:\Users\username\.nuget\packages\ 
            // G:\nuget-package-path\microsoft.testplatform.testhost\version\**\testhost.dll
            // G:\tmp\netcore-test\bin\Debug\netcoreapp1.0\netcore-test.dll
            startInfo.Arguments = args;
            startInfo.EnvironmentVariables = environmentVariables ?? new Dictionary<string, string>();
            startInfo.WorkingDirectory = sourceDirectory;

            return startInfo;
        }

        /// <inheritdoc/>
        public IEnumerable<string> GetTestPlatformExtensions(IEnumerable<string> sources)
        {
            var sourceDirectory = Path.GetDirectoryName(sources.Single());

            if (!string.IsNullOrEmpty(sourceDirectory) && this.fileHelper.DirectoryExists(sourceDirectory))
            {
                return this.fileHelper.EnumerateFiles(sourceDirectory, TestPlatformConstants.TestAdapterRegexPattern, SearchOption.TopDirectoryOnly);
            }

            return Enumerable.Empty<string>();
        }

        public bool CanExecuteCurrentRunConfiguration(string runConfiguration)
        {
            RunConfiguration config = XmlRunSettingsUtilities.GetRunConfigurationNode(runConfiguration);
            var framework = config.TargetFrameworkVersion;

            // This is expected to be called once every run so returning a new instance every time.
            if (framework.Name.IndexOf("netstandard", StringComparison.OrdinalIgnoreCase) >= 0
                || framework.Name.IndexOf("netcoreapp", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        public void OnHostLaunched(HostProviderEventArgs e)
        {
            this.HostLaunched.SafeInvoke(this, e, "HostProviderEvents.OnHostLaunched");
        }

        public void OnHostExited(HostProviderEventArgs e)
        {
            if (!this.hostExitedEventRaised)
            {
                this.hostExitedEventRaised = true;
                this.HostExited.SafeInvoke(this, e, "HostProviderEvents.OnHostExited");
            }
        }

        /// <inheritdoc/>
        private int LaunchHost(TestProcessStartInfo testHostStartInfo)
        {
            this.testHostProcessStdError = new StringBuilder(this.ErrorLength, this.ErrorLength);
            if (this.testHostLauncher == null)
            {
                this.testHostProcess = this.processHelper.LaunchProcess(testHostStartInfo.FileName, testHostStartInfo.Arguments, testHostStartInfo.WorkingDirectory, testHostStartInfo.EnvironmentVariables, this.ErrorReceivedCallback, this.ExitCallBack);
            }
            else
            {
                var processId = this.testHostLauncher.LaunchTestHost(testHostStartInfo);
                this.testHostProcess = Process.GetProcessById(processId);
            }

            this.OnHostLaunched(new HostProviderEventArgs("Test Runtime launched with Pid: " + this.testHostProcess.Id));

            return this.testHostProcess.Id;
        }

        private string GetTestHostPath(string runtimeConfigDevPath, string depsFilePath, string sourceDirectory)
        {
            string testHostPackageName = "microsoft.testplatform.testhost";
            string testHostPath = string.Empty;

            if (this.fileHelper.Exists(runtimeConfigDevPath) && this.fileHelper.Exists(depsFilePath))
            {
                EqtTrace.Verbose("DotnetTestHostmanager: Reading file {0} to get path of testhost.dll", depsFilePath);

                // Get testhost relative path
                using (var stream = this.fileHelper.GetStream(depsFilePath, FileMode.Open))
                {
                    var context = new DependencyContextJsonReader().Read(stream);
                    var testhostPackage = context.RuntimeLibraries.Where(lib => lib.Name.Equals(testHostPackageName, StringComparison.CurrentCultureIgnoreCase)).FirstOrDefault();

                    if (testhostPackage != null)
                    {
                        foreach (var runtimeAssemblyGroup in testhostPackage.RuntimeAssemblyGroups)
                        {
                            foreach (var path in runtimeAssemblyGroup.AssetPaths)
                            {
                                if (path.EndsWith("testhost.dll", StringComparison.CurrentCultureIgnoreCase))
                                {
                                    testHostPath = path;
                                    break;
                                }
                            }
                        }

                        testHostPath = Path.Combine(testhostPackage.Path, testHostPath);
                        hostPackageVersion = testhostPackage.Version;
                        EqtTrace.Verbose("DotnetTestHostmanager: Relative path of testhost.dll with respect to package folder is {0}", testHostPath);
                    }
                }

                // Get probing path
                using (StreamReader file = new StreamReader(this.fileHelper.GetStream(runtimeConfigDevPath, FileMode.Open)))
                using (JsonTextReader reader = new JsonTextReader(file))
                {
                    JObject context = (JObject)JToken.ReadFrom(reader);
                    JObject runtimeOptions = (JObject)context.GetValue("runtimeOptions");
                    JToken additionalProbingPaths = runtimeOptions.GetValue("additionalProbingPaths");
                    foreach (var x in additionalProbingPaths)
                    {
                        EqtTrace.Verbose("DotnetTestHostmanager: Looking for path {0} in folder {1}", testHostPath, x.ToString());
                        string testHostFullPath = Path.Combine(x.ToString(), testHostPath);
                        if (this.fileHelper.Exists(testHostFullPath))
                        {
                            return testHostFullPath;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(testHostPath))
            {
                // Try resolving testhost from output directory of test project. This is required if user has published the test project
                // and is running tests in an isolated machine. A second scenario is self test: test platform unit tests take a project
                // dependency on testhost (instead of nuget dependency), this drops testhost to output path.
                testHostPath = Path.Combine(sourceDirectory, "testhost.dll");
                EqtTrace.Verbose("DotnetTestHostManager: Assume published test project, with test host path = {0}.", testHostPath);
            }

            return testHostPath;
        }
        
        private CancellationTokenSource GetCancellationTokenSource()
        {
            this.hostLaunchCts = new CancellationTokenSource(this.TimeOut);
            return this.hostLaunchCts;
        }
    }
}

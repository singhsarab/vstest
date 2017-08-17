// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.CrossPlatEngine.Hosting
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    using Microsoft.TestPlatform.TestHostProvider.Hosting;
    using Microsoft.VisualStudio.TestPlatform.CoreUtilities.Extensions;
    using Microsoft.VisualStudio.TestPlatform.CrossPlatEngine.Helpers;
    using Microsoft.VisualStudio.TestPlatform.CrossPlatEngine.Helpers.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Host;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Utilities;
    using Microsoft.VisualStudio.TestPlatform.PlatformAbstractions;
    using Microsoft.VisualStudio.TestPlatform.PlatformAbstractions.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.Utilities;
    using Microsoft.VisualStudio.TestPlatform.Utilities.Helpers;
    using Microsoft.VisualStudio.TestPlatform.Utilities.Helpers.Interfaces;

    /// <summary>
    /// The default test host launcher for the engine.
    /// This works for Desktop local scenarios
    /// </summary>
    [ExtensionUri(DefaultTestHostUri)]
    [FriendlyName(DefaultTestHostFriendlyName)]
    public class DefaultTestHostManager : ITestRuntimeProvider
    {
        private const string X64TestHostProcessName = "testhost.exe";
        private const string X86TestHostProcessName = "testhost.x86.exe";

        private const string DefaultTestHostUri = "HostProvider://DefaultTestHost";
        private const string DefaultTestHostFriendlyName = "DefaultTestHost";
        private const string TestAdapterRegexPattern = @"TestAdapter.dll";

        private Architecture architecture;

        private IProcessHelper processHelper;
        private IFileHelper fileHelper;
        private IEnvironment environment;
        private IDotnetHostHelper dotnetHostHelper;

        private ITestHostLauncher customTestHostLauncher;
        private Process testHostProcess;
        private StringBuilder testHostProcessStdError;
        private IMessageLogger messageLogger;
        private bool hostExitedEventRaised;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultTestHostManager"/> class.
        /// </summary>
        public DefaultTestHostManager()
            : this(new ProcessHelper(), new FileHelper(), new PlatformEnvironment(), new DotnetHostHelper())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultTestHostManager"/> class.
        /// </summary>
        /// <param name="processHelper">Process helper instance.</param>
        /// <param name="fileHelper">File helper instance.</param>
        /// <param name="environment">Instance of platform environment.</param>
        /// <param name="dotnetHostHelper">Instance of dotnet host helper.</param>
        internal DefaultTestHostManager(IProcessHelper processHelper, IFileHelper fileHelper, IEnvironment environment, IDotnetHostHelper dotnetHostHelper)
        {
            this.processHelper = processHelper;
            this.fileHelper = fileHelper;
            this.environment = environment;
            this.dotnetHostHelper = dotnetHostHelper;
        }

        /// <inheritdoc/>
        public event EventHandler<HostProviderEventArgs> HostLaunched;

        /// <inheritdoc/>
        public event EventHandler<HostProviderEventArgs> HostExited;

        /// <inheritdoc/>
        public bool Shared { get; private set; }

        /// <summary>
        /// Gets the properties of the test executor launcher. These could be the targetID for emulator/phone specific scenarios.
        /// </summary>
        public IDictionary<string, string> Properties => new Dictionary<string, string>();

        /// <summary>
        /// Gets or sets the error length for runtime error stream.
        /// </summary>
        protected int ErrorLength { get; set; } = 4096;

        /// <summary>
        /// Gets callback on process exit
        /// </summary>
        private Action<object> ExitCallBack => (process) =>
        {
            TestHostManagerCallbacks.ExitCallBack(this.processHelper, process, this.testHostProcessStdError, this.OnHostExited);
        };

        /// <summary>
        /// Gets callback to read from process error stream
        /// </summary>
        private Action<object, string> ErrorReceivedCallback => (process, data) =>
        {
            TestHostManagerCallbacks.ErrorReceivedCallback(this.testHostProcessStdError, data);
        };

        /// <inheritdoc/>
        public void SetCustomLauncher(ITestHostLauncher customLauncher)
        {
            this.customTestHostLauncher = customLauncher;
        }

        /// <inheritdoc/>
        public TestHostConnectionInfo GetTestHostConnectionInfo()
        {
            return new TestHostConnectionInfo { Endpoint = "127.0.0.1:0", Role = ConnectionRole.Client, Transport = Transport.Sockets };
        }

        /// <inheritdoc/>
        public async Task<bool> LaunchTestHostAsync(TestProcessStartInfo testHostStartInfo, CancellationToken cancellationToken)
        {
            return await Task.Run(() => this.LaunchHost(testHostStartInfo, cancellationToken), cancellationToken);
        }

        /// <inheritdoc/>
        public virtual TestProcessStartInfo GetTestHostProcessStartInfo(
            IEnumerable<string> sources,
            IDictionary<string, string> environmentVariables,
            TestRunnerConnectionInfo connectionInfo)
        {
            // Default test host manager supports shared test sources
            var testHostProcessName = (this.architecture == Architecture.X86) ? X86TestHostProcessName : X64TestHostProcessName;
            var currentWorkingDirectory = Path.Combine(Path.GetDirectoryName(typeof(DefaultTestHostManager).GetTypeInfo().Assembly.Location), "..//");
            var argumentsString = " " + connectionInfo.ToCommandLineOptions();

            // "TestHost" is the name of the folder which contain Full CLR built testhost package assemblies.
            testHostProcessName = Path.Combine("TestHost", testHostProcessName);

            if (!this.Shared)
            {
                // Not sharing the host which means we need to pass the test assembly path as argument
                // so that the test host can create an appdomain on startup (Main method) and set appbase
                argumentsString += " " + "--testsourcepath " + sources.FirstOrDefault().AddDoubleQuote();
            }

            var testhostProcessPath = Path.Combine(currentWorkingDirectory, testHostProcessName);
            EqtTrace.Verbose("DefaultTestHostmanager: Full path of {0} is {1}", testHostProcessName, testhostProcessPath);

            var launcherPath = testhostProcessPath;
            if (!this.environment.OperatingSystem.Equals(PlatformOperatingSystem.Windows) &&
                !this.processHelper.GetCurrentProcessFileName().EndsWith(DotnetHostHelper.MONOEXENAME, StringComparison.OrdinalIgnoreCase))
            {
                launcherPath = this.dotnetHostHelper.GetMonoPath();
                argumentsString = testhostProcessPath + " " + argumentsString;
            }

            // For IDEs and other scenario, current directory should be the
            // working directory (not the vstest.console.exe location).
            // For VS - this becomes the solution directory for example
            // "TestResults" directory will be created at "current directory" of test host
            var processWorkingDirectory = Directory.GetCurrentDirectory();

            return new TestProcessStartInfo
            {
                FileName = launcherPath,
                Arguments = argumentsString,
                EnvironmentVariables = environmentVariables ?? new Dictionary<string, string>(),
                WorkingDirectory = processWorkingDirectory
            };
        }

        /// <inheritdoc/>
        public IEnumerable<string> GetTestPlatformExtensions(IEnumerable<string> sources, IEnumerable<string> extensions)
        {
            if (!this.Shared)
            {
                var sourceDirectory = Path.GetDirectoryName(sources.Single());

                if (!string.IsNullOrEmpty(sourceDirectory) && this.fileHelper.DirectoryExists(sourceDirectory))
                {
                    extensions = extensions.Concat(this.fileHelper.EnumerateFiles(sourceDirectory, SearchOption.TopDirectoryOnly, TestAdapterRegexPattern));
                }
            }

            return extensions;
        }

        /// <inheritdoc/>
        public IEnumerable<string> GetTestSources(IEnumerable<string> sources)
        {
            // We do not have scenario where full CLR tests are deployed to remote machine, so no need to udpate sources
            return sources;
        }

        /// <inheritdoc/>
        public bool CanExecuteCurrentRunConfiguration(string runsettingsXml)
        {
            var config = XmlRunSettingsUtilities.GetRunConfigurationNode(runsettingsXml);
            var framework = config.TargetFrameworkVersion;

            // This is expected to be called once every run so returning a new instance every time.
            if (framework.Name.IndexOf("NETFramework", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        /// <inheritdoc/>
        public void Initialize(IMessageLogger logger, string runsettingsXml)
        {
            var runConfiguration = XmlRunSettingsUtilities.GetRunConfigurationNode(runsettingsXml);

            this.messageLogger = logger;
            this.architecture = runConfiguration.TargetPlatform;
            this.testHostProcess = null;

            this.Shared = !(runConfiguration.DisableAppDomain || runConfiguration.TestAdaptersPaths == null || runConfiguration.TestAdaptersPaths.Length == 0);
            this.hostExitedEventRaised = false;
        }

        /// <inheritdoc/>
        public Task CleanTestHostAsync(CancellationToken cancellationToken)
        {
            try
            {
                this.processHelper.TerminateProcess(this.testHostProcess);
            }
            catch (Exception ex)
            {
                EqtTrace.Warning("DefaultTestHostManager: Unable to terminate test host process: " + ex);
            }

            this.testHostProcess?.Dispose();

            return Task.FromResult(true);
        }

        /// <summary>
        /// Raises HostLaunched event
        /// </summary>
        /// <param name="e">hostprovider event args</param>
        private void OnHostLaunched(HostProviderEventArgs e)
        {
            this.HostLaunched.SafeInvoke(this, e, "HostProviderEvents.OnHostLaunched");
        }

        /// <summary>
        /// Raises HostExited event
        /// </summary>
        /// <param name="e">hostprovider event args</param>
        private void OnHostExited(HostProviderEventArgs e)
        {
            if (!this.hostExitedEventRaised)
            {
                this.hostExitedEventRaised = true;
                this.HostExited.SafeInvoke(this, e, "HostProviderEvents.OnHostExited");
            }
        }

        private bool LaunchHost(TestProcessStartInfo testHostStartInfo, CancellationToken cancellationToken)
        {
            try
            {
                this.testHostProcessStdError = new StringBuilder(this.ErrorLength, this.ErrorLength);
                EqtTrace.Verbose("Launching default test Host Process {0} with arguments {1}", testHostStartInfo.FileName, testHostStartInfo.Arguments);

                if (this.customTestHostLauncher == null)
                {
                    EqtTrace.Verbose("DefaultTestHostManager: Starting process '{0}' with command line '{1}'", testHostStartInfo.FileName, testHostStartInfo.Arguments);

                    cancellationToken.ThrowIfCancellationRequested();
                    this.testHostProcess = this.processHelper.LaunchProcess(testHostStartInfo.FileName, testHostStartInfo.Arguments, testHostStartInfo.WorkingDirectory, testHostStartInfo.EnvironmentVariables, this.ErrorReceivedCallback, this.ExitCallBack) as Process;
                }
                else
                {
                    int processId = this.customTestHostLauncher.LaunchTestHost(testHostStartInfo);
                    this.testHostProcess = Process.GetProcessById(processId);
                }
            }
            catch (OperationCanceledException ex)
            {
                this.messageLogger.SendMessage(TestMessageLevel.Error, ex.Message);
                return false;
            }

            this.OnHostLaunched(new HostProviderEventArgs("Test Runtime launched", 0, this.testHostProcess.Id));
            return this.testHostProcess != null;
        }
    }
}

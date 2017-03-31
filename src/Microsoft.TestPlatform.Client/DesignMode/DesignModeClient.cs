// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.Client.DesignMode
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;

    using Microsoft.VisualStudio.TestPlatform.Client.RequestHelper;
    using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
    using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.Interfaces;
    using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.ObjectModel;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using System.Linq;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel.Client;

    /// <summary>
    /// The design mode client.
    /// </summary>
    public class DesignModeClient : IDesignModeClient
    {
        private readonly ICommunicationManager communicationManager;

        private readonly IDataSerializer dataSerializer;

        protected Action<Message> onAckMessageReceived;

        private object ackLockObject = new object();

        /// <summary>
        /// The timeout for the client to connect to the server.
        /// </summary>
        private const int ClientListenTimeOut = 5 * 1000;

        /// <summary>
        /// Initializes a new instance of the <see cref="DesignModeClient"/> class.
        /// </summary>
        public DesignModeClient()
            : this(new SocketCommunicationManager(), JsonDataSerializer.Instance)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DesignModeClient"/> class.
        /// </summary>
        /// <param name="communicationManager">
        /// The communication manager.
        /// </param>
        /// <param name="dataSerializer">
        /// The data Serializer.
        /// </param>
        internal DesignModeClient(ICommunicationManager communicationManager, IDataSerializer dataSerializer)
        {
            this.communicationManager = communicationManager;
            this.dataSerializer = dataSerializer;
        }

        /// <summary>
        /// Property exposing the Instance
        /// </summary>
        public static IDesignModeClient Instance { get; private set; }

        /// <summary>
        /// Initializes DesignMode
        /// </summary>
        public static void Initialize()
        {
            Instance = new DesignModeClient();
        }

        /// <summary>
        /// Creates a client and waits for server to accept connection asynchronously
        /// </summary>
        /// <param name="port">
        /// Port number to connect
        /// </param>
        /// <param name="testRequestManager">
        /// The test Request Manager.
        /// </param>
        public void ConnectToClientAndProcessRequests(int port, ITestRequestManager testRequestManager)
        {
            EqtTrace.Info("Trying to connect to server on port : {0}", port);
            this.communicationManager.SetupClientAsync(port);

            // Wait for the connection to the server and listen for requests.
            if (this.communicationManager.WaitForServerConnection(ClientListenTimeOut))
            {
                this.communicationManager.SendMessage(MessageType.SessionConnected);
                this.ProcessRequests(testRequestManager);
            }
            else
            {
                EqtTrace.Info("Client timed out while connecting to the server.");
                this.Dispose();
                throw new TimeoutException();
            }
        }

        public void HandleParentProcessExit()
        {
            // Dispose off the communications to end the session
            // this should end the "ProcessRequests" loop with an exception
            this.Dispose();
        }

        /// <summary>
        /// Process Requests from the IDE
        /// </summary>
        /// <param name="testRequestManager">
        /// The test Request Manager.
        /// </param>
        private void ProcessRequests(ITestRequestManager testRequestManager)
        {
            var isSessionEnd = false;

            do
            {
                try
                {
                    var message = this.communicationManager.ReceiveMessage();

                    EqtTrace.Info("DesignModeClient: Processing Message of message type: {0}", message.MessageType);
                    switch (message.MessageType)
                    {
                        case MessageType.VersionCheck:
                            {
                                // At this point, we cannot add stuff to object model like "ProtocolVersionMessage"
                                // as that cannot be acessed from testwindow which still uses TP-V1
                                // we are sending a version number as an integer for now
                                // TODO: Find a better way without breaking TW which using TP-V1
                                var payload = 1;
                                this.communicationManager.SendMessage(MessageType.VersionCheck, payload);
                                break;
                            }

                        case MessageType.ExtensionsInitialize:
                            {
                                var extensionPaths = this.communicationManager.DeserializePayload<IEnumerable<string>>(message);
                                testRequestManager.InitializeExtensions(extensionPaths);
                                break;
                            }

                        case MessageType.StartDiscovery:
                            {
                                var discoveryPayload = message.Payload.ToObject<DiscoveryRequestPayload>();
                                testRequestManager.DiscoverTests(discoveryPayload, new DesignModeTestEventsRegistrar(this));
                                break;
                            }

                        case MessageType.GetTestRunnerProcessStartInfoForRunAll:
                        case MessageType.GetTestRunnerProcessStartInfoForRunSelected:
                            {
                                var testRunPayload =
                                    this.communicationManager.DeserializePayload<TestRunRequestPayload>(
                                        message);
                                this.StartTestRun(testRunPayload, testRequestManager, skipTestHostLaunch: true);
                                break;
                            }

                        case MessageType.TestRunAllSourcesWithDefaultHost:
                        case MessageType.TestRunSelectedTestCasesDefaultHost:
                            {
                                var testRunPayload =
                                    this.communicationManager.DeserializePayload<TestRunRequestPayload>(
                                        message);
                                this.StartTestRun(testRunPayload, testRequestManager, skipTestHostLaunch: false);
                                break;
                            }

                        case MessageType.CancelDiscovery:
                            {
                                testRequestManager.CancelDiscovery();
                                break;
                            }

                        case MessageType.CancelTestRun:
                            {
                                testRequestManager.CancelTestRun();
                                break;
                            }

                        case MessageType.AbortTestRun:
                            {
                                testRequestManager.AbortTestRun();
                                break;
                            }

                        case MessageType.CustomTestHostLaunchCallback:
                            {
                                this.onAckMessageReceived?.Invoke(message);
                                break;
                            }

                        case MessageType.SessionEnd:
                            {
                                EqtTrace.Info("DesignModeClient: Session End message received from server. Closing the connection.");
                                isSessionEnd = true;
                                this.Dispose();
                                break;
                            }

                        default:
                            {
                                EqtTrace.Info("DesignModeClient: Invalid Message received: {0}", message);
                                break;
                            }
                    }
                }
                catch(Exception ex)
                {
                    EqtTrace.Error("DesignModeClient: Error processing request: {0}", ex);
                    isSessionEnd = true;
                    this.Dispose();
                }
            }
            while (!isSessionEnd);
        }

        /// <summary>
        /// Send a custom host launch message to IDE
        /// </summary>
        /// <param name="testProcessStartInfo">
        /// The test Process Start Info.
        /// </param>
        public int LaunchCustomHost(TestProcessStartInfo testProcessStartInfo)
        {
            lock (ackLockObject)
            {
                var waitHandle = new AutoResetEvent(false);
                Message ackMessage = null;
                this.onAckMessageReceived = (ackRawMessage) =>
                {
                    ackMessage = ackRawMessage;
                    waitHandle.Set();
                };

                this.communicationManager.SendMessage(MessageType.CustomTestHostLaunch, testProcessStartInfo);

                // LifeCycle of the TP through DesignModeClient is maintained by the IDEs or user-facing-clients like LUTs, who call TestPlatform
                // TP is handing over the control of launch to these IDEs and so, TP has to wait indefinite
                // Even if TP has a timeout here, there is no way TP can abort or stop the thread/task that is hung in IDE or LUT
                // Even if TP can abort the API somehow, TP is essentially putting IDEs or Clients in inconsistent state without having info on
                // Since the IDEs own user-UI-experience here, TP will let the custom host launch as much time as IDEs define it for their users
                waitHandle.WaitOne();
                this.onAckMessageReceived = null;

                var ackPayload = this.dataSerializer.DeserializePayload<CustomHostLaunchAckPayload>(ackMessage);

                if (ackPayload.HostProcessId > 0)
                {
                    return ackPayload.HostProcessId;
                }
                else
                {
                    throw new TestPlatformException(ackPayload.ErrorMessage);
                }
            }
        }

        /// <summary>
        /// Send the raw messages to IDE
        /// </summary>
        /// <param name="rawMessage"></param>
        public void SendRawMessage(string rawMessage)
        {
            this.communicationManager.SendRawMessage(rawMessage);
        }

        private void StartTestRun(TestRunRequestPayload testRunPayload, ITestRequestManager testRequestManager, bool skipTestHostLaunch)
        {
            Task.Run(
            delegate
            {
                try
                {
                    testRequestManager.ResetOptions();

                    var customLauncher = skipTestHostLaunch ?
                        DesignModeTestHostLauncherFactory.GetCustomHostLauncherForTestRun(this, testRunPayload) : null;

                    testRequestManager.RunTests(testRunPayload, customLauncher, new DesignModeTestEventsRegistrar(this));
                }
                catch(Exception ex)
                {
                    // If there is an exception during test run request creation or some time during the process
                    // In such cases, TestPlatform will never send a TestRunComplete event and IDE need to be sent a run complete message
                    // We need recoverability in translationlayer-designmode scenarios 
                    var runCompletePayload = new TestRunCompletePayload()
                    {
                        TestRunCompleteArgs = new TestRunCompleteEventArgs(null, false, true, ex, null, TimeSpan.MinValue),
                        LastRunTests = null
                    };

                    // Send run complete to translation layer
                    this.communicationManager.SendMessage(MessageType.ExecutionComplete, runCompletePayload);
                }
            });
        }

        #region IDisposable Support

        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    this.communicationManager?.StopClient();
                }

                disposedValue = true;
            }
        }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
        }

        #endregion
    }
}
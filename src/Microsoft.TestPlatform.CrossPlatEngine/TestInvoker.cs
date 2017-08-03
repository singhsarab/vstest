using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.EventHandlers;
using Microsoft.VisualStudio.TestPlatform.CrossPlatEngine.DataCollection.Interfaces;
using Microsoft.VisualStudio.TestPlatform.CrossPlatEngine.EventHandlers;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Engine;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Utilities;
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Engine.ClientProtocol;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.Interfaces;

namespace Microsoft.VisualStudio.TestPlatform.CrossPlatEngine
{
    public class TestInvoker :
#if NET451
        MarshalByRefObject,
#endif
        ITestInvoker
    {
        TestHostManagerFactory factory;

        public TestInvoker()
        {
            this.factory = new TestHostManagerFactory();
        }

        public void InvokeRun(List<string> pathToAdditionalExtensions, Dictionary<string, IEnumerable<string>> sourceMap, string runsettings, TestExecutionContext testExecutionContext, ITestRequestProxy handler)
        {
            var runEventsHandler = new TestRunEventsHandler2(handler);
            
            this.factory.GetExecutionManager().Initialize(pathToAdditionalExtensions);

            this.factory.GetExecutionManager()
              .StartTestRun(
                  sourceMap,
                  runsettings,
                  testExecutionContext,
                  //TODO: null for now 
                  null,
                  runEventsHandler);
        }
    }
}

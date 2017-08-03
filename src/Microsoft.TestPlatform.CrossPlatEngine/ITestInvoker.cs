using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.Interfaces;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Engine.ClientProtocol;
using System.Collections.Generic;

namespace Microsoft.VisualStudio.TestPlatform.CrossPlatEngine
{
    public interface ITestInvoker
    {
        void InvokeRun(List<string> pathToAdditionalExtensions, Dictionary<string, IEnumerable<string>> sourceMap, string runsettings, TestExecutionContext testExecutionContext, ITestRequestProxy handler);
    }
}
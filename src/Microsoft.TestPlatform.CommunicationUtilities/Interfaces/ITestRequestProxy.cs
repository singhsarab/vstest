// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.VisualStudio.TestPlatform.CommunicationUtilities.Interfaces
{
    using System;

    public interface ITestRequestProxy
    {
        event EventHandler<string> OnRawMessageReceived;

        void SendRawMessage(string message);

        void SendComplete(string message);
    }
}

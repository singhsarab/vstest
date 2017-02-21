﻿﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace TestPlatform.CommunicationUtilities.UnitTests.Serialization
{
    using System;
    using System.Linq;

    using Microsoft.VisualStudio.TestPlatform.CommunicationUtilities;
    using Microsoft.VisualStudio.TestPlatform.ObjectModel;
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    [TestClass]
    public class TestCaseSerializationTests
    {
        private static TestCase testCase = new TestCase(
                                               "sampleTestClass.sampleTestCase",
                                               new Uri("executor://sampleTestExecutor"),
                                               "sampleTest.dll")
                                               {
                                                   CodeFilePath = "/user/src/testFile.cs",
                                                   DisplayName = "sampleTestCase",
                                                   Id = new Guid("be78d6fc-61b0-4882-9d07-40d796fd96ce"),
                                                   LineNumber = 999,
                                                   Traits = { new Trait("Priority", "0"), new Trait("Category", "unit") }
                                               };

        [TestMethod]
        public void TestCaseJsonShouldContainAllPropertiesOnSerialization()
        {
            var json = Serialize(testCase);

            // Use raw deserialization to validate basic properties
            dynamic data = JObject.Parse(json);
            dynamic properties = data["Properties"];
            Assert.AreEqual("TestCase.FullyQualifiedName", properties[0]["Key"]["Id"].Value);
            Assert.AreEqual("sampleTestClass.sampleTestCase", properties[0]["Value"].Value);
            Assert.AreEqual("TestCase.ExecutorUri", properties[1]["Key"]["Id"].Value);
            Assert.AreEqual("executor://sampleTestExecutor", properties[1]["Value"].Value);
            Assert.AreEqual("TestCase.Source", properties[2]["Key"]["Id"].Value);
            Assert.AreEqual("sampleTest.dll", properties[2]["Value"].Value);
            Assert.AreEqual("TestCase.CodeFilePath", properties[3]["Key"]["Id"].Value);
            Assert.AreEqual("/user/src/testFile.cs", properties[3]["Value"].Value);
            Assert.AreEqual("TestCase.DisplayName", properties[4]["Key"]["Id"].Value);
            Assert.AreEqual("sampleTestCase", properties[4]["Value"].Value);
            Assert.AreEqual("TestCase.Id", properties[5]["Key"]["Id"].Value);
            Assert.AreEqual("be78d6fc-61b0-4882-9d07-40d796fd96ce", properties[5]["Value"].Value);
            Assert.AreEqual("TestCase.LineNumber", properties[6]["Key"]["Id"].Value);
            Assert.AreEqual(999, properties[6]["Value"].Value);

            // Traits require special handling with TestPlatformContract resolver. It should be null without it.
            Assert.AreEqual("TestObject.Traits", properties[7]["Key"]["Id"].Value);
            Assert.IsNull(properties[7]["Key"]["Value"]);
        }

        [TestMethod]
        public void TestCaseObjectShouldContainAllPropertiesOnDeserialization()
        {
            var json = "{\"Properties\":[{\"Key\":{\"Id\":\"TestCase.FullyQualifiedName\",\"Label\":\"FullyQualifiedName\",\"Category\":\"\",\"Description\":\"\",\"Attributes\":1,\"ValueType\":\"System.String\"},\"Value\":\"sampleTestClass.sampleTestCase\"},"
                + "{\"Key\":{\"Id\":\"TestCase.ExecutorUri\",\"Label\":\"Executor Uri\",\"Category\":\"\",\"Description\":\"\",\"Attributes\":1,\"ValueType\":\"System.Uri\"},\"Value\":\"executor://sampleTestExecutor\"},"
                + "{\"Key\":{\"Id\":\"TestCase.Source\",\"Label\":\"Source\",\"Category\":\"\",\"Description\":\"\",\"Attributes\":0,\"ValueType\":\"System.String\"},\"Value\":\"sampleTest.dll\"},"
                + "{\"Key\":{\"Id\":\"TestCase.CodeFilePath\",\"Label\":\"File Path\",\"Category\":\"\",\"Description\":\"\",\"Attributes\":0,\"ValueType\":\"System.String\"},\"Value\":\"/user/src/testFile.cs\"},"
                + "{\"Key\":{\"Id\":\"TestCase.DisplayName\",\"Label\":\"Name\",\"Category\":\"\",\"Description\":\"\",\"Attributes\":0,\"ValueType\":\"System.String\"},\"Value\":\"sampleTestCase\"},"
                + "{\"Key\":{\"Id\":\"TestCase.Id\",\"Label\":\"Id\",\"Category\":\"\",\"Description\":\"\",\"Attributes\":1,\"ValueType\":\"System.Guid\"},\"Value\":\"be78d6fc-61b0-4882-9d07-40d796fd96ce\"},"
                + "{\"Key\":{\"Id\":\"TestCase.LineNumber\",\"Label\":\"Line Number\",\"Category\":\"\",\"Description\":\"\",\"Attributes\":1,\"ValueType\":\"System.Int32\"},\"Value\":999},"
                + "{\"Key\":{\"Id\":\"TestObject.Traits\",\"Label\":\"Traits\",\"Category\":\"\",\"Description\":\"\",\"Attributes\":5,\"ValueType\":\"System.Collections.Generic.KeyValuePair`2[[System.String],[System.String]][]\"},\"Value\":[{\"Key\":\"Priority\",\"Value\":\"0\"},{\"Key\":\"Category\",\"Value\":\"unit\"}]}]}";
            var test = Deserialize<TestCase>(json);

            Assert.AreEqual(testCase.CodeFilePath, test.CodeFilePath);
            Assert.AreEqual(testCase.DisplayName, test.DisplayName);
            Assert.AreEqual(testCase.ExecutorUri, test.ExecutorUri);
            Assert.AreEqual(testCase.FullyQualifiedName, test.FullyQualifiedName);
            Assert.AreEqual(testCase.LineNumber, test.LineNumber);
            Assert.AreEqual(testCase.Source, test.Source);
            Assert.AreEqual(testCase.Traits.First().Name, test.Traits.First().Name);
            Assert.AreEqual(testCase.Id, test.Id);
        }

        [TestMethod]
        public void TestCaseObjectShouldSerializeWindowsPathWithEscaping()
        {
            var test = new TestCase("a.b", new Uri("uri://x"), @"C:\Test\TestAssembly.dll");

            var json = Serialize(test);

            // Use raw deserialization to validate basic properties
            dynamic data = JObject.Parse(json);
            dynamic properties = data["Properties"];
            Assert.AreEqual(@"TestCase.Source", properties[2]["Key"]["Id"].Value);
            Assert.AreEqual(@"C:\Test\TestAssembly.dll", properties[2]["Value"].Value);
        }

        [TestMethod]
        public void TestCaseObjectShouldDeserializeEscapedWindowsPath()
        {
            var json = "{\"Properties\":[{\"Key\":{\"Id\":\"TestCase.FullyQualifiedName\",\"Label\":\"FullyQualifiedName\",\"Category\":\"\",\"Description\":\"\",\"Attributes\":1,\"ValueType\":\"System.String\"},\"Value\":\"a.b\"},"
                + "{\"Key\":{\"Id\":\"TestCase.ExecutorUri\",\"Label\":\"Executor Uri\",\"Category\":\"\",\"Description\":\"\",\"Attributes\":1,\"ValueType\":\"System.Uri\"},\"Value\":\"uri://x\"},"
                + "{\"Key\":{\"Id\":\"TestCase.Source\",\"Label\":\"Source\",\"Category\":\"\",\"Description\":\"\",\"Attributes\":0,\"ValueType\":\"System.String\"},\"Value\":\"C:\\\\Test\\\\TestAssembly.dll\"}]}";

            var test = Deserialize<TestCase>(json);

            Assert.AreEqual(@"C:\Test\TestAssembly.dll", test.Source);
        }

        [TestMethod]
        public void TestCaseObjectShouldSerializeTraitsWithSpecialCharacters()
        {
            var test = new TestCase("a.b", new Uri("uri://x"), @"/tmp/a.b.dll");
            test.Traits.Add("t", @"SDJDDHW>,:&^%//\\\\");

            var json = Serialize(test);

            // Use raw deserialization to validate basic properties
            dynamic data = JObject.Parse(json);
            dynamic properties = data["Properties"];
            Assert.AreEqual(@"TestObject.Traits", properties[3]["Key"]["Id"].Value);
            Assert.AreEqual("[{\"Key\":\"t\",\"Value\":\"SDJDDHW>,:&^%//\\\\\\\\\\\\\\\\\"}]", properties[3]["Value"].ToString(Formatting.None));
        }

        [TestMethod]
        public void TestCaseObjectShouldDeserializeTraitsWithSpecialCharacters()
        {
            var json = "{\"Properties\":[{\"Key\":{\"Id\":\"TestCase.FullyQualifiedName\",\"Label\":\"FullyQualifiedName\",\"Category\":\"\",\"Description\":\"\",\"Attributes\":1,\"ValueType\":\"System.String\"},\"Value\":\"a.b\"},"
                + "{\"Key\":{\"Id\":\"TestCase.ExecutorUri\",\"Label\":\"Executor Uri\",\"Category\":\"\",\"Description\":\"\",\"Attributes\":1,\"ValueType\":\"System.Uri\"},\"Value\":\"uri://x\"},"
                + "{\"Key\":{\"Id\":\"TestCase.Source\",\"Label\":\"Source\",\"Category\":\"\",\"Description\":\"\",\"Attributes\":0,\"ValueType\":\"System.String\"},\"Value\":\"/tmp/a.b.dll\"},"
                + "{\"Key\":{\"Id\":\"TestObject.Traits\",\"Label\":\"Traits\",\"Category\":\"\",\"Description\":\"\",\"Attributes\":5,\"ValueType\":\"System.Collections.Generic.KeyValuePair`2[[System.String],[System.String]][]\"},\"Value\":[{\"Key\":\"t\",\"Value\":\"SDJDDHW>,:&^%//\\\\\\\\\\\\\\\\\"}]}]}";

            var test = Deserialize<TestCase>(json);

            var traits = test.Traits.ToArray();
            Assert.AreEqual(1, traits.Length);
            Assert.AreEqual(@"SDJDDHW>,:&^%//\\\\", traits[0].Value);
        }

        private static string Serialize<T>(T data)
        {
            return JsonDataSerializer.Instance.Serialize(data);
        }

        private static T Deserialize<T>(string json)
        {
            return JsonDataSerializer.Instance.Deserialize<T>(json);
        }
    }
}
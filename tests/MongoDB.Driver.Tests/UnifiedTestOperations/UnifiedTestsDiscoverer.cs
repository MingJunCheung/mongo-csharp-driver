﻿/* Copyright 2010-present MongoDB Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MongoDB.Bson;
using MongoDB.Bson.TestHelpers.JsonDrivenTests;
using MongoDB.Driver;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace MongoDB.Driver.Tests.UnifiedTestOperations
{
    public sealed class UnifiedTestsDiscoverer(IMessageSink messageSink) : IXunitTestCaseDiscoverer
    {
        private const string SpecPathPrefix = "MongoDB.Driver.Tests.Specifications";

        private readonly IMessageSink _messageSink = messageSink;

        public IEnumerable<IXunitTestCase> Discover(
            ITestFrameworkDiscoveryOptions discoveryOptions,
            ITestMethod testMethod,
            IAttributeInfo factAttribute)
        {
            var specPath = factAttribute.GetNamedArgument<string>(nameof(UnifiedTestsTheoryAttribute.Path));
            var skipTestsProvider = factAttribute.GetNamedArgument<string>(nameof(UnifiedTestsTheoryAttribute.SkippedTestsProvider));
            var skipFilesProvider = factAttribute.GetNamedArgument<string>(nameof(UnifiedTestsTheoryAttribute.SkippedFilesProvider));
            var testsToSkip = GetHashSetMember(testMethod.TestClass.Class, skipTestsProvider);
            var filesToSkip = GetHashSetMember(testMethod.TestClass.Class, skipFilesProvider);

            var testsFactory = new UnifiedTestCaseFactory(specPath, testsToSkip, filesToSkip);

            foreach (var testCaseArguments in testsFactory)
            {
                var jsonTestCase = testCaseArguments[0] as JsonDrivenTestCase;

                var testCase = new XunitTestCase(
                    _messageSink,
                    TestMethodDisplay.ClassAndMethod,
                    TestMethodDisplayOptions.None,
                    testMethod,
                    [jsonTestCase]);

                testCase.SourceInformation = new SourceInformation()
                {
                    FileName = jsonTestCase.Shared["_localPath"].AsString,
                    LineNumber = jsonTestCase.Test["_lineNumber"].AsInt32
                };

                yield return testCase;
            }
        }

        private HashSet<string> GetHashSetMember(ITypeInfo testClassName, string memberName)
        {
            if (memberName == null || testClassName is not ReflectionTypeInfo reflectionTypeInfo)
            {
                return null;
            }

            var provider = reflectionTypeInfo.Type.GetField(memberName, BindingFlags.NonPublic | BindingFlags.Static);
            return provider.GetValue(null) as HashSet<string>;
        }

        private sealed class UnifiedTestCaseFactory(string path, HashSet<string> testsToSkip, HashSet<string> filesToSkip) : JsonDrivenTestCaseFactory
        {
            private readonly HashSet<string> _filesToSkip = filesToSkip;
            private readonly HashSet<string> _testsToSkip = testsToSkip;
            private readonly string _path = $"{SpecPathPrefix}.{path}.";

            protected override string PathPrefix => _path;

            // protected methods
            protected override IEnumerable<JsonDrivenTestCase> CreateTestCases(BsonDocument document)
            {
                var path = document["_path"].AsString;
                var fileName = path.Replace(PathPrefix, "");

                using var stream = Assembly.GetManifestResourceStream(path);
                using var streamReader = new StreamReader(stream);
                var lines = streamReader.ReadToEnd().Split('\n')
                    .Select((Line, Index) => (Line, Index))
                    .Where(p => p.Line.Contains("description"))
                    .ToArray();

                var relativeLocalPath = path
                    .Replace(SpecPathPrefix, "")
                    .Replace(".json", "")
                    .Replace("_", "-")
                    .Replace('.', '\\');

                document
                    .Add("_localPath", Path.GetFullPath($"..\\..\\..\\..\\..\\specifications{relativeLocalPath}.json"))
                    .Add("_fileName", fileName);

                foreach (var testCase in base.CreateTestCases(document))
                {
                    if (_testsToSkip?.Contains(testCase.Name) == true)
                    {
                        continue;
                    }

                    var description = testCase.Test["description"].AsString;
                    var lineNumber = lines.FirstOrDefault(p => p.Line.Contains(description)).Index;
                    testCase.Test.Add("_lineNumber", lineNumber);

                    var test = testCase.Test.Add("async", false);
                    var name = $"{fileName}:{testCase.Name}:async={false}";
                    yield return new JsonDrivenTestCase(name, testCase.Shared, test);

                    test = testCase.Test.DeepClone().AsBsonDocument.Set("async", true);
                    name = $"{fileName}:{testCase.Name}:async={true}";
                    yield return new JsonDrivenTestCase(name, testCase.Shared, test);
                }
            }

            protected override string GetTestCaseName(BsonDocument shared, BsonDocument test, int index) =>
                GetTestName(test, index);

            protected override bool ShouldReadJsonDocument(string path) =>
                base.ShouldReadJsonDocument(path) &&
                _filesToSkip?.Any(path.Contains) != true;
        }
    }

    [XunitTestCaseDiscoverer("MongoDB.Driver.Tests.UnifiedTestOperations.UnifiedTestsDiscoverer", "MongoDB.Driver.Tests")]
    public class UnifiedTestsTheoryAttribute(string path) : FactAttribute
    {
        public string Path { get; set; } = path;
        public string SkippedTestsProvider { get; set; } = "__ignoredTests";
        public string SkippedFilesProvider { get; set; } = "__ignoredTestFiles";
    }
}

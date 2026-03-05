using GVFS.Common;
using GVFS.Hooks;
using GVFS.Tests.Should;
using NUnit.Framework;
using System.Collections.Generic;

namespace GVFS.UnitTests.Hooks
{
    [TestFixture]
    public class LsTreeParserTests
    {
        [TestCase]
        public void ParseTreeEntriesTests()
        {
            // Typical ls-tree output with trees and blobs
            string output =
                "040000 tree abc123def456abc123def456abc123def456abc1\tbase\n" +
                "040000 tree def456abc123def456abc123def456abc123def4\tshell\n" +
                "100644 blob 111111111111111111111111111111111111111a\t.gitattributes\n" +
                "040000 tree 222222222222222222222222222222222222222b\tbuild\n";

            Dictionary<string, string> result = LsTreeParser.ParseTreeEntries(
                new ProcessResult(output, string.Empty, 0));

            result.Count.ShouldEqual(3);
            result["base"].ShouldEqual("abc123def456abc123def456abc123def456abc1");
            result["shell"].ShouldEqual("def456abc123def456abc123def456abc123def4");
            result["build"].ShouldEqual("222222222222222222222222222222222222222b");
            result.ContainsKey(".gitattributes").ShouldEqual(false);

            // Empty output
            LsTreeParser.ParseTreeEntries(new ProcessResult(string.Empty, string.Empty, 0))
                .Count.ShouldEqual(0);

            // Null output
            LsTreeParser.ParseTreeEntries(new ProcessResult(null, string.Empty, 0))
                .Count.ShouldEqual(0);

            // Non-zero exit code
            LsTreeParser.ParseTreeEntries(new ProcessResult(output, "error", 1))
                .Count.ShouldEqual(0);

            // Malformed lines ignored
            string malformed = "not a valid line\n040000 tree aaa\tgood\n";
            Dictionary<string, string> partial = LsTreeParser.ParseTreeEntries(
                new ProcessResult(malformed, string.Empty, 0));
            partial.Count.ShouldEqual(1);
            partial["good"].ShouldEqual("aaa");

            // Case-insensitive keys
            result.ContainsKey("BASE").ShouldEqual(true);
            result.ContainsKey("Shell").ShouldEqual(true);
        }
    }
}

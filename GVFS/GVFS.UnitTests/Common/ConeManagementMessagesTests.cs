using GVFS.Common.NamedPipes;
using GVFS.Tests.Should;
using NUnit.Framework;
using System.Collections.Generic;

namespace GVFS.UnitTests.Common
{
    [TestFixture]
    public class ConeManagementMessagesTests
    {
        [TestCase]
        public void WidenParameters_RoundTrips_InlinePathspecs()
        {
            NamedPipeMessages.ConeManagement.WidenParameters original =
                new NamedPipeMessages.ConeManagement.WidenParameters(
                    "session-123",
                    "C:/repo/src",
                    new List<string> { "out1/f.txt", "out2/g.txt" },
                    null,
                    false);

            NamedPipeMessages.ConeManagement.WidenParameters parsed = RoundTrip(original);

            parsed.SessionId.ShouldEqual("session-123");
            parsed.CurrentDirectory.ShouldEqual("C:/repo/src");
            parsed.Pathspecs.ShouldMatchInOrder("out1/f.txt", "out2/g.txt");
            parsed.PathspecFromFile.ShouldEqual(string.Empty);
            parsed.PathspecFileNul.ShouldBeFalse();
        }

        [TestCase]
        public void WidenParameters_RoundTrips_PathspecFromFileWithNul()
        {
            NamedPipeMessages.ConeManagement.WidenParameters original =
                new NamedPipeMessages.ConeManagement.WidenParameters(
                    string.Empty,
                    "C:/repo/src",
                    new List<string>(),
                    "list.txt",
                    true);

            NamedPipeMessages.ConeManagement.WidenParameters parsed = RoundTrip(original);

            parsed.SessionId.ShouldEqual(string.Empty);
            parsed.PathspecFromFile.ShouldEqual("list.txt");
            parsed.PathspecFileNul.ShouldBeTrue();
            parsed.Pathspecs.ShouldBeEmpty();
        }

        [TestCase]
        public void WidenParameters_RoundTrips_PathWithSpacesAndPipe()
        {
            // '|' is legal in a pathspec and must survive because the pipe
            // header/body split is on the first '|' only.
            NamedPipeMessages.ConeManagement.WidenParameters original =
                new NamedPipeMessages.ConeManagement.WidenParameters(
                    "s",
                    "C:/repo/src",
                    new List<string> { "my dir/a b.txt", "glob|magic.txt" },
                    null,
                    false);

            NamedPipeMessages.ConeManagement.WidenParameters parsed = RoundTrip(original);

            parsed.Pathspecs.ShouldMatchInOrder("my dir/a b.txt", "glob|magic.txt");
        }

        [TestCase]
        public void WidenParameters_TryParse_RejectsTooFewFields()
        {
            NamedPipeMessages.ConeManagement.WidenParameters.TryParse(
                "only\0two", out NamedPipeMessages.ConeManagement.WidenParameters parsed)
                .ShouldBeFalse();
            parsed.ShouldBeNull();
        }

        [TestCase]
        public void WidenParameters_TryParse_RejectsNull()
        {
            NamedPipeMessages.ConeManagement.WidenParameters.TryParse(
                null, out NamedPipeMessages.ConeManagement.WidenParameters parsed)
                .ShouldBeFalse();
            parsed.ShouldBeNull();
        }

        [TestCase]
        public void WidenParameters_TryParse_EmptyPathspecsIsValid()
        {
            // Four fields, no trailing pathspecs (a --pathspec-from-file case).
            NamedPipeMessages.ConeManagement.WidenParameters.TryParse(
                "s\0cwd\0list.txt\0Z", out NamedPipeMessages.ConeManagement.WidenParameters parsed)
                .ShouldBeTrue();
            parsed.Pathspecs.ShouldBeEmpty();
            parsed.PathspecFromFile.ShouldEqual("list.txt");
            parsed.PathspecFileNul.ShouldBeTrue();
        }

        [TestCase]
        public void NarrowParameters_RoundTrips()
        {
            NamedPipeMessages.ConeManagement.NarrowParameters original =
                new NamedPipeMessages.ConeManagement.NarrowParameters("session-abc");

            NamedPipeMessages.ConeManagement.NarrowParameters.TryParse(
                original.ToBody(), out NamedPipeMessages.ConeManagement.NarrowParameters parsed)
                .ShouldBeTrue();

            parsed.SessionId.ShouldEqual("session-abc");
        }

        [TestCase]
        public void NarrowParameters_EmptySessionRoundTrips()
        {
            NamedPipeMessages.ConeManagement.NarrowParameters original =
                new NamedPipeMessages.ConeManagement.NarrowParameters(null);

            NamedPipeMessages.ConeManagement.NarrowParameters.TryParse(
                original.ToBody(), out NamedPipeMessages.ConeManagement.NarrowParameters parsed)
                .ShouldBeTrue();

            parsed.SessionId.ShouldEqual(string.Empty);
        }

        private static NamedPipeMessages.ConeManagement.WidenParameters RoundTrip(
            NamedPipeMessages.ConeManagement.WidenParameters original)
        {
            NamedPipeMessages.ConeManagement.WidenParameters.TryParse(
                original.ToBody(), out NamedPipeMessages.ConeManagement.WidenParameters parsed)
                .ShouldBeTrue();
            return parsed;
        }
    }
}

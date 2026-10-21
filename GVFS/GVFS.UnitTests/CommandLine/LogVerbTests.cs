using GVFS.CommandLine;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GVFS.UnitTests.CommandLine
{
    [TestFixture]
    public class LogVerbTests
    {
        [TestCase]
        public void EqualCreationTimesSelectTheHighestNumericPartInAnyEnumerationOrder()
        {
            DateTime time = new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
            string basename = @"mock:\logs\gvfs_mount_process_20261021_120000_0123456789abcdef0123456789abcdef";
            (string Path, DateTime CreationTimeUtc)[] files =
            {
                (basename + ".log", time),
                (basename + "_part000002.log", time),
                (basename + "_part1000000.log", time),
                (basename + "_part999999.log", time),
                (basename + "_part000001.log", time),
            };

            Assert.That(LogVerb.FindNewestFile(files), Is.EqualTo(basename + "_part1000000.log"));
            Assert.That(LogVerb.FindNewestFile(files.Reverse()), Is.EqualTo(basename + "_part1000000.log"));
        }

        [TestCase]
        public void PartsUseTheirSequenceEvenWhenCreationTimesAreOutOfOrder()
        {
            DateTime time = DateTime.UtcNow;
            string basename = @"mock:\logs\gvfs_prefetch_20261021_120000";
            Assert.That(LogVerb.FindNewestFile(new[]
            {
                (basename + ".log", time),
                (basename + "_part000002.log", time.AddMinutes(-1)),
                (basename + "_part000001.log", time.AddMinutes(1)),
            }), Is.EqualTo(basename + "_part000002.log"));
        }

        [TestCase]
        public void NewerLegacyInvocationWinsOverAnOlderSegmentedInvocation()
        {
            DateTime time = DateTime.UtcNow;
            string latest = @"mock:\logs\gvfs_mount_verb_20261021_130000.log";
            Assert.That(LogVerb.FindNewestFile(new[]
            {
                (@"mock:\logs\gvfs_mount_process_20261021_120000_part000002.log", time),
                (latest, time.AddHours(1)),
            }), Is.EqualTo(latest));
        }

        [TestCase]
        public void LegacyCollisionsHaveADeterministicTieBreak()
        {
            DateTime time = DateTime.UtcNow;
            (string Path, DateTime CreationTimeUtc)[] files =
            {
                (@"mock:\logs\gvfs_clone_20261021_120000_abcdef.log", time),
                (@"mock:\logs\gvfs_clone_20261021_120000_012345.log", time),
                (@"mock:\logs\gvfs_clone_20261021_120000.log", time),
            };
            Assert.That(LogVerb.FindNewestFile(files), Is.EqualTo(files[0].Path));
            Assert.That(LogVerb.FindNewestFile(files.Reverse()), Is.EqualTo(files[0].Path));
        }

        [TestCase]
        public void EmptyDirectoryReturnsNoFile()
        {
            Assert.That(LogVerb.FindNewestFile(new List<(string, DateTime)>()), Is.Null);
        }
    }
}

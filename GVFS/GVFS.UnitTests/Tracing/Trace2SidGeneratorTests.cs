using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GVFS.Common.Tracing;
using GVFS.Tests.Should;
using Newtonsoft.Json;
using NUnit.Framework;

namespace GVFS.UnitTests.Tracing
{
    [TestFixture]
    public class Trace2SidGeneratorTests
    {
        [TestCase]
        public void GenerateProducesValidFormat()
        {
            Trace2SidGenerator generator = new Trace2SidGenerator();
            string sid = generator.Generate();

            sid.ShouldNotBeNull();

            // Format: yyyyMMddTHHmmss.ffffffZ-H{hash}-P{pid}
            string[] parts = sid.Split('-');
            parts.Length.ShouldEqual(3);

            // Timestamp part should end with Z
            parts[0].ShouldContain("T");
            parts[0].ShouldContain("Z");

            // Host component should start with H (or be Localhost)
            (parts[1].StartsWith("H") || parts[1] == "Localhost").ShouldBeTrue();

            // PID component should start with P
            parts[2].StartsWith("P").ShouldBeTrue();
        }

        [TestCase]
        public void GenerateProducesUniqueSids()
        {
            Trace2SidGenerator generator = new Trace2SidGenerator();
            HashSet<string> sids = new HashSet<string>();

            for (int i = 0; i < 100; i++)
            {
                string sid = generator.Generate();
                sids.Add(sid).ShouldBeTrue("Duplicate SID detected: " + sid);
            }
        }

        [TestCase]
        public void GenerateIsMonotonic()
        {
            Trace2SidGenerator generator = new Trace2SidGenerator();
            string previousSid = generator.Generate();

            for (int i = 0; i < 100; i++)
            {
                string currentSid = generator.Generate();
                string.CompareOrdinal(currentSid, previousSid).ShouldBeAtLeast(1,
                    $"SID should be monotonically increasing: prev={previousSid}, curr={currentSid}");
                previousSid = currentSid;
            }
        }

        [TestCase]
        public void GenerateWithParentSidPrependsParent()
        {
            Trace2SidGenerator generator = new Trace2SidGenerator();
            string parentSid = "20260319T170000.000000Z-Habcdef01-P00001234";
            string sid = generator.Generate(parentSid);

            sid.StartsWith(parentSid + "/").ShouldBeTrue(
                "SID should start with parent SID followed by /");

            // The child component after the / should also be valid
            string childComponent = sid.Substring(parentSid.Length + 1);
            childComponent.ShouldContain("T");
            childComponent.ShouldContain("Z");
        }

        [TestCase]
        public void GenerateWithNullParentSidProducesTopLevel()
        {
            Trace2SidGenerator generator = new Trace2SidGenerator();
            string sid = generator.Generate(null);

            sid.ShouldNotContain(false, "/");
        }

        [TestCase]
        public void GenerateIsThreadSafe()
        {
            Trace2SidGenerator generator = new Trace2SidGenerator();
            int threadCount = 8;
            int sidsPerThread = 50;
            List<string>[] results = new List<string>[threadCount];

            Task[] tasks = Enumerable.Range(0, threadCount).Select(t =>
            {
                return Task.Run(() =>
                {
                    results[t] = new List<string>();
                    for (int i = 0; i < sidsPerThread; i++)
                    {
                        results[t].Add(generator.Generate());
                    }
                });
            }).ToArray();

            Task.WaitAll(tasks);

            HashSet<string> allSids = new HashSet<string>();
            foreach (List<string> threadSids in results)
            {
                foreach (string sid in threadSids)
                {
                    allSids.Add(sid).ShouldBeTrue("Duplicate SID across threads: " + sid);
                }
            }

            allSids.Count.ShouldEqual(threadCount * sidsPerThread);
        }
    }
}

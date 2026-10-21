using GVFS.Common.Tracing;
using GVFS.UnitTests.Category;
using NUnit.Framework;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace GVFS.UnitTests.Tracing
{
    [TestFixture]
    public class LogFileEventListenerTests
    {
        private const string LogPath = @"mock:\logs\gvfs_mount_process_20261021_120000.log";

        [TestCase]
        public void DefaultLimitIs64MiB()
        {
            Assert.That(LogFileEventListener.DefaultMaxSegmentBytes, Is.EqualTo(67_108_864));
        }

        [TestCase]
        public void DisabledModeAppendsWithoutHeadersOrSegments()
        {
            MemoryLogFiles files = new MemoryLogFiles();
            files.Add(LogPath, "existing\r\n");
            using (LogFileEventListener listener = CreateListener(files, maxBytes: 1, enabled: false))
            {
                for (int i = 0; i < 20; ++i)
                {
                    listener.RecordMessage(Record(i));
                }
            }

            Assert.That(files.Files.Count, Is.EqualTo(1));
            Assert.That(files.Files[LogPath].GetText(), Does.StartWith("existing\r\n"));
            Assert.That(files.Files[LogPath].GetText(), Does.Not.Contain("LogSegmentContext"));
            Assert.That(files.Files[LogPath].GetText(), Does.Not.Contain("LogRotation"));
            Assert.That(files.GetSequences(), Is.EqualTo(Enumerable.Range(0, 20)));
            Assert.That(files.Modes, Is.EqualTo(new[] { FileMode.OpenOrCreate }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExactBoundaryIncludesUtf8BytesAndNewlines(bool multibyte)
        {
            const int Limit = 2048;
            MemoryLogFiles files = new MemoryLogFiles();
            using (LogFileEventListener listener = CreateListener(files, maxBytes: Limit))
            {
                long headerBytes = files.Files[LogPath].Length;
                listener.RecordMessage(Record(0));
                long recordBytes = files.Files[LogPath].Length - headerBytes;
                string unicode = multibyte ? "\u00e9\u4e2d\ud83d\ude00" : string.Empty;
                string payload = new string('x', (int)(Limit - files.Files[LogPath].Length - recordBytes) - Encoding.UTF8.GetByteCount(unicode)) + unicode;
                // Retain the measured payload so the filler accounts for every remaining byte.
                payload += "{\"Sequence\":0}";
                listener.RecordMessage(new TraceEventMessage
                {
                    EventName = "Record",
                    Payload = payload,
                    Level = EventLevel.Informational,
                    Keywords = Keywords.Any,
                });

                Assert.That(files.Files.Count, Is.EqualTo(1));
                Assert.That(files.Files[LogPath].Length, Is.EqualTo(Limit));
                Assert.That(files.Files[LogPath].GetText(), Does.EndWith(payload + Environment.NewLine));
                listener.RecordMessage(Record(1));
                Assert.That(files.Files.Count, Is.EqualTo(2));
                Assert.That(files.Files[LogPath].ToArray().Length, Is.EqualTo(Limit));
                Assert.That(files.Files.Values.All(file => file.ToArray().Length <= Limit), Is.True);
                Assert.That(files.Files.Values.Any(file => file.GetText().Contains("Oversized")), Is.False);
            }
        }

        [TestCase]
        public void RecordsRemainCompleteAndOrderedAcrossManySegments()
        {
            MemoryLogFiles files = new MemoryLogFiles();
            using (LogFileEventListener listener = CreateListener(files, maxBytes: 4096))
            {
                for (int i = 0; i < 20_000; ++i)
                {
                    listener.RecordMessage(Record(i));
                }
            }

            Assert.That(files.Files.Count, Is.GreaterThan(100));
            Assert.That(files.GetSequences(), Is.EqualTo(Enumerable.Range(0, 20_000)));
            Assert.That(files.Files.Values.All(file => file.ToArray().Length <= 4096), Is.True);
            Assert.That(files.Files.Values.All(file => file.GetText().EndsWith(Environment.NewLine, StringComparison.Ordinal)), Is.True);
            Assert.That(files.Files.Values.All(file => file.IsDisposed), Is.True);
        }

        [TestCase]
        public void EverySegmentContainsBoundedOriginalIdentityAndLinks()
        {
            MemoryLogFiles files = new MemoryLogFiles();
            using (LogFileEventListener listener = CreateListener(files, maxBytes: 1024))
            {
                listener.RecordMessage(new TraceEventMessage
                {
                    EventName = "OriginalStartup",
                    Opcode = EventOpcode.Start,
                    Level = EventLevel.Informational,
                    Keywords = Keywords.Any,
                    Payload = "{\"OrdinaryData\":\"do not replay\"}",
                });
                for (int i = 0; i < 100; ++i)
                {
                    listener.RecordMessage(Record(i));
                }
            }

            string[] identityKeys = { "ProcessId", "ProcessName", "Version", "ProcessStartTimeUtc", "LogStartTimeUtc" };
            Dictionary<string, string> identity = new Dictionary<string, string>();
            string previous = null;
            for (int i = 0; i < files.Paths.Count; ++i)
            {
                string firstLine = files.Files[files.Paths[i]].GetText().Split(new[] { Environment.NewLine }, StringSplitOptions.None)[0];
                Assert.That(Encoding.UTF8.GetByteCount(firstLine), Is.LessThan(1024));
                Assert.That(firstLine, Does.Contain("] LogSegmentContext "));
                using (JsonDocument document = JsonDocument.Parse(firstLine.Substring(firstLine.IndexOf('{'))))
                {
                    JsonElement root = document.RootElement;
                    foreach (string key in identityKeys)
                    {
                        string value = root.GetProperty(key).ToString();
                        Assert.That(value, Is.Not.Empty);
                        if (i == 0)
                        {
                            identity.Add(key, value);
                        }
                        else
                        {
                            Assert.That(value, Is.EqualTo(identity[key]));
                        }
                    }

                    Assert.That(root.GetProperty("OriginalLogFile").GetString(), Is.EqualTo(Path.GetFileName(LogPath)));
                    Assert.That(root.GetProperty("Segment").GetString(), Is.EqualTo(Path.GetFileName(files.Paths[i])));
                    Assert.That(root.GetProperty("PreviousSegment").GetString(), Is.EqualTo(previous));
                    Assert.That(root.GetProperty("Part").GetInt64(), Is.EqualTo(i));
                }

                previous = Path.GetFileName(files.Paths[i]);
            }

            Assert.That(string.Concat(files.Files.Values.Select(file => file.GetText())).Split("] OriginalStartup ").Length - 1, Is.EqualTo(1));
        }

        [TestCase]
        public void OversizedRecordsGetOneSegmentEachAndAnExplicitExceptionRecord()
        {
            MemoryLogFiles files = new MemoryLogFiles();
            using (LogFileEventListener listener = CreateListener(files, maxBytes: 1024))
            {
                for (int i = 0; i < 3; ++i)
                {
                    TraceEventMessage record = Record(i);
                    record.Payload = "{\"Sequence\":" + i + ",\"Text\":\"" + new string('x', 4096) + "\"}";
                    listener.RecordMessage(record);
                }

                Assert.That(files.Files.Count, Is.EqualTo(3));
                Assert.That(files.Files.Values.All(file => file.GetText().Split("] Record ").Length - 1 == 1), Is.True);
                Assert.That(files.Files.Values.All(file => file.GetText().Split("] LogRotationOversizedRecord ").Length - 1 == 1), Is.True);
                listener.RecordMessage(Record(3));
                Assert.That(files.Files.Count, Is.EqualTo(4));
                Assert.That(files.Files[files.Paths[3]].Length, Is.LessThanOrEqualTo(1024));
            }

            Assert.That(files.GetSequences(), Is.EqualTo(Enumerable.Range(0, 4)));
        }

        [TestCase]
        public void ConcurrentProducersDoNotLoseDuplicateOrReorderTheirRecords()
        {
            const int Producers = 4;
            const int RecordsPerProducer = 500;
            MemoryLogFiles files = new MemoryLogFiles();
            using (LogFileEventListener listener = CreateListener(files, maxBytes: 2048))
            {
                Thread[] threads = new Thread[Producers];
                for (int producer = 0; producer < Producers; ++producer)
                {
                    int id = producer;
                    threads[producer] = new Thread(() =>
                    {
                        for (int i = 0; i < RecordsPerProducer; ++i)
                        {
                            listener.RecordMessage(Record((id * RecordsPerProducer) + i));
                        }
                    });
                    threads[producer].Start();
                }

                foreach (Thread thread in threads)
                {
                    Assert.That(thread.Join(TimeSpan.FromSeconds(20)), Is.True);
                }
            }

            int[] sequences = files.GetSequences().ToArray();
            Assert.That(sequences.OrderBy(value => value), Is.EqualTo(Enumerable.Range(0, Producers * RecordsPerProducer)));
            for (int producer = 0; producer < Producers; ++producer)
            {
                Assert.That(sequences.Where(value => value / RecordsPerProducer == producer),
                    Is.EqualTo(Enumerable.Range(producer * RecordsPerProducer, RecordsPerProducer)));
            }
        }

        [TestCase]
        [Category(CategoryConstants.ExceptionExpected)]
        public void ExistingPartFilesAreNotTruncatedAndCollisionGuidStaysInTheBasename()
        {
            string path = LogPath.Replace(".log", "_0123456789abcdef0123456789abcdef.log");
            string firstCollision = path.Replace(".log", "_part000001.log");
            string secondCollision = path.Replace(".log", "_part000002.log");
            MemoryLogFiles files = new MemoryLogFiles();
            files.Add(firstCollision, "first collision");
            files.Add(secondCollision, "second collision");
            using (LogFileEventListener listener = CreateListener(files, maxBytes: 1024, path: path))
            {
                listener.RecordMessage(Record(0));
                listener.RecordMessage(new TraceEventMessage
                {
                    EventName = "Record",
                    Payload = new string('x', 1024),
                    Level = EventLevel.Informational,
                    Keywords = Keywords.Any,
                });
                Assert.That(files.Files.ContainsKey(path.Replace(".log", "_part000003.log")), Is.True);
            }

            Assert.That(files.Files[firstCollision].GetText(), Is.EqualTo("first collision"));
            Assert.That(files.Files[secondCollision].GetText(), Is.EqualTo("second collision"));
        }

        [TestCase(false)]
        [TestCase(true)]
        [Category(CategoryConstants.ExceptionExpected)]
        public void InitialFilenameCollisionCreatesANewInvocationWithoutAnOverwrite(bool alreadyHasGuid)
        {
            string path = alreadyHasGuid ? LogPath.Replace(".log", "_0123456789abcdef0123456789abcdef.log") : LogPath;
            MemoryLogFiles files = new MemoryLogFiles();
            files.Add(path, "previous invocation");
            using (LogFileEventListener listener = CreateListener(files, path: path))
            {
                listener.RecordMessage(Record(0));
            }

            Assert.That(files.Files[path].GetText(), Is.EqualTo("previous invocation"));
            string newPath = files.Paths.Last();
            Assert.That(newPath, Does.StartWith(LogPath.Substring(0, LogPath.Length - 4) + "_"));
            Assert.That(Guid.TryParseExact(Path.GetFileNameWithoutExtension(newPath).Split('_').Last(), "N", out _), Is.True);
            Assert.That(Path.GetFileNameWithoutExtension(newPath).Split('_').Length, Is.EqualTo(6));
            Assert.That(files.Files[newPath].GetText(), Does.Contain("\"OriginalLogFile\":\"" + Path.GetFileName(newPath) + "\""));
        }

        [TestCase]
        [Category(CategoryConstants.ExceptionExpected)]
        public void InitialOpenFailureUsesTheListenerFailureSink()
        {
            MemoryLogFiles files = new MemoryLogFiles { OpenError = _ => new IOException("initial open failure") };
            RecordingSink sink = new RecordingSink();
            Assert.Throws<IOException>(() => CreateListener(files, sink));
            Assert.That(sink.Failures.Single(), Does.Contain("initial open failure"));
        }

        [TestCase("write")]
        [TestCase("flush")]
        [Category(CategoryConstants.ExceptionExpected)]
        public void InitialContextFailureIsReportedAndClosesTheStream(string operation)
        {
            MemoryLogFiles files = new MemoryLogFiles
            {
                OnCreate = (path, stream) =>
                {
                    stream.FailWrite = operation == "write";
                    stream.FailFlush = operation == "flush";
                },
            };
            RecordingSink sink = new RecordingSink();
            Assert.Throws<IOException>(() => CreateListener(files, sink));
            Assert.That(sink.Failures.Any(error => error.Contains(operation + " failure")), Is.True);
            Assert.That(files.Files[LogPath].IsDisposed, Is.True);
        }

        [TestCase("open")]
        [TestCase("write")]
        [TestCase("flush")]
        [Category(CategoryConstants.ExceptionExpected)]
        public void ReplacementFailureKeepsThePreviousWriterAndRetries(string operation)
        {
            MemoryLogFiles files = new MemoryLogFiles();
            RecordingSink sink = new RecordingSink();
            using (LogFileEventListener listener = CreateListener(files, sink, maxBytes: 1024))
            {
                listener.RecordMessage(Record(0));
                files.OpenError = path => operation == "open" && path != LogPath ? new IOException("replacement open failure") : null;
                files.OnCreate = (path, stream) =>
                {
                    stream.FailWrite = operation == "write";
                    stream.FailFlush = operation == "flush";
                };

                TraceEventMessage largeRecord = Record(1);
                largeRecord.Payload = "{\"Sequence\":1,\"Text\":\"" + new string('x', 1024) + "\"}";
                listener.RecordMessage(largeRecord);
                Assert.That(files.Files[LogPath].IsDisposed, Is.False);
                Assert.That(files.Files[LogPath].GetText(), Does.Contain("\"Sequence\":1"));
                Assert.That(sink.Failures, Is.Not.Empty);

                files.OpenError = null;
                files.OnCreate = null;
                listener.RecordMessage(Record(2));
                Assert.That(files.Files[LogPath].IsDisposed, Is.True);
                Assert.That(sink.Recoveries, Is.GreaterThan(0));
            }

            Assert.That(files.GetSequences(), Is.EqualTo(Enumerable.Range(0, 3)));
            Assert.That(files.Files.Values.All(file => file.IsDisposed), Is.True);
        }

        [TestCase("write")]
        [TestCase("flush")]
        [Category(CategoryConstants.ExceptionExpected)]
        public void RecordFailuresUseTheListenerFailureSink(string operation)
        {
            MemoryLogFiles files = new MemoryLogFiles();
            RecordingSink sink = new RecordingSink();
            using (LogFileEventListener listener = CreateListener(files, sink))
            {
                TestStream stream = files.Files[LogPath];
                stream.FailWrite = operation == "write";
                stream.FailFlush = operation == "flush";
                listener.RecordMessage(Record(0));
                Assert.That(sink.Failures.Last(), Does.Contain(operation + " failure"));
                stream.FailWrite = false;
                stream.FailFlush = false;
                listener.RecordMessage(Record(1));
                Assert.That(sink.Recoveries, Is.GreaterThan(0));
                if (operation == "flush")
                {
                    Assert.That(files.GetSequences(), Is.EqualTo(new[] { 0, 1 }));
                }
            }
        }

        [TestCase]
        [Category(CategoryConstants.ExceptionExpected)]
        public void RepeatedRolloverFailuresAreDeduplicatedAndRecoveryDoesNotRecurse()
        {
            MemoryLogFiles files = new MemoryLogFiles();
            using (JsonTracer tracer = new JsonTracer("test", "test", disableTelemetry: true))
            {
                CapturingListener capture = new CapturingListener();
                tracer.AddEventListener(capture);
                LogFileEventListener listener = CreateListener(files, tracer, maxBytes: 1024);
                tracer.AddEventListener(listener);
                files.OpenError = path => path == LogPath ? null : new IOException("rollover failure");
                for (int i = 0; i < 100; ++i)
                {
                    listener.RecordMessage(Record(i));
                }

                Assert.That(files.GetSequences(), Is.EqualTo(Enumerable.Range(0, 100)));
                Assert.That(capture.Messages.Count(message => message.EventName == "TraceEventListenerFailure"), Is.EqualTo(1));
                files.OpenError = null;
                listener.RecordMessage(Record(100));
                Assert.That(capture.Messages.Count(message => message.EventName == "TraceEventListenerRecovery"), Is.EqualTo(1));
                Assert.That(files.GetSequences(), Is.EqualTo(Enumerable.Range(0, 101)));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DisposalWaitsForTheCompletePendingWriteOrRollover(bool duringRollover)
        {
            MemoryLogFiles files = new MemoryLogFiles();
            RecordingSink sink = new RecordingSink();
            using (ManualResetEventSlim rolloverEntered = new ManualResetEventSlim())
            using (ManualResetEventSlim releaseRollover = new ManualResetEventSlim())
            using (ManualResetEventSlim disposeStarted = new ManualResetEventSlim())
            using (ManualResetEventSlim disposeFinished = new ManualResetEventSlim())
            using (LogFileEventListener listener = CreateListener(files, sink, maxBytes: duringRollover ? 1024 : 8192))
            {
                listener.RecordMessage(Record(0));
                Action pause = () =>
                {
                    rolloverEntered.Set();
                    releaseRollover.Wait();
                };
                if (duringRollover)
                {
                    files.OnCreate = (path, stream) => pause();
                }
                else
                {
                    files.Files[LogPath].BeforeFlush = pause;
                }

                TraceEventMessage largeRecord = Record(1);
                largeRecord.Payload = "{\"Sequence\":1,\"Text\":\"" + new string('x', 1024) + "\"}";
                Thread writer = new Thread(() => listener.RecordMessage(largeRecord));
                Thread disposer = new Thread(() =>
                {
                    disposeStarted.Set();
                    listener.Dispose();
                    disposeFinished.Set();
                });

                writer.Start();
                try
                {
                    Assert.That(rolloverEntered.Wait(TimeSpan.FromSeconds(10)), Is.True);
                    disposer.Start();
                    Assert.That(disposeStarted.Wait(TimeSpan.FromSeconds(10)), Is.True);
                    Assert.That(disposeFinished.IsSet, Is.False);
                }
                finally
                {
                    releaseRollover.Set();
                    Assert.That(writer.Join(TimeSpan.FromSeconds(10)), Is.True);
                    if (disposer.ThreadState != ThreadState.Unstarted)
                    {
                        Assert.That(disposer.Join(TimeSpan.FromSeconds(10)), Is.True);
                    }
                }

                listener.RecordMessage(Record(2));
                Assert.That(files.GetSequences(), Is.EqualTo(new[] { 0, 1 }));
                Assert.That(files.Files.Values.All(file => file.IsDisposed), Is.True);
                Assert.That(sink.Failures, Is.Empty);
            }
        }

        [TestCase]
        [Category(CategoryConstants.ExceptionExpected)]
        public void DisposalFailureIsReportedAndTheStreamStillCloses()
        {
            MemoryLogFiles files = new MemoryLogFiles();
            RecordingSink sink = new RecordingSink();
            LogFileEventListener listener = CreateListener(files, sink);
            files.Files[LogPath].FailFlush = true;
            listener.Dispose();
            listener.Dispose();
            Assert.That(sink.Failures.Single(), Does.Contain("flush failure"));
            Assert.That(files.Files[LogPath].IsDisposed, Is.True);
        }

        private static LogFileEventListener CreateListener(
            MemoryLogFiles files,
            IEventListenerEventSink sink = null,
            long maxBytes = 2048,
            bool enabled = true,
            string path = LogPath)
        {
            return new LogFileEventListener(path, EventLevel.Verbose, Keywords.Any, sink, enabled, maxBytes, files.Open);
        }

        private static TraceEventMessage Record(int sequence)
        {
            return new TraceEventMessage
            {
                EventName = "Record",
                Level = EventLevel.Informational,
                Keywords = Keywords.Any,
                Payload = "{\"Sequence\":" + sequence + "}",
            };
        }

        private class RecordingSink : IEventListenerEventSink
        {
            public ConcurrentQueue<string> Failures { get; } = new ConcurrentQueue<string>();
            public int Recoveries { get; private set; }

            public void OnListenerFailure(GVFS.Common.Tracing.EventListener listener, string errorMessage)
            {
                this.Failures.Enqueue(errorMessage);
            }

            public void OnListenerRecovery(GVFS.Common.Tracing.EventListener listener)
            {
                ++this.Recoveries;
            }
        }

        private class CapturingListener : GVFS.Common.Tracing.EventListener
        {
            public CapturingListener()
                : base(EventLevel.Verbose, Keywords.Any, null)
            {
            }

            public List<TraceEventMessage> Messages { get; } = new List<TraceEventMessage>();

            protected override void RecordMessageInternal(TraceEventMessage message)
            {
                this.Messages.Add(message);
            }
        }

        private class MemoryLogFiles
        {
            public Dictionary<string, TestStream> Files { get; } = new Dictionary<string, TestStream>();
            public List<string> Paths { get; } = new List<string>();
            public List<FileMode> Modes { get; } = new List<FileMode>();
            public Func<string, Exception> OpenError { get; set; }
            public Action<string, TestStream> OnCreate { get; set; }

            public void Add(string path, string text)
            {
                TestStream stream = new TestStream();
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                this.Files.Add(path, stream);
                this.Paths.Add(path);
            }

            public Stream Open(string path, FileMode mode)
            {
                Exception error = this.OpenError?.Invoke(path);
                if (error != null)
                {
                    throw error;
                }

                this.Modes.Add(mode);
                if (this.Files.TryGetValue(path, out TestStream existing))
                {
                    if (mode == FileMode.CreateNew)
                    {
                        throw new IOException("file already exists", unchecked((int)0x80070050));
                    }

                    return existing;
                }

                TestStream stream = new TestStream();
                this.OnCreate?.Invoke(path, stream);
                this.Files.Add(path, stream);
                this.Paths.Add(path);
                return stream;
            }

            public IEnumerable<int> GetSequences()
            {
                foreach (string path in this.Paths)
                {
                    foreach (string line in this.Files[path].GetText().Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        const string Marker = "] Record ";
                        int index = line.IndexOf(Marker, StringComparison.Ordinal);
                        if (index >= 0)
                        {
                            using (JsonDocument document = JsonDocument.Parse(line.Substring(index + Marker.Length)))
                            {
                                yield return document.RootElement.GetProperty("Sequence").GetInt32();
                            }
                        }
                    }
                }
            }
        }

        private class TestStream : MemoryStream
        {
            public bool FailWrite { get; set; }
            public bool FailFlush { get; set; }
            public bool IsDisposed { get; private set; }
            public Action BeforeFlush { get; set; }

            public string GetText()
            {
                return Encoding.UTF8.GetString(this.ToArray());
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (this.FailWrite)
                {
                    throw new IOException("write failure");
                }

                base.Write(buffer, offset, count);
            }

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                if (this.FailWrite)
                {
                    throw new IOException("write failure");
                }

                base.Write(buffer);
            }

            public override void Flush()
            {
                this.BeforeFlush?.Invoke();
                if (this.FailFlush)
                {
                    throw new IOException("flush failure");
                }

                base.Flush();
            }

            protected override void Dispose(bool disposing)
            {
                this.IsDisposed = true;
                base.Dispose(disposing);
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace GVFS.Common.Tracing
{
    public class LogFileEventListener : EventListener
    {
        internal const long DefaultMaxSegmentBytes = 64 * 1024 * 1024;

        private readonly object writeLock = new object();
        private readonly bool enableRotation;
        private readonly long maxSegmentBytes;
        private readonly Func<string, FileMode, Stream> openFile;
        private readonly EventMetadata startupContext;

        private Stream logFile;
        private StreamWriter writer;
        private string originalLogFilePath;
        private string currentLogFilePath;
        private long nextPart;
        private long currentBytes;
        private bool hasRecords;
        private bool needsFlush;
        private bool disposed;

        public LogFileEventListener(string logFilePath, EventLevel maxVerbosity, Keywords keywordFilter, IEventListenerEventSink eventSink, bool enableRotation = false)
            : this(logFilePath, maxVerbosity, keywordFilter, eventSink, enableRotation, DefaultMaxSegmentBytes, OpenLogFile)
        {
        }

        internal LogFileEventListener(
            string logFilePath,
            EventLevel maxVerbosity,
            Keywords keywordFilter,
            IEventListenerEventSink eventSink,
            bool enableRotation,
            long maxSegmentBytes,
            Func<string, FileMode, Stream> openFile)
            : base(maxVerbosity, keywordFilter, eventSink)
        {
            if (maxSegmentBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSegmentBytes));
            }

            this.enableRotation = enableRotation;
            this.maxSegmentBytes = maxSegmentBytes;
            this.openFile = openFile;
            this.originalLogFilePath = logFilePath;

            if (enableRotation)
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    this.startupContext = new EventMetadata
                    {
                        { "ProcessId", process.Id },
                        { "ProcessName", process.ProcessName },
                        { "Version", ProcessHelper.GetCurrentProcessVersion() },
                        { "ProcessStartTimeUtc", process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) },
                        { "LogStartTimeUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                    };
                }
            }

            try
            {
                this.OpenInitialFile();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                this.RaiseListenerFailure(ex.ToString());
                throw;
            }
        }

        public override void Dispose()
        {
            string error;
            lock (this.writeLock)
            {
                if (this.disposed)
                {
                    return;
                }

                this.disposed = true;
                error = CloseWriter(this.writer, this.logFile);
                this.writer = null;
                this.logFile = null;
            }

            if (error != null)
            {
                this.RaiseListenerFailure(error);
            }
        }

        protected override void RecordMessageInternal(TraceEventMessage message)
        {
            string rolloverError = null;
            lock (this.writeLock)
            {
                if (this.disposed)
                {
                    return;
                }

                if (!this.enableRotation)
                {
                    this.writer.WriteLine(this.GetLogString(message.EventName, message.Opcode, message.Payload));
                    this.writer.Flush();
                    return;
                }

                // Retry a failed flush before a rollover can close the previous writer.
                if (this.needsFlush)
                {
                    this.writer.Flush();
                    this.currentBytes = this.logFile.Position;
                    this.needsFlush = false;
                }

                string record = this.GetLogString(message.EventName, message.Opcode, message.Payload) + this.writer.NewLine;
                long recordBytes = this.writer.Encoding.GetByteCount(record);

                if (this.hasRecords && this.currentBytes + recordBytes > this.maxSegmentBytes)
                {
                    rolloverError = this.Rotate();
                }

                if (!this.hasRecords && this.currentBytes + recordBytes > this.maxSegmentBytes)
                {
                    EventMetadata metadata = new EventMetadata
                    {
                        { TracingConstants.MessageKey.WarningMessage, "A complete log record and its context exceed the segment byte limit." },
                        { "RecordBytes", recordBytes },
                        { "ContextBytes", this.currentBytes },
                        { "MaxSegmentBytes", this.maxSegmentBytes },
                    };
                    this.WriteRecord(this.GetLogString("LogRotationOversizedRecord", EventOpcode.Info, EventMetadataConverter.SerializeToString(metadata)) + this.writer.NewLine);
                }

                this.hasRecords = true;
                this.WriteRecord(record);
            }

            // A sink can write to other listeners. Do not call it under the file lock.
            if (rolloverError != null)
            {
                this.RaiseListenerFailure(rolloverError);
            }
            else
            {
                this.RaiseListenerRecovery();
            }
        }

        private static Stream OpenLogFile(string path, FileMode mode)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return File.Open(path, mode, FileAccess.ReadWrite, FileShare.Read);
        }

        private static bool IsFileAlreadyExists(IOException exception)
        {
            int errorCode = exception.HResult & 0xFFFF;
            return errorCode == 80 || errorCode == 183;
        }

        private static string CloseWriter(StreamWriter writer, Stream file)
        {
            string error = null;
            try
            {
                writer?.Dispose();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = ex.ToString();
            }
            finally
            {
                try
                {
                    file?.Dispose();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    error = error == null ? ex.ToString() : error + Environment.NewLine + ex.ToString();
                }
            }

            return error;
        }

        private void OpenInitialFile()
        {
            string path = this.originalLogFilePath;
            while (true)
            {
                try
                {
                    this.logFile = this.openFile(path, this.enableRotation ? FileMode.CreateNew : FileMode.OpenOrCreate);
                    break;
                }
                catch (IOException ex) when (this.enableRotation && IsFileAlreadyExists(ex))
                {
                    string basename = Path.GetFileNameWithoutExtension(this.originalLogFilePath);
                    int suffix = basename.LastIndexOf('_');
                    if (suffix >= 0 && Guid.TryParseExact(basename.Substring(suffix + 1), "N", out _))
                    {
                        basename = basename.Substring(0, suffix);
                    }

                    path = Path.Combine(
                        Path.GetDirectoryName(this.originalLogFilePath),
                        basename + "_" + Guid.NewGuid().ToString("N") + ".log");
                }
            }

            this.originalLogFilePath = path;
            this.currentLogFilePath = path;
            bool initialized = false;
            try
            {
                this.logFile.Seek(0, SeekOrigin.End);
                this.writer = new StreamWriter(this.logFile, new UTF8Encoding(false, true), 1024, leaveOpen: true);
                this.currentBytes = this.logFile.Position;
                if (this.enableRotation)
                {
                    this.WriteRecord(this.GetSegmentContext(path, previousPath: null, part: 0));
                }

                initialized = true;
            }
            finally
            {
                if (!initialized)
                {
                    string error = CloseWriter(this.writer, this.logFile);
                    if (error != null)
                    {
                        this.RaiseListenerFailure(error);
                    }
                }
            }
        }

        private string Rotate()
        {
            Stream nextFile = null;
            StreamWriter nextWriter = null;
            string path;
            try
            {
                while (true)
                {
                    this.nextPart = checked(this.nextPart + 1);
                    path = Path.Combine(
                        Path.GetDirectoryName(this.originalLogFilePath),
                        Path.GetFileNameWithoutExtension(this.originalLogFilePath) + "_part" + this.nextPart.ToString("D6", CultureInfo.InvariantCulture) + ".log");
                    try
                    {
                        nextFile = this.openFile(path, FileMode.CreateNew);
                        break;
                    }
                    catch (IOException ex) when (IsFileAlreadyExists(ex))
                    {
                    }
                }

                nextWriter = new StreamWriter(nextFile, new UTF8Encoding(false, true), 1024, leaveOpen: true);
                nextWriter.Write(this.GetSegmentContext(path, this.currentLogFilePath, this.nextPart));
                nextWriter.Flush();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (nextFile == null)
                {
                    --this.nextPart;
                }

                string closeError = CloseWriter(nextWriter, nextFile);
                return closeError == null ? ex.ToString() : ex.ToString() + Environment.NewLine + closeError;
            }

            StreamWriter previousWriter = this.writer;
            Stream previousFile = this.logFile;
            this.writer = nextWriter;
            this.logFile = nextFile;
            this.currentLogFilePath = path;
            this.currentBytes = nextFile.Position;
            this.hasRecords = false;
            return CloseWriter(previousWriter, previousFile);
        }

        private string GetSegmentContext(string path, string previousPath, long part)
        {
            EventMetadata metadata = new EventMetadata(this.startupContext)
            {
                { "OriginalLogFile", Path.GetFileName(this.originalLogFilePath) },
                { "Segment", Path.GetFileName(path) },
                { "PreviousSegment", previousPath == null ? null : Path.GetFileName(previousPath) },
                { "Part", part },
            };

            return this.GetLogString("LogSegmentContext", EventOpcode.Info, EventMetadataConverter.SerializeToString(metadata)) + Environment.NewLine;
        }

        private void WriteRecord(string record)
        {
            this.needsFlush = true;
            this.writer.Write(record);
            this.writer.Flush();
            this.currentBytes = this.logFile.Position;
            this.needsFlush = false;
        }
    }
}

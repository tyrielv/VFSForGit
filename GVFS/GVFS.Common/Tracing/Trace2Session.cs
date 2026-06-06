using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace GVFS.Common.Tracing
{
    /// <summary>
    /// Represents a single Trace2 session on a dedicated pipe connection.
    /// Each session is a complete Trace2 event stream: version → start →
    /// events → exit → atexit. The pipe connection is opened at Begin()
    /// and closed at Dispose().
    ///
    /// Thread-safe and self-contained: each instance owns its own pipe
    /// connection, SID, stopwatch, and nesting depth. Multiple sessions
    /// can be active concurrently from different threads.
    /// </summary>
    public class Trace2Session : IDisposable
    {
        private const string EventFormatVersion = "4";

        private readonly string sid;
        private readonly string pipeName;
        private readonly Stopwatch elapsed;
        private readonly string exeVersion;
        private NamedPipeClientStream pipe;
        private StreamWriter writer;
        private int nestingDepth;
        private bool disposed;

        private Trace2Session(string pipeName, string sid, string exeVersion)
        {
            this.pipeName = pipeName;
            this.sid = sid;
            this.exeVersion = exeVersion;
            this.elapsed = Stopwatch.StartNew();
        }

        public string Sid => this.sid;

        /// <summary>
        /// Begin a new Trace2 session. Opens a pipe connection and sends
        /// the session preamble (version, start, cmd_name, def_params).
        /// Returns null if the pipe is unavailable.
        /// </summary>
        public static Trace2Session Begin(
            string pipeName,
            Trace2SidGenerator sidGenerator,
            string cmdName,
            string cmdMode,
            string[] argv,
            string mountId,
            string enlistmentId,
            string worktree,
            string parentSid = null)
        {
            string sid = sidGenerator.Generate(parentSid);
            string exeVersion = ProcessHelper.GetCurrentProcessVersion();

            var session = new Trace2Session(pipeName, sid, exeVersion);

            try
            {
                session.pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                session.pipe.Connect(timeout: 0);
                session.writer = new StreamWriter(session.pipe, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception)
            {
                session.Dispose();
                return null;
            }

            // Emit session preamble
            session.WriteVersionEvent();
            session.WriteStartEvent(argv);

            string verb = cmdName;
            string hierarchy = cmdName;
            if (cmdName.Contains(":"))
            {
                verb = cmdName.Substring(cmdName.IndexOf(':') + 1);
            }

            session.WriteCmdNameEvent(verb, hierarchy);

            if (cmdMode != null)
            {
                session.WriteCmdModeEvent(cmdMode);
            }

            if (mountId != null)
            {
                session.WriteDefParamEvent("gvfs.mount-id", mountId);
            }

            if (enlistmentId != null)
            {
                session.WriteDefParamEvent("gvfs.enlistment-id", enlistmentId);
            }

            if (worktree != null)
            {
                session.WriteDefRepoEvent(worktree);
            }

            return session;
        }

        public void RegionEnter(string category, string label, string message = null)
        {
            this.nestingDepth++;
            this.WriteJson(jw =>
            {
                jw.WriteString("event", "region_enter");
                this.WriteCommonFields(jw);
                WriteOptionalInt(jw, "repo", 1);
                WriteOptionalInt(jw, "nesting", this.nestingDepth);
                WriteOptionalString(jw, "category", category);
                WriteOptionalString(jw, "label", label);
                WriteOptionalString(jw, "msg", message);
            });
        }

        public void RegionLeave(string category, string label, double elapsedSeconds)
        {
            this.WriteJson(jw =>
            {
                jw.WriteString("event", "region_leave");
                this.WriteCommonFields(jw);
                WriteOptionalInt(jw, "repo", 1);
                jw.WriteNumber("t_rel", Math.Round(elapsedSeconds, 6));
                WriteOptionalInt(jw, "nesting", this.nestingDepth);
                WriteOptionalString(jw, "category", category);
                WriteOptionalString(jw, "label", label);
            });
            this.nestingDepth--;
        }

        public void WriteDataEvent(string category, string key, string value)
        {
            this.WriteJson(jw =>
            {
                jw.WriteString("event", "data");
                this.WriteCommonFields(jw);
                WriteOptionalInt(jw, "repo", 1);
                jw.WriteNumber("t_abs", Math.Round(this.elapsed.Elapsed.TotalSeconds, 6));
                jw.WriteNumber("t_rel", Math.Round(this.elapsed.Elapsed.TotalSeconds, 6));
                WriteOptionalInt(jw, "nesting", this.nestingDepth);
                WriteOptionalString(jw, "category", category);
                WriteOptionalString(jw, "key", key);
                WriteOptionalString(jw, "value", value);
            });
        }

        public void WriteDataJsonEvent(string category, string key, object value)
        {
            this.WriteJson(jw =>
            {
                jw.WriteString("event", "data_json");
                this.WriteCommonFields(jw);
                WriteOptionalInt(jw, "repo", 1);
                jw.WriteNumber("t_abs", Math.Round(this.elapsed.Elapsed.TotalSeconds, 6));
                jw.WriteNumber("t_rel", Math.Round(this.elapsed.Elapsed.TotalSeconds, 6));
                WriteOptionalInt(jw, "nesting", this.nestingDepth);
                WriteOptionalString(jw, "category", category);
                WriteOptionalString(jw, "key", key);
                jw.WritePropertyName("value");
                jw.WriteRawValue(JsonSerializer.Serialize(value));
            });
        }

        public void WriteErrorEvent(string message)
        {
            this.WriteJson(jw =>
            {
                jw.WriteString("event", "error");
                this.WriteCommonFields(jw);
                WriteOptionalString(jw, "msg", message);
                WriteOptionalString(jw, "fmt", message);
            });
        }

        /// <summary>
        /// End the session with an exit code. Sends exit + atexit events
        /// and closes the pipe connection.
        /// </summary>
        public void End(int exitCode)
        {
            if (this.disposed)
            {
                return;
            }

            double totalSeconds = this.elapsed.Elapsed.TotalSeconds;

            this.WriteJson(jw =>
            {
                jw.WriteString("event", "exit");
                this.WriteCommonFields(jw);
                jw.WriteNumber("t_abs", Math.Round(totalSeconds, 6));
                jw.WriteNumber("code", exitCode);
            });

            this.WriteJson(jw =>
            {
                jw.WriteString("event", "atexit");
                this.WriteCommonFields(jw);
                jw.WriteNumber("t_abs", Math.Round(totalSeconds, 6));
                jw.WriteNumber("code", exitCode);
            });

            this.CloseConnection();
        }

        public void Dispose()
        {
            if (!this.disposed)
            {
                this.End(0);
            }
        }

        private void WriteVersionEvent()
        {
            this.WriteJson(jw =>
            {
                jw.WriteString("event", "version");
                this.WriteCommonFields(jw);
                jw.WriteString("evt", EventFormatVersion);
                jw.WriteString("exe", this.exeVersion);
            });
        }

        private void WriteStartEvent(string[] argv)
        {
            this.WriteJson(jw =>
            {
                jw.WriteString("event", "start");
                this.WriteCommonFields(jw);
                jw.WriteNumber("t_abs", Math.Round(this.elapsed.Elapsed.TotalSeconds, 6));
                jw.WritePropertyName("argv");
                jw.WriteStartArray();
                if (argv != null)
                {
                    foreach (string arg in argv)
                    {
                        jw.WriteStringValue(arg);
                    }
                }

                jw.WriteEndArray();
            });
        }

        private void WriteCmdNameEvent(string name, string hierarchy)
        {
            this.WriteJson(jw =>
            {
                jw.WriteString("event", "cmd_name");
                this.WriteCommonFields(jw);
                WriteOptionalString(jw, "name", name);
                WriteOptionalString(jw, "hierarchy", hierarchy);
            });
        }

        private void WriteCmdModeEvent(string mode)
        {
            this.WriteJson(jw =>
            {
                jw.WriteString("event", "cmd_mode");
                this.WriteCommonFields(jw);
                WriteOptionalString(jw, "name", mode);
            });
        }

        private void WriteDefParamEvent(string param, string value)
        {
            this.WriteJson(jw =>
            {
                jw.WriteString("event", "def_param");
                this.WriteCommonFields(jw);
                WriteOptionalString(jw, "scope", "local");
                WriteOptionalString(jw, "param", param);
                WriteOptionalString(jw, "value", value);
            });
        }

        private void WriteDefRepoEvent(string worktree)
        {
            this.WriteJson(jw =>
            {
                jw.WriteString("event", "def_repo");
                this.WriteCommonFields(jw);
                WriteOptionalInt(jw, "repo", 1);
                WriteOptionalString(jw, "worktree", worktree);
            });
        }

        private void WriteCommonFields(Utf8JsonWriter jw)
        {
            jw.WriteString("sid", this.sid);
            jw.WriteString("thread", System.Threading.Thread.CurrentThread.Name ?? "main");
            jw.WriteString("time", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ"));
        }

        private void WriteJson(Action<Utf8JsonWriter> writeContent)
        {
            if (this.disposed || this.writer == null)
            {
                return;
            }

            try
            {
                using (MemoryStream ms = new MemoryStream(256))
                {
                    using (Utf8JsonWriter jw = new Utf8JsonWriter(ms))
                    {
                        jw.WriteStartObject();
                        writeContent(jw);
                        jw.WriteEndObject();
                    }

                    this.writer.WriteLine(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                }
            }
            catch (Exception)
            {
                this.CloseConnection();
            }
        }

        private void CloseConnection()
        {
            this.disposed = true;

            try
            {
                this.writer?.Dispose();
            }
            catch
            {
            }

            try
            {
                this.pipe?.Dispose();
            }
            catch
            {
            }

            this.writer = null;
            this.pipe = null;
        }

        private static void WriteOptionalString(Utf8JsonWriter jw, string key, string value)
        {
            if (value != null)
            {
                jw.WriteString(key, value);
            }
        }

        private static void WriteOptionalInt(Utf8JsonWriter jw, string key, int value)
        {
            jw.WriteNumber(key, value);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using GVFS.Common.Git;

namespace GVFS.Common.Tracing
{
    /// <summary>
    /// EventListener that routes TraceEventMessages to Trace2 sessions on
    /// a named pipe consumed by trace2receiver (OTEL collector).
    ///
    /// Sessions are automatically opened when the first StartActivity event
    /// arrives on a thread with no active session, and closed when the
    /// matching Stop event brings the nesting depth back to zero. This means
    /// existing StartActivity/Stop patterns in maintenance steps, heartbeat,
    /// etc. naturally create discrete OTEL traces without any special calls.
    ///
    /// Events on threads with no active session (e.g., the mount root
    /// tracer's long-lived activity) are silently dropped. Only activities
    /// created via StartActivity (ParentActivityId != Guid.Empty) trigger
    /// session creation.
    /// </summary>
    public class Trace2PipeEventListener : EventListener
    {
        private readonly string pipeName;
        private readonly string enlistmentId;
        private readonly string mountId;
        private readonly string worktree;
        private readonly Trace2SidGenerator sidGenerator;

        private readonly AsyncLocal<Trace2Session> activeSession = new AsyncLocal<Trace2Session>();
        private readonly AsyncLocal<int> nestingDepth = new AsyncLocal<int>();

        private string gitCommandSessionId;

        private Trace2PipeEventListener(
            string pipeName,
            string enlistmentId,
            string mountId,
            string worktree,
            IEventListenerEventSink eventSink)
            : base(EventLevel.Verbose, Keywords.Any, eventSink)
        {
            this.pipeName = pipeName;
            this.enlistmentId = enlistmentId;
            this.mountId = mountId;
            this.worktree = worktree;
            this.sidGenerator = new Trace2SidGenerator();
        }

        public string GitCommandSessionId
        {
            get { return this.gitCommandSessionId; }
            set { this.gitCommandSessionId = value; }
        }

        /// <summary>
        /// Returns the SID of the active Trace2 session on the current thread,
        /// or null if no session is active. Used to set GIT_TRACE2_PARENT_SID
        /// on child git processes for parent-child trace correlation.
        /// </summary>
        public string GetActiveSessionSid()
        {
            return this.activeSession.Value?.Sid;
        }

        public override Dictionary<string, string> GetChildProcessEnvironment()
        {
            string sid = this.GetActiveSessionSid();
            if (sid != null)
            {
                return new Dictionary<string, string>
                {
                    ["GIT_TRACE2_PARENT_SID"] = sid
                };
            }

            return null;
        }

        public static Trace2PipeEventListener CreateIfEnabled(
            string gitBinRoot,
            string enlistmentId,
            string mountId,
            string worktree,
            IEventListenerEventSink eventSink)
        {
            string pipeName = GetConfigValue(gitBinRoot, GVFSConstants.GitConfig.GVFSTrace2Pipe);
            if (string.IsNullOrEmpty(pipeName))
            {
                return null;
            }

            return new Trace2PipeEventListener(pipeName, enlistmentId, mountId, worktree, eventSink);
        }

        protected override void RecordMessageInternal(TraceEventMessage message)
        {
            switch (message.Opcode)
            {
                case EventOpcode.Start:
                    this.HandleStart(message);
                    break;

                case EventOpcode.Stop:
                    this.HandleStop(message);
                    break;

                default:
                    this.HandleInfo(message);
                    break;
            }
        }

        private void HandleStart(TraceEventMessage message)
        {
            Trace2Session session = this.activeSession.Value;

            if (session == null)
            {
                // Only auto-open for child activities (from StartActivity),
                // not the root tracer's long-lived activity.
                if (message.ParentActivityId == Guid.Empty)
                {
                    return;
                }

                string parentSid = string.IsNullOrEmpty(this.gitCommandSessionId)
                    ? null
                    : this.gitCommandSessionId;

                session = Trace2Session.Begin(
                    this.pipeName,
                    this.sidGenerator,
                    "gvfs:" + message.EventName,
                    cmdMode: null,
                    argv: new[] { "gvfs", message.EventName },
                    this.mountId,
                    this.enlistmentId,
                    this.worktree,
                    parentSid);

                if (session == null)
                {
                    return;
                }

                this.activeSession.Value = session;
                this.nestingDepth.Value = 0;
            }

            this.nestingDepth.Value++;
            session.RegionEnter(message.EventName, message.EventName);
        }

        private void HandleStop(TraceEventMessage message)
        {
            Trace2Session session = this.activeSession.Value;
            if (session == null)
            {
                return;
            }

            double durationSec = 0;
            if (message.Payload != null)
            {
                try
                {
                    var metadata = Newtonsoft.Json.JsonConvert.DeserializeObject<EventMetadata>(message.Payload);
                    if (metadata != null && metadata.ContainsKey("DurationMs"))
                    {
                        durationSec = Convert.ToDouble(metadata["DurationMs"]) / 1000.0;
                    }
                }
                catch
                {
                }
            }

            session.RegionLeave(message.EventName, message.EventName, durationSec);

            this.nestingDepth.Value--;
            if (this.nestingDepth.Value <= 0)
            {
                this.activeSession.Value = null;
                this.nestingDepth.Value = 0;
                session.Dispose();
            }
        }

        private void HandleInfo(TraceEventMessage message)
        {
            Trace2Session session = this.activeSession.Value;
            if (session == null)
            {
                return;
            }

            if (message.Level <= EventLevel.Error)
            {
                string errorMsg = ExtractMessage(message.Payload, TracingConstants.MessageKey.ErrorMessage);
                if (errorMsg != null)
                {
                    session.WriteErrorEvent(errorMsg);
                }
            }
            else if (message.Payload != null)
            {
                session.WriteDataEvent(message.EventName, "payload", message.Payload);
            }
        }

        private static string ExtractMessage(string jsonPayload, string key)
        {
            if (string.IsNullOrEmpty(jsonPayload))
            {
                return null;
            }

            try
            {
                var dict = Newtonsoft.Json.JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, object>>(jsonPayload);
                if (dict != null && dict.TryGetValue(key, out object value) && value != null)
                {
                    return value.ToString();
                }
            }
            catch
            {
            }

            return null;
        }

        private static string GetConfigValue(string gitBinRoot, string configKey)
        {
            string value = string.Empty;
            string error;

            GitProcess.ConfigResult result = GitProcess.GetFromSystemConfig(gitBinRoot, configKey);
            if (!result.TryParseAsString(out value, out error, defaultValue: string.Empty) || string.IsNullOrWhiteSpace(value))
            {
                result = GitProcess.GetFromGlobalConfig(gitBinRoot, configKey);
                result.TryParseAsString(out value, out error, defaultValue: string.Empty);
            }

            return value.TrimEnd('\r', '\n');
        }
    }
}

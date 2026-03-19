using System;
using GVFS.Common.Git;

namespace GVFS.Common.Tracing
{
    /// <summary>
    /// Manages Trace2 telemetry to a named pipe consumed by the trace2receiver
    /// OTEL collector. Unlike TelemetryDaemonEventListener (which maintains a
    /// single long-lived pipe connection), this creates discrete per-operation
    /// sessions, each with its own pipe connection and Trace2 event stream.
    ///
    /// This is necessary because trace2receiver maps one pipe connection to
    /// one OTEL trace and only exports data when the connection closes. A
    /// long-running process like gvfs mount would cause unbounded memory
    /// growth and zero telemetry until exit.
    ///
    /// Usage:
    ///   using (var session = listener.BeginSession("gvfs:prefetch", "prefetch"))
    ///   {
    ///       session.RegionEnter("prefetch", "download-packs");
    ///       // ... do work ...
    ///       session.RegionLeave("prefetch", "download-packs", elapsed);
    ///   }
    /// </summary>
    public class Trace2PipeEventListener : IDisposable
    {
        private readonly string pipeName;
        private readonly string enlistmentId;
        private readonly string mountId;
        private readonly string worktree;
        private readonly Trace2SidGenerator sidGenerator;

        private string gitCommandSessionId;

        private Trace2PipeEventListener(
            string pipeName,
            string enlistmentId,
            string mountId,
            string worktree)
        {
            this.pipeName = pipeName;
            this.enlistmentId = enlistmentId;
            this.mountId = mountId;
            this.worktree = worktree;
            this.sidGenerator = new Trace2SidGenerator();
        }

        /// <summary>
        /// Gets or sets the Trace2 SID of the currently active git command.
        /// When set, new sessions created during this window will use it as
        /// a parent SID, creating a hierarchical OTEL trace that links the
        /// git command and VFS operation together.
        /// </summary>
        public string GitCommandSessionId
        {
            get { return this.gitCommandSessionId; }
            set { this.gitCommandSessionId = value; }
        }

        /// <summary>
        /// Create a Trace2PipeEventListener if the gvfs.trace2-pipe config
        /// setting is present. Returns null if not configured.
        /// </summary>
        public static Trace2PipeEventListener CreateIfEnabled(
            string gitBinRoot,
            string enlistmentId,
            string mountId,
            string worktree)
        {
            string pipeName = GetConfigValue(gitBinRoot, GVFSConstants.GitConfig.GVFSTrace2Pipe);
            if (string.IsNullOrEmpty(pipeName))
            {
                return null;
            }

            return new Trace2PipeEventListener(pipeName, enlistmentId, mountId, worktree);
        }

        /// <summary>
        /// Begin a new discrete Trace2 session. Opens a pipe connection and
        /// sends the session preamble. Returns null if the pipe is unavailable.
        ///
        /// If a git command is currently active (GitCommandSessionId is set),
        /// the session will use it as a parent SID, creating a parent-child
        /// span relationship in the same OTEL trace.
        /// </summary>
        public Trace2Session BeginSession(string cmdName, string cmdMode = null, string[] argv = null)
        {
            string parentSid = string.IsNullOrEmpty(this.gitCommandSessionId)
                ? null
                : this.gitCommandSessionId;

            return Trace2Session.Begin(
                this.pipeName,
                this.sidGenerator,
                cmdName,
                cmdMode,
                argv ?? new[] { "gvfs", cmdName },
                this.mountId,
                this.enlistmentId,
                this.worktree,
                parentSid);
        }

        public void Dispose()
        {
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

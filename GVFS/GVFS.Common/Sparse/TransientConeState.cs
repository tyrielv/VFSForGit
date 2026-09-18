using System;
using System.Collections.Generic;

namespace GVFS.Common.Sparse
{
    /// <summary>
    /// Tracks the cone entries that each Git command session has transiently added by
    /// widening. A widen unions the requesting session's paths into the cone; the
    /// matching narrow, keyed by the same session id, drops them again. The cone the
    /// mount applies is always the union of the modified-path-derived entries plus the
    /// live transient entries across every session, so overlapping commands never undo
    /// one another's widen.
    /// </summary>
    /// <remarks>
    /// This type performs no I/O and is not thread safe; the mount serializes all cone
    /// operations under a single lock, so the state is only ever touched by one thread
    /// at a time. It exists separately from the mount orchestration so the union and
    /// drop policy can be unit tested without a live mount.
    /// </remarks>
    public class TransientConeState
    {
        private readonly Dictionary<string, HashSet<string>> pathsBySession =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        /// <summary>The number of sessions that currently hold a transient widen.</summary>
        public int SessionCount => this.pathsBySession.Count;

        /// <summary>
        /// Add transient cone entries for a session. A repeated widen in the same session
        /// accumulates, so a command that names more paths over its lifetime keeps them all.
        /// A null or empty session id is tracked under a single shared key.
        /// </summary>
        public void AddPaths(string sessionId, IEnumerable<string> gitPaths)
        {
            ArgumentNullException.ThrowIfNull(gitPaths);

            string key = sessionId ?? string.Empty;
            if (!this.pathsBySession.TryGetValue(key, out HashSet<string> sessionPaths))
            {
                sessionPaths = new HashSet<string>(StringComparer.Ordinal);
                this.pathsBySession[key] = sessionPaths;
            }

            foreach (string gitPath in gitPaths)
            {
                if (!string.IsNullOrEmpty(gitPath))
                {
                    sessionPaths.Add(gitPath);
                }
            }
        }

        /// <summary>
        /// Drop a session's transient cone entries. Returns true when the session had a
        /// live widen, false when there was nothing to drop (an unmatched narrow).
        /// </summary>
        public bool RemoveSession(string sessionId)
        {
            return this.pathsBySession.Remove(sessionId ?? string.Empty);
        }

        /// <summary>
        /// The union of every session's live transient cone entries. The mount feeds this,
        /// together with the modified paths, into <see cref="ConeBuilder"/>.
        /// </summary>
        public IEnumerable<string> GetAllTransientPaths()
        {
            HashSet<string> union = new HashSet<string>(StringComparer.Ordinal);
            foreach (HashSet<string> sessionPaths in this.pathsBySession.Values)
            {
                union.UnionWith(sessionPaths);
            }

            return union;
        }
    }
}

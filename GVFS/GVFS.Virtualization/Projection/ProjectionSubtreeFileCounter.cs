using System.Collections.Generic;
using GVFS.Common;
using GVFS.Common.Sparse;

namespace GVFS.Virtualization.Projection
{
    /// <summary>
    /// Counts subtree files for the cone granularity heuristic by walking the in-memory
    /// projection.
    /// </summary>
    /// <remarks>
    /// Reads only through <see cref="GitIndexProjection.TryGetProjectedItemsFromMemory"/>, so it
    /// never touches disk, never fetches an object, and never takes the projection write lock.
    /// A folder that is not resolvable from memory -- because the projection is still parsing,
    /// or the path is not projected -- returns false, which the heuristic treats as "do not
    /// collapse". The conservative answer is always the narrower cone.
    /// <para>
    /// The walk is depth first with an explicit stack and stops the moment the running count
    /// exceeds the cap, so the cost is bounded by the cap rather than by the real subtree size.
    /// </para>
    /// </remarks>
    public class ProjectionSubtreeFileCounter : IConeSubtreeFileCounter
    {
        private readonly GitIndexProjection projection;

        public ProjectionSubtreeFileCounter(GitIndexProjection projection)
        {
            this.projection = projection;
        }

        public bool TryCountSubtreeFiles(string directory, int cap, out int count)
        {
            count = 0;

            if (this.projection == null || string.IsNullOrEmpty(directory) || cap < 0)
            {
                return false;
            }

            Stack<string> pending = new Stack<string>();
            pending.Push(directory);

            while (pending.Count > 0)
            {
                string current = pending.Pop();

                List<ProjectedFileInfo> items;
                if (!this.projection.TryGetProjectedItemsFromMemory(current, out items) || items == null)
                {
                    // Unresolvable folder. Fail closed so the caller keeps the parent-only chain.
                    count = 0;
                    return false;
                }

                foreach (ProjectedFileInfo item in items)
                {
                    if (item.IsFolder)
                    {
                        pending.Push(current + GVFSConstants.GitPathSeparatorString + item.Name);
                    }
                    else
                    {
                        count++;
                        if (count > cap)
                        {
                            count = 0;
                            return false;
                        }
                    }
                }
            }

            return true;
        }
    }
}

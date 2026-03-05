using GVFS.Common;
using System;
using System.Collections.Generic;

namespace GVFS.Hooks
{
    /// <summary>
    /// Parses git ls-tree output for use in pre-checkout dehydration decisions.
    /// </summary>
    public static class LsTreeParser
    {
        /// <summary>
        /// Parses non-recursive ls-tree output into a dictionary of {name: sha}.
        /// Only includes tree entries (directories), not blobs.
        /// Each line is: "&lt;mode&gt; &lt;type&gt; &lt;sha&gt;\t&lt;name&gt;"
        /// </summary>
        public static Dictionary<string, string> ParseTreeEntries(ProcessResult result)
        {
            Dictionary<string, string> entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (result.ExitCode != 0 || string.IsNullOrEmpty(result.Output))
            {
                return entries;
            }

            foreach (string line in result.Output.Split(
                new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                // Format: "<mode> <type> <sha>\t<name>"
                int tabIndex = line.IndexOf('\t');
                if (tabIndex < 0)
                {
                    continue;
                }

                string name = line.Substring(tabIndex + 1);
                string[] parts = line.Substring(0, tabIndex).Split(' ');
                if (parts.Length >= 3 && parts[1] == "tree")
                {
                    entries[name] = parts[2]; // SHA
                }
            }

            return entries;
        }
    }
}

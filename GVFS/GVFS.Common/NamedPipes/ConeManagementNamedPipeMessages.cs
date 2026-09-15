using System.Collections.Generic;

namespace GVFS.Common.NamedPipes
{
    public static partial class NamedPipeMessages
    {
        /// <summary>
        /// Messages for automatic sparse-index cone management. The pre-command
        /// hook sends a <see cref="WidenRequest"/> so the mount can widen the
        /// sparse-index cone to cover the paths a Git command names; the
        /// post-command hook sends a <see cref="NarrowRequest"/> so the mount can
        /// narrow the cone back. The mount-side handler is owned by the
        /// cone-management work; this type defines only the wire contract and is
        /// used by the hook client.
        /// </summary>
        /// <remarks>
        /// Bodies are encoded as <see cref="FieldSeparator"/>-separated fields.
        /// NUL never appears in a Git argv token or in a pipe frame (the stream
        /// terminator is 0x03), so it is a safe field separator. The message
        /// header/body split is on the first '|' only, so a field may contain '|'.
        /// </remarks>
        public static class ConeManagement
        {
            public const string WidenRequest = "ConeWiden";
            public const string NarrowRequest = "ConeNarrow";

            public const string SuccessResult = "S";
            public const string FailureResult = "F";

            /// <summary>The mount received the request but auto-sparse-index is not enabled.</summary>
            public const string NotEnabledResult = "NE";

            private const char FieldSeparator = '\0';
            private const string NulFlagTrue = "Z";

            /// <summary>
            /// Parameters for a cone-widen request: the paths a Git command names,
            /// as typed (relative to <see cref="CurrentDirectory"/>). The mount
            /// resolves relative paths and classifies each pathspec's cone shape.
            /// </summary>
            public class WidenParameters
            {
                public WidenParameters(
                    string sessionId,
                    string currentDirectory,
                    IReadOnlyList<string> pathspecs,
                    string pathspecFromFile,
                    bool pathspecFileNul)
                {
                    this.SessionId = sessionId ?? string.Empty;
                    this.CurrentDirectory = currentDirectory ?? string.Empty;
                    this.Pathspecs = pathspecs ?? new List<string>();
                    this.PathspecFromFile = pathspecFromFile ?? string.Empty;
                    this.PathspecFileNul = pathspecFileNul;
                }

                /// <summary>The Git trace2 parent session id, used to correlate widen and narrow.</summary>
                public string SessionId { get; }

                /// <summary>The hook's normalized working directory. Relative pathspecs resolve against this.</summary>
                public string CurrentDirectory { get; }

                /// <summary>The literal pathspecs named on the command line.</summary>
                public IReadOnlyList<string> Pathspecs { get; }

                /// <summary>The --pathspec-from-file argument, if any. The mount reads the file.</summary>
                public string PathspecFromFile { get; }

                /// <summary>True when --pathspec-file-nul applies to <see cref="PathspecFromFile"/>.</summary>
                public bool PathspecFileNul { get; }

                public string ToBody()
                {
                    List<string> fields = new List<string>(4 + this.Pathspecs.Count)
                    {
                        this.SessionId,
                        this.CurrentDirectory,
                        this.PathspecFromFile,
                        this.PathspecFileNul ? NulFlagTrue : string.Empty,
                    };
                    fields.AddRange(this.Pathspecs);
                    return string.Join(FieldSeparator.ToString(), fields);
                }

                public static bool TryParse(string body, out WidenParameters parameters)
                {
                    parameters = null;
                    if (body == null)
                    {
                        return false;
                    }

                    string[] fields = body.Split(FieldSeparator);
                    if (fields.Length < 4)
                    {
                        return false;
                    }

                    List<string> pathspecs = new List<string>(fields.Length - 4);
                    for (int i = 4; i < fields.Length; i++)
                    {
                        pathspecs.Add(fields[i]);
                    }

                    parameters = new WidenParameters(
                        fields[0],
                        fields[1],
                        pathspecs,
                        fields[2],
                        fields[3] == NulFlagTrue);
                    return true;
                }
            }

            /// <summary>
            /// Parameters for a cone-narrow request: the session id whose widen the
            /// mount should undo.
            /// </summary>
            public class NarrowParameters
            {
                public NarrowParameters(string sessionId)
                {
                    this.SessionId = sessionId ?? string.Empty;
                }

                public string SessionId { get; }

                public string ToBody()
                {
                    return this.SessionId;
                }

                public static bool TryParse(string body, out NarrowParameters parameters)
                {
                    if (body == null)
                    {
                        parameters = null;
                        return false;
                    }

                    parameters = new NarrowParameters(body);
                    return true;
                }
            }
        }
    }
}

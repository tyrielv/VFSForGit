using System;
using System.IO;
using System.Text;
using GVFS.Common.FileSystem;

namespace GVFS.Common.Sparse
{
    /// <summary>
    /// Serializes a <see cref="ConePatternSet"/> to git's exact cone-mode
    /// <c>.git/info/sparse-checkout</c> format and writes it atomically.
    /// </summary>
    /// <remarks>
    /// The serialization mirrors git's write_cone_to_file
    /// (builtin/sparse-checkout.c): a root include line, a root directory exclude line,
    /// a positive/negative pair for each parent-only directory, and a single positive
    /// line for each recursive directory. Glob-special characters are escaped exactly as
    /// git's escaped_pattern does. Lines end with <c>\n</c>, matching git's own writer, so
    /// git parses the file identically to one it wrote itself.
    /// </remarks>
    public class ConeFileWriter
    {
        /// <summary>
        /// Suffix for the mount-owned backup of the previous sparse-checkout file, kept so
        /// a failed reapply can be rolled back.
        /// </summary>
        public const string BackupExtension = ".gvfsbackup";

        /// <summary>
        /// The non-cone content that GVFS writes today (see the disk-layout upgrade). It is
        /// a single <c>/.gitattributes</c> line and is not valid cone format.
        /// </summary>
        public const string LegacyContent = "/.gitattributes";

        private const string LineEnding = "\n";
        private const string ConeHeader = "/*" + LineEnding + "!/*/" + LineEnding;

        private readonly PhysicalFileSystem fileSystem;

        public ConeFileWriter(PhysicalFileSystem fileSystem)
        {
            ArgumentNullException.ThrowIfNull(fileSystem);
            this.fileSystem = fileSystem;
        }

        /// <summary>
        /// Serialize a cone pattern set to git's cone-mode sparse-checkout file content.
        /// </summary>
        public static string Serialize(ConePatternSet cone)
        {
            ArgumentNullException.ThrowIfNull(cone);

            StringBuilder builder = new StringBuilder();
            builder.Append(ConeHeader);

            foreach (string parentOnlyDirectory in cone.ParentOnlyDirectories)
            {
                string escaped = EscapePattern(parentOnlyDirectory);
                builder.Append(GVFSConstants.GitPathSeparator).Append(escaped).Append(GVFSConstants.GitPathSeparator).Append(LineEnding);
                builder.Append('!').Append(GVFSConstants.GitPathSeparator).Append(escaped).Append("/*/").Append(LineEnding);
            }

            foreach (string recursiveDirectory in cone.RecursiveDirectories)
            {
                string escaped = EscapePattern(recursiveDirectory);
                builder.Append(GVFSConstants.GitPathSeparator).Append(escaped).Append(GVFSConstants.GitPathSeparator).Append(LineEnding);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Escape glob-special characters (<c>*</c>, <c>?</c>, <c>[</c>, <c>\</c>) with a
        /// leading backslash, matching git's escaped_pattern.
        /// </summary>
        public static string EscapePattern(string pattern)
        {
            ArgumentNullException.ThrowIfNull(pattern);

            StringBuilder escaped = new StringBuilder(pattern.Length);
            foreach (char character in pattern)
            {
                if (IsGlobSpecial(character))
                {
                    escaped.Append('\\');
                }

                escaped.Append(character);
            }

            return escaped.ToString();
        }

        /// <summary>
        /// Return true when the content is the legacy non-cone <c>/.gitattributes</c> file
        /// that GVFS writes today, ignoring surrounding whitespace and line endings.
        /// </summary>
        public static bool IsLegacyContent(string content)
        {
            if (content == null)
            {
                return false;
            }

            return string.Equals(content.Trim(), LegacyContent, StringComparison.Ordinal);
        }

        /// <summary>
        /// Write the cone file atomically. If a file already exists at the target path, its
        /// content is first copied to a mount-owned backup so a failed reapply can be rolled
        /// back. On success, <paramref name="backupPath"/> is the backup path, or null when
        /// there was no existing file to back up.
        /// </summary>
        public bool TryWrite(string sparseCheckoutPath, ConePatternSet cone, out string backupPath, out Exception handledException)
        {
            ArgumentNullException.ThrowIfNull(sparseCheckoutPath);
            ArgumentNullException.ThrowIfNull(cone);

            backupPath = null;
            handledException = null;

            string content = Serialize(cone);

            if (this.fileSystem.FileExists(sparseCheckoutPath))
            {
                string existingContent;
                try
                {
                    existingContent = this.fileSystem.ReadAllText(sparseCheckoutPath);
                }
                catch (IOException e)
                {
                    handledException = e;
                    return false;
                }
                catch (UnauthorizedAccessException e)
                {
                    handledException = e;
                    return false;
                }

                string candidateBackupPath = sparseCheckoutPath + BackupExtension;
                if (!this.fileSystem.TryWriteTempFileAndRename(candidateBackupPath, existingContent, out handledException))
                {
                    return false;
                }

                backupPath = candidateBackupPath;
            }

            if (!this.fileSystem.TryWriteTempFileAndRename(sparseCheckoutPath, content, out handledException))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Restore the sparse-checkout file from a backup written by <see cref="TryWrite"/>.
        /// </summary>
        public bool TryRestoreBackup(string sparseCheckoutPath, string backupPath, out Exception handledException)
        {
            ArgumentNullException.ThrowIfNull(sparseCheckoutPath);
            ArgumentNullException.ThrowIfNull(backupPath);

            handledException = null;

            string backupContent;
            try
            {
                backupContent = this.fileSystem.ReadAllText(backupPath);
            }
            catch (IOException e)
            {
                handledException = e;
                return false;
            }
            catch (UnauthorizedAccessException e)
            {
                handledException = e;
                return false;
            }

            return this.fileSystem.TryWriteTempFileAndRename(sparseCheckoutPath, backupContent, out handledException);
        }

        private static bool IsGlobSpecial(char character)
        {
            return character == '*' || character == '?' || character == '[' || character == '\\';
        }
    }
}

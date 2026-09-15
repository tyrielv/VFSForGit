using GVFS.Common;
using GVFS.Common.Database;
using GVFS.Common.FileSystem;
using GVFS.Common.Git;
using GVFS.Common.Tracing;
using System;
using System.IO;

namespace GVFS.CommandLine
{
    /// <summary>
    /// Manages VFS for Git's built-in sparse index (the gvfs.auto-sparse-index feature).
    /// </summary>
    /// <remarks>
    /// This is a different feature from 'gvfs sparse'. 'gvfs sparse' narrows the projection:
    /// it changes what VFS for Git shows in the working directory. 'gvfs sparse-index' keeps the
    /// projection full and only lets the on-disk .git/index collapse to cone-format directory
    /// entries, which is where the size and entry-count win comes from. The two features both
    /// write sparse state and are mutually exclusive; enable refuses when a projection sparse-set
    /// is present.
    /// </remarks>
    public class SparseIndexVerb : GVFSVerb.ForExistingEnlistment
    {
        private const string SparseIndexVerbName = "sparse-index";

        public bool Enable { get; set; }

        public bool Disable { get; set; }

        public bool Status { get; set; }

        public static System.CommandLine.Command CreateCommand()
        {
            System.CommandLine.Command cmd = new System.CommandLine.Command(
                "sparse-index",
                "Manage the built-in sparse index. Keeps the full projection but lets the on-disk .git/index collapse. This is different from 'gvfs sparse', which narrows the projection.");

            System.CommandLine.Argument<string> enlistmentArg = GVFSVerb.CreateEnlistmentPathArgument();
            cmd.Add(enlistmentArg);

            System.CommandLine.Option<bool> enableOption = new System.CommandLine.Option<bool>("--enable", new[] { "-e" }) { Description = "Enable the sparse index (sets gvfs.auto-sparse-index=true). Refused when 'gvfs sparse' has a projection sparse-set." };
            cmd.Add(enableOption);

            System.CommandLine.Option<bool> disableOption = new System.CommandLine.Option<bool>("--disable", new[] { "-d" }) { Description = "Disable the sparse index and expand the on-disk index back to a full index. This is the recovery path for an index VFS for Git cannot project." };
            cmd.Add(disableOption);

            System.CommandLine.Option<bool> statusOption = new System.CommandLine.Option<bool>("--status", new[] { "-s" }) { Description = "Report whether the feature is on, whether the on-disk index is sparse, the entry count, and the index size. This is the default when no option is given." };
            cmd.Add(statusOption);

            System.CommandLine.Option<string> internalOption = GVFSVerb.CreateInternalParametersOption();
            cmd.Add(internalOption);

            GVFSVerb.SetActionForVerbWithEnlistment<SparseIndexVerb>(cmd, enlistmentArg, internalOption, defaultEnlistmentPathToCwd: true,
                (verb, result) =>
                {
                    verb.Enable = result.GetValue(enableOption);
                    verb.Disable = result.GetValue(disableOption);
                    verb.Status = result.GetValue(statusOption);
                });

            return cmd;
        }

        protected override string VerbName => SparseIndexVerbName;

        protected override void Execute(GVFSEnlistment enlistment)
        {
            this.CheckOptions();

            using (JsonTracer tracer = new JsonTracer(GVFSConstants.GVFSEtwProviderName, SparseIndexVerbName))
            {
                tracer.AddLogFileEventListener(
                    GVFSEnlistment.GetNewGVFSLogFileName(enlistment.GVFSLogsRoot, GVFSConstants.LogFileTypes.SparseIndex),
                    EventLevel.Informational,
                    Keywords.Any);

                if (this.Enable)
                {
                    this.RunEnable(tracer, enlistment);
                }
                else if (this.Disable)
                {
                    this.RunDisable(tracer, enlistment);
                }
                else
                {
                    this.RunStatus(tracer, enlistment);
                }
            }
        }

        private void CheckOptions()
        {
            int actionCount = (this.Enable ? 1 : 0) + (this.Disable ? 1 : 0);
            if (actionCount > 1)
            {
                this.ReportErrorAndExit("--enable and --disable cannot be combined. Choose one.");
            }

            if (this.Status && actionCount > 0)
            {
                this.ReportErrorAndExit("--status cannot be combined with --enable or --disable.");
            }
        }

        private void RunStatus(ITracer tracer, GVFSEnlistment enlistment)
        {
            bool featureEnabled = this.IsAutoSparseIndexEnabled(tracer, enlistment);

            this.Output.WriteLine();
            this.Output.WriteLine("Sparse index status for " + enlistment.PrimaryEnlistmentRoot);
            this.Output.WriteLine("  Feature (gvfs.auto-sparse-index): " + (featureEnabled ? "Enabled" : "Disabled"));

            GitIndexInfo info;
            string error;
            if (GitIndexInspector.TryReadIndexInfo(enlistment.GitIndexPath, out info, out error))
            {
                this.Output.WriteLine("  On-disk index:                    " + (info.IsSparse ? "Sparse" : "Full"));
                this.Output.WriteLine("  Index entries:                    " + info.EntryCount.ToString("N0"));
                this.Output.WriteLine("  Index size:                       " + FormatBytes(info.SizeInBytes));
            }
            else
            {
                this.Output.WriteLine("  On-disk index:                    Unknown (" + error + ")");
                long sizeInBytes;
                if (TryGetFileSize(enlistment.GitIndexPath, out sizeInBytes))
                {
                    this.Output.WriteLine("  Index size:                       " + FormatBytes(sizeInBytes));
                }

                tracer.RelatedWarning("Could not read index for status: " + error);
            }

            this.Output.WriteLine();
        }

        private void RunEnable(ITracer tracer, GVFSEnlistment enlistment)
        {
            int projectionFolderCount = this.GetProjectionSparseFolderCount(enlistment);
            if (projectionFolderCount > 0)
            {
                this.ReportErrorAndExit(
                    tracer,
                    "Cannot enable the sparse index while 'gvfs sparse' has a projection sparse-set (" + projectionFolderCount + " folder(s))." + Environment.NewLine +
                    "'gvfs sparse' narrows what VFS for Git projects into the working directory." + Environment.NewLine +
                    "'gvfs sparse-index' keeps the full projection and only lets the on-disk .git/index collapse." + Environment.NewLine +
                    "The two features cannot be combined. Run 'gvfs sparse --disable' first, or use one feature or the other.");
            }

            if (this.IsAutoSparseIndexEnabled(tracer, enlistment))
            {
                this.WriteMessage(tracer, "The sparse index is already enabled.");
                this.RunStatus(tracer, enlistment);
                return;
            }

            GitProcess git = new GitProcess(enlistment);

            this.SetLocalConfigOrExit(tracer, git, GVFSConstants.GitConfig.AutoSparseIndex, "true");
            this.SetLocalConfigOrExit(tracer, git, GitConfigSetting.CoreSparseCheckoutConeName, "true");
            this.SetLocalConfigOrExit(tracer, git, GitConfigSetting.IndexSparseName, "true");
            this.SetLocalConfigOrExit(tracer, git, GitConfigSetting.SparseExpectFilesOutsideOfPatternsName, "true");

            this.WriteMessage(tracer, "Sparse index enabled. The projection stays full; only the on-disk .git/index is allowed to collapse.");
            this.WriteMessage(tracer, "The index collapses when cone-format sparse-checkout patterns are applied. Until then it stays full.");
        }

        private void RunDisable(ITracer tracer, GVFSEnlistment enlistment)
        {
            // Expanding the index must not race a running mount, which owns the index. Unmount
            // first; this is a no-op when the repo is already unmounted (the recovery case).
            this.Unmount(tracer);

            GitIndexInfo before;
            string readError;
            bool readBefore = GitIndexInspector.TryReadIndexInfo(enlistment.GitIndexPath, out before, out readError);

            if (readBefore && before.IsSparse)
            {
                GitProcess git = new GitProcess(enlistment);
                if (!this.ShowStatusWhileRunning(
                    () =>
                    {
                        GitProcess.Result expandResult = git.ForceExpandSparseIndex();
                        return expandResult.ExitCodeIsSuccess;
                    },
                    "Expanding the sparse index"))
                {
                    this.ReportErrorAndExit(
                        tracer,
                        "Failed to expand the sparse index. Make sure the repo is unmounted, then retry 'gvfs sparse-index --disable'.");
                }
            }
            else if (!readBefore)
            {
                this.WriteMessage(tracer, "Could not read the index (" + readError + "). Clearing the sparse index config anyway.");
            }
            else
            {
                this.WriteMessage(tracer, "The on-disk index is already full. Clearing the sparse index config.");
            }

            this.ClearSparseIndexConfig(tracer, enlistment);

            GitIndexInfo after;
            string afterError;
            if (GitIndexInspector.TryReadIndexInfo(enlistment.GitIndexPath, out after, out afterError))
            {
                if (readBefore && before.IsSparse)
                {
                    this.WriteMessage(
                        tracer,
                        "Index expanded: " + before.EntryCount.ToString("N0") + " -> " + after.EntryCount.ToString("N0") + " entries, " +
                        FormatBytes(before.SizeInBytes) + " -> " + FormatBytes(after.SizeInBytes) + ".");
                }
            }

            this.WriteMessage(tracer, "Sparse index disabled. You can now run 'gvfs mount'.");
        }

        private void ClearSparseIndexConfig(ITracer tracer, GVFSEnlistment enlistment)
        {
            GitProcess git = new GitProcess(enlistment);

            // Symmetric with the settings written by enable and RequiredGitConfig when the feature
            // is on. GVFS never manages core.sparseCheckout or .git/info/sparse-checkout for this
            // feature, so they are intentionally left untouched.
            git.DeleteFromLocalConfig(GitConfigSetting.IndexSparseName);
            git.DeleteFromLocalConfig(GitConfigSetting.CoreSparseCheckoutConeName);
            git.DeleteFromLocalConfig(GitConfigSetting.SparseExpectFilesOutsideOfPatternsName);
            git.DeleteFromLocalConfig(GVFSConstants.GitConfig.AutoSparseIndex);

            tracer.RelatedInfo("Cleared sparse index git config settings.");
        }

        private void SetLocalConfigOrExit(ITracer tracer, GitProcess git, string name, string value)
        {
            GitProcess.Result result = git.SetInLocalConfig(name, value);
            if (result.ExitCodeIsFailure)
            {
                this.ReportErrorAndExit(tracer, "Failed to set " + name + ": " + result.Errors);
            }
        }

        private bool IsAutoSparseIndexEnabled(ITracer tracer, GVFSEnlistment enlistment)
        {
            return LibGit2Repo.GetConfigBoolOrDefault(
                tracer,
                enlistment.WorkingDirectoryBackingRoot,
                GVFSConstants.GitConfig.AutoSparseIndex,
                GVFSConstants.GitConfig.AutoSparseIndexDefault);
        }

        private int GetProjectionSparseFolderCount(GVFSEnlistment enlistment)
        {
            using (GVFSDatabase database = new GVFSDatabase(new PhysicalFileSystem(), enlistment.DotGVFSRoot, new SqliteDatabase()))
            {
                SparseTable sparseTable = new SparseTable(database);
                return sparseTable.GetAll().Count;
            }
        }

        private void WriteMessage(ITracer tracer, string message)
        {
            this.Output.WriteLine(message);
            tracer.RelatedEvent(
                EventLevel.Informational,
                SparseIndexVerbName,
                new EventMetadata
                {
                    { TracingConstants.MessageKey.InfoMessage, message }
                });
        }

        private static bool TryGetFileSize(string path, out long sizeInBytes)
        {
            sizeInBytes = 0;
            try
            {
                if (File.Exists(path))
                {
                    sizeInBytes = new FileInfo(path).Length;
                    return true;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return false;
        }

        private static string FormatBytes(long sizeInBytes)
        {
            string[] units = new[] { "bytes", "KB", "MB", "GB" };
            double size = sizeInBytes;
            int unitIndex = 0;
            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            if (unitIndex == 0)
            {
                return sizeInBytes.ToString("N0") + " bytes";
            }

            return sizeInBytes.ToString("N0") + " bytes (" + size.ToString("0.##") + " " + units[unitIndex] + ")";
        }
    }
}

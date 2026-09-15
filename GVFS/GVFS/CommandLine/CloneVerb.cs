using GVFS.Common;
using GVFS.Common.FileSystem;
using GVFS.Common.Git;
using GVFS.Common.Http;
using GVFS.Common.NamedPipes;
using GVFS.Common.Sparse;
using GVFS.Common.Tracing;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace GVFS.CommandLine
{
    public class CloneVerb : GVFSVerb
    {
        private const string CloneVerbName = "clone";

        public string RepositoryURL { get; set; }

        public override string EnlistmentRootPathParameter { get; set; }

        public string CacheServerUrl { get; set; }

        public string PrefetchCacheServerUrl { get; set; }

        public string GetCacheServerUrl { get; set; }

        public string PostCacheServerUrl { get; set; }

        public string SizesCacheServerUrl { get; set; }

        public string Branch { get; set; }

        public bool SingleBranch { get; set; }

        public bool NoMount { get; set; }

        public bool NoPrefetch { get; set; }

        public bool SparseIndex { get; set; }

        public string LocalCacheRoot { get; set; }

        public static System.CommandLine.Command CreateCommand()
        {
            System.CommandLine.Command cmd = new System.CommandLine.Command("clone", "Clone a git repo and mount it as a GVFS virtual repo");

            System.CommandLine.Argument<string> repoUrlArg = new System.CommandLine.Argument<string>("repository-url")
            {
                Description = "The url of the repo",
                Arity = System.CommandLine.ArgumentArity.ExactlyOne,
            };
            cmd.Add(repoUrlArg);

            System.CommandLine.Argument<string> enlistmentArg = new System.CommandLine.Argument<string>("enlistment-root-path")
            {
                Description = "Full or relative path to the GVFS enlistment root",
                Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
                DefaultValueFactory = (_) => "",
            };
            cmd.Add(enlistmentArg);

            System.CommandLine.Option<string> cacheServerOption = new System.CommandLine.Option<string>("--cache-server-url") { Description = "The url or friendly name of the cache server" };
            cmd.Add(cacheServerOption);

            System.CommandLine.Option<string> prefetchCacheServerOption = new System.CommandLine.Option<string>("--prefetch-cache-server-url") { Description = "The cache server URL for the prefetch endpoint" };
            AddEndpointCacheServerUrlValidator(prefetchCacheServerOption);
            cmd.Add(prefetchCacheServerOption);

            System.CommandLine.Option<string> getCacheServerOption = new System.CommandLine.Option<string>("--get-cache-server-url") { Description = "The cache server URL for the objects GET endpoint" };
            AddEndpointCacheServerUrlValidator(getCacheServerOption);
            cmd.Add(getCacheServerOption);

            System.CommandLine.Option<string> postCacheServerOption = new System.CommandLine.Option<string>("--post-cache-server-url") { Description = "The cache server URL for the objects POST endpoint" };
            AddEndpointCacheServerUrlValidator(postCacheServerOption);
            cmd.Add(postCacheServerOption);

            System.CommandLine.Option<string> sizesCacheServerOption = new System.CommandLine.Option<string>("--sizes-cache-server-url") { Description = "The cache server URL for the sizes endpoint" };
            AddEndpointCacheServerUrlValidator(sizesCacheServerOption);
            cmd.Add(sizesCacheServerOption);

            System.CommandLine.Option<string> branchOption = new System.CommandLine.Option<string>("--branch", new[] { "-b" }) { Description = "Branch to checkout after clone" };
            cmd.Add(branchOption);

            System.CommandLine.Option<bool> singleBranchOption = new System.CommandLine.Option<bool>("--single-branch") { Description = "Use this option to only download metadata for the branch that will be checked out" };
            cmd.Add(singleBranchOption);

            System.CommandLine.Option<bool> noMountOption = new System.CommandLine.Option<bool>("--no-mount") { Description = "Use this option to only clone, but not mount the repo" };
            cmd.Add(noMountOption);

            System.CommandLine.Option<bool> noPrefetchOption = new System.CommandLine.Option<bool>("--no-prefetch") { Description = "Use this option to not prefetch commits after clone" };
            cmd.Add(noPrefetchOption);

            System.CommandLine.Option<bool> sparseIndexOption = new System.CommandLine.Option<bool>("--sparse-index") { Description = "Enable the built-in sparse index (gvfs.auto-sparse-index). The projection stays full; only the on-disk index is allowed to collapse. This is different from 'gvfs sparse', which narrows the projection." };
            cmd.Add(sparseIndexOption);

            System.CommandLine.Option<string> localCacheOption = new System.CommandLine.Option<string>("--local-cache-path") { Description = "Use this option to override the path for the local GVFS cache." };
            cmd.Add(localCacheOption);

            System.CommandLine.Option<string> internalOption = GVFSVerb.CreateInternalParametersOption();
            cmd.Add(internalOption);

            cmd.SetAction((System.CommandLine.ParseResult result) =>
            {
                CloneVerb verb = new CloneVerb();
                verb.RepositoryURL = result.GetValue(repoUrlArg);
                verb.EnlistmentRootPathParameter = result.GetValue(enlistmentArg) ?? "";
                if (verb.EnlistmentRootPathParameter.StartsWith("-"))
                {
                    Console.Error.WriteLine($"Unrecognized option '{verb.EnlistmentRootPathParameter}'");
                    Environment.Exit((int)ReturnCode.ParsingError);
                }

                verb.CacheServerUrl = result.GetValue(cacheServerOption);
                verb.PrefetchCacheServerUrl = result.GetValue(prefetchCacheServerOption);
                verb.GetCacheServerUrl = result.GetValue(getCacheServerOption);
                verb.PostCacheServerUrl = result.GetValue(postCacheServerOption);
                verb.SizesCacheServerUrl = result.GetValue(sizesCacheServerOption);
                verb.Branch = result.GetValue(branchOption);
                verb.SingleBranch = result.GetValue(singleBranchOption);
                verb.NoMount = result.GetValue(noMountOption);
                verb.NoPrefetch = result.GetValue(noPrefetchOption);
                verb.SparseIndex = result.GetValue(sparseIndexOption);
                verb.LocalCacheRoot = result.GetValue(localCacheOption);

                GVFSVerb.ApplyInternalParameters(verb, result, internalOption);
                try
                {
                    verb.Execute();
                }
                catch (GVFSVerb.VerbAbortedException)
                {
                }

                Environment.Exit((int)verb.ReturnCode);
            });

            return cmd;
        }

        private static void AddEndpointCacheServerUrlValidator(System.CommandLine.Option<string> option)
        {
            option.Validators.Add(
                result =>
                {
                    string url = result.GetValueOrDefault<string>();
                    if (url != null && !CacheServerInfo.IsValidUrl(url))
                    {
                        result.AddError($"Option '{option.Name}' requires an absolute URL.");
                    }
                });
        }

        protected override string VerbName
        {
            get { return CloneVerbName; }
        }

        public override void Execute()
        {
            int exitCode = 0;

            this.ValidatePathParameter(this.EnlistmentRootPathParameter);
            this.ValidatePathParameter(this.LocalCacheRoot);

            string fullEnlistmentRootPathParameter;
            string normalizedEnlistmentRootPath = this.GetCloneRoot(out fullEnlistmentRootPathParameter);

            if (!string.IsNullOrWhiteSpace(this.LocalCacheRoot))
            {
                string fullLocalCacheRootPath = Path.GetFullPath(this.LocalCacheRoot);

                string errorMessage;
                string normalizedLocalCacheRootPath;
                if (!GVFSPlatform.Instance.FileSystem.TryGetNormalizedPath(fullLocalCacheRootPath, out normalizedLocalCacheRootPath, out errorMessage))
                {
                    this.ReportErrorAndExit($"Failed to determine normalized path for '--local-cache-path' path {fullLocalCacheRootPath}: {errorMessage}");
                }

                if (normalizedLocalCacheRootPath.StartsWith(
                    Path.Combine(normalizedEnlistmentRootPath, GVFSConstants.WorkingDirectoryRootName),
                    GVFSPlatform.Instance.Constants.PathComparison))
                {
                    this.ReportErrorAndExit("'--local-cache-path' cannot be inside the src folder");
                }
            }

            this.CheckKernelDriverSupported(normalizedEnlistmentRootPath);
            this.CheckNotInsideExistingRepo(normalizedEnlistmentRootPath);
            this.BlockEmptyCacheServerUrl(this.CacheServerUrl);
            this.BlockEmptyCacheServerUrl(this.PrefetchCacheServerUrl);
            this.BlockEmptyCacheServerUrl(this.GetCacheServerUrl);
            this.BlockEmptyCacheServerUrl(this.PostCacheServerUrl);
            this.BlockEmptyCacheServerUrl(this.SizesCacheServerUrl);
            this.BlockInvalidEndpointCacheServerUrl("--prefetch-cache-server-url", this.PrefetchCacheServerUrl);
            this.BlockInvalidEndpointCacheServerUrl("--get-cache-server-url", this.GetCacheServerUrl);
            this.BlockInvalidEndpointCacheServerUrl("--post-cache-server-url", this.PostCacheServerUrl);
            this.BlockInvalidEndpointCacheServerUrl("--sizes-cache-server-url", this.SizesCacheServerUrl);

            try
            {
                GVFSEnlistment enlistment;
                Result cloneResult = new Result(false);

                CacheServerInfo cacheServer = null;
                ServerGVFSConfig serverGVFSConfig = null;
                bool trustPackIndexes = GVFSConstants.GitConfig.TrustPackIndexesDefault;

                using (JsonTracer tracer = new JsonTracer(GVFSConstants.GVFSEtwProviderName, "GVFSClone"))
                {
                    cloneResult = this.TryCreateEnlistment(fullEnlistmentRootPathParameter, normalizedEnlistmentRootPath, out enlistment);
                    if (cloneResult.Success)
                    {
                        // Create the enlistment root explicitly with CreateDirectoryAccessibleByAuthUsers before calling
                        // AddLogFileEventListener to ensure that elevated and non-elevated users have access to the root.
                        string createDirectoryError;
                        if (!GVFSPlatform.Instance.FileSystem.TryCreateDirectoryAccessibleByAuthUsers(enlistment.PrimaryEnlistmentRoot, out createDirectoryError))
                        {
                            this.ReportErrorAndExit($"Failed to create '{enlistment.PrimaryEnlistmentRoot}': {createDirectoryError}");
                        }

                        tracer.AddLogFileEventListener(
                            GVFSEnlistment.GetNewGVFSLogFileName(enlistment.GVFSLogsRoot, GVFSConstants.LogFileTypes.Clone),
                            EventLevel.Informational,
                            Keywords.Any);
                        tracer.WriteStartEvent(
                            enlistment.PrimaryEnlistmentRoot,
                            enlistment.RepoUrl,
                            this.CacheServerUrl,
                            new EventMetadata
                            {
                                { "Branch", this.Branch },
                                { "LocalCacheRoot", this.LocalCacheRoot },
                                { "SingleBranch", this.SingleBranch },
                                { "NoMount", this.NoMount },
                                { "NoPrefetch", this.NoPrefetch },
                                { "Unattended", this.Unattended },
                                { "IsElevated", GVFSPlatform.Instance.IsElevated() },
                                { "NamedPipeName", enlistment.NamedPipeName },
                                { "ProcessID", Process.GetCurrentProcess().Id },
                                { nameof(this.EnlistmentRootPathParameter), this.EnlistmentRootPathParameter },
                                { nameof(fullEnlistmentRootPathParameter), fullEnlistmentRootPathParameter },
                            });

                        CacheServerResolver cacheServerResolver = new CacheServerResolver(tracer, enlistment);
                        cacheServer = cacheServerResolver.ParseUrlOrFriendlyName(this.CacheServerUrl);

                        string resolvedLocalCacheRoot;
                        if (string.IsNullOrWhiteSpace(this.LocalCacheRoot))
                        {
                            string localCacheRootError;
                            if (!LocalCacheResolver.TryGetDefaultLocalCacheRoot(enlistment, out resolvedLocalCacheRoot, out localCacheRootError))
                            {
                                this.ReportErrorAndExit(
                                    tracer,
                                    $"Failed to determine the default location for the local GVFS cache: `{localCacheRootError}`");
                            }
                        }
                        else
                        {
                            resolvedLocalCacheRoot = Path.GetFullPath(this.LocalCacheRoot);
                        }

                        this.Output.WriteLine("Clone parameters:");
                        this.Output.WriteLine("  Repo URL:     " + enlistment.RepoUrl);
                        this.Output.WriteLine("  Branch:       " + (string.IsNullOrWhiteSpace(this.Branch) ? "Default" : this.Branch));
                        this.Output.WriteLine("  Cache Server: " + cacheServer);
                        this.Output.WriteLine("  Local Cache:  " + resolvedLocalCacheRoot);
                        this.Output.WriteLine("  Destination:  " + enlistment.PrimaryEnlistmentRoot);

                        RetryConfig retryConfig = this.GetRetryConfig(tracer, enlistment, TimeSpan.FromMinutes(RetryConfig.FetchAndCloneTimeoutMinutes));

                        string authErrorMessage;
                        if (!this.TryAuthenticateAndQueryGVFSConfig(
                            tracer,
                            enlistment,
                            retryConfig,
                            out serverGVFSConfig,
                            out authErrorMessage,
                            fallbackCacheServer: cacheServer))
                        {
                            this.ReportErrorAndExit(tracer, "Cannot clone because authentication failed: " + authErrorMessage);
                        }

                        cacheServer = this.ResolveCacheServer(tracer, cacheServer, cacheServerResolver, serverGVFSConfig);
                        cacheServer = cacheServer.WithEndpointOverrides(
                            this.PrefetchCacheServerUrl,
                            this.GetCacheServerUrl,
                            this.PostCacheServerUrl,
                            this.SizesCacheServerUrl);

                        this.ValidateClientVersions(tracer, enlistment, serverGVFSConfig, showWarnings: true);

                        this.ShowStatusWhileRunning(
                            () =>
                            {
                                cloneResult = this.TryClone(tracer, enlistment, cacheServer, retryConfig, serverGVFSConfig, resolvedLocalCacheRoot);
                                return cloneResult.Success;
                            },
                            "Cloning",
                            normalizedEnlistmentRootPath);
                    }

                    if (!cloneResult.Success)
                    {
                        tracer.RelatedError(cloneResult.ErrorMessage);
                    }
                    else
                    {
                        trustPackIndexes = LibGit2Repo.GetConfigBoolOrDefault(
                            tracer,
                            enlistment.WorkingDirectoryBackingRoot,
                            GVFSConstants.GitConfig.TrustPackIndexes,
                            GVFSConstants.GitConfig.TrustPackIndexesDefault);
                    }
                }

                if (cloneResult.Success)
                {
                    if (!this.NoPrefetch)
                    {
                        /* If pack indexes are not trusted, the prefetch can take a long time.
                         * We will run the prefetch command in the background.
                         */
                        if (trustPackIndexes)
                        {
                            ReturnCode result = this.Execute<PrefetchVerb>(
                                enlistment,
                                verb =>
                                {
                                    verb.Commits = true;
                                    verb.SkipVersionCheck = true;
                                    verb.ResolvedCacheServer = cacheServer;
                                    verb.ServerGVFSConfig = serverGVFSConfig;
                                });

                            if (result != ReturnCode.Success)
                            {
                                this.Output.WriteLine("\r\nError during prefetch @ {0}", fullEnlistmentRootPathParameter);
                                exitCode = (int)result;
                            }
                        }
                        else
                        {
                            try
                            {
                                string gvfsExecutable = Environment.ProcessPath;
                                Process.Start(new ProcessStartInfo(
                                    fileName: gvfsExecutable,
                                    arguments: "prefetch --commits")
                                    {
                                        UseShellExecute = true,
                                        WindowStyle = ProcessWindowStyle.Minimized,
                                        WorkingDirectory = enlistment.PrimaryEnlistmentRoot
                                    });
                                this.Output.WriteLine("\r\nPrefetch of commit graph has been started as a background process. Git operations involving history may be slower until prefetch has completed.\r\n");
                            }
                            catch (Win32Exception ex)
                            {
                                this.Output.WriteLine("\r\nError starting prefetch: " + ex.Message);
                                this.Output.WriteLine("Run 'gvfs prefetch --commits' from within your enlistment to prefetch the commit graph.");
                            }
                        }
                    }

                    if (this.NoMount)
                    {
                        this.Output.WriteLine("\r\nIn order to mount, first cd to within your enlistment, then call: ");
                        this.Output.WriteLine("gvfs mount");
                    }
                    else
                    {
                        this.Execute<MountVerb>(
                            enlistment,
                            verb =>
                            {
                                verb.SkipMountedCheck = true;
                                verb.SkipVersionCheck = true;
                                verb.ResolvedCacheServer = cacheServer;
                                verb.DownloadedGVFSConfig = serverGVFSConfig;
                            });
                    }
                }
                else
                {
                    this.Output.WriteLine("\r\nCannot clone @ {0}", fullEnlistmentRootPathParameter);
                    this.Output.WriteLine("Error: {0}", cloneResult.ErrorMessage);
                    exitCode = (int)ReturnCode.GenericError;
                }
            }
            catch (AggregateException e)
            {
                this.Output.WriteLine("Cannot clone @ {0}:", fullEnlistmentRootPathParameter);
                foreach (Exception ex in e.Flatten().InnerExceptions)
                {
                    this.Output.WriteLine("Exception: {0}", ex.ToString());
                }

                exitCode = (int)ReturnCode.GenericError;
            }
            catch (VerbAbortedException)
            {
                throw;
            }
            catch (Exception e)
            {
                this.ReportErrorAndExit("Cannot clone @ {0}: {1}", fullEnlistmentRootPathParameter, e.ToString());
            }

            Environment.Exit(exitCode);
        }

        private static bool IsForceCheckoutErrorCloneFailure(string checkoutError)
        {
            if (string.IsNullOrWhiteSpace(checkoutError) ||
                checkoutError.Contains("Already on"))
            {
                return false;
            }

            return true;
        }

        private Result TryCreateEnlistment(
            string fullEnlistmentRootPathParameter,
            string normalizedEnlistementRootPath,
            out GVFSEnlistment enlistment)
        {
            enlistment = null;

            // Check that EnlistmentRootPath is empty before creating a tracer and LogFileEventListener as
            // LogFileEventListener will create a file in EnlistmentRootPath
            if (Directory.Exists(normalizedEnlistementRootPath) && Directory.EnumerateFileSystemEntries(normalizedEnlistementRootPath).Any())
            {
                if (fullEnlistmentRootPathParameter.Equals(normalizedEnlistementRootPath, GVFSPlatform.Instance.Constants.PathComparison))
                {
                    return new Result($"Clone directory '{fullEnlistmentRootPathParameter}' exists and is not empty");
                }

                return new Result($"Clone directory '{fullEnlistmentRootPathParameter}' ['{normalizedEnlistementRootPath}'] exists and is not empty");
            }

            string gitBinPath = GVFSPlatform.Instance.GitInstallation.GetInstalledGitBinPath();
            if (string.IsNullOrWhiteSpace(gitBinPath))
            {
                return new Result(GVFSConstants.GitIsNotInstalledError);
            }

            this.CheckGVFSHooksVersion(tracer: null, hooksVersion: out _);

            try
            {
                enlistment = new GVFSEnlistment(
                    normalizedEnlistementRootPath,
                    this.RepositoryURL,
                    gitBinPath,
                    authentication: null);
            }
            catch (InvalidRepoException e)
            {
                return new Result($"Error when creating a new GVFS enlistment at '{normalizedEnlistementRootPath}'. {e.Message}");
            }

            return new Result(true);
        }

        private Result TryClone(
            JsonTracer tracer,
            GVFSEnlistment enlistment,
            CacheServerInfo cacheServer,
            RetryConfig retryConfig,
            ServerGVFSConfig serverGVFSConfig,
            string resolvedLocalCacheRoot)
        {
            Result pipeResult;
            using (NamedPipeServer pipeServer = this.StartNamedPipe(tracer, enlistment, out pipeResult))
            {
                if (!pipeResult.Success)
                {
                    return pipeResult;
                }

                using (GitObjectsHttpRequestor objectRequestor = new GitObjectsHttpRequestor(tracer, enlistment, cacheServer, retryConfig))
                {
                    GitRefs refs = objectRequestor.QueryInfoRefs(this.SingleBranch ? this.Branch : null);

                    if (refs == null)
                    {
                        return new Result("Could not query info/refs from: " + Uri.EscapeDataString(enlistment.RepoUrl));
                    }

                    if (this.Branch == null)
                    {
                        this.Branch = refs.GetDefaultBranch();

                        EventMetadata metadata = new EventMetadata();
                        metadata.Add("Branch", this.Branch);
                        tracer.RelatedEvent(EventLevel.Informational, "CloneDefaultRemoteBranch", metadata);
                    }
                    else
                    {
                        if (!refs.HasBranch(this.Branch))
                        {
                            EventMetadata metadata = new EventMetadata();
                            metadata.Add("Branch", this.Branch);
                            tracer.RelatedEvent(EventLevel.Warning, "CloneBranchDoesNotExist", metadata);

                            string errorMessage = string.Format("Remote branch {0} not found in upstream origin", this.Branch);
                            return new Result(errorMessage);
                        }
                    }

                    if (!enlistment.TryCreateEnlistmentSubFolders())
                    {
                        string error = "Could not create enlistment directories";
                        tracer.RelatedError(error);
                        return new Result(error);
                    }

                    if (!GVFSPlatform.Instance.FileSystem.IsFileSystemSupported(enlistment.PrimaryEnlistmentRoot, out string fsError))
                    {
                        string error = $"FileSystem unsupported: {fsError}";
                        tracer.RelatedError(error);
                        return new Result(error);
                    }

                    string localCacheError;
                    if (!this.TryDetermineLocalCacheAndInitializePaths(tracer, enlistment, serverGVFSConfig, cacheServer, resolvedLocalCacheRoot, out localCacheError))
                    {
                        tracer.RelatedError(localCacheError);
                        return new Result(localCacheError);
                    }

                    // There's no need to use CreateDirectoryAccessibleByAuthUsers as these directories will inherit
                    // the ACLs used to create LocalCacheRoot
                    Directory.CreateDirectory(enlistment.GitObjectsRoot);
                    Directory.CreateDirectory(enlistment.GitPackRoot);
                    Directory.CreateDirectory(enlistment.BlobSizesRoot);

                    return this.CreateClone(tracer, enlistment, objectRequestor, refs, this.Branch);
                }
            }
        }

        private NamedPipeServer StartNamedPipe(ITracer tracer, GVFSEnlistment enlistment, out Result errorResult)
        {
            try
            {
                errorResult = new Result(true);
                return AllowAllLocksNamedPipeServer.Create(tracer, enlistment);
            }
            catch (PipeNameLengthException)
            {
                errorResult = new Result("Failed to clone. Path exceeds the maximum number of allowed characters");
                return null;
            }
        }

        private string GetCloneRoot(out string fullEnlistmentRootPathParameter)
        {
            fullEnlistmentRootPathParameter = null;

            try
            {
                string repoName = this.RepositoryURL.Substring(this.RepositoryURL.LastIndexOf('/') + 1);
                fullEnlistmentRootPathParameter =
                    string.IsNullOrWhiteSpace(this.EnlistmentRootPathParameter)
                    ? Path.Combine(Environment.CurrentDirectory, repoName)
                    : this.EnlistmentRootPathParameter;

                fullEnlistmentRootPathParameter = Path.GetFullPath(fullEnlistmentRootPathParameter);

                string errorMessage;
                string enlistmentRootPath;
                if (!GVFSPlatform.Instance.FileSystem.TryGetNormalizedPath(fullEnlistmentRootPathParameter, out enlistmentRootPath, out errorMessage))
                {
                    this.ReportErrorAndExit("Unable to determine normalized path of clone root: " + errorMessage);
                    return null;
                }

                return enlistmentRootPath;
            }
            catch (IOException e)
            {
                this.ReportErrorAndExit("Unable to determine clone root: " + e.ToString());
                return null;
            }
        }

        private void CheckKernelDriverSupported(string normalizedEnlistmentRootPath)
        {
            string warning;
            string error;
            if (!GVFSPlatform.Instance.KernelDriver.IsSupported(normalizedEnlistmentRootPath, out warning, out error))
            {
                this.ReportErrorAndExit($"Error: {error}");
            }
            else if (!string.IsNullOrEmpty(warning))
            {
                this.Output.WriteLine();
                this.Output.WriteLine($"WARNING: {warning}");
            }
        }

        private void CheckNotInsideExistingRepo(string normalizedEnlistmentRootPath)
        {
            string errorMessage;
            string existingEnlistmentRoot;
            if (GVFSPlatform.Instance.TryGetGVFSEnlistmentRoot(normalizedEnlistmentRootPath, out existingEnlistmentRoot, out errorMessage))
            {
                this.ReportErrorAndExit("Error: You can't clone inside an existing GVFS repo ({0})", existingEnlistmentRoot);
            }

            if (this.IsExistingPipeListening(normalizedEnlistmentRootPath))
            {
                this.ReportErrorAndExit($"Error: There is currently a GVFS.Mount process running for '{normalizedEnlistmentRootPath}'. This process must be stopped before cloning.");
            }
        }

        private bool TryDetermineLocalCacheAndInitializePaths(
            ITracer tracer,
            GVFSEnlistment enlistment,
            ServerGVFSConfig serverGVFSConfig,
            CacheServerInfo currentCacheServer,
            string localCacheRoot,
            out string errorMessage)
        {
            errorMessage = null;
            LocalCacheResolver localCacheResolver = new LocalCacheResolver(enlistment);

            string error;
            string localCacheKey;
            if (!localCacheResolver.TryGetLocalCacheKeyFromLocalConfigOrRemoteCacheServers(
                tracer,
                serverGVFSConfig,
                currentCacheServer,
                localCacheRoot,
                localCacheKey: out localCacheKey,
                errorMessage: out error))
            {
                errorMessage = "Error determining local cache key: " + error;
                return false;
            }

            EventMetadata metadata = new EventMetadata();
            metadata.Add("localCacheRoot", localCacheRoot);
            metadata.Add("localCacheKey", localCacheKey);
            metadata.Add(TracingConstants.MessageKey.InfoMessage, "Initializing cache paths");
            tracer.RelatedEvent(EventLevel.Informational, "CloneVerb_TryDetermineLocalCacheAndInitializePaths", metadata);

            enlistment.InitializeCachePathsFromKey(localCacheRoot, localCacheKey);

            return true;
        }

        private void BlockInvalidEndpointCacheServerUrl(string optionName, string url)
        {
            if (url != null && !CacheServerInfo.IsValidUrl(url))
            {
                this.ReportErrorAndExit($"Option '{optionName}' requires an absolute URL.");
            }
        }

        private Result CreateClone(
            ITracer tracer,
            GVFSEnlistment enlistment,
            GitObjectsHttpRequestor objectRequestor,
            GitRefs refs,
            string branch)
        {
            Result initRepoResult = this.TryInitRepo(tracer, refs, enlistment);
            if (!initRepoResult.Success)
            {
                return initRepoResult;
            }

            PhysicalFileSystem fileSystem = new PhysicalFileSystem();
            string errorMessage;
            if (!this.TryCreateAlternatesFile(fileSystem, enlistment, out errorMessage))
            {
                return new Result("Error configuring alternate: " + errorMessage);
            }

            GitRepo gitRepo = new GitRepo(tracer, enlistment, fileSystem);
            GVFSContext context = new GVFSContext(tracer, fileSystem, gitRepo, enlistment);
            GVFSGitObjects gitObjects = new GVFSGitObjects(context, objectRequestor);

            if (!this.TryDownloadCommit(
                refs.GetTipCommitId(branch),
                enlistment,
                objectRequestor,
                gitObjects,
                gitRepo,
                out errorMessage))
            {
                return new Result(errorMessage);
            }

            if (this.SparseIndex)
            {
                // Persist the feature flag before writing required config so that the sparse
                // settings land in the same pass, and so that later mounts read it back.
                GitProcess.Result setFlagResult = new GitProcess(enlistment).SetInLocalConfig(GVFSConstants.GitConfig.AutoSparseIndex, "true");
                if (setFlagResult.ExitCodeIsFailure)
                {
                    return new Result("Unable to enable the sparse index: " + setFlagResult.Errors);
                }
            }

            if (!GVFSVerb.TrySetRequiredGitConfigSettings(enlistment, this.SparseIndex) ||
                !GVFSVerb.TrySetOptionalGitConfigSettings(enlistment))
            {
                return new Result("Unable to configure git repo");
            }

            CacheServerResolver cacheServerResolver = new CacheServerResolver(tracer, enlistment);
            if (!cacheServerResolver.TrySaveUrlToLocalConfig(objectRequestor.CacheServer, out errorMessage))
            {
                return new Result("Unable to configure cache server: " + errorMessage);
            }

            if (!cacheServerResolver.TrySaveEndpointUrlsToLocalConfig(objectRequestor.CacheServer, out errorMessage))
            {
                return new Result("Unable to configure endpoint-specific cache servers: " + errorMessage);
            }

            GitProcess git = new GitProcess(enlistment);
            string originBranchName = "origin/" + branch;
            GitProcess.Result createBranchResult = git.CreateBranchWithUpstream(branch, originBranchName);
            if (createBranchResult.ExitCodeIsFailure)
            {
                return new Result("Unable to create branch '" + originBranchName + "': " + createBranchResult.Errors + "\r\n" + createBranchResult.Output);
            }

            File.WriteAllText(
                Path.Combine(enlistment.WorkingDirectoryBackingRoot, GVFSConstants.DotGit.Head),
                "ref: refs/heads/" + branch);

            if (!this.TryDownloadRootGitAttributes(enlistment, gitObjects, gitRepo, out errorMessage))
            {
                return new Result(errorMessage);
            }

            this.CreateGitScript(enlistment);

            string installHooksError;
            if (!HooksInstaller.InstallHooks(context, out installHooksError))
            {
                tracer.RelatedError(installHooksError);
                return new Result(installHooksError);
            }

            GitProcess.Result forceCheckoutResult = git.ForceCheckout(branch);
            if (forceCheckoutResult.ExitCodeIsFailure && forceCheckoutResult.Errors.IndexOf("unable to read tree") > 0)
            {
                // It is possible to have the above TryDownloadCommit() fail because we
                // already have the commit and root tree we intend to check out, but
                // don't have a tree further down the working directory. If we fail
                // checkout here, its' because we don't have these trees and the
                // read-object hook is not available yet. Force downloading the commit
                // again and retry the checkout.

                if (!this.TryDownloadCommit(
                    refs.GetTipCommitId(branch),
                    enlistment,
                    objectRequestor,
                    gitObjects,
                    gitRepo,
                    out errorMessage,
                    checkLocalObjectCache: false))
                {
                    return new Result(errorMessage);
                }

                forceCheckoutResult = git.ForceCheckout(branch);
            }

            if (forceCheckoutResult.ExitCodeIsFailure)
            {
                string[] errorLines = forceCheckoutResult.Errors.Split('\n');
                StringBuilder checkoutErrors = new StringBuilder();
                foreach (string gitError in errorLines)
                {
                    if (IsForceCheckoutErrorCloneFailure(gitError))
                    {
                        checkoutErrors.AppendLine(gitError);
                    }
                }

                if (checkoutErrors.Length > 0)
                {
                    string error = "Could not complete checkout of branch: " + branch + ", " + checkoutErrors.ToString();
                    tracer.RelatedError(error);
                    return new Result(error);
                }
            }

            if (this.SparseIndex)
            {
                // The full index is now written. Collapse it to a sparse index in place so the
                // enlistment is already sparse before its first mount, instead of paying the
                // one-time collapse of a large full index later. See decisions/0015.
                if (!this.TryConstructSparseIndex(tracer, enlistment, git, fileSystem, out errorMessage))
                {
                    tracer.RelatedError(errorMessage);
                    return new Result(errorMessage);
                }
            }

            if (!RepoMetadata.TryInitialize(tracer, enlistment.DotGVFSRoot, out errorMessage))
            {
                tracer.RelatedError(errorMessage);
                return new Result(errorMessage);
            }

            try
            {
                RepoMetadata.Instance.SaveCloneMetadata(tracer, enlistment);
                this.LogEnlistmentInfoAndSetConfigValues(tracer, git, enlistment);
            }
            catch (Exception e)
            {
                tracer.RelatedError(e.ToString());
                return new Result(e.Message);
            }
            finally
            {
                RepoMetadata.Shutdown();
            }

            // Prepare the working directory folder for GVFS last to ensure that gvfs mount will fail if gvfs clone has failed
            Exception exception;
            string prepFileSystemError;
            if (!GVFSPlatform.Instance.KernelDriver.TryPrepareFolderForCallbacks(enlistment.WorkingDirectoryBackingRoot, out prepFileSystemError, out exception))
            {
                EventMetadata metadata = new EventMetadata();
                metadata.Add(nameof(prepFileSystemError), prepFileSystemError);
                if (exception != null)
                {
                    metadata.Add("Exception", exception.ToString());
                }

                tracer.RelatedError(metadata, $"{nameof(this.CreateClone)}: TryPrepareFolderForCallbacks failed");
                return new Result(prepFileSystemError);
            }

            return new Result(true);
        }

        // TODO(#1364): Don't call this method on POSIX platforms (or have it no-op on them)
        /// <summary>
        /// Construct a sparse index directly at clone time so the enlistment is already sparse
        /// before its first mount, instead of paying the one-time collapse of a large full index
        /// later.
        /// </summary>
        /// <remarks>
        /// A fresh clone has no modified paths, so the natural starting cone is minimal: root
        /// files only ("/*", "!/*/"). The steps are:
        ///   1. Build the minimal cone (empty modified-path set) with W6's ConeBuilder and write
        ///      it to the per-worktree info/sparse-checkout in git's exact cone format.
        ///   2. Set core.sparseCheckout=true so git applies the cone when it rewrites the index.
        ///      The three sparse-index keys (core.sparseCheckoutCone, index.sparse,
        ///      sparse.expectFilesOutsideOfPatterns) are already written by
        ///      TrySetRequiredGitConfigSettings when SparseIndex is true.
        ///   3. Collapse the just-written full index in place with 'update-index
        ///      --force-write-index' (GVFS hooks disabled). This is cheap because the fresh index
        ///      has skip-worktree on every entry and a valid cache-tree, so git collapses every
        ///      out-of-cone directory without walking the working tree.
        /// See decisions/0015 for why direct construction during checkout is infeasible with
        /// stock git, and why 'update-index --force-write-index' is used instead of
        /// 'sparse-checkout reapply'.
        /// </remarks>
        private bool TryConstructSparseIndex(ITracer tracer, GVFSEnlistment enlistment, GitProcess git, PhysicalFileSystem fileSystem, out string errorMessage)
        {
            errorMessage = null;

            GitIndexInfo before;
            string beforeError;
            bool readBefore = GitIndexInspector.TryReadIndexInfo(enlistment.GitIndexPath, out before, out beforeError);

            // 1. A fresh clone has no modified paths, so the minimal cone is root files only.
            ConePatternSet cone = ConeBuilder.BuildFromModifiedPaths(Array.Empty<string>());
            string sparseCheckoutPath = SparseCheckoutPathResolver.GetSparseCheckoutFilePath(enlistment);
            string backupPath;
            Exception writeException;
            if (!new ConeFileWriter(fileSystem).TryWrite(sparseCheckoutPath, cone, out backupPath, out writeException))
            {
                errorMessage = "Unable to write the sparse-checkout cone file: " + (writeException != null ? writeException.Message : "unknown error");
                return false;
            }

            // 2. Enable cone-mode sparse checkout so git applies the cone when it rewrites the index.
            GitProcess.Result setSparseCheckout = git.SetInLocalConfig(GitConfigSetting.CoreSparseCheckoutName, "true");
            if (setSparseCheckout.ExitCodeIsFailure)
            {
                errorMessage = "Unable to enable cone-mode sparse checkout: " + setSparseCheckout.Errors;
                return false;
            }

            // 3. Collapse the full index to a sparse index in place, with the GVFS hooks disabled.
            GitProcess.Result collapseResult = git.CollapseSparseIndex();
            if (collapseResult.ExitCodeIsFailure)
            {
                errorMessage = "Unable to collapse the index to a sparse index: " + collapseResult.Errors;
                return false;
            }

            GitIndexInfo after;
            string afterError;
            if (GitIndexInspector.TryReadIndexInfo(enlistment.GitIndexPath, out after, out afterError))
            {
                EventMetadata metadata = new EventMetadata();
                metadata.Add("IsSparse", after.IsSparse);
                metadata.Add("EntryCount", after.EntryCount);
                metadata.Add("SizeInBytes", after.SizeInBytes);
                if (readBefore)
                {
                    metadata.Add("EntryCountBefore", before.EntryCount);
                    metadata.Add("SizeInBytesBefore", before.SizeInBytes);
                }

                tracer.RelatedEvent(EventLevel.Informational, "SparseIndexConstructed", metadata);

                if (!after.IsSparse)
                {
                    // The collapse ran but produced a full index. This is expected only for a
                    // repository with no out-of-cone directories to collapse; warn, do not fail.
                    tracer.RelatedWarning("Clone-time sparse construction left a full index; the repository may have no directories to collapse.");
                }
            }
            else
            {
                tracer.RelatedWarning("Could not read the index after clone-time sparse construction: " + afterError);
            }

            return true;
        }

        private void CreateGitScript(GVFSEnlistment enlistment)
        {
            FileInfo gitCmd = new FileInfo(Path.Combine(enlistment.PrimaryEnlistmentRoot, "git.cmd"));
            using (FileStream fs = gitCmd.Create())
            using (StreamWriter writer = new StreamWriter(fs))
            {
                writer.Write(
@"
@echo OFF
echo .
echo ^[105;30m
echo      This repo was cloned using GVFS, and the git repo is in the 'src' directory
echo      Switching you to the 'src' directory and rerunning your git command
echo                                                                                      [0m

@echo ON
cd src
git %*
");
            }

            gitCmd.Attributes = FileAttributes.Hidden;
        }

        private Result TryInitRepo(ITracer tracer, GitRefs refs, Enlistment enlistmentToInit)
        {
            string repoPath = enlistmentToInit.WorkingDirectoryBackingRoot;
            GitProcess.Result initResult = GitProcess.Init(enlistmentToInit);
            if (initResult.ExitCodeIsFailure)
            {
                string error = string.Format("Could not init repo at to {0}: {1}", repoPath, initResult.Errors);
                tracer.RelatedError(error);
                return new Result(error);
            }

            try
            {
                GVFSPlatform.Instance.FileSystem.EnsureDirectoryIsOwnedByCurrentUser(enlistmentToInit.DotGitRoot);
            }
            catch (IOException e)
            {
                string error = string.Format("Could not ensure .git directory is owned by current user: {0}", e.Message);
                tracer.RelatedError(error);
                return new Result(error);
            }

            GitProcess.Result remoteAddResult = new GitProcess(enlistmentToInit).RemoteAdd("origin", enlistmentToInit.RepoUrl);
            if (remoteAddResult.ExitCodeIsFailure)
            {
                string error = string.Format("Could not add remote to {0}: {1}", repoPath, remoteAddResult.Errors);
                tracer.RelatedError(error);
                return new Result(error);
            }

            File.WriteAllText(
                Path.Combine(repoPath, GVFSConstants.DotGit.PackedRefs),
                refs.ToPackedRefs());

            return new Result(true);
        }

        private class Result
        {
            public Result(bool success)
            {
                this.Success = success;
                this.ErrorMessage = string.Empty;
            }

            public Result(string errorMessage)
            {
                this.Success = false;
                this.ErrorMessage = errorMessage;
            }

            public bool Success { get; }
            public string ErrorMessage { get; }
        }
    }
}

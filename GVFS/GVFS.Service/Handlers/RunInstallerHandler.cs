using GVFS.Common;
using GVFS.Common.NamedPipes;
using GVFS.Common.Tracing;
using System;
using System.Diagnostics;
using System.IO;

namespace GVFS.Service.Handlers
{
    public class RunInstallerHandler
    {
        private const string InstallerArgs = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /STAGEIFMOUNTED=true /LOG=\"{0}\"";

        private readonly ITracer tracer;
        private readonly NamedPipeServer.Connection connection;
        private readonly NamedPipeMessages.RunInstallerRequest request;

        public RunInstallerHandler(
            ITracer tracer,
            NamedPipeServer.Connection connection,
            NamedPipeMessages.RunInstallerRequest request)
        {
            this.tracer = tracer;
            this.connection = connection;
            this.request = request;
        }

        public void Run()
        {
            NamedPipeMessages.RunInstallerRequest.Response response =
                new NamedPipeMessages.RunInstallerRequest.Response();

            EventMetadata metadata = new EventMetadata();
            metadata.Add("InstallerPath", this.request.InstallerPath);
            metadata.Add("AllowUnsigned", this.request.AllowUnsigned);

            try
            {
                string installerPath = this.request.InstallerPath;
                if (string.IsNullOrWhiteSpace(installerPath))
                {
                    response.State = NamedPipeMessages.CompletionState.Failure;
                    response.ErrorMessage = "Installer path is required";
                    this.tracer.RelatedError(metadata, response.ErrorMessage);
                    return;
                }

                // Resolve to full path to prevent path traversal.
                installerPath = Path.GetFullPath(installerPath);

                if (!InstallerVerifier.TryVerifyInstaller(
                        this.tracer,
                        installerPath,
                        this.request.AllowUnsigned,
                        out string verifyError))
                {
                    response.State = NamedPipeMessages.CompletionState.Failure;
                    response.ErrorMessage = verifyError;
                    return;
                }

                string logPath = Path.Combine(
                    Configuration.AssemblyPath,
                    "ProgramData",
                    "upgrade-install.log");

                this.tracer.RelatedInfo(
                    metadata,
                    $"{nameof(RunInstallerHandler)}: Verification passed, launching installer (log: {logPath})");

                int exitCode = LaunchInstallerAndWait(installerPath, logPath);

                metadata.Add("InstallerExitCode", exitCode);

                if (exitCode == 0)
                {
                    response.State = NamedPipeMessages.CompletionState.Success;
                    this.tracer.RelatedInfo(
                        metadata,
                        $"{nameof(RunInstallerHandler)}: Installer launched successfully");
                }
                else
                {
                    response.State = NamedPipeMessages.CompletionState.Failure;
                    response.ErrorMessage = $"Installer failed to launch (error {exitCode})";
                    this.tracer.RelatedError(metadata, response.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                response.State = NamedPipeMessages.CompletionState.Failure;
                response.ErrorMessage = $"Failed to run installer: {ex.Message}";
                metadata.Add("Exception", ex.ToString());
                this.tracer.RelatedError(
                    metadata,
                    $"{nameof(RunInstallerHandler)}: {response.ErrorMessage}");
            }
            finally
            {
                this.connection.TrySendResponse(response.ToMessage().ToString());
            }
        }

        /// <summary>
        /// Launches the installer as a detached process. The installer will
        /// stop GVFS.Service as part of its upgrade flow, so we must not wait
        /// for it to exit (that would deadlock — parent waiting on child that
        /// kills parent). Returns 0 if the process started, or -1 on failure.
        /// </summary>
        private static int LaunchInstallerAndWait(string installerPath, string logPath)
        {
            string args = string.Format(InstallerArgs, logPath);
            try
            {
                Process installerProcess = new Process();
                installerProcess.StartInfo = new ProcessStartInfo
                {
                    FileName = installerPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                if (!installerProcess.Start())
                {
                    return -1;
                }

                // Do NOT call WaitForExit(). The installer will stop
                // GVFS.Service (our parent), so we'd deadlock.
                return 0;
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}

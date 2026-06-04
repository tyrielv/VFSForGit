using GVFS.Common;
using GVFS.Common.NamedPipes;
using System;
using System.IO;

namespace GVFS.CommandLine
{
    public class UpgradeVerb : GVFSVerb.ForNoEnlistment
    {
        private const string UpgradeVerbName = "upgrade";

        public UpgradeVerb()
        {
            this.Output = Console.Out;
        }

        public string InstallerPath { get; set; }

        public bool AllowUnsigned { get; set; }

        public static System.CommandLine.Command CreateCommand()
        {
            System.CommandLine.Command cmd = new System.CommandLine.Command("upgrade", "Upgrade VFS for Git by running an installer through the GVFS service (no UAC required).");

            System.CommandLine.Argument<string> installerPathArg = new System.CommandLine.Argument<string>("installer-path")
            {
                Description = "Path to the SetupGVFS.*.exe installer",
            };
            cmd.Add(installerPathArg);

            System.CommandLine.Option<bool> allowUnsignedOption = new System.CommandLine.Option<bool>(
                "--allow-unsigned") { Description = "Skip Authenticode signature verification (for development builds only)" };
            cmd.Add(allowUnsignedOption);

            System.CommandLine.Option<string> internalOption = GVFSVerb.CreateInternalParametersOption();
            cmd.Add(internalOption);

            GVFSVerb.SetActionForNoEnlistment<UpgradeVerb>(cmd, internalOption,
                (verb, result) =>
                {
                    verb.InstallerPath = result.GetValue(installerPathArg);
                    verb.AllowUnsigned = result.GetValue(allowUnsignedOption);
                });

            return cmd;
        }

        protected override string VerbName
        {
            get { return UpgradeVerbName; }
        }

        public override void Execute()
        {
            if (string.IsNullOrWhiteSpace(this.InstallerPath))
            {
                this.ReportErrorAndExit("Installer path is required. Usage: gvfs upgrade <installer-path>");
                return;
            }

            string fullPath = Path.GetFullPath(this.InstallerPath);
            if (!File.Exists(fullPath))
            {
                this.ReportErrorAndExit($"Installer not found: {fullPath}");
                return;
            }

            this.Output.WriteLine($"Requesting upgrade via GVFS service...");
            this.Output.WriteLine($"Installer: {fullPath}");

            if (this.AllowUnsigned)
            {
                this.Output.WriteLine("WARNING: Authenticode signature verification is disabled (--allow-unsigned)");
            }

            NamedPipeMessages.RunInstallerRequest request = new NamedPipeMessages.RunInstallerRequest
            {
                InstallerPath = fullPath,
                AllowUnsigned = this.AllowUnsigned,
            };

            using (NamedPipeClient client = new NamedPipeClient(this.ServicePipeName))
            {
                if (!client.Connect())
                {
                    this.ReportErrorAndExit(
                        "Unable to connect to GVFS service. Is GVFS.Service running?");
                    return;
                }

                try
                {
                    client.SendRequest(request.ToMessage());
                    NamedPipeMessages.RunInstallerRequest.Response response =
                        NamedPipeMessages.RunInstallerRequest.Response.FromMessage(
                            client.ReadResponse());

                    if (response.State == NamedPipeMessages.CompletionState.Success)
                    {
                        this.Output.WriteLine("Upgrade started. The installer is running in the background.");
                        this.Output.WriteLine("GVFS service will restart automatically. Check 'gvfs version' after a few seconds.");
                    }
                    else
                    {
                        this.ReportErrorAndExit(
                            $"Upgrade failed: {response.ErrorMessage}");
                    }
                }
                catch (BrokenPipeException ex)
                {
                    this.ReportErrorAndExit(
                        $"Lost connection to GVFS service during upgrade: {ex.Message}");
                }
            }
        }
    }
}

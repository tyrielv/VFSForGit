using GVFS.FunctionalTests.Tools;
using GVFS.Tests.Should;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using System;
using System.IO;

namespace GVFS.FunctionalTests.Tests.EnlistmentPerTestCase
{
    [TestFixture]
    public class BlockedProcessTests : TestsWithEnlistmentPerTestCase
    {
        private const string ConfigKey = "gvfs.blocked-processes";
        private const string ProjectedFile = "GVFS.sln";

        [TestCase]
        public void BlockedProcessCannotEnumerateOrCreatePlaceholders()
        {
            this.SetBlockedProcesses(Path.GetFileName(Environment.ProcessPath));
            long before = this.GetPlaceholderCount();

            Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(this.Enlistment.RepoRoot));
            Assert.Throws<UnauthorizedAccessException>(() => File.GetAttributes(this.Enlistment.GetVirtualPathTo(ProjectedFile)));
            Assert.Throws<UnauthorizedAccessException>(() => File.GetAttributes(this.Enlistment.GetVirtualPathTo("GVFS")));
            this.GetPlaceholderCount().ShouldEqual(before);

            ProcessResult allowed = ProcessHelper.Run(
                "cmd.exe", $"/d /c dir /b \"{this.Enlistment.RepoRoot}\"", this.Enlistment.RepoRoot);
            allowed.ExitCode.ShouldEqual(0, allowed.Errors);
            allowed.Output.ShouldContain(ProjectedFile);

            allowed = ProcessHelper.Run(
                "cmd.exe", $"/d /c type \"{this.Enlistment.GetVirtualPathTo(ProjectedFile)}\"", this.Enlistment.RepoRoot);
            allowed.ExitCode.ShouldEqual(0, allowed.Errors);
            allowed.Output.Length.ShouldBeAtLeast(1);

            this.SetBlockedProcesses(string.Empty);
            File.ReadAllText(this.Enlistment.GetVirtualPathTo(ProjectedFile)).Length.ShouldBeAtLeast(1);
        }

        [TestCase]
        public void BlockedProcessCannotHydrateExistingPlaceholderButCanReadHydratedFile()
        {
            string file = this.Enlistment.GetVirtualPathTo(ProjectedFile);
            new FileInfo(file).Length.ShouldBeAtLeast(1);

            this.SetBlockedProcesses(Path.GetFileName(Environment.ProcessPath));
            long before = this.GetPlaceholderCount();
            Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(file));
            this.GetPlaceholderCount().ShouldEqual(before);

            ProcessResult allowed = ProcessHelper.Run(
                "cmd.exe", $"/d /c type \"{file}\"", this.Enlistment.RepoRoot);
            allowed.ExitCode.ShouldEqual(0, allowed.Errors);
            allowed.Output.Length.ShouldBeAtLeast(1);

            // Once data is local, ProjFS does not ask GVFS to hydrate it again.
            File.ReadAllText(file).ShouldEqual(allowed.Output);
        }

        [TestCase("cmd.exe")]
        [TestCase("powershell.exe")]
        public void BlockedShellCannotEnumerateButOtherProcessesCan(string shell)
        {
            this.SetBlockedProcesses(shell.ToUpperInvariant());
            long before = this.GetPlaceholderCount();

            string arguments = shell == "cmd.exe"
                ? $"/d /c dir /s /b \"{this.Enlistment.RepoRoot}\""
                : $"-NoProfile -NonInteractive -Command \"Get-ChildItem -LiteralPath '{this.Enlistment.RepoRoot}' -Recurse -ErrorAction Stop\"";
            ProcessResult blocked = ProcessHelper.Run(
                shell, arguments, this.Enlistment.RepoRoot, timeoutMs: 30000);
            blocked.ExitCode.ShouldNotEqual(0);
            this.GetPlaceholderCount().ShouldEqual(before);

            Directory.GetFiles(this.Enlistment.RepoRoot).ShouldContain(path => Path.GetFileName(path) == ProjectedFile);
            File.ReadAllText(this.Enlistment.GetVirtualPathTo(ProjectedFile)).Length.ShouldBeAtLeast(1);
        }

        [TestCase]
        public void InvalidBlockedProcessListPreventsMountAndCanBeCorrected()
        {
            this.Enlistment.UnmountGVFS();
            GitProcess.Invoke(this.Enlistment.RepoRoot, $"config --local {ConfigKey} \"rg*.exe\"");
            this.Enlistment.TryMountGVFS(out string output).ShouldBeFalse();
            output.ShouldContain("gvfs log");
            Directory.GetFiles(this.Enlistment.GVFSLogsRoot, "gvfs_mount_process_*.log")
                .ShouldContain(path => File.ReadAllText(path).Contains(ConfigKey + " must contain"));

            GitProcess.Invoke(this.Enlistment.RepoRoot, $"config --local {ConfigKey} \"\"");
            this.Enlistment.MountGVFS();
        }

        [TestCase]
        [Explicit("Set GVFS_TEST_SCANNER_EXE to a portable rg.exe or WinDirStat.exe.")]
        public void RecordScannerExperience()
        {
            string scanner = Environment.GetEnvironmentVariable("GVFS_TEST_SCANNER_EXE");
            Assert.That(scanner, Is.Not.Null.And.Not.Empty);
            string processName = Path.GetFileName(scanner);
            bool isRipgrep = processName.Equals("rg.exe", StringComparison.OrdinalIgnoreCase);
            Assert.That(isRipgrep || processName.Equals("windirstat.exe", StringComparison.OrdinalIgnoreCase), Is.True);

            string target = this.Enlistment.GetVirtualPathTo("EnumerateAndReadTestFiles");
            File.GetAttributes(target);
            string report = Path.Combine(this.Enlistment.EnlistmentRoot, "scan.csv");
            string arguments = isRipgrep
                ? $"--no-ignore --text --line-number . \"{target}\""
                : $"/SaveTo \"{report}\" \"{target}\"";

            this.SetBlockedProcesses(processName);
            long before = this.GetPlaceholderCount();
            ProcessResult blocked = ProcessHelper.Run(scanner, arguments, this.Enlistment.EnlistmentRoot, timeoutMs: 30000);
            long after = this.GetPlaceholderCount();
            Console.WriteLine($"Blocked {processName}: exit={blocked.ExitCode}, placeholderDelta={after - before}, stdout={blocked.Output}, stderr={blocked.Errors}");
            if (File.Exists(report))
            {
                Console.WriteLine("Blocked report: " + File.ReadAllText(report));
                File.Delete(report);
            }

            after.ShouldEqual(before);

            string directFileArguments = $"--no-ignore --text --line-number . \"{Path.Combine(target, "a.txt")}\"";
            if (isRipgrep)
            {
                ProcessResult direct = ProcessHelper.Run(scanner, directFileArguments, this.Enlistment.EnlistmentRoot, timeoutMs: 30000);
                Console.WriteLine($"Blocked cold direct-file {processName}: exit={direct.ExitCode}, stdout={direct.Output}, stderr={direct.Errors}");
                direct.ExitCode.ShouldEqual(2);
                this.GetPlaceholderCount().ShouldEqual(before);
            }

            this.SetBlockedProcesses(string.Empty);
            before = this.GetPlaceholderCount();
            ProcessResult allowed = ProcessHelper.Run(scanner, arguments, this.Enlistment.EnlistmentRoot, timeoutMs: 30000);
            after = this.GetPlaceholderCount();
            Console.WriteLine($"Allowed {processName}: exit={allowed.ExitCode}, placeholderDelta={after - before}, stdout={allowed.Output}, stderr={allowed.Errors}");
            allowed.ExitCode.ShouldEqual(0, allowed.Errors);
            if (File.Exists(report))
            {
                Console.WriteLine("Allowed report: " + File.ReadAllText(report));
            }

            if (isRipgrep)
            {
                this.SetBlockedProcesses(processName);
                before = this.GetPlaceholderCount();
                ProcessResult warm = ProcessHelper.Run(scanner, directFileArguments, this.Enlistment.EnlistmentRoot, timeoutMs: 30000);
                Console.WriteLine($"Blocked hydrated direct-file {processName}: exit={warm.ExitCode}, placeholderDelta={this.GetPlaceholderCount() - before}, stdout={warm.Output}, stderr={warm.Errors}");
                warm.ExitCode.ShouldEqual(0, warm.Errors);
                this.GetPlaceholderCount().ShouldEqual(before);
            }
        }

        private void SetBlockedProcesses(string names)
        {
            this.Enlistment.UnmountGVFS();
            GitProcess.Invoke(this.Enlistment.RepoRoot, $"config --local {ConfigKey} \"{names}\"");
            this.Enlistment.MountGVFS();
        }

        private long GetPlaceholderCount()
        {
            string database = Path.Combine(this.Enlistment.DotGVFSRoot, "databases", "VFSForGit.sqlite");
            using (SqliteConnection connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False"))
            {
                connection.Open();
                using (SqliteCommand command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT COUNT(*) FROM Placeholder";
                    return (long)command.ExecuteScalar();
                }
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;

namespace GVFS.Common
{
    public static class ProcessHelper
    {
        public const int TimedOutExitCode = -1;

        private static string currentProcessVersion = null;

        public static ProcessResult Run(string programName, string args, bool redirectOutput = true)
        {
            return Run(programName, args, redirectOutput, timeoutMs: -1);
        }

        /// <summary>
        /// Runs a process with an optional timeout. If the process does not exit within
        /// <paramref name="timeoutMs"/> milliseconds, it is killed and a failure result is returned.
        /// Pass -1 for no timeout.
        /// </summary>
        public static ProcessResult Run(string programName, string args, bool redirectOutput, int timeoutMs)
        {
            ProcessStartInfo processInfo = new ProcessStartInfo(programName);
            processInfo.UseShellExecute = false;
            processInfo.RedirectStandardInput = true;
            processInfo.RedirectStandardOutput = redirectOutput;
            processInfo.RedirectStandardError = redirectOutput;
            processInfo.WindowStyle = ProcessWindowStyle.Hidden;
            processInfo.CreateNoWindow = redirectOutput;
            processInfo.Arguments = args;

            return Run(processInfo, timeoutMs: timeoutMs);
        }

        public static string GetCurrentProcessLocation()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            return Path.GetDirectoryName(assembly.Location);
        }

        public static string GetEntryClassName()
        {
            Assembly assembly = Assembly.GetEntryAssembly();
            if (assembly == null)
            {
                // The PR build tests doesn't produce an entry assembly because it is run from unmanaged code,
                // so we'll fall back on using this assembly. This should never ever happen for a normal exe invocation.
                assembly = Assembly.GetExecutingAssembly();
            }

            return assembly.GetName().Name;
        }

        public static string GetCurrentProcessVersion()
        {
            if (currentProcessVersion == null)
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                FileVersionInfo fileVersionInfo = FileVersionInfo.GetVersionInfo(assembly.Location);
                currentProcessVersion = fileVersionInfo.ProductVersion;
            }

            return currentProcessVersion;
        }

        public static bool IsDevelopmentVersion()
        {
            // Official CI builds use version numbers where major > 0.
            // Development builds always start with 0.
            string version = ProcessHelper.GetCurrentProcessVersion();
            return version.StartsWith("0.");
        }

        public static string GetProgramLocation(string programLocaterCommand, string processName)
        {
            ProcessResult result = ProcessHelper.Run(programLocaterCommand, processName);
            if (result.ExitCode != 0)
            {
                return null;
            }

            string firstPath =
                string.IsNullOrWhiteSpace(result.Output)
                ? null
                : result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstPath == null)
            {
                return null;
            }

            try
            {
                return Path.GetDirectoryName(firstPath);
            }
            catch (IOException)
            {
                return null;
            }
        }

        public static ProcessResult Run(ProcessStartInfo processInfo, string errorMsgDelimeter = "\r\n", object executionLock = null, int timeoutMs = -1)
        {
            using (Process executingProcess = new Process())
            {
                string output = string.Empty;
                string errors = string.Empty;

                // From https://msdn.microsoft.com/en-us/library/system.diagnostics.process.standardoutput.aspx
                // To avoid deadlocks, use asynchronous read operations on at least one of the streams.
                // Do not perform a synchronous read to the end of both redirected streams.
                executingProcess.StartInfo = processInfo;
                executingProcess.ErrorDataReceived += (sender, args) =>
                {
                    if (args.Data != null)
                    {
                        errors = errors + args.Data + errorMsgDelimeter;
                    }
                };

                if (executionLock != null)
                {
                    lock (executionLock)
                    {
                        output = StartProcess(executingProcess, timeoutMs);
                    }
                }
                else
                {
                    output = StartProcess(executingProcess, timeoutMs);
                }

                if (executingProcess.HasExited)
                {
                    return new ProcessResult(output, errors, executingProcess.ExitCode);
                }

                // Process was killed due to timeout
                return new ProcessResult(output, errors, TimedOutExitCode);
            }
        }

        private static string StartProcess(Process executingProcess, int timeoutMs = -1)
        {
            executingProcess.Start();

            if (executingProcess.StartInfo.RedirectStandardError)
            {
                executingProcess.BeginErrorReadLine();
            }

            string output = string.Empty;
            if (executingProcess.StartInfo.RedirectStandardOutput)
            {
                output = executingProcess.StandardOutput.ReadToEnd();
            }

            if (timeoutMs >= 0)
            {
                if (!executingProcess.WaitForExit(timeoutMs))
                {
                    try
                    {
                        executingProcess.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                        // Process already exited between WaitForExit and Kill
                    }

                    return output;
                }
            }
            else
            {
                executingProcess.WaitForExit();
            }

            return output;
        }
    }
}

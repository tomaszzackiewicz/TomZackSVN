using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace SVN.Core
{
    public static class SVNRun
    {
        public static async Task<string> ExecuteAsync(
    string arguments,
    string workingDir)
        {
            SVNLogBridge.LogLine(
                $"[SVNRun] Executing: svn {arguments} | Directory: {workingDir}");

            return await Task.Run(() =>
            {
                StringBuilder outputBuilder = new StringBuilder();

                try
                {
                    if (string.IsNullOrEmpty(workingDir) ||
                        !System.IO.Directory.Exists(workingDir))
                    {
                        return "<color=red>Error: Working Directory is invalid or not set.</color>";
                    }

                    string finalArgs = arguments;

                    if (!string.IsNullOrEmpty(SvnRunner.KeyPath))
                    {
                        string path = SvnRunner.KeyPath
                            .Replace("\"", "")
                            .Trim()
                            .Replace("\\", "/");

                        string sshCommand =
                            $"C:/Windows/System32/OpenSSH/ssh.exe " +
                            "-T " +
                            "-o IdentitiesOnly=yes " +
                            "-o StrictHostKeyChecking=no " +
                            "-o BatchMode=yes " +
                            $"-i \\\"{path}\\\"";

                        finalArgs +=
                            $" --config-option " +
                            $"config:tunnels:ssh=\"{sshCommand}\"";

                        SVNLogBridge.LogLine(
                            $"[SVNRun SSH] {sshCommand}");
                    }

                    SVNLogBridge.LogLine(
                        $"[SVNRun FINAL ARGS] {finalArgs}");

                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "svn",
                        Arguments = finalArgs,
                        WorkingDirectory = workingDir,

                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,

                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };

                    using (Process process = new Process
                    {
                        StartInfo = psi
                    })
                    {
                        process.Start();

                        string output = process.StandardOutput.ReadToEnd();
                        string error = process.StandardError.ReadToEnd();

                        process.WaitForExit();

                        if (!string.IsNullOrEmpty(output))
                        {
                            outputBuilder.Append(output);
                        }

                        if (!string.IsNullOrEmpty(error))
                        {
                            outputBuilder.Append(
                                $"\n<color=red>SVN Error: {error}</color>");
                        }

                        if (outputBuilder.Length == 0 &&
                            process.ExitCode != 0)
                        {
                            outputBuilder.Append(
                                $"<color=red>Process exited with code {process.ExitCode}.</color>");
                        }
                    }
                }
                catch (Exception ex)
                {
                    return $"<color=red>Critical Exception: {ex.Message}</color>";
                }

                return string.IsNullOrEmpty(outputBuilder.ToString())
                    ? "Command executed."
                    : outputBuilder.ToString();
            });
        }
    }
}
using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace SVN.Core
{
    public static class SVNRun
    {
        public static async Task<string> ExecuteAsync(string arguments, string workingDir)
        {
            UnityEngine.Debug.Log($"[SVNRun] Executing: svn {arguments} | Directory: {workingDir}");

            return await Task.Run(() =>
            {
                StringBuilder outputBuilder = new StringBuilder();
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "svn",
                        Arguments = arguments,
                        WorkingDirectory = workingDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    };

                    if (string.IsNullOrEmpty(workingDir) || !System.IO.Directory.Exists(workingDir))
                    {
                        return "<color=red>Error: Working Directory is invalid or not set.</color>";
                    }

                    using (Process process = new Process { StartInfo = psi })
                    {
                        process.Start();

                        string output = process.StandardOutput.ReadToEnd();
                        string error = process.StandardError.ReadToEnd();

                        process.WaitForExit();

                        if (!string.IsNullOrEmpty(output))
                            outputBuilder.Append(output);

                        if (!string.IsNullOrEmpty(error))
                            outputBuilder.Append($"\n<color=red>SVN Error: {error}</color>");

                        if (outputBuilder.Length == 0 && process.ExitCode != 0)
                        {
                            outputBuilder.Append($"<color=red>Process exited with code {process.ExitCode} without output.</color>");
                        }
                    }
                }
                catch (Exception ex)
                {
                    return $"<color=red>Critical Exception: {ex.Message}</color>";
                }

                string finalResult = outputBuilder.ToString();
                return string.IsNullOrEmpty(finalResult) ? "Command executed (No output)." : finalResult;
            });
        }
    }
}
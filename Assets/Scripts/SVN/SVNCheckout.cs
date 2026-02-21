using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNCheckout : SVNBase
    {
        private CancellationTokenSource _checkoutCTS;
        private readonly SynchronizationContext _mainThreadContext;

        private long _cachedTotalSizeBytes = 0;

        public SVNCheckout(SVNUI svnUI, SVNManager manager) : base(svnUI, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
        }

        public async void UpdateProjectInfo()
        {
            string url = svnUI.CheckoutRepoUrlInput.text.Trim();
            string destPath = svnUI.CheckoutDestFolderInput.text.Trim();

            if (string.IsNullOrEmpty(url)) return;

            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "Analyzing repository...", "Info");

            try
            {
                // 1. Fetch exact file sizes from the server
                _cachedTotalSizeBytes = await GetRepositorySizeAsync(url, (status) =>
                {
                    _mainThreadContext.Post(_ => svnUI.CheckoutStatusInfoText.text = status, null);
                });

                double repoDataGB = _cachedTotalSizeBytes / (1024d * 1024d * 1024d);

                // 2. Check available disk space
                string driveLabel = Path.GetPathRoot(Path.GetFullPath(destPath));
                DriveInfo drive = new DriveInfo(driveLabel);
                double freeSpaceGB = drive.AvailableFreeSpace / (1024d * 1024d * 1024d);

                // Simple check: if Repo Size > Free Space, it definitely won't fit
                string spaceColor = (freeSpaceGB < repoDataGB) ? "red" : "green";

                SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                    $"<b>Repository Size:</b> {repoDataGB:F2} GB\n" +
                    $"<b>Available Space ({driveLabel}):</b> <color={spaceColor}>{freeSpaceGB:F2} GB</color>\n\n" +
                    (freeSpaceGB < repoDataGB
                        ? "<color=red><b>ERROR:</b> Not enough space even for raw files!</color>"
                        : "<color=green>Ready to download.</color>"), "Info");
            }
            catch (Exception ex)
            {
                Debug.LogError($"UpdateProjectInfo failed: {ex.Message}");
                SVNLogBridge.LogLine($"<color=red>Error:</color> {ex.Message}");
            }
        }

        public static async Task<long> GetRepositorySizeAsync(string url, Action<string> onStatusUpdate)
        {
            long totalBytes = 0;
            // -R is recursive, --xml provides the <size> tags
            string args = $"list \"{url}\" --recursive --xml --non-interactive --trust-server-cert";

            onStatusUpdate?.Invoke("Calculating total size from server...");

            await SvnRunner.RunLiveAsync(args, "", (line) =>
            {
                // Simple XML parsing to find <size> value without loading a massive XML tree into memory
                if (line.Contains("<size>"))
                {
                    try
                    {
                        int start = line.IndexOf("<size>") + 6;
                        int end = line.IndexOf("</size>");
                        string sizeStr = line.Substring(start, end - start);
                        if (long.TryParse(sizeStr, out long fileSize))
                        {
                            totalBytes += fileSize;
                        }
                    }
                    catch { /* Skip malformed lines */ }
                }
            });

            return totalBytes;
        }

        private async Task EnsureRepoStructure(string repoRoot)
        {
            try
            {
                string result = await SvnRunner.RunAsync($"ls \"{repoRoot}\"", "");

                if (string.IsNullOrEmpty(result) || !result.Contains("trunk"))
                {
                    SVNLogBridge.LogLine("<b>[Server]</b> Creating trunk/branches/tags structure...");
                    string cmd = $"mkdir \"{repoRoot}/trunk\" \"{repoRoot}/branches\" \"{repoRoot}/tags\" -m \"Initial structure\" --parents --non-interactive";
                    await SvnRunner.RunAsync(cmd, "");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Structure check failed (repo might be empty): " + ex.Message);
            }
        }

        public async void Checkout()
        {
            if (IsProcessing) return;

            string baseUrl = svnUI.CheckoutRepoUrlInput.text.Trim().TrimEnd('/');
            string path = svnUI.CheckoutDestFolderInput.text.Trim();

            if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length > 0)
            {
                SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=red>Error:</color> Folder is not empty!", "Checkout");
                return;
            }

            await EnsureRepoStructure(baseUrl);

            string trunkUrl = $"{baseUrl}/trunk";

            SVNLogBridge.LogLine($"<b>[Checkout]</b> Target set to: {trunkUrl}");

            await ExecuteSvnOperation(trunkUrl, path, $"checkout \"{trunkUrl}\" \"{path}\" --force");
        }

        public async void ResumeCheckout()
        {
            if (IsProcessing) return;

            string url = svnUI.CheckoutRepoUrlInput.text.Trim();
            string path = svnUI.CheckoutDestFolderInput.text.Trim();

            if (!Directory.Exists(Path.Combine(path, ".svn")))
            {
                SVNLogBridge.LogLine("<color=red>Error:</color> No .svn metadata found. Use Checkout instead.");
                return;
            }

            SVNLogBridge.LogLine("<b>Resuming...</b> Clearing locks (Cleanup).");

            try
            {
                // Temporary token for cleanup
                _checkoutCTS = new CancellationTokenSource();
                await SvnRunner.RunLiveAsync("cleanup", path, null, _checkoutCTS.Token);

                // Continue with update
                await ExecuteSvnOperation(url, path, "update --force", isResume: true);
            }
            catch (Exception ex)
            {
                Debug.LogError($"Resume failed: {ex.Message}");
                IsProcessing = false;
            }
        }

        private async Task ExecuteSvnOperation(string url, string path, string command, bool isResume = false)
        {
            IsProcessing = true;
            _checkoutCTS = new CancellationTokenSource();
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            int filesProcessed = 0;
            DateTime start = DateTime.Now;
            DateTime lastActivity = DateTime.Now;

            // 1. Get initial state
            long sizeBeforeThisSession = GetDirectorySize(path);

            try
            {
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);

                var monitor = Task.Run(async () =>
                {
                    while (IsProcessing)
                    {
                        long currentTotalDiskSize = GetDirectorySize(path);
                        long bytesDownloadedInThisSession = currentTotalDiskSize - sizeBeforeThisSession;
                        if (bytesDownloadedInThisSession < 0) bytesDownloadedInThisSession = 0;

                        double sessionMB = bytesDownloadedInThisSession / (1024d * 1024d);
                        double totalGBOnDisk = currentTotalDiskSize / (1024d * 1024d * 1024d);

                        double totalSeconds = (DateTime.Now - start).TotalSeconds;
                        double speed = totalSeconds > 1 ? sessionMB / totalSeconds : 0;
                        double silentSec = (DateTime.Now - lastActivity).TotalSeconds;

                        _mainThreadContext.Post(_ =>
                        {
                            if (svnUI.CheckoutStatusInfoText != null)
                            {
                                string statusColor = silentSec > 10 ? "yellow" : "green";

                                // 1. Progress and Time Calculation
                                string progressStr = "";
                                string timeStr = "<b>Time Remaining:</b> <color=yellow>Estimating...</color>\n";

                                if (_cachedTotalSizeBytes > 0)
                                {
                                    // We cap at 99.9% because .svn metadata makes the folder larger than the repo size
                                    double percent = (currentTotalDiskSize / (double)_cachedTotalSizeBytes) * 100;
                                    percent = Math.Min(percent, 99.9);

                                    double totalGB = _cachedTotalSizeBytes / (1024d * 1024d * 1024d);
                                    progressStr = $"<b>Progress (Approx):</b> {percent:F1}% ({totalGBOnDisk:F2} / {totalGB:F2} GB)\n";

                                    // 2. Time Remaining Calculation
                                    if (speed > 0.05) // Only calculate if speed is significant
                                    {
                                        double bytesRemaining = _cachedTotalSizeBytes - currentTotalDiskSize;
                                        if (bytesRemaining > 0)
                                        {
                                            // Bytes to MB / Speed (MB/s)
                                            double remainingSeconds = (bytesRemaining / (1024d * 1024d)) / speed;
                                            TimeSpan t = TimeSpan.FromSeconds(remainingSeconds);

                                            string timeFormatted = t.TotalHours >= 1
                                                ? $"{(int)t.TotalHours}h {t.Minutes}m"
                                                : $"{t.Minutes}m {t.Seconds}s";

                                            timeStr = $"<b>Time Remaining (Est):</b> <color=yellow>{timeFormatted}</color>\n";
                                        }
                                        else
                                        {
                                            timeStr = "<b>Time Remaining:</b> Finishing soon...\n";
                                        }
                                    }
                                }
                                else
                                {
                                    progressStr = $"<b>Total on Disk:</b> {totalGBOnDisk:F2} GB\n";
                                    timeStr = "<i>(Total size unknown)</i>\n";
                                }

                                // 3. Final String Assembly
                                if (svnUI.CheckoutStatusInfoText != null)
                                {
                                    string statusColorLocal = silentSec > 10 ? "yellow" : "green";

                                    // Show real, measured data only
                                    svnUI.CheckoutStatusInfoText.text =
                                        $"<b>Status:</b> <color={statusColorLocal}>{(isResume ? "Resuming..." : "Transferring")}</color>\n" +
                                        $"<b>Total on Disk:</b> {totalGBOnDisk:F2} GB\n" +
                                        $"<b>Session Download:</b> {sessionMB:F2} MB\n" +
                                        $"<b>Current Speed:</b> <color=yellow>{speed:F2} MB/s</color>\n" +
                                        $"<b>Files Processed:</b> {filesProcessed}\n";
                                }
                            }
                        }, null);

                        await Task.Delay(5000);
                    }
                });

                // Execute SVN
                string result = await SvnRunner.RunLiveAsync(command, isResume ? path : "", (line) =>
                {
                    filesProcessed++;
                    lastActivity = DateTime.Now;
                }, _checkoutCTS.Token);

                // Result handling (same as before...)
                if (result == "Success")
                {
                    SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=green>Finished!</color>", "SVN");
                    if (!isResume) RegisterNewProjectAfterCheckout(path, url, SvnRunner.KeyPath);
                }
            }
            finally
            {
                IsProcessing = false;
                Screen.sleepTimeout = -1;
                _checkoutCTS?.Dispose();
                _checkoutCTS = null;
            }
        }

        private long GetDirectorySize(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return 0;

            try
            {
                DirectoryInfo di = new DirectoryInfo(folderPath);
                long size = 0;
                // EnumerateFiles is more memory efficient than GetFiles for large projects
                foreach (FileInfo fi in di.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    size += fi.Length;
                }
                return size;
            }
            catch
            {
                // Files are often locked by SVN during write, return 0 or previous known size
                return 0;
            }
        }

        public void CancelCheckout()
        {
            _checkoutCTS?.Cancel();
        }

        private void RegisterNewProjectAfterCheckout(string path, string repoUrl, string keyPath)
        {
            var newProj = new SVNProject
            {
                projectName = Path.GetFileName(path.TrimEnd('/', '\\')),
                repoUrl = repoUrl,
                workingDir = path,
                privateKeyPath = keyPath,
                lastOpened = DateTime.Now
            };

            List<SVNProject> projects = ProjectSettings.LoadProjects();
            if (!projects.Exists(p => p.workingDir == path))
            {
                projects.Add(newProj);
                ProjectSettings.SaveProjects(projects);
                SVNLogBridge.LogLine($"<color=green>Project '{newProj.projectName}' added to Selection List.</color>");
            }

            PlayerPrefs.SetString("SVN_LastOpenedProjectPath", path);
            PlayerPrefs.Save();
        }
    }
}
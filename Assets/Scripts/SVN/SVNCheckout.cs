using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using System.Xml.Linq;

namespace SVN.Core
{
    public class SVNCheckout : SVNBase
    {
        private CancellationTokenSource _checkoutCTS;
        private readonly SynchronizationContext _mainThreadContext;

        private long _cachedTotalSizeBytes = 0;
        private bool _canResume = false;

        private enum OperationState
        {
            Idle,
            Running,
            Pausing,
            Paused,
            Cancelling,
            Cancelled,
            Completed,
            Failed
        }

        private volatile OperationState _state = OperationState.Idle;

        private enum CheckoutState
        {
            Idle,
            CheckingSize,
            Downloading,
            Paused,
            Cancelled
        }

        private CheckoutState _currentStatus = CheckoutState.Idle;

        private const double BytesInGB = 1024d * 1024d * 1024d;
        private const double BytesInMB = 1024d * 1024d;
        private const double MinSpeedThresholdMB = 0.01d;
        private const int MonitorUpdateIntervalMs = 1000;


        public SVNCheckout(SVNUI svnUI, SVNManager manager) : base(svnUI, manager)
        {
            _mainThreadContext = SynchronizationContext.Current;
        }

        private const double SvnOverheadMultiplier = 2.0;

        public async void UpdateProjectInfo()
        {
            string url = svnUI.CheckoutRepoUrlInput.text.Trim();
            string destPath = svnUI.CheckoutDestFolderInput.text.Trim();

            if (string.IsNullOrEmpty(url)) return;

            if (string.IsNullOrEmpty(destPath))
            {
                SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=yellow><b>Info:</b> Wprowadź ścieżkę docelową, aby sprawdzić miejsce na dysku.</color>", "Info");
                return;
            }

            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "Analyzing repository...", "Info");

            try
            {
                _cachedTotalSizeBytes = await GetRemoteRepositorySizeAsync(url);

                double repoDataGB = _cachedTotalSizeBytes / BytesInGB;
                double requiredSpaceGB = repoDataGB * SvnOverheadMultiplier;

                string driveLabel = Path.GetPathRoot(Path.GetFullPath(destPath));
                DriveInfo drive = new DriveInfo(driveLabel);
                double freeSpaceGB = drive.AvailableFreeSpace / BytesInGB;

                string spaceColor = (freeSpaceGB < requiredSpaceGB) ? "red" : "green";

                SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText,
                    $"<b>Repository Size (Raw):</b> {repoDataGB:F2} GB\n" +
                    $"<b>Required Space (with .svn):</b> {requiredSpaceGB:F2} GB\n" +
                    $"<b>Available Space ({driveLabel}):</b> <color={spaceColor}>{freeSpaceGB:F2} GB</color>\n\n" +
                    (freeSpaceGB < requiredSpaceGB
                        ? $"<color=red><b>ERROR:</b> Not enough space! SVN needs ~{requiredSpaceGB:F2} GB.</color>"
                        : "<color=green>Ready to download.</color>"), "Info");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"UpdateProjectInfo failed: {ex.Message}");
                SVNLogBridge.LogLine($"<color=red>Error:</color> {ex.Message}");
            }
        }

        public static async Task<long> GetRepositorySizeAsync(string url, Action<string> onStatusUpdate)
        {
            long totalBytes = 0;
            string args = $"list \"{url}\" --recursive --xml --non-interactive --trust-server-cert";

            onStatusUpdate?.Invoke("Calculating total size from server...");

            await SvnRunner.RunLiveAsync(args, Path.GetTempPath(), (line) =>
            {
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

        public async void StartCheckout()
        {
            if (IsProcessing)
                return;

            string baseUrl = svnUI.CheckoutRepoUrlInput.text.Trim().TrimEnd('/');
            string path = svnUI.CheckoutDestFolderInput.text.Trim();

            if (string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(path))
            {
                SVNLogBridge.UpdateUIField(
                    svnUI.CheckoutStatusInfoText,
                    "<color=red>Error:</color> Inputs cannot be empty!",
                    "Checkout"
                );
                return;
            }

            if (Directory.Exists(path) &&
                Directory.GetFileSystemEntries(path).Length > 0)
            {
                SVNLogBridge.UpdateUIField(
                    svnUI.CheckoutStatusInfoText,
                    "<color=red>Error:</color> Folder is not empty!",
                    "Checkout"
                );
                return;
            }

            string keyPath = SvnRunner.KeyPath;

            if (string.IsNullOrWhiteSpace(keyPath))
            {
                keyPath = SVNManager.Instance.CurrentKey;
                SVNLogBridge.LogLine("[SVN] Using fallback SSH key.");
            }

            keyPath = keyPath
                .Replace("\"", "")
                .Replace("/", "\\");

            if (!File.Exists(keyPath))
            {
                SVNLogBridge.LogError($"[SVN] SSH key not found: {keyPath}");
                return;
            }

            try
            {
                _state = OperationState.Idle;

                SVNLogBridge.UpdateUIField(
                    svnUI.CheckoutStatusInfoText,
                    "Checking repository...",
                    "SVN"
                );

                bool repoOk = await EnsureRepoStructure(baseUrl);

                if (!repoOk)
                {
                    SVNLogBridge.LogError("[SVN] Repository validation failed.");
                    return;
                }

                string trunkUrl = $"{baseUrl}/trunk";

                SVNLogBridge.LogLine(
                    $"<b>[Checkout]</b> Target: {trunkUrl}"
                );

                SVNLogBridge.UpdateUIField(
                    svnUI.CheckoutStatusInfoText,
                    "Calculating repository size...",
                    "SVN"
                );

                _cachedTotalSizeBytes =
                    await GetRemoteRepositorySizeAsync(trunkUrl);

                string checkoutArgs =
                    $"checkout \"{trunkUrl}\" \"{path}\" --force --non-interactive";

                await ExecuteSvnOperation(
                    trunkUrl,
                    path,
                    checkoutArgs,
                    false
                );
            }
            catch (Exception ex)
            {
                _state = OperationState.Failed;

                SVNLogBridge.LogError(
                    $"[SVN] Checkout failed:\n{ex}"
                );

                SVNLogBridge.UpdateUIField(
                    svnUI.CheckoutStatusInfoText,
                    $"<color=red>{ex.Message}</color>",
                    "SVN"
                );

                IsProcessing = false;
            }
        }

        private async Task<bool> EnsureRepoStructure(string repoRoot)
        {
            try
            {
                string safeWorkingDir = Path.GetTempPath();
                string result = await SvnRunner.RunAsync($"ls \"{repoRoot}\"", safeWorkingDir);

                if (string.IsNullOrEmpty(result) || result.StartsWith("SVN Exception:"))
                {
                    SVNLogBridge.LogError($"Structure check failed or access denied. Connection logs: {result}");
                    return false;
                }

                if (!result.Contains("trunk"))
                {
                    SVNLogBridge.LogLine("<b>[Server]</b> Creating trunk/branches/tags structure...");
                    string cmd = $"mkdir \"{repoRoot}/trunk\" \"{repoRoot}/branches\" \"{repoRoot}/tags\" -m \"Initial structure\" --parents --non-interactive";
                    await SvnRunner.RunAsync(cmd, safeWorkingDir);
                }
                return true;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError("Structure check failed: " + ex.Message);
                return false;
            }
        }

        public async void ResumeCheckout()
        {
            if (IsProcessing)
            {
                SVNLogBridge.LogLine(
                    "<color=yellow>[SVN]</color> Another operation is already running."
                );
                return;
            }

            if (!_canResume)
            {
                SVNLogBridge.UpdateUIField(
                    svnUI.CheckoutStatusInfoText,
                    "<color=yellow><b>Cannot resume:</b> This operation was explicitly cancelled.\n" +
                    "To start fresh, please clean/empty the destination folder and use <b>Checkout</b> instead.</color>",
                    "SVN"
                );
                return;
            }

            string url =
                svnUI.CheckoutRepoUrlInput.text.Trim().TrimEnd('/');

            string path =
                svnUI.CheckoutDestFolderInput.text.Trim();

            if (string.IsNullOrWhiteSpace(path))
            {
                SVNLogBridge.UpdateUIField(
                    svnUI.CheckoutStatusInfoText,
                    "<color=red>Error:</color> Destination path cannot be empty!",
                    "Checkout"
                );
                return;
            }

            if (!Directory.Exists(Path.Combine(path, ".svn")))
            {
                SVNLogBridge.LogLine(
                    "<color=red>Error:</color> No .svn metadata found. Use Checkout instead."
                );
                return;
            }

            try
            {
                _state = OperationState.Running;

                SVNLogBridge.UpdateUIField(
                    svnUI.CheckoutStatusInfoText,
                    "<color=yellow><b>Resuming checkout...</b></color>",
                    "SVN"
                );

                if (_cachedTotalSizeBytes <= 0)
                {
                    SVNLogBridge.UpdateUIField(
                        svnUI.CheckoutStatusInfoText,
                        "Checking remote repository size...",
                        "SVN"
                    );

                    _cachedTotalSizeBytes =
                        await GetRemoteRepositorySizeAsync(url);
                }

                SVNLogBridge.LogLine(
                    "<b>[SVN]</b> Resuming checkout..."
                );

                await ExecuteSvnOperation(
                    url,
                    path,
                    "update --force --non-interactive",
                    true
                );
            }
            catch (Exception ex)
            {
                _state = OperationState.Failed;

                SVNLogBridge.LogError(
                    $"Resume failed:\n{ex}"
                );

                SVNLogBridge.UpdateUIField(
                    svnUI.CheckoutStatusInfoText,
                    $"<color=red>{ex.Message}</color>",
                    "SVN"
                );
            }
        }

        public void PauseCheckout()
        {
            if (!IsProcessing) return;

            _canResume = true;

            if (_state != OperationState.Running)
                return;

            _state = OperationState.Pausing;

            SVNLogBridge.LogLine("<color=yellow>[SVN]</color> Pausing checkout...");

            SVNLogBridge.LogLine("<b>[Checkout]</b> Pausing process... Waiting for current file to complete.");
            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=yellow>Pausing...</color>", "SVN");

            _checkoutCTS?.Cancel();
        }

        public void CancelCheckout()
        {
            if (!IsProcessing) return;
            _canResume = false;

            if (_state == OperationState.Cancelling)
                return;

            _state = OperationState.Cancelling;

            SVNLogBridge.LogLine("<color=red>[SVN]</color> Cancelling checkout...");
            SVNLogBridge.LogLine("<color=red><b>[Checkout]</b> Cancelling operation completely...</color>");
            SVNLogBridge.UpdateUIField(svnUI.CheckoutStatusInfoText, "<color=red>Cancelling...</color>", "SVN");

            _checkoutCTS?.Cancel();
        }

        private async Task<long> GetRemoteRepositorySizeAsync(string url)
        {
            if (string.IsNullOrEmpty(url)) return 0;

            try
            {
                string args = $"list --xml -R \"{url}\" --non-interactive --trust-server-cert";

                string xmlOutput = await SvnRunner.RunAsync(args, Path.GetTempPath(), false, CancellationToken.None);

                if (string.IsNullOrEmpty(xmlOutput)) return 0;

                XDocument doc = XDocument.Parse(xmlOutput);
                long totalSizeBytes = 0;

                foreach (var sizeElement in doc.Descendants("size"))
                {
                    if (long.TryParse(sizeElement.Value, out long fileSize))
                    {
                        totalSizeBytes += fileSize;
                    }
                }

                return totalSizeBytes;
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"[SVN] Nie udało się obliczyć rozmiaru repozytorium: {ex.Message}");
                return 0;
            }
        }

        private int GetFileCount(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    return 0;

                return Directory.GetFiles(
                    path,
                    "*",
                    SearchOption.AllDirectories
                ).Length;
            }
            catch
            {
                return 0;
            }
        }

        private async Task ExecuteSvnOperation(
    string url,
    string path,
    string command,
    bool isResume = false)
        {
            if (IsProcessing)
            {
                SVNLogBridge.LogLine(
                    "<color=yellow>[SVN]</color> Operation already running."
                );
                return;
            }

            IsProcessing = true;

            _state = OperationState.Running;

            _checkoutCTS?.Dispose();
            _checkoutCTS = new CancellationTokenSource();

            CancellationToken token = _checkoutCTS.Token;

            DateTime start = DateTime.Now;
            DateTime lastActivity = DateTime.Now;

            long sizeBeforeThisSession =
                Directory.Exists(path)
                    ? GetDirectorySize(path)
                    : 0;

            Task monitorTask = null;

            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                if (isResume)
                {
                    _mainThreadContext.Post(_ =>
                    {
                        SVNLogBridge.UpdateUIField(
                            svnUI.CheckoutStatusInfoText,
                            "Unlocking working copy (svn cleanup)...",
                            "SVN"
                        );
                    }, null);

                    string cleanupArgs =
                        "cleanup --non-interactive --trust-server-cert";

                    string cleanupResult = await SvnRunner.RunAsync(
                        cleanupArgs,
                        path,
                        false,
                        token
                    );

                    if (token.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(token);
                    }

                    if (!string.IsNullOrWhiteSpace(cleanupResult) &&
                        cleanupResult.ToLower().Contains("error"))
                    {
                        throw new Exception(
                            $"SVN cleanup failed:\n{cleanupResult}"
                        );
                    }

                    command =
                        "update --force --non-interactive --trust-server-cert";

                    SVNLogBridge.LogLine(
                        "<color=yellow>[SVN]</color> Resuming via 'svn update'..."
                    );
                }

                monitorTask = Task.Run(async () =>
                {
                    while (!token.IsCancellationRequested &&
                           (_state == OperationState.Running ||
                            _state == OperationState.Pausing))
                    {
                        try
                        {
                            long currentTotalDiskSize =
                                GetDirectorySize(path);

                            long bytesDownloadedInSession =
                                currentTotalDiskSize - sizeBeforeThisSession;

                            if (bytesDownloadedInSession < 0)
                            {
                                bytesDownloadedInSession = 0;
                            }

                            double sessionMB =
                                bytesDownloadedInSession / BytesInMB;

                            double totalGB =
                                currentTotalDiskSize / BytesInGB;

                            int realFiles =
                                GetFileCount(path);

                            double totalSeconds =
                                Math.Max(
                                    (DateTime.Now - start).TotalSeconds,
                                    1
                                );

                            double speedMB =
                                sessionMB / totalSeconds;

                            double silentSeconds =
                                (DateTime.Now - lastActivity).TotalSeconds;

                            string progressStr =
                                "<b>Progress:</b> Calculating...\n";

                            string timeStr =
                                "<b>Time Remaining:</b> Estimating...\n";

                            if (_cachedTotalSizeBytes > 0)
                            {
                                double percent =
                                    ((double)currentTotalDiskSize /
                                     _cachedTotalSizeBytes) * 100d;

                                percent = Math.Min(percent, 100d);

                                progressStr =
                                    $"<b>Progress:</b> {percent:F1}%\n";

                                long remainingBytes =
                                    _cachedTotalSizeBytes -
                                    currentTotalDiskSize;

                                if (remainingBytes > 0 &&
                                    speedMB > MinSpeedThresholdMB)
                                {
                                    double remainingSeconds =
                                        remainingBytes /
                                        (speedMB * BytesInMB);

                                    TimeSpan t =
                                        TimeSpan.FromSeconds(
                                            remainingSeconds
                                        );

                                    timeStr =
                                        t.TotalHours >= 1
                                            ? $"<b>Time Remaining:</b> {(int)t.TotalHours}h {t.Minutes}m\n"
                                            : $"<b>Time Remaining:</b> {t.Minutes}m {t.Seconds}s\n";
                                }
                                else if (remainingBytes <= 0)
                                {
                                    timeStr =
                                        "<b>Time Remaining:</b> Finishing...\n";
                                }
                            }

                            _mainThreadContext.Post(_ =>
                            {
                                if (svnUI.CheckoutStatusInfoText == null)
                                    return;

                                string stateText = _state switch
                                {
                                    OperationState.Running =>
                                        isResume
                                            ? "Resuming"
                                            : "Downloading",

                                    OperationState.Pausing =>
                                        "Pausing",

                                    OperationState.Paused =>
                                        "Paused",

                                    OperationState.Cancelling =>
                                        "Cancelling",

                                    OperationState.Cancelled =>
                                        "Cancelled",

                                    OperationState.Completed =>
                                        "Completed",

                                    OperationState.Failed =>
                                        "Failed",

                                    _ =>
                                        "Idle"
                                };

                                string stateColor =
                                    _state switch
                                    {
                                        OperationState.Running => "green",
                                        OperationState.Pausing => "yellow",
                                        OperationState.Paused => "yellow",
                                        OperationState.Cancelling => "red",
                                        OperationState.Cancelled => "red",
                                        OperationState.Completed => "green",
                                        OperationState.Failed => "red",
                                        _ => "white"
                                    };

                                if (silentSeconds > 15 &&
                                    _state == OperationState.Running)
                                {
                                    stateColor = "yellow";
                                }

                                svnUI.CheckoutStatusInfoText.text =
                                    $"<b>Status:</b> <color={stateColor}>{stateText}</color>\n" +
                                    progressStr +
                                    timeStr +
                                    $"<b>Total on Disk:</b> {totalGB:F2} GB\n" +
                                    $"<b>Session:</b> {sessionMB:F2} MB\n" +
                                    $"<b>Speed:</b> {speedMB:F2} MB/s\n" +
                                    $"<b>Files:</b> {realFiles}\n";
                            }, null);

                            await Task.Delay(
                                MonitorUpdateIntervalMs,
                                token
                            );
                        }
                        catch (TaskCanceledException)
                        {
                            break;
                        }
                        catch
                        {

                        }
                    }
                }, token);

                string workingDirectory;

                if (isResume)
                {
                    workingDirectory = path;
                }
                else
                {
                    workingDirectory =
                        Directory.GetParent(path)?.FullName;

                    if (string.IsNullOrWhiteSpace(workingDirectory))
                    {
                        workingDirectory = Path.GetTempPath();
                    }
                }

                SVNLogBridge.LogLine(
                    $"<color=grey>[SVN]</color> Working Directory: {workingDirectory}"
                );

                string result = await SvnRunner.RunLiveAsync(
                    command,
                    workingDirectory,
                    (line) =>
                    {
                        if (string.IsNullOrWhiteSpace(line))
                            return;

                        lastActivity = DateTime.Now;

                        SVNLogBridge.LogLine(line);
                    },
                    token
                );

                if (token.IsCancellationRequested)
                {
                    throw new OperationCanceledException(token);
                }

                bool isSuccess =
                    !string.IsNullOrWhiteSpace(result) &&
                    !result.ToLower().Contains("error") &&
                    !result.ToLower().Contains("exception") &&
                    !result.ToLower().Contains("failed");

                bool hasSvnMetadata =
                    Directory.Exists(Path.Combine(path, ".svn"));

                if (!isSuccess || !hasSvnMetadata)
                {
                    _state = OperationState.Failed;

                    SVNLogBridge.LogError(
                        $"[Checkout/Update FAILED]\n{result}"
                    );

                    _mainThreadContext.Post(_ =>
                    {
                        SVNLogBridge.UpdateUIField(
                            svnUI.CheckoutStatusInfoText,
                            "<color=red><b>Checkout Failed</b></color>",
                            "SVN"
                        );
                    }, null);

                    return;
                }

                _state = OperationState.Completed;

                _mainThreadContext.Post(_ =>
                {
                    SVNLogBridge.UpdateUIField(
                        svnUI.CheckoutStatusInfoText,
                        "<color=green><b>Checkout completed successfully</b></color>",
                        "SVN"
                    );
                }, null);

                RegisterNewProjectAfterCheckout(
                    path,
                    url,
                    SvnRunner.KeyPath
                );
            }
            catch (OperationCanceledException)
            {
                if (_state == OperationState.Pausing)
                {
                    _state = OperationState.Paused;

                    _mainThreadContext.Post(_ =>
                    {
                        SVNLogBridge.UpdateUIField(
                            svnUI.CheckoutStatusInfoText,
                            "<color=yellow><b>Operation Paused</b></color>\nFiles preserved on disk.",
                            "SVN"
                        );
                    }, null);

                    SVNLogBridge.LogLine(
                        "<color=yellow>[SVN]</color> Checkout paused."
                    );
                }
                else
                {
                    _state = OperationState.Cancelled;

                    _mainThreadContext.Post(_ =>
                    {
                        SVNLogBridge.UpdateUIField(
                            svnUI.CheckoutStatusInfoText,
                            "<color=red><b>Operation Cancelled</b></color>",
                            "SVN"
                        );
                    }, null);

                    SVNLogBridge.LogLine(
                        "<color=red>[SVN]</color> Checkout cancelled."
                    );
                }
            }
            catch (Exception ex)
            {
                _state = OperationState.Failed;

                SVNLogBridge.LogError(
                    $"SVN Exception:\n{ex}"
                );

                _mainThreadContext.Post(_ =>
                {
                    SVNLogBridge.UpdateUIField(
                        svnUI.CheckoutStatusInfoText,
                        $"<color=red>Error:</color> {ex.Message}",
                        "SVN"
                    );
                }, null);
            }
            finally
            {
                try
                {
                    if (monitorTask != null)
                    {
                        await monitorTask;
                    }
                }
                catch
                {
                }

                _checkoutCTS?.Dispose();
                _checkoutCTS = null;

                IsProcessing = false;

                if (_state != OperationState.Paused)
                {
                    _state = OperationState.Idle;
                }
            }
        }

        private long GetDirectorySize(string folderPath)
        {
            if (!Directory.Exists(folderPath)) return 0;

            long size = 0;
            try
            {
                DirectoryInfo di = new DirectoryInfo(folderPath);

                foreach (FileInfo fi in di.EnumerateFiles())
                {
                    try { size += fi.Length; } catch { }
                }

                foreach (DirectoryInfo subDir in di.EnumerateDirectories())
                {
                    size += GetDirectorySize(subDir.FullName);
                }
            }
            catch { }

            return size;
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
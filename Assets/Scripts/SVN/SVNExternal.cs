using SFB;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNExternal : SVNBase, IDisposable
    {
        private readonly SemaphoreSlim _processingLock = new SemaphoreSlim(1, 1);
        private int _disposed;

        public SVNExternal(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void OpenInExplorer()
        {
            try
            {
                string root = svnManager.WorkingDir;
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    SVNLogBridge.LogLine("<color=#FFAA00>Error: Working directory is not set or does not exist!</color>");
                    return;
                }

                OpenFolderInFileManager(root);
                SVNLogBridge.LogLine($"<color=green>Explorer:</color> Opened {root}");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Explorer Error:</color> {ex.Message}");
            }
        }

        public void OpenInExplorerAndSelect(string relativePath)
        {
            try
            {
                string root = svnManager.WorkingDir;
                if (string.IsNullOrEmpty(root)) return;

                string fullPath = Path.Combine(root, relativePath ?? "");
                fullPath = fullPath.Replace('/', Path.DirectorySeparatorChar);

                if (File.Exists(fullPath) || Directory.Exists(fullPath))
                    SelectPathInFileManager(fullPath);
                else
                    OpenFolderInFileManager(root);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Explorer Error:</color> {ex.Message}");
            }
        }

        private static void OpenFolderInFileManager(string path)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            string winPath = path.Replace('/', '\\');
            SafeStart(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{winPath}\"",
                UseShellExecute = true
            });
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            SafeStart(new ProcessStartInfo("open", $"\"{path}\"") { UseShellExecute = false });
#else
            try
            {
                SafeStart(new ProcessStartInfo("xdg-open", $"\"{path}\"") { UseShellExecute = false });
            }
            catch
            {
                Application.OpenURL("file://" + path);
            }
#endif
        }

        private static void SelectPathInFileManager(string fullPath)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            string winPath = fullPath.Replace('/', '\\');
            SafeStart(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{winPath}\"",
                UseShellExecute = true
            });
#elif UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX
            SafeStart(new ProcessStartInfo("open", $"-R \"{fullPath}\"") { UseShellExecute = false });
#else
            string dir = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : fullPath;
            OpenFolderInFileManager(dir ?? fullPath);
#endif
        }

        private static void SafeStart(ProcessStartInfo psi)
        {
            var p = Process.Start(psi);
            p?.Dispose();
        }

        public async void ShowChangesForSelected(string relativePath)
        {
            try
            {
                await ShowChangesForSelectedAsync(relativePath);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Diff Critical Error:</color> {ex.Message}");
            }
        }

        private async Task ShowChangesForSelectedAsync(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                SVNLogBridge.LogLine("<color=yellow>Warning:</color> No file selected for Diff.");
                return;
            }

            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Working directory is not set.");
                return;
            }

            string fullPath = Path.Combine(root, relativePath);
            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> File not found on disk.");
                return;
            }

            SVNLogBridge.LogLine($"Opening Diff for: {relativePath}...");

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                SafeStart(new ProcessStartInfo
                {
                    FileName = "TortoiseProc.exe",
                    Arguments = $"/command:diff /path:\"{fullPath.Replace('/', '\\')}\"",
                    UseShellExecute = true
                });
                return;
            }
            catch
            {
                // TortoiseProc nieosadzony — fallback niżej.
            }
#endif

            // === FIX K2: wcześniej fallback wykonywał 'svn diff' i WYRZUCAŁ wynik
            // (output szedł w nic). Teraz delegacja do modułu SVNDiff — pełna
            // ścieżka: skonfigurowany tool → podgląd w Unity → raport .diff.
            var diffModule = svnManager.GetModule<SVNDiff>();
            if (diffModule != null)
            {
                await diffModule.ShowDiff(relativePath).ConfigureAwait(false);
            }
            else
            {
                SVNLogBridge.LogLine("<color=yellow>Notice:</color> SVNDiff module unavailable — cannot display diff.");
            }
        }

        public void BrowseDestinationFolderPathLoad()
        {
            string[] paths = StandaloneFileBrowser.OpenFolderPanel("Select SVN Working Directory", "", false);
            if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0]))
            {
                SVNLogBridge.LogLine("Folder selection canceled.");
                return;
            }

            string p = NormalizePath(paths[0]);

            if (svnUI.LoadDestFolderInput != null)
                svnUI.LoadDestFolderInput.text = p;

            SVNLogBridge.LogLine($"SVN path selected: {p}");
            _ = LoadSelectedPathAsProjectAsync(p);
        }

        // === S4: pełne przełączenie przez LoadProject (eventy OnProjectChanged,
        // cache modułów Merge/BranchTag, snapshoty, walidacja .svn). Wcześniej
        // bezpośrednie 'svnManager.WorkingDir = p' + SetWorkingDirectory —
        // pół-przełączenie: manager na nowym katalogu, moduły ze starym cache.
        private async Task LoadSelectedPathAsProjectAsync(string path)
        {
            try
            {
                SVNProject project = ProjectSettings.AddOrUpdateProject(path, (p, created) =>
                {
                    if (created)
                        p.projectName = Path.GetFileName(path.Replace("\\", "/").TrimEnd('/'));
                    p.lastOpened = DateTime.UtcNow;
                });

                bool loaded = await svnManager.LoadProject(project).ConfigureAwait(false);
                if (!loaded)
                    SVNLogBridge.LogLine($"<color=#FFAA00>[Load]</color> '{path}' is not a valid SVN working copy — project not loaded.");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"Failed to load project: {ex.Message}");
            }
        }

        public void BrowsePrivateKeyPathLoad()
        {
            var ext = new[]
            {
                new ExtensionFilter("Private Key Files", "ppk", "key", "pem", "ssh"),
                new ExtensionFilter("All Files", "*")
            };

            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select Private Key File", "", ext, false);
            if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0]))
            {
                SVNLogBridge.LogLine("Private Key selection canceled.");
                return;
            }

            string p = NormalizePath(paths[0]);
            svnManager.CurrentKey = p;

            if (svnUI.LoadPrivateKeyInput != null)
                svnUI.LoadPrivateKeyInput.text = p;

            SVNLogBridge.LogLine($"Private Key path set to: {p}");
        }

        public void BrowseDestinationFolderPathAdd()
        {
            string[] paths = StandaloneFileBrowser.OpenFolderPanel("Select SVN Working Directory", "", false);
            if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0])) return;

            string p = NormalizePath(paths[0]);
            if (svnUI.AddProjectFolderPathInput != null)
                svnUI.AddProjectFolderPathInput.text = p;

            if (svnUI.AddProjectNameInput != null &&
                string.IsNullOrEmpty(svnUI.AddProjectNameInput.text))
            {
                svnUI.AddProjectNameInput.text = Path.GetFileName(p);
            }
        }

        public void BrowsePrivateKeyPathAdd()
        {
            var ext = new[]
            {
                new ExtensionFilter("Private Key Files", "ppk", "key", "pem", "ssh"),
                new ExtensionFilter("All Files", "*")
            };

            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select Private Key", "", ext, false);
            if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0])) return;

            if (svnUI.AddProjectKeyPathInput != null)
                svnUI.AddProjectKeyPathInput.text = NormalizePath(paths[0]);
        }

        public void BrowseDestinationFolderPathCheckout()
        {
            string[] paths = StandaloneFileBrowser.OpenFolderPanel("Select Checkout Destination Directory", "", false);
            if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0])) return;

            string p = NormalizePath(paths[0]);
            if (svnUI.CheckoutDestFolderInput != null)
                svnUI.CheckoutDestFolderInput.text = p;

            SVNLogBridge.LogLine($"[Checkout] Destination path set to: {p}");
        }

        public void BrowsePrivateKeyPathCheckout()
        {
            var ext = new[] { new ExtensionFilter("All Files", "*") };
            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select SSH Private Key for Checkout", "", ext, false);
            if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0])) return;

            string p = NormalizePath(paths[0]);
            if (svnUI.CheckoutPrivateKeyInput != null)
                svnUI.CheckoutPrivateKeyInput.text = p;

            SVNLogBridge.LogLine($"[Checkout] SSH Key path set to: {p}");
        }

        public void BrowseResolveFilePath()
        {
            BrowseFileRelativeToWorkingDir(
                "Select File to Resolve",
                path =>
                {
                    if (svnUI.ResolveTargetFileInput != null)
                    {
                        svnUI.ResolveTargetFileInput.text = path;
                        SVNLogBridge.LogLine($"<color=green>Resolve:</color> Selected target file: {path}");
                    }
                    else
                    {
                        SVNLogBridge.LogErrorToOutput("[SVN] ResolveTargetFileInput is not assigned in SVNUI!");
                    }
                });
        }

        public void BrowseDiffFilePath()
        {
            BrowseFileRelativeToWorkingDir(
                "Select File to Diff",
                path =>
                {
                    if (svnUI.DiffTargetFileInput != null)
                    {
                        svnUI.DiffTargetFileInput.text = path;
                        SVNLogBridge.LogLine($"<color=green>Diff:</color> Selected file: {path}");
                    }
                    else
                    {
                        SVNLogBridge.LogError("[SVN] DiffTargetFileInput is not assigned in SVNUI!");
                    }
                });
        }

        public void BrowseBlameFilePath()
        {
            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Working Directory is not set or does not exist!");
                return;
            }

            BrowseFileRelativeToWorkingDir(
                "Select File for Blame",
                path =>
                {
                    if (svnUI.BlameTargetFileInput != null)
                    {
                        svnUI.BlameTargetFileInput.text = path;
                        SVNLogBridge.LogLine($"<color=green>Blame:</color> Target file set to: {path}");
                    }
                });
        }

        // === FIX K1: prefix-match ze SLASHEM. Wcześniej 'sel.StartsWith(normRoot)'
        // bez separatora — katalog 'D:/Repo' pasował do 'D:/RepoBackup/...' i do
        // pola trafiała śmieciowa ścieżka względna (np. 'Backup/file.txt') →
        // diff/resolve/blame działały na INNYM pliku lub padały na not-found.
        private void BrowseFileRelativeToWorkingDir(string title, Action<string> onSelected)
        {
            string root = svnManager.WorkingDir ?? "";
            var ext = new[] { new ExtensionFilter("All Files", "*") };
            string[] paths = StandaloneFileBrowser.OpenFilePanel(title, root, ext, false);

            if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0]))
                return;

            string sel = NormalizePath(paths[0]);
            string normRoot = NormalizePath(root);

            if (!string.IsNullOrEmpty(normRoot))
            {
                if (sel.Equals(normRoot, StringComparison.OrdinalIgnoreCase))
                {
                    sel = "";
                }
                else if (sel.StartsWith(normRoot + "/", StringComparison.OrdinalIgnoreCase))
                {
                    sel = sel.Substring(normRoot.Length + 1);
                }
                else
                {
                    SVNLogBridge.LogLine("<color=yellow>Warning:</color> Selected file is outside of the Working Directory!");
                }
            }

            onSelected?.Invoke(sel);
        }

        public void OpenTortoiseLog()
        {
            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root))
            {
                SVNLogBridge.LogLine("<color=yellow>Warning:</color> Working directory not set.");
                return;
            }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                SafeStart(new ProcessStartInfo
                {
                    FileName = "TortoiseProc.exe",
                    Arguments = $"/command:log /path:\"{root.Replace('/', '\\')}\"",
                    UseShellExecute = true
                });
                SVNLogBridge.LogLine("<b>[External]</b> Opening TortoiseSVN Log...");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogErrorToOutput($"<color=#FFAA00>TortoiseSVN Error:</color> {ex.Message}");
            }
#else
            SVNLogBridge.LogLine("<color=yellow>TortoiseSVN is only available on Windows.</color>");
#endif
        }

        public void SaveHistoryToFile(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                SVNLogBridge.LogLine("<color=yellow>Warning:</color> No content to export.");
                return;
            }

            string defaultName = $"SVN_History_{DateTime.Now:yyyyMMdd_HHmm}";
            string path = StandaloneFileBrowser.SaveFilePanel("Save SVN History Report", "", defaultName, "txt");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                File.WriteAllText(path, content);
                SVNLogBridge.LogLine($"<color=green>Success:</color> History exported to {path}");
                SafeStart(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Export Error:</color> {ex.Message}");
            }
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        // UWAGA: 'long wEventId' poprawne dla x64 (Unity player standard); dla x86
        // buildów sygnatura powinna być int — jeśli kiedyś wspieracie 32-bit, zmienić.
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern void SHChangeNotify(long wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

        private const long SHCNE_UPDATEDIR = 0x00001000L;
        private const uint SHCNF_PATHW = 0x0005;
#endif

        public void RefreshWindowsShellIcons(string targetPath)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            try
            {
                string full = Path.Combine(svnManager.WorkingDir ?? "", targetPath ?? "");
                string dir = File.Exists(full) ? Path.GetDirectoryName(full) : full;

                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    IntPtr ptr = Marshal.StringToHGlobalUni(dir);
                    try
                    {
                        SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, ptr, IntPtr.Zero);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(ptr);
                    }
                }
            }
            catch (Exception ex)
            {
                LogBoth($"[Shell Error] Failed to refresh icons: {ex.Message}");
            }
#else
            LogBoth("[Shell] Icon refresh is only supported on Windows.");
#endif
        }

        public async void TestConnection()
        {
            if (Volatile.Read(ref _disposed) == 1) return;

            if (!await _processingLock.WaitAsync(0))
            {
                SVNLogBridge.LogLine("[WARN] Another operation is already running. Please wait for it to finish.");
                return;
            }

            IsProcessing = true;

            try
            {
                // === FIX Ś-drobiazg: token z twardym limitem — diagnostyka potrafi
                // trwać ~40 s (10 kroków po kilka s) i była nieprzerywalna.
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                await RunDiagnosticsAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange>[DIAG] Timed out (3 min).</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FF9900>[ERROR]</color> Diagnostics crashed: {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                try { _processingLock.Release(); }
                catch (SemaphoreFullException) { }
            }
        }

        private async Task RunDiagnosticsAsync(CancellationToken token)
        {
            bool hadErrors = false;
            var report = new System.Text.StringBuilder();
            const string colOK = "#00E5FF";
            const string colWARN = "#FFCC00";
            const string colERR = "#FF9900";
            const string colSTEP = "#00008B";

            report.AppendLine($"Session Token: {svnManager.SessionToken}");
            report.AppendLine("====================================");
            report.AppendLine(" CONNECTION DIAGNOSTICS");
            report.AppendLine("====================================");
            report.AppendLine();

            string repoUrl = svnManager.RepositoryUrl;
            if (string.IsNullOrEmpty(repoUrl))
            {
                report.AppendLine($"<color={colERR}>[ERROR]</color> Repository URL not set.");
                ShowReport(report.ToString());
                return;
            }

            string host = "unknown", protocol = "unknown", port = "unknown";
            string repoPath = "unknown", username = "unknown";
            bool validUrl = true;
            int targetPort = 22;

            // 0 – URL
            SVNLogBridge.LogLine("[DIAG] [0/10] Checking repository URL...");
            report.AppendLine($"<color={colSTEP}>[0/10] CHECKING REPOSITORY URL...</color>");

            try
            {
                var uri = new Uri(repoUrl);
                host = uri.Host;
                protocol = uri.Scheme.ToUpperInvariant();
                repoPath = uri.AbsolutePath.TrimStart('/');
                username = !string.IsNullOrEmpty(uri.UserInfo)
                    ? uri.UserInfo
                    : (svnManager.CurrentUserName ?? "unknown");

                if (protocol is "SVN+SSH" or "SSH") targetPort = 22;
                else if (protocol == "HTTPS") targetPort = 443;
                else if (protocol == "HTTP") targetPort = 80;
                else if (protocol == "SVN") targetPort = 3690;

                if (!uri.IsDefaultPort) targetPort = uri.Port;
                port = targetPort.ToString();
            }
            catch (Exception ex)
            {
                validUrl = false;
                hadErrors = true;
                report.AppendLine($"<color={colERR}>[ERROR]</color> Invalid URL: {ex.Message}");
            }

            report.AppendLine($" Repository URL : {repoUrl}");
            report.AppendLine($" Protocol : {protocol}");
            report.AppendLine($" Host : {host}");
            report.AppendLine($" Port : {port}");
            report.AppendLine($" Repository Path: {repoPath}");
            report.AppendLine($" Username : {username}");
            report.AppendLine();

            if (!validUrl)
            {
                report.AppendLine("====================================");
                report.AppendLine(" DIAGNOSTICS ABORTED");
                report.AppendLine("====================================");
                ShowReport(report.ToString());
                return;
            }

            // 1 – SVN client
            SVNLogBridge.LogLine("[DIAG] [1/10] Checking SVN client...");
            report.AppendLine($"<color={colSTEP}>[1/10] CHECKING SVN CLIENT...</color>");
            try
            {
                string ver = await SvnRunner.RunAsync("--version --quiet", svnManager.WorkingDir, token: token);
                report.AppendLine($"<color={colOK}>[OK]</color> SVN client version : {ver.Trim()}");
            }
            catch (Exception ex)
            {
                hadErrors = true;
                report.AppendLine($"<color={colERR}>[ERROR]</color> Unable to detect SVN client: {ex.Message}");
            }
            report.AppendLine();

            // 2 – OpenSSH
            SVNLogBridge.LogLine("[DIAG] [2/10] Checking OpenSSH...");
            report.AppendLine($"<color={colSTEP}>[2/10] CHECKING OPENSSH CLIENT...</color>");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ssh",
                    Arguments = "-V",
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var sshProc = Process.Start(psi);
                if (sshProc != null)
                {
                    // === FIX Ś2: czytamy OBA strumienie asynchronicznie + await exit —
                    // 'ssh -V' pisze na stderr, ale nieoskubany stdout to wzorzec
                    // deadlocku pipe (przy większym output proces by się zawiesił).
                    var stderrTask = sshProc.StandardError.ReadToEndAsync();
                    var stdoutTask = sshProc.StandardOutput.ReadToEndAsync();
                    await Task.WhenAll(stderrTask, stdoutTask).ConfigureAwait(false);
                    sshProc.WaitForExit(5000);
                    report.AppendLine($"<color={colOK}>[OK]</color> OpenSSH version : {stderrTask.Result.Trim()}");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"<color={colWARN}>[WARN]</color> Could not detect OpenSSH version: {ex.Message}");
            }
            report.AppendLine();

            // 3 – SSH key
            SVNLogBridge.LogLine("[DIAG] [3/10] Checking SSH key...");
            report.AppendLine($"<color={colSTEP}>[3/10] CHECKING SSH KEY...</color>");
            string keyPath = SvnRunner.KeyPath;
            if (!string.IsNullOrEmpty(keyPath))
            {
                string clean = keyPath.Replace("\"", "").Trim().Replace("\\", "/");
                if (File.Exists(clean))
                {
                    report.AppendLine($"<color={colOK}>[OK]</color> Key file exists : {clean}");
                    try
                    {
                        var fi = new FileInfo(clean);
                        report.AppendLine($"<color={colOK}>[OK]</color> Key file size : {fi.Length} bytes");
                        report.AppendLine($"<color={colOK}>[OK]</color> Key file modified : {fi.LastWriteTime}");
                    }
                    catch { /* ignore */ }
                }
                else
                {
                    hadErrors = true;
                    report.AppendLine($"<color={colERR}>[ERROR]</color> Key file not found at path: {clean}");
                }
            }
            else
            {
                report.AppendLine($"<color={colWARN}>[WARN]</color> No SSH key configured.");
            }
            report.AppendLine();

            // 4 – DNS
            SVNLogBridge.LogLine("[DIAG] [4/10] Testing DNS resolution...");
            report.AppendLine($"<color={colSTEP}>[4/10] TESTING DNS RESOLUTION...</color>");
            try
            {
                var addrs = await System.Net.Dns.GetHostAddressesAsync(host);
                foreach (var a in addrs)
                    report.AppendLine($"<color={colOK}>[OK]</color> DNS resolved → {a}");
            }
            catch (Exception ex)
            {
                hadErrors = true;
                report.AppendLine($"<color={colERR}>[ERROR]</color> DNS resolution failed: {ex.Message}");
            }
            report.AppendLine();

            // 5 – Ping
            SVNLogBridge.LogLine("[DIAG] [5/10] Pinging host...");
            report.AppendLine($"<color={colSTEP}>[5/10] TESTING HOST REACHABILITY (ICMP)...</color>");
            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                var reply = await ping.SendPingAsync(host, 3000);
                if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                {
                    report.AppendLine(
                        $"<color={colOK}>[OK]</color> Host reachable : {reply.Address} (response time: {reply.RoundtripTime}ms)");
                }
                else
                {
                    report.AppendLine("<color=black>[INFO]</color> Ping blocked – ICMP may be disabled on this host.");
                }
            }
            catch (Exception ex)
            {
                report.AppendLine($"<color=black>[INFO]</color> Ping unavailable: {ex.Message}");
            }
            report.AppendLine();

            // 6 – TCP
            SVNLogBridge.LogLine("[DIAG] [6/10] Testing TCP port...");
            report.AppendLine($"<color={colSTEP}>[6/10] TESTING TCP PORT {targetPort}...</color>");
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var connectTask = client.ConnectAsync(host, targetPort);

                // === FIX Ś2: timeout przez WhenAny; przegrany connectTask jest
                // OBSERWOWANY (ContinueWith łapie wyjątek) — wcześniej faultował
                // w tle jako unobserved.
                var completed = await Task.WhenAny(connectTask, Task.Delay(5000)).ConfigureAwait(false);

                if (completed == connectTask && client.Connected)
                {
                    report.AppendLine($"<color={colOK}>[OK]</color> TCP port {targetPort} is open and reachable.");
                }
                else
                {
                    _ = connectTask.ContinueWith(t => { try { t.Wait(); } catch { } }, TaskScheduler.Default);
                    client.Close();
                    hadErrors = true;
                    report.AppendLine(
                        $"<color={colERR}>[ERROR]</color> TCP port {targetPort} timed out – service may be down or firewalled.");
                }
            }
            catch (Exception ex)
            {
                hadErrors = true;
                report.AppendLine($"<color={colERR}>[ERROR]</color> TCP test failed: {ex.Message}");
            }
            report.AppendLine();

            // 7 – SSH
            SVNLogBridge.LogLine("[DIAG] [7/10] Testing SSH connection...");
            report.AppendLine($"<color={colSTEP}>[7/10] TESTING DIRECT SSH CONNECTION...</color>");
            if (!string.IsNullOrEmpty(keyPath))
            {
                try
                {
                    string cleanKey = keyPath.Replace("\"", "").Trim().Replace("\\", "/");
                    if (File.Exists(cleanKey))
                    {
                        string sshArgs =
                            $"-T -i \"{cleanKey}\" -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=10 {username}@{host}";

                        var psi = new ProcessStartInfo
                        {
                            FileName = "ssh",
                            Arguments = sshArgs,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };

                        using var sshProc = Process.Start(psi);
                        if (sshProc != null)
                        {
                            // === FIX Ś2: strumienie czytane asynchronicznie PRZED
                            // WaitForExit (kill po timeout) — koniec wzorca
                            // deadlocku pełnego pipe'a + nieczytanego stdout.
                            var stderrTask = sshProc.StandardError.ReadToEndAsync();
                            var stdoutTask = sshProc.StandardOutput.ReadToEndAsync();

                            bool exited = await Task.Run(() => sshProc.WaitForExit(10000), token).ConfigureAwait(false);
                            if (!exited)
                            {
                                try { sshProc.Kill(); } catch { /* ignore */ }
                                _ = stderrTask.ContinueWith(_ => { }, TaskScheduler.Default);
                                _ = stdoutTask.ContinueWith(_ => { }, TaskScheduler.Default);
                                report.AppendLine($"<color={colWARN}>[WARN]</color> SSH handshake timed out after 10 seconds.");
                            }
                            else
                            {
                                string error = (await stderrTask.ConfigureAwait(false)).Trim();
                                if (sshProc.ExitCode == 0)
                                {
                                    report.AppendLine($"<color={colOK}>[OK]</color> SSH connection successfully established.");
                                }
                                else if (sshProc.ExitCode == 1)
                                {
                                    if (error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                                        error.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
                                    {
                                        hadErrors = true;
                                        report.AppendLine($"<color={colERR}>[ERROR]</color> SSH connection failed: {error}");
                                    }
                                    else
                                    {
                                        report.AppendLine($"<color={colOK}>[OK]</color> SSH connection established (warnings ignored).");
                                    }
                                }
                                else
                                {
                                    hadErrors = true;
                                    report.AppendLine(
                                        $"<color={colERR}>[ERROR]</color> SSH connection failed with exit code {sshProc.ExitCode} - {error}");
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    hadErrors = true;
                    report.AppendLine($"<color={colERR}>[ERROR]</color> SSH test failed: {ex.Message}");
                }
            }
            report.AppendLine();

            // 8 – SVN auth + remote access (end-to-end)
            SVNLogBridge.LogLine("[DIAG] [8/10] Authenticating to repository...");
            report.AppendLine($"<color={colSTEP}>[8/10] TESTING SVN AUTHENTICATION & REMOTE ACCESS...</color>");
            var sw = Stopwatch.StartNew();
            try
            {
                string remoteInfo = await SvnRunner.RunAsync(
                    $"info \"{EscapeSvnArg(repoUrl)}\"",
                    svnManager.WorkingDir, token: token);

                if (!string.IsNullOrWhiteSpace(remoteInfo))
                    report.AppendLine($"<color={colOK}>[OK]</color> Remote repository accessible via SVN.");

                string uuid = await SvnRunner.RunAsync("info --show-item repos-uuid", svnManager.WorkingDir, token: token);
                sw.Stop();
                report.AppendLine($"<color={colOK}>[OK]</color> Repository UUID : {uuid.Trim()}");
                report.AppendLine($"<color={colOK}>[OK]</color> Authentication time : {sw.Elapsed.TotalSeconds:F2}s");

                try
                {
                    string rev = (await SvnRunner.RunAsync("info --show-item revision", svnManager.WorkingDir, token: token)).Trim();
                    report.AppendLine($"<color={colOK}>[OK]</color> Current revision : r{rev}");
                }
                catch { /* ignore */ }

                try
                {
                    string branch = (await SvnRunner.RunAsync("info --show-item relative-url", svnManager.WorkingDir, token: token)).Trim();
                    report.AppendLine($"<color={colOK}>[OK]</color> Checked-out branch: {branch}");
                }
                catch { /* ignore */ }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                sw.Stop();
                hadErrors = true;
                report.AppendLine($"<color={colERR}>[ERROR]</color> Authentication / remote access failed: {ex.Message}");
            }
            report.AppendLine();

            // 9 – Working copy
            SVNLogBridge.LogLine("[DIAG] [9/10] Checking working copy...");
            report.AppendLine($"<color={colSTEP}>[9/10] CHECKING WORKING COPY STATE...</color>");
            try
            {
                string status = await SvnRunner.RunAsync("status", svnManager.WorkingDir, token: token);
                var lines = status.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).ToList();

                if (lines.Any(x => x.StartsWith("L")))
                    report.AppendLine($"<color={colWARN}>[WARN]</color> Locked files detected.");
                if (lines.Any(x => x.StartsWith("C")))
                    report.AppendLine($"<color={colWARN}>[WARN]</color> Conflicts detected.");
                if (lines.Any(x => x.StartsWith("!")))
                    report.AppendLine($"<color={colWARN}>[WARN]</color> Missing files detected.");

                if (!lines.Any(x => x.StartsWith("L") || x.StartsWith("C") || x.StartsWith("!")))
                    report.AppendLine($"<color={colOK}>[OK]</color> Working copy is healthy.");
            }
            catch (Exception ex)
            {
                report.AppendLine($"<color={colWARN}>[WARN]</color> Could not check working copy state: {ex.Message}");
            }
            report.AppendLine();

            // 10 – Speed
            SVNLogBridge.LogLine("[DIAG] [10/10] Measuring response speed...");
            report.AppendLine($"<color={colSTEP}>[10/10] TESTING REPOSITORY RESPONSE SPEED...</color>");
            sw.Restart();
            try
            {
                string logOutput = await SvnRunner.RunAsync("log -l 5 --quiet", svnManager.WorkingDir, token: token);
                sw.Stop();
                int count = logOutput
                    .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Count(l => l.StartsWith("r"));
                report.AppendLine($"<color={colOK}>[OK]</color> Fetched {count} revisions in {sw.Elapsed.TotalSeconds:F2}s.");
            }
            catch (Exception ex)
            {
                report.AppendLine($"<color={colWARN}>[WARN]</color> Speed test failed: {ex.Message}");
            }
            report.AppendLine();

            report.AppendLine("====================================");
            report.AppendLine(" DIAGNOSTICS COMPLETE");
            report.AppendLine("====================================");
            report.AppendLine(hadErrors
                ? "VERDICT: FAILED – Review the errors above."
                : "VERDICT: HEALTHY – All tests passed successfully.");
            report.AppendLine($"Session Token: {svnManager.SessionToken}");

            ShowReport(report.ToString());
        }

        private void ShowReport(string reportText)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                foreach (var line in reportText.Split('\n'))
                {
                    string trimmed = line.TrimEnd('\r');
                    if (!string.IsNullOrWhiteSpace(trimmed))
                        SVNLogBridge.LogLine(trimmed, append: true, "DIAG");
                }
                SVNLogBridge.FlushImmediate();
            });
        }

        // === FIX Ś1: przez dispatcher — RefreshWindowsShellIcons bywa wołane z
        // puli wątków (SVNResolve po ConfigureAwait(false)).
        private void LogBoth(string msg)
        {
            UnityMainThreadDispatcher.Enqueue(() =>
            {
                SVNLogBridge.LogLine(msg);
                if (svnUI?.ResolveLogConsole != null)
                    SVNLogBridge.UpdateUIField(svnUI.ResolveLogConsole, msg, "RESOLVE", true);
            });
        }

        public async void ExportRevision(string revision, string relativePath = "")
        {
            try
            {
                await ExportRevisionAsync(revision, relativePath);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Export Critical Error:</color> {ex.Message}");
            }
        }

        public async Task ExportRevisionAsync(string revision, string relativePath = "")
        {
            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Working Directory is not set or does not exist!");
                return;
            }

            if (string.IsNullOrWhiteSpace(revision))
            {
                SVNLogBridge.LogLine("<color=yellow>Warning:</color> No revision specified for export.");
                return;
            }

            // === FIX Ś3: walidacja rewizji (public API — wcześniej szła wprost do
            // komendy; SVNRevision walidował, ale moduł sam nie).
            string rev = revision.Trim().TrimStart('r', 'R');
            if (!System.Text.RegularExpressions.Regex.IsMatch(rev, @"^\d+(:\d+)?$"))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Invalid revision format (expected: 150 or 140:150).");
                return;
            }

            string[] paths = StandaloneFileBrowser.OpenFolderPanel("Select Export Destination Directory", root, false);
            if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0]))
            {
                SVNLogBridge.LogLine("Export revision canceled.");
                return;
            }

            string exportFolder = NormalizePath(paths[0]);
            string sourcePath = string.IsNullOrEmpty(relativePath)
                ? root
                : NormalizePath(Path.Combine(root, relativePath));

            try
            {
                SVNLogBridge.LogLine($"<color=green>Exporting</color> revision r{rev} to: {exportFolder}...");

                string cmd =
                    $"export -r {rev} \"{EscapeSvnArg(sourcePath)}\" \"{EscapeSvnArg(exportFolder)}\" --force";

                await SvnRunner.RunAsync(cmd, root);

                SVNLogBridge.LogLine(
                    $"<color=green>Export Success:</color> Revision r{rev} exported successfully to {exportFolder}");

                OpenFolderInFileManager(exportFolder);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Export Error:</color> {ex.Message}");
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            return path.Replace('\\', '/').TrimEnd('/');
        }

        private static string EscapeSvnArg(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return arg;
            return arg.Replace('\\', '/').Replace("\"", "\\\"");
        }

        public void Dispose()
        {
            // === FIX: atomowy wzorzec _disposed (spójny z resztą modułów).
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _processingLock.Dispose(); } catch { }
        }
    }
}
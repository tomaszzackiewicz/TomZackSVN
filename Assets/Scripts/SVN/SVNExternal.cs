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
    public class SVNExternal : SVNBase
    {
        private readonly SemaphoreSlim _processingLock = new SemaphoreSlim(1, 1);

        public SVNExternal(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        // ═══════════════ EXPLORER ═══════════════
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
                using var process = Process.Start(new ProcessStartInfo("explorer.exe", root.Replace('/', '\\')) { UseShellExecute = true });
                SVNLogBridge.LogLine($"<color=green>Explorer:</color> Opened {root}");
            }
            catch (Exception ex) { SVNLogBridge.LogLine($"<color=#FFAA00>Explorer Error:</color> {ex.Message}"); }
        }

        public void OpenInExplorerAndSelect(string relativePath)
        {
            try
            {
                string root = svnManager.WorkingDir;
                if (string.IsNullOrEmpty(root)) return;
                string fullPath = Path.Combine(root, relativePath).Replace('/', '\\');
                if (File.Exists(fullPath) || Directory.Exists(fullPath))
                {
                    using var process = Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
                }
                else
                {
                    using var process = Process.Start(new ProcessStartInfo("explorer.exe", root.Replace('/', '\\')) { UseShellExecute = true });
                }
            }
            catch (Exception ex) { SVNLogBridge.LogLine($"<color=#FFAA00>Explorer Error:</color> {ex.Message}"); }
        }

        // ═══════════════ DIFF ═══════════════
        public async void ShowChangesForSelected(string relativePath)
        {
            try { await ShowChangesForSelectedAsync(relativePath); }
            catch (Exception ex) { SVNLogBridge.LogLine($"<color=#FFAA00>Diff Critical Error:</color> {ex.Message}"); }
        }

        private async Task ShowChangesForSelectedAsync(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) { SVNLogBridge.LogLine("<color=yellow>Warning:</color> No file selected for Diff."); return; }
            string root = svnManager.WorkingDir;
            string fullPath = Path.Combine(root, relativePath);
            if (!File.Exists(fullPath)) { SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> File not found on disk."); return; }
            try
            {
                SVNLogBridge.LogLine($"Opening Diff for: {relativePath}...");
                await SvnRunner.RunAsync($"diff \"{relativePath}\" --external-diff-cmd TortoiseMerge", root);
            }
            catch (Exception ex) { SVNLogBridge.LogLine($"<color=#FFAA00>Diff Error:</color> {ex.Message}"); }
        }

        // ═══════════════ BROWSERS ═══════════════
        public void BrowseDestinationFolderPathLoad()
        {
            string[] paths = StandaloneFileBrowser.OpenFolderPanel("Select SVN Working Directory", "", false);
            if (paths?.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string p = paths[0].Replace('\\', '/');
                svnManager.WorkingDir = p;
                if (svnUI.LoadDestFolderInput) svnUI.LoadDestFolderInput.text = p;
                _ = svnManager.SetWorkingDirectory(p).ContinueWith(t =>
                {
                    if (t.Exception != null) SVNLogBridge.LogError($"Failed to set working directory: {t.Exception.Message}");
                }, TaskScheduler.FromCurrentSynchronizationContext());
                SVNLogBridge.LogLine($"SVN path selected: {p}");
            }
            else SVNLogBridge.LogLine("Folder selection canceled.");
        }

        public void BrowsePrivateKeyPathLoad()
        {
            var ext = new[] { new ExtensionFilter("All Files", "*"), new ExtensionFilter("Private Key Files", "ppk", "key", "pem", "ssh") };
            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select Private Key File", "", ext, false);
            if (paths?.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string p = paths[0].Replace('\\', '/');
                svnManager.CurrentKey = p;
                if (svnUI.LoadPrivateKeyInput) svnUI.LoadPrivateKeyInput.text = p;
                SVNLogBridge.LogLine($"Private Key path set to: {p}");
            }
            else SVNLogBridge.LogLine("Private Key selection canceled.");
        }

        public void BrowseDestinationFolderPathAdd()
        {
            string[] paths = StandaloneFileBrowser.OpenFolderPanel("Select SVN Working Directory", "", false);
            if (paths?.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string p = paths[0].Replace('\\', '/');
                SVNUI.Instance.AddProjectFolderPathInput.text = p;
                if (string.IsNullOrEmpty(SVNUI.Instance.AddProjectNameInput.text)) SVNUI.Instance.AddProjectNameInput.text = Path.GetFileName(p);
            }
        }

        public void BrowsePrivateKeyPathAdd()
        {
            var ext = new[] { new ExtensionFilter("Private Key Files", "ppk", "key", "pem", "ssh"), new ExtensionFilter("All Files", "*") };
            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select Private Key", "", ext, false);
            if (paths?.Length > 0) SVNUI.Instance.AddProjectKeyPathInput.text = paths[0].Replace('\\', '/');
        }

        public void BrowseDestinationFolderPathCheckout()
        {
            string[] paths = StandaloneFileBrowser.OpenFolderPanel("Select Checkout Destination Directory", "", false);
            if (paths?.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string p = paths[0].Replace('\\', '/');
                if (svnUI.CheckoutDestFolderInput) svnUI.CheckoutDestFolderInput.text = p;
                SVNLogBridge.LogLine($"[Checkout] Destination path set to: {p}");
            }
        }

        public void BrowsePrivateKeyPathCheckout()
        {
            var ext = new[] { new ExtensionFilter("All Files", "*") };
            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select SSH Private Key for Checkout", "", ext, false);
            if (paths?.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string p = paths[0].Replace('\\', '/');
                if (svnUI.CheckoutPrivateKeyInput) svnUI.CheckoutPrivateKeyInput.text = p;
                SVNLogBridge.LogLine($"[Checkout] SSH Key path set to: {p}");
            }
        }

        public void BrowseResolveFilePath()
        {
            string root = svnManager.WorkingDir;
            var ext = new[] { new ExtensionFilter("All Files", "*") };
            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select File to Resolve", root, ext, false);
            if (paths?.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string sel = paths[0].Replace('\\', '/'), normRoot = root.Replace('\\', '/');
                if (sel.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase)) sel = sel.Substring(normRoot.Length).TrimStart('/');
                else SVNLogBridge.LogLine("<color=yellow>Warning:</color> Selected file is outside of the Working Directory!");
                if (svnUI.ResolveTargetFileInput) { svnUI.ResolveTargetFileInput.text = sel; SVNLogBridge.LogLine($"<color=green>Resolve:</color> Selected target file: {sel}"); }
                else SVNLogBridge.LogError("[SVN] ResolveTargetFileInput is not assigned in SVNUI!");
            }
        }

        public void BrowseDiffFilePath()
        {
            string root = svnManager.WorkingDir;
            var ext = new[] { new ExtensionFilter("All Files", "*") };
            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select File to Diff", root, ext, false);
            if (paths?.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string sel = paths[0].Replace('\\', '/'), normRoot = root.Replace('\\', '/');
                if (sel.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase)) sel = sel.Substring(normRoot.Length).TrimStart('/');
                else SVNLogBridge.LogLine("<color=yellow>Warning:</color> Selected file is outside of the Working Directory!");
                if (svnUI.DiffTargetFileInput) { svnUI.DiffTargetFileInput.text = sel; SVNLogBridge.LogLine($"<color=green>Diff:</color> Selected file: {sel}"); }
                else SVNLogBridge.LogError("[SVN] DiffTargetFileInput is not assigned in SVNUI!");
            }
        }

        public void BrowseBlameFilePath()
        {
            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Working Directory is not set or does not exist!"); return; }
            var ext = new[] { new ExtensionFilter("All Files", "*") };
            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select File for Blame", root, ext, false);
            if (paths?.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string sel = paths[0].Replace('\\', '/'), normRoot = root.Replace('\\', '/');
                if (sel.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
                {
                    sel = sel.Substring(normRoot.Length).TrimStart('/');
                    if (svnUI.BlameTargetFileInput) { svnUI.BlameTargetFileInput.text = sel; SVNLogBridge.LogLine($"<color=green>Blame:</color> Target file set to: {sel}"); }
                }
                else SVNLogBridge.LogLine("<color=yellow>Warning:</color> Selected file is outside of the Working Directory!");
            }
        }

        // ═══════════════ TORTOISE / SAVE ═══════════════
        public void OpenTortoiseLog()
        {
            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root)) { SVNLogBridge.LogLine("<color=yellow>Warning:</color> Working directory not set."); return; }
            try
            {
                using var process = Process.Start("TortoiseProc.exe", $"/command:log /path:\"{root}\"");
                SVNLogBridge.LogLine("<b>[External]</b> Opening TortoiseSVN Log...");
            }
            catch (Exception ex) { SVNLogBridge.LogLine($"<color=#FFAA00>TortoiseSVN Error:</color> {ex.Message}"); }
        }

        public void SaveHistoryToFile(string content)
        {
            if (string.IsNullOrEmpty(content)) { SVNLogBridge.LogLine("<color=yellow>Warning:</color> No content to export."); return; }
            string defaultName = $"SVN_History_{DateTime.Now:yyyyMMdd_HHmm}";
            string path = StandaloneFileBrowser.SaveFilePanel("Save SVN History Report", "", defaultName, "txt");
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    File.WriteAllText(path, content);
                    SVNLogBridge.LogLine($"<color=green>Success:</color> History exported to {path}");
                    using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception ex) { SVNLogBridge.LogLine($"<color=#FFAA00>Export Error:</color> {ex.Message}"); }
            }
        }

        // ═══════════════ SHELL / ICONS ═══════════════
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern void SHChangeNotify(long wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
        private const long SHCNE_UPDATEDIR = 0x00001000L;
        private const uint SHCNF_PATHW = 0x0005;

        public void RefreshWindowsShellIcons(string targetPath)
        {
            try
            {
                Process[] procs = Process.GetProcessesByName("TSVNCache");
                foreach (var p in procs) { try { p.Kill(); } catch { } finally { p.Dispose(); } }
                string full = Path.Combine(svnManager.WorkingDir, targetPath);
                string dir = File.Exists(full) ? Path.GetDirectoryName(full) : full;
                if (!string.IsNullOrEmpty(dir))
                {
                    IntPtr ptr = Marshal.StringToHGlobalUni(dir);
                    try { SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, ptr, IntPtr.Zero); }
                    finally { Marshal.FreeHGlobal(ptr); }
                }
                LogBoth("[Shell] Triggered Windows Explorer icon cache update.");
            }
            catch (Exception ex) { LogBoth($"[Shell Error] Failed to refresh icons: {ex.Message}"); }
        }

        // ═══════════════ TEST CONNECTION (Z POSTĘPEM W LOGU) ═══════════════
        public async void TestConnection()
        {
            // Tymczasowo wyświetl informację, że diagnostyka ruszyła
            if (SVNUI.Instance != null && SVNUI.Instance.LogText != null)
                SVNLogBridge.LogLine($"[{DateTime.Now:HH:mm:ss}] [INFO] Starting connection diagnostics...");

            if (!await _processingLock.WaitAsync(0))
            {
                SVNLogBridge.LogLine("[WARN] Another operation is already running. Please wait for it to finish.");
                return;
            }

            IsProcessing = true;

            try
            {
                bool hadErrors = false;
                var report = new System.Text.StringBuilder();

                string colOK = "#00E5FF";
                string colWARN = "#FFCC00";
                string colERR = "#FF5555";
                string colSTEP = "#00008B";

                report.AppendLine($"Session Token: {svnManager.SessionToken}");
                report.AppendLine("====================================");
                report.AppendLine("  CONNECTION DIAGNOSTICS");
                report.AppendLine("====================================");
                report.AppendLine();

                string repoUrl = svnManager.RepositoryUrl;

                if (string.IsNullOrEmpty(repoUrl))
                {
                    report.AppendLine($"<color={colERR}>[ERROR]</color> Repository URL not set.");
                    ShowReport(report.ToString());
                    return;
                }

                string host = "unknown", protocol = "unknown", port = "unknown", repoPath = "unknown", username = "unknown";
                bool validUrl = true;
                int targetPort = 22;

                // --- Krok 0: URL ---
                SVNLogBridge.LogLine("[DIAG] [0/10] Checking repository URL...");
                report.AppendLine($"<color={colSTEP}>[0/10] CHECKING REPOSITORY URL...</color>");
                try
                {
                    var uri = new Uri(repoUrl);
                    host = uri.Host; protocol = uri.Scheme.ToUpper(); repoPath = uri.AbsolutePath.TrimStart('/');
                    username = !string.IsNullOrEmpty(uri.UserInfo) ? uri.UserInfo : (svnManager.CurrentUserName ?? "unknown");
                    if (protocol == "SVN+SSH" || protocol == "SSH") targetPort = 22;
                    else if (protocol == "HTTPS") targetPort = 443; else if (protocol == "HTTP") targetPort = 80; else if (protocol == "SVN") targetPort = 3690;
                    if (!uri.IsDefaultPort) targetPort = uri.Port;
                    port = targetPort.ToString();
                }
                catch (Exception ex) { validUrl = false; hadErrors = true; report.AppendLine($"<color={colERR}>[ERROR]</color> Invalid URL: {ex.Message}"); }

                report.AppendLine($"  Repository URL : {repoUrl}");
                report.AppendLine($"  Protocol       : {protocol}");
                report.AppendLine($"  Host           : {host}");
                report.AppendLine($"  Port           : {port}");
                report.AppendLine($"  Repository Path: {repoPath}");
                report.AppendLine($"  Username       : {username}");
                report.AppendLine();

                if (!validUrl)
                {
                    report.AppendLine("====================================");
                    report.AppendLine("  DIAGNOSTICS ABORTED");
                    report.AppendLine("====================================");
                    ShowReport(report.ToString());
                    return;
                }

                // [1/10] SVN client
                SVNLogBridge.LogLine("[DIAG] [1/10] Checking SVN client...");
                report.AppendLine($"<color={colSTEP}>[1/10] CHECKING SVN CLIENT...</color>");
                try { string ver = await SvnRunner.RunAsync("--version --quiet", svnManager.WorkingDir); report.AppendLine($"<color={colOK}>[OK]</color>   SVN client version : {ver.Trim()}"); }
                catch (Exception ex) { hadErrors = true; report.AppendLine($"<color={colERR}>[ERROR]</color> Unable to detect SVN client: {ex.Message}"); }
                report.AppendLine();

                // [2/10] OpenSSH
                SVNLogBridge.LogLine("[DIAG] [2/10] Checking OpenSSH...");
                report.AppendLine($"<color={colSTEP}>[2/10] CHECKING OPENSSH CLIENT...</color>");
                try
                {
                    var psi = new ProcessStartInfo { FileName = "ssh", Arguments = "-V", RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                    using var sshProc = Process.Start(psi);
                    if (sshProc != null) { string ver = await sshProc.StandardError.ReadToEndAsync(); report.AppendLine($"<color={colOK}>[OK]</color>   OpenSSH version  : {ver.Trim()}"); }
                }
                catch (Exception ex) { report.AppendLine($"<color={colWARN}>[WARN]</color> Could not detect OpenSSH version: {ex.Message}"); }
                report.AppendLine();

                // [3/10] SSH key
                SVNLogBridge.LogLine("[DIAG] [3/10] Checking SSH key...");
                report.AppendLine($"<color={colSTEP}>[3/10] CHECKING SSH KEY...</color>");
                string keyPath = SvnRunner.KeyPath;
                if (!string.IsNullOrEmpty(keyPath))
                {
                    string clean = keyPath.Replace("\"", "").Trim().Replace("\\", "/");
                    if (File.Exists(clean))
                    {
                        report.AppendLine($"<color={colOK}>[OK]</color>   Key file exists   : {clean}");
                        try { var fi = new FileInfo(clean); report.AppendLine($"<color={colOK}>[OK]</color>   Key file size     : {fi.Length} bytes"); report.AppendLine($"<color={colOK}>[OK]</color>   Key file modified : {fi.LastWriteTime}"); } catch { }
                    }
                    else { hadErrors = true; report.AppendLine($"<color={colERR}>[ERROR]</color> Key file not found at path: {clean}"); }
                }
                else report.AppendLine($"<color={colWARN}>[WARN]</color> No SSH key configured.");
                report.AppendLine();

                // [4/10] DNS
                SVNLogBridge.LogLine("[DIAG] [4/10] Testing DNS resolution...");
                report.AppendLine($"<color={colSTEP}>[4/10] TESTING DNS RESOLUTION...</color>");
                try { var addrs = await System.Net.Dns.GetHostAddressesAsync(host); foreach (var a in addrs) report.AppendLine($"<color={colOK}>[OK]</color>   DNS resolved → {a}"); }
                catch (Exception ex) { hadErrors = true; report.AppendLine($"<color={colERR}>[ERROR]</color> DNS resolution failed: {ex.Message}"); }
                report.AppendLine();

                // [5/10] Ping
                SVNLogBridge.LogLine("[DIAG] [5/10] Pinging host...");
                report.AppendLine($"<color={colSTEP}>[5/10] TESTING HOST REACHABILITY (ICMP)...</color>");
                try
                {
                    using var ping = new System.Net.NetworkInformation.Ping();
                    var reply = await ping.SendPingAsync(host, 3000);
                    if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                        report.AppendLine($"<color={colOK}>[OK]</color>   Host reachable  : {reply.Address} (response time: {reply.RoundtripTime}ms)");
                    else report.AppendLine($"<color=black>[INFO]</color> Ping blocked – ICMP may be disabled on this host.");
                }
                catch (Exception ex) { report.AppendLine($"<color=black>[INFO]</color> Ping unavailable: {ex.Message}"); }
                report.AppendLine();

                // [6/10] TCP
                SVNLogBridge.LogLine("[DIAG] [6/10] Testing TCP port...");
                report.AppendLine($"<color={colSTEP}>[6/10] TESTING TCP PORT {targetPort}...</color>");
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    using var delayCts = new CancellationTokenSource();
                    var connectTask = client.ConnectAsync(host, targetPort);
                    var delayTask = Task.Delay(5000, delayCts.Token);
                    var completed = await Task.WhenAny(connectTask, delayTask);
                    if (completed == connectTask && client.Connected) { delayCts.Cancel(); report.AppendLine($"<color={colOK}>[OK]</color>   TCP port {targetPort} is open and reachable."); }
                    else { client.Close(); hadErrors = true; report.AppendLine($"<color={colERR}>[ERROR]</color> TCP port {targetPort} timed out – service may be down or firewalled."); }
                }
                catch (Exception ex) { hadErrors = true; report.AppendLine($"<color={colERR}>[ERROR]</color> TCP test failed: {ex.Message}"); }
                report.AppendLine();

                // [7/10] SSH handshake – corrected
                SVNLogBridge.LogLine("[DIAG] [7/10] Testing SSH connection...");
                report.AppendLine($"<color={colSTEP}>[7/10] TESTING DIRECT SSH CONNECTION...</color>");
                if (!string.IsNullOrEmpty(keyPath))
                {
                    try
                    {
                        string cleanKey = keyPath.Replace("\"", "").Trim().Replace("\\", "/");
                        if (File.Exists(cleanKey))
                        {
                            string sshArgs = $"-T -i \"{cleanKey}\" -o BatchMode=yes -o StrictHostKeyChecking=no -o ConnectTimeout=10 {username}@{host}";
                            var psi = new ProcessStartInfo { FileName = "ssh", Arguments = sshArgs, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                            using var sshProc = Process.Start(psi);
                            if (sshProc != null)
                            {
                                bool exited = await Task.Run(() => sshProc.WaitForExit(10000));
                                if (!exited) { try { sshProc.Kill(); } catch { } report.AppendLine($"<color={colWARN}>[WARN]</color> SSH handshake timed out after 10 seconds."); }
                                else
                                {
                                    string error = await sshProc.StandardError.ReadToEndAsync();
                                    if (sshProc.ExitCode == 0)
                                        report.AppendLine($"<color={colOK}>[OK]</color>   SSH connection successfully established.");
                                    else if (sshProc.ExitCode == 1)
                                    {
                                        if (error.Contains("Permission denied") || error.Contains("Authentication failed"))
                                        { hadErrors = true; report.AppendLine($"<color={colERR}>[ERROR]</color> SSH connection failed: {error}"); }
                                        else report.AppendLine($"<color={colOK}>[OK]</color>   SSH connection established (warnings ignored).");
                                    }
                                    else { hadErrors = true; report.AppendLine($"<color={colERR}>[ERROR]</color> SSH connection failed with exit code {sshProc.ExitCode} - {error}"); }
                                }
                            }
                        }
                    }
                    catch (Exception ex) { hadErrors = true; report.AppendLine($"<color={colERR}>[ERROR]</color> SSH test failed: {ex.Message}"); }
                }
                report.AppendLine();

                // [8/10] SVN auth
                SVNLogBridge.LogLine("[DIAG] [8/10] Authenticating to repository...");
                report.AppendLine($"<color={colSTEP}>[8/10] TESTING SVN AUTHENTICATION...</color>");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    string uuid = await SvnRunner.RunAsync("info --show-item repos-uuid", svnManager.WorkingDir);
                    sw.Stop();
                    report.AppendLine($"<color={colOK}>[OK]</color>   Repository UUID  : {uuid.Trim()}");
                    report.AppendLine($"<color={colOK}>[OK]</color>   Authentication time : {sw.Elapsed.TotalSeconds:F2}s");
                    try { report.AppendLine($"<color={colOK}>[OK]</color>   Current revision  : r{(await SvnRunner.RunAsync("info --show-item revision", svnManager.WorkingDir)).Trim()}"); } catch { }
                    try { report.AppendLine($"<color={colOK}>[OK]</color>   Checked‑out branch: {(await SvnRunner.RunAsync("info --show-item relative-url", svnManager.WorkingDir)).Trim()}"); } catch { }
                }
                catch (Exception ex) { sw.Stop(); hadErrors = true; report.AppendLine($"<color={colERR}>[ERROR]</color> Authentication failed: {ex.Message}"); }
                report.AppendLine();

                // [9/10] Working copy
                SVNLogBridge.LogLine("[DIAG] [9/10] Checking working copy...");
                report.AppendLine($"<color={colSTEP}>[9/10] CHECKING WORKING COPY STATE...</color>");
                try
                {
                    string status = await SvnRunner.RunAsync("status", svnManager.WorkingDir);
                    var lines = status.Split('\n', '\r').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                    if (lines.Any(x => x.StartsWith("L"))) report.AppendLine($"<color={colWARN}>[WARN]</color> Locked files detected.");
                    if (lines.Any(x => x.StartsWith("C"))) report.AppendLine($"<color={colWARN}>[WARN]</color> Conflicts detected.");
                    if (lines.Any(x => x.StartsWith("!"))) report.AppendLine($"<color={colWARN}>[WARN]</color> Missing files detected.");
                    if (!lines.Any(x => x.StartsWith("L") || x.StartsWith("C") || x.StartsWith("!"))) report.AppendLine($"<color={colOK}>[OK]</color>   Working copy is healthy.");
                }
                catch (Exception ex) { report.AppendLine($"<color={colWARN}>[WARN]</color> Could not check working copy state: {ex.Message}"); }
                report.AppendLine();

                // [10/10] Speed
                SVNLogBridge.LogLine("[DIAG] [10/10] Measuring response speed...");
                report.AppendLine($"<color={colSTEP}>[10/10] TESTING REPOSITORY RESPONSE SPEED...</color>");
                sw.Restart();
                try
                {
                    string logOutput = await SvnRunner.RunAsync("log -l 5 --quiet", svnManager.WorkingDir);
                    sw.Stop();
                    int count = logOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Count(l => l.StartsWith("r"));
                    report.AppendLine($"<color={colOK}>[OK]</color>   Fetched {count} revisions in {sw.Elapsed.TotalSeconds:F2}s.");
                }
                catch (Exception ex) { report.AppendLine($"<color={colWARN}>[WARN]</color> Speed test failed: {ex.Message}"); }
                report.AppendLine();

                report.AppendLine("====================================");
                report.AppendLine("  DIAGNOSTICS COMPLETE");
                report.AppendLine("====================================");
                report.AppendLine(hadErrors ? "VERDICT: FAILED – Review the errors above." : "VERDICT: HEALTHY – All tests passed successfully.");
                report.AppendLine($"Session Token: {svnManager.SessionToken}");

                ShowReport(report.ToString());
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FF5555>[ERROR]</color> Diagnostics crashed: {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
                _processingLock.Release();
            }
        }

        private void ShowReport(string reportText)
        {
            foreach (var line in reportText.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (!string.IsNullOrWhiteSpace(trimmed))
                    SVNLogBridge.LogLine(trimmed, append: true, "DIAG");
            }
            SVNLogBridge.FlushImmediate();
        }

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            SVNLogBridge.UpdateUIField(svnUI.ResolveLogConsole, msg, "RESOLVE", true);
        }
    }
}
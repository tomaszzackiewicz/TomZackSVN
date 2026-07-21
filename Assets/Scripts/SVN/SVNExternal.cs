using SFB;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNExternal : SVNBase
    {
        public SVNExternal(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public void OpenInExplorer()
        {
            try
            {
                string root = svnManager.WorkingDir;

                if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root))
                {
                    SVNLogBridge.LogLine("<color=#FFAA00>Error: Working directory is not set or does not exist!</color>");
                    return;
                }

                System.Diagnostics.Process.Start("explorer.exe", root.Replace('/', '\\'));
                SVNLogBridge.LogLine($"<color=green>Explorer:</color> Opened {root}");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Explorer Error:</color> {ex.Message}");
            }
        }

        public async void ShowChangesForSelected(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath))
            {
                SVNLogBridge.LogLine("<color=yellow>Warning:</color> No file selected for Diff.");
                return;
            }

            string root = svnManager.WorkingDir;
            string fullPath = System.IO.Path.Combine(root, relativePath);

            if (!System.IO.File.Exists(fullPath))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> File not found on disk.");
                return;
            }

            try
            {
                SVNLogBridge.LogLine($"Opening Diff for: {relativePath}...");
                await SvnRunner.RunAsync($"diff \"{relativePath}\" --external-diff-cmd TortoiseMerge", root);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Diff Error:</color> {ex.Message}");
            }
        }

        public void BrowseDestinationFolderPathLoad()
        {
            string[] paths = StandaloneFileBrowser.OpenFolderPanel("Select SVN Working Directory", "", false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');
                svnManager.WorkingDir = selectedPath;

                if (svnUI.LoadDestFolderInput != null)
                    svnUI.LoadDestFolderInput.text = selectedPath;

                _ = svnManager.SetWorkingDirectory(selectedPath).ContinueWith(task =>
                {
                    if (task.Exception != null)
                    {
                        SVNLogBridge.LogError($"Failed to set working directory: {task.Exception.Message}");
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());

                SVNLogBridge.LogLine($"SVN path selected: {selectedPath}");
            }
            else
            {
                SVNLogBridge.LogLine("Folder selection canceled.");
            }
        }

        public void BrowsePrivateKeyPathLoad()
        {
            var extensions = new[] {
                new ExtensionFilter("All Files", "*"),
                new ExtensionFilter("Private Key Files", "ppk", "key", "pem", "ssh")
            };

            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select Private Key File", "", extensions, false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');
                svnManager.CurrentKey = selectedPath;

                if (svnUI.LoadPrivateKeyInput != null)
                {
                    svnUI.LoadPrivateKeyInput.text = selectedPath;
                }

                SVNLogBridge.LogLine($"Private Key path set to: {selectedPath}");
            }
            else
            {
                SVNLogBridge.LogLine("Private Key selection canceled by user.");
            }
        }

        public void BrowseDestinationFolderPathAdd()
        {
            string[] paths = StandaloneFileBrowser.OpenFolderPanel("Select SVN Working Directory", "", false);
            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string path = paths[0].Replace('\\', '/');
                SVNUI.Instance.AddProjectFolderPathInput.text = path;

                if (string.IsNullOrEmpty(SVNUI.Instance.AddProjectNameInput.text))
                {
                    SVNUI.Instance.AddProjectNameInput.text = System.IO.Path.GetFileName(path);
                }
            }
        }

        public void BrowsePrivateKeyPathAdd()
        {
            var extensions = new[] {
                new ExtensionFilter("Private Key Files", "ppk", "key", "pem", "ssh"),
                new ExtensionFilter("All Files", "*")
            };
            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select Private Key", "", extensions, false);
            if (paths != null && paths.Length > 0)
            {
                SVNUI.Instance.AddProjectKeyPathInput.text = paths[0].Replace('\\', '/');
            }
        }

        public void BrowseDestinationFolderPathCheckout()
        {
            string[] paths = StandaloneFileBrowser.OpenFolderPanel("Select Checkout Destination Directory", "", false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');

                if (svnUI.CheckoutDestFolderInput != null)
                {
                    svnUI.CheckoutDestFolderInput.text = selectedPath;
                }

                SVNLogBridge.LogLine($"[Checkout] Destination path set to: {selectedPath}");
            }
        }

        public void BrowsePrivateKeyPathCheckout()
        {
            var extensions = new[] {
                new ExtensionFilter("All Files", "*"),
            };

            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select SSH Private Key for Checkout", "", extensions, false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');

                if (svnUI.CheckoutPrivateKeyInput != null)
                {
                    svnUI.CheckoutPrivateKeyInput.text = selectedPath;
                }

                SVNLogBridge.LogLine($"[Checkout] SSH Key path set to: {selectedPath}");
            }
        }

        public void BrowseResolveFilePath()
        {
            string root = svnManager.WorkingDir;

            var extensions = new[] { new ExtensionFilter("All Files", "*") };

            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select File to Resolve", root, extensions, false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');

                if (selectedPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    selectedPath = selectedPath.Substring(root.Length).TrimStart('/');
                }
                else
                {
                    SVNLogBridge.LogLine("<color=yellow>Warning:</color> Selected file is outside of the Working Directory!", true);
                }

                if (svnUI.ResolveTargetFileInput != null)
                {
                    svnUI.ResolveTargetFileInput.text = selectedPath;
                    SVNLogBridge.LogLine($"<color=green>Resolve:</color> Selected target file: {selectedPath}");
                }
                else
                {
                    SVNLogBridge.LogError("[SVN] ResolveTargetFileInput is not assigned in SVNUI!");
                }
            }
        }

        public void BrowseDiffFilePath()
        {
            string root = svnManager.WorkingDir;

            var extensions = new[] { new ExtensionFilter("All Files", "*") };

            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select File to Diff", root, extensions, false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');
                string normalizedRoot = root.Replace('\\', '/');

                if (selectedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    selectedPath = selectedPath.Substring(normalizedRoot.Length).TrimStart('/');
                }
                else
                {
                    SVNLogBridge.LogLine("<color=yellow>Warning:</color> Selected file is outside of the Working Directory!", true);
                }

                if (svnUI.DiffTargetFileInput != null)
                {
                    svnUI.DiffTargetFileInput.text = selectedPath;
                    SVNLogBridge.LogLine($"<color=green>Diff:</color> Selected file: {selectedPath}");
                }
                else
                {
                    SVNLogBridge.LogError("[SVN] DiffTargetFileInput is not assigned in SVNUI!");
                }
            }
        }

        public void BrowseBlameFilePath()
        {
            string root = svnManager.WorkingDir;

            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Working Directory is not set or does not exist!");
                return;
            }

            var extensions = new[] {
        new ExtensionFilter("All Files", "*")
    };

            string[] paths = StandaloneFileBrowser.OpenFilePanel("Select File for Blame", root, extensions, false);

            if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
            {
                string selectedPath = paths[0].Replace('\\', '/');
                string normalizedRoot = root.Replace('\\', '/');

                if (selectedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    selectedPath = selectedPath.Substring(normalizedRoot.Length).TrimStart('/');

                    if (svnUI.BlameTargetFileInput != null)
                    {
                        svnUI.BlameTargetFileInput.text = selectedPath;
                        SVNLogBridge.LogLine($"<color=green>Blame:</color> Target file set to: {selectedPath}");
                    }
                }
                else
                {
                    SVNLogBridge.LogLine("<color=yellow>Warning:</color> Selected file is outside of the Working Directory!");
                }
            }
        }

        public void OpenTortoiseLog()
        {
            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root))
            {
                SVNLogBridge.LogLine("<color=yellow>Warning:</color> Working directory not set.");
                return;
            }

            string args = $"/command:log /path:\"{root}\"";
            System.Diagnostics.Process.Start("TortoiseProc.exe", args);
            SVNLogBridge.LogLine("<b>[External]</b> Opening TortoiseSVN SVNLogBridge.LogLine...");
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

            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    System.IO.File.WriteAllText(path, content);
                    SVNLogBridge.LogLine($"<color=green>Success:</color> History exported to {path}");

                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    SVNLogBridge.LogLine($"<color=#FFAA00>Export Error:</color> {ex.Message}");
                }
            }
        }

        public void OpenInExplorerAndSelect(string relativePath)
        {
            try
            {
                string root = svnManager.WorkingDir;
                if (string.IsNullOrEmpty(root)) return;

                string fullPath = System.IO.Path.Combine(root, relativePath).Replace('/', '\\');

                if (System.IO.File.Exists(fullPath) || System.IO.Directory.Exists(fullPath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
                }
                else
                {
                    System.Diagnostics.Process.Start("explorer.exe", root.Replace('/', '\\'));
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FFAA00>Explorer Error:</color> {ex.Message}");
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern void SHChangeNotify(long wEventId, uint uFlags, string dwItem1, string dwItem2);

        private const long SHCNE_UPDATEDIR = 0x00001000L;
        private const uint SHCNF_PATHW = 0x0005;

        public void RefreshWindowsShellIcons(string targetPath)
        {
            try
            {
                Process[] cacheProcesses = Process.GetProcessesByName("TSVNCache");
                foreach (var process in cacheProcesses)
                {
                    process.Kill();
                }

                string fullPath = Path.Combine(svnManager.WorkingDir, targetPath);
                string directoryToRefresh = File.Exists(fullPath) ? Path.GetDirectoryName(fullPath) : fullPath;

                if (!string.IsNullOrEmpty(directoryToRefresh))
                {
                    SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, directoryToRefresh, null);
                }

                LogBoth("[Shell] Triggered Windows Explorer icon cache update.");
            }
            catch (Exception ex)
            {
                LogBoth($"[Shell Error] Failed to refresh icons: {ex.Message}");
            }
        }

        public async void TestConnection()
        {
            if (SVNUI.Instance != null && SVNUI.Instance.LogText != null)
                SVNUI.Instance.LogText.text = $"[{DateTime.Now:HH:mm:ss}] [INFO] Starting connection diagnostics...\n";

            if (IsProcessing)
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
                    SVNLogBridge.UpdateUIField(SVNUI.Instance.LogText, report.ToString(), "DIAG", append: false);
                    return;
                }

                string host = "unknown";
                string protocol = "unknown";
                string port = "unknown";
                string repoPath = "unknown";
                string username = "unknown";
                bool validUrl = true;
                int targetPort = 22;

                report.AppendLine($"<color={colSTEP}>[0/10] CHECKING REPOSITORY URL...</color>");
                report.AppendLine("  Verifying that the provided repository URL is syntactically correct");
                report.AppendLine("  and uses a supported protocol (SVN+SSH is expected).");

                try
                {
                    var uri = new Uri(repoUrl);
                    host = uri.Host;
                    protocol = uri.Scheme.ToUpper();
                    repoPath = uri.AbsolutePath.TrimStart('/');
                    username = !string.IsNullOrEmpty(uri.UserInfo)
                                   ? uri.UserInfo
                                   : (svnManager.CurrentUserName ?? "unknown");

                    if (protocol == "SVN+SSH" || protocol == "SSH") targetPort = 22;
                    else if (protocol == "HTTPS") targetPort = 443;
                    else if (protocol == "HTTP") targetPort = 80;
                    else if (protocol == "SVN") targetPort = 3690;
                    if (!uri.IsDefaultPort) targetPort = uri.Port;
                    port = targetPort.ToString();

                    string[] supportedProtocols = { "SVN+SSH", "HTTP", "HTTPS", "SVN" };
                    if (!supportedProtocols.Contains(protocol))
                        report.AppendLine($"<color={colWARN}>[WARN]</color> Unrecognized protocol: {protocol}");
                    else if (protocol != "SVN+SSH")
                        report.AppendLine($"<color={colWARN}>[WARN]</color> Protocol {protocol} detected. SVN+SSH is recommended.");
                    else
                        report.AppendLine($"<color={colOK}>[OK]</color>   Protocol: SVN+SSH – supported and active.");
                }
                catch (Exception ex)
                {
                    validUrl = false;
                    hadErrors = true;
                    report.AppendLine($"<color={colERR}>[ERROR]</color> Invalid URL: {ex.Message}");
                }

                report.AppendLine();
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
                    SVNLogBridge.UpdateUIField(SVNUI.Instance.LogText, report.ToString(), "DIAG", append: false);
                    return;
                }

                report.AppendLine($"<color={colSTEP}>[1/10] CHECKING SVN CLIENT...</color>");
                report.AppendLine("  Querying the local SVN command‑line client for its version number.");
                try
                {
                    SVNLogBridge.LogLine("<color=#55FF55>[RUNNING] Querying SVN client version...</color>");
                    string version = await SvnRunner.RunAsync("--version --quiet", svnManager.WorkingDir);
                    report.AppendLine($"<color={colOK}>[OK]</color>   SVN client version : {version.Trim()}");
                }
                catch (Exception ex)
                {
                    hadErrors = true;
                    report.AppendLine($"<color={colERR}>[ERROR]</color> Unable to detect SVN client: {ex.Message}");
                }
                report.AppendLine();

                report.AppendLine($"<color={colSTEP}>[2/10] CHECKING OPENSSH CLIENT...</color>");
                report.AppendLine("  Verifying that an OpenSSH client is installed and can be invoked.");
                report.AppendLine("  This is required for SVN+SSH connections.");
                try
                {
                    SVNLogBridge.LogLine("<color=#55FF55>[RUNNING] Detecting OpenSSH version...</color>");
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
                        string sshVersion = await sshProc.StandardError.ReadToEndAsync();
                        report.AppendLine($"<color={colOK}>[OK]</color>   OpenSSH version  : {sshVersion.Trim()}");
                    }
                }
                catch (Exception ex)
                {
                    report.AppendLine($"<color={colWARN}>[WARN]</color> Could not detect OpenSSH version: {ex.Message}");
                }
                report.AppendLine();

                report.AppendLine($"<color={colSTEP}>[3/10] CHECKING SSH KEY...</color>");
                report.AppendLine("  Looking for the private SSH key that will be used for authentication.");
                string keyPath = SvnRunner.KeyPath;
                if (!string.IsNullOrEmpty(keyPath))
                {
                    string cleanPath = keyPath.Replace("\"", "").Trim().Replace("\\", "/");
                    if (File.Exists(cleanPath))
                    {
                        report.AppendLine($"<color={colOK}>[OK]</color>   Key file exists   : {cleanPath}");
                        try
                        {
                            var info = new FileInfo(cleanPath);
                            report.AppendLine($"<color={colOK}>[OK]</color>   Key file size     : {info.Length} bytes");
                            report.AppendLine($"<color={colOK}>[OK]</color>   Key file modified : {info.LastWriteTime}");
                        }
                        catch { }
                    }
                    else
                    {
                        hadErrors = true;
                        report.AppendLine($"<color={colERR}>[ERROR]</color> Key file not found at path: {cleanPath}");
                    }
                }
                else
                {
                    report.AppendLine($"<color={colWARN}>[WARN]</color> No SSH key has been configured.");
                }
                report.AppendLine();

                report.AppendLine($"<color={colSTEP}>[4/10] TESTING DNS RESOLUTION...</color>");
                report.AppendLine("  Translating the hostname into an IP address using system DNS.");
                try
                {
                    SVNLogBridge.LogLine("<color=#55FF55>[RUNNING] Resolving hostname...</color>");
                    var addresses = await System.Net.Dns.GetHostAddressesAsync(host);
                    foreach (var address in addresses)
                        report.AppendLine($"<color={colOK}>[OK]</color>   DNS resolved → {address}");
                }
                catch (Exception ex)
                {
                    hadErrors = true;
                    report.AppendLine($"<color={colERR}>[ERROR]</color> DNS resolution failed: {ex.Message}");
                }
                report.AppendLine();

                report.AppendLine($"<color={colSTEP}>[5/10] TESTING HOST REACHABILITY (ICMP)...</color>");
                report.AppendLine("  Sending an ICMP Echo Request (ping) to the host.");
                report.AppendLine("  A failure here is common – many servers block ICMP for security.");
                try
                {
                    SVNLogBridge.LogLine("<color=#55FF55>[RUNNING] Pinging host...</color>");
                    var ping = new System.Net.NetworkInformation.Ping();
                    var reply = await ping.SendPingAsync(host, 3000);
                    if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                        report.AppendLine($"<color={colOK}>[OK]</color>   Host reachable  : {reply.Address} (response time: {reply.RoundtripTime}ms)");
                    else
                        report.AppendLine($"<color=black>[INFO]</color> Ping blocked – ICMP may be disabled on this host.");
                }
                catch (Exception ex)
                {
                    report.AppendLine($"<color=black>[INFO]</color> Ping unavailable: {ex.Message}");
                }
                report.AppendLine();

                report.AppendLine($"<color={colSTEP}>[6/10] TESTING TCP PORT {targetPort}...</color>");
                report.AppendLine($"  Attempting to establish a raw TCP connection to port {targetPort}.");
                report.AppendLine("  This confirms that the server is reachable and the port is open.");
                try
                {
                    SVNLogBridge.LogLine("<color=#55FF55>[RUNNING] Connecting to remote port...</color>");
                    using var client = new System.Net.Sockets.TcpClient();
                    var connectTask = client.ConnectAsync(host, targetPort);
                    var completed = await Task.WhenAny(connectTask, Task.Delay(5000));
                    if (completed == connectTask && client.Connected)
                        report.AppendLine($"<color={colOK}>[OK]</color>   TCP port {targetPort} is open and reachable.");
                    else
                    {
                        hadErrors = true;
                        report.AppendLine($"<color={colERR}>[ERROR]</color> TCP port {targetPort} timed out – service may be down or firewalled.");
                    }
                }
                catch (Exception ex)
                {
                    hadErrors = true;
                    report.AppendLine($"<color={colERR}>[ERROR]</color> TCP test failed: {ex.Message}");
                }
                report.AppendLine();

                report.AppendLine($"<color={colSTEP}>[7/10] TESTING DIRECT SSH CONNECTION...</color>");
                report.AppendLine("  Performing an SSH handshake (without executing any remote command)");
                report.AppendLine("  to verify that the key is accepted and authentication works.");
                if (!string.IsNullOrEmpty(keyPath))
                {
                    try
                    {
                        string cleanKeyPath = keyPath.Replace("\"", "").Trim().Replace("\\", "/");
                        if (File.Exists(cleanKeyPath))
                        {
                            SVNLogBridge.LogLine("<color=#55FF55>[RUNNING] Attempting SSH handshake (timeout 10s)...</color>");
                            report.AppendLine("  Attempting SSH handshake (timeout 10s)...");
                            string sshArgs = $"-T -i \"{cleanKeyPath}\" -o BatchMode=yes -o StrictHostKeyChecking=no -o ConnectTimeout=10 {username}@{host}";
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
                                var exitTask = Task.Run(() => sshProc.WaitForExit());
                                if (await Task.WhenAny(exitTask, Task.Delay(10000)) != exitTask)
                                {
                                    try { sshProc.Kill(); } catch { }
                                    report.AppendLine($"<color={colWARN}>[WARN]</color> SSH handshake timed out after 10 seconds.");
                                }
                                else
                                {
                                    string output = await sshProc.StandardOutput.ReadToEndAsync();
                                    string error = await sshProc.StandardError.ReadToEndAsync();
                                    if (sshProc.ExitCode == 0 || (sshProc.ExitCode == 1 && !error.Contains("Permission denied") && !error.Contains("Authentication failed")))
                                        report.AppendLine($"<color={colOK}>[OK]</color>   SSH connection successfully established.");
                                    else
                                    {
                                        hadErrors = true;
                                        report.AppendLine($"<color={colERR}>[ERROR]</color> SSH connection failed: {error}");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        hadErrors = true;
                        report.AppendLine($"<color={colERR}>[ERROR]</color> SSH test failed: {ex.Message}");
                    }
                }
                report.AppendLine();

                report.AppendLine($"<color={colSTEP}>[8/10] TESTING SVN AUTHENTICATION...</color>");
                report.AppendLine("  Connecting to the repository via SVN and retrieving its UUID.");
                report.AppendLine("  This confirms that your credentials are valid.");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    SVNLogBridge.LogLine("<color=#55FF55>[RUNNING] Authenticating with repository...</color>");
                    string uuid = await SvnRunner.RunAsync("info --show-item repos-uuid", svnManager.WorkingDir);
                    sw.Stop();
                    report.AppendLine($"<color={colOK}>[OK]</color>   Repository UUID  : {uuid.Trim()}");
                    report.AppendLine($"<color={colOK}>[OK]</color>   Authentication time : {sw.Elapsed.TotalSeconds:F2}s");

                    try
                    {
                        string revision = await SvnRunner.RunAsync("info --show-item revision", svnManager.WorkingDir);
                        report.AppendLine($"<color={colOK}>[OK]</color>   Current revision  : r{revision.Trim()}");
                    }
                    catch { }
                    try
                    {
                        string branch = await SvnRunner.RunAsync("info --show-item relative-url", svnManager.WorkingDir);
                        report.AppendLine($"<color={colOK}>[OK]</color>   Checked‑out branch: {branch.Trim()}");
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    hadErrors = true;
                    report.AppendLine($"<color={colERR}>[ERROR]</color> Authentication failed: {ex.Message}");
                }
                report.AppendLine();

                report.AppendLine($"<color={colSTEP}>[9/10] CHECKING WORKING COPY STATE...</color>");
                report.AppendLine("  Scanning the local working copy for locks, conflicts or missing files.");
                try
                {
                    SVNLogBridge.LogLine("<color=#55FF55>[RUNNING] Scanning working copy status...</color>");
                    string status = await SvnRunner.RunAsync("status", svnManager.WorkingDir);
                    var lines = status.Split('\n', '\r').Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                    bool hasLocks = lines.Any(x => x.StartsWith("L"));
                    bool hasConflicts = lines.Any(x => x.StartsWith("C"));
                    bool hasMissing = lines.Any(x => x.StartsWith("!"));
                    if (hasLocks) report.AppendLine($"<color={colWARN}>[WARN]</color> Locked files detected – some files are locked.");
                    if (hasConflicts) report.AppendLine($"<color={colWARN}>[WARN]</color> Conflicts detected – resolve before committing.");
                    if (hasMissing) report.AppendLine($"<color={colWARN}>[WARN]</color> Missing files detected – use Fix Missing to repair.");
                    if (!hasLocks && !hasConflicts && !hasMissing)
                        report.AppendLine($"<color={colOK}>[OK]</color>   Working copy is healthy – no locks, conflicts or missing files.");
                }
                catch (Exception ex)
                {
                    report.AppendLine($"<color={colWARN}>[WARN]</color> Could not check working copy state: {ex.Message}");
                }
                report.AppendLine();

                report.AppendLine($"<color={colSTEP}>[10/10] TESTING REPOSITORY RESPONSE SPEED...</color>");
                report.AppendLine("  Fetching the last 5 log entries to measure server response time.");
                sw.Restart();
                try
                {
                    SVNLogBridge.LogLine("<color=#55FF55>[RUNNING] Fetching recent log entries...</color>");
                    string logOutput = await SvnRunner.RunAsync("log -l 5 --quiet", svnManager.WorkingDir);
                    sw.Stop();
                    int count = logOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Count(l => l.StartsWith("r"));
                    report.AppendLine($"<color={colOK}>[OK]</color>   Fetched {count} revisions in {sw.Elapsed.TotalSeconds:F2}s.");
                }
                catch (Exception ex)
                {
                    report.AppendLine($"<color={colWARN}>[WARN]</color> Speed test failed: {ex.Message}");
                }
                report.AppendLine();

                report.AppendLine("====================================");
                report.AppendLine("  DIAGNOSTICS COMPLETE");
                report.AppendLine("====================================");
                report.AppendLine("  Summary:");
                report.AppendLine("    - Every test marked [OK] passed successfully.");
                report.AppendLine("    - [WARN] items are non‑critical but may require attention.");
                report.AppendLine("    - [ERROR] items indicate a problem that prevents normal operation.");
                report.AppendLine();
                if (hadErrors)
                    report.AppendLine($"<color={colERR}><b>VERDICT: FAILED</b> – Review the errors above.</color>");
                else
                    report.AppendLine($"<color=#55FF55><b>VERDICT: HEALTHY</b> – All tests passed successfully.</color>");
                report.AppendLine($"Session Token: {svnManager.SessionToken}");

                SVNLogBridge.UpdateUIField(SVNUI.Instance.LogText, report.ToString(), "DIAG", append: false);
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogLine($"<color=#FF5555>[ERROR]</color> Diagnostics crashed: {ex.Message}");
            }
            finally
            {
                IsProcessing = false;
            }
        }

        private void LogBoth(string msg)
        {
            SVNLogBridge.LogLine(msg);
            SVNLogBridge.UpdateUIField(svnUI.ResolveLogConsole, msg, "RESOLVE", true);
        }
    }
}
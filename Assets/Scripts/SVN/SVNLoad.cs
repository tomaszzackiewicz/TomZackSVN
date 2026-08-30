using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SVN.Core
{
    public class SVNLoad : SVNBase, IDisposable
    {
        private int _isBusy = 0;
        private int _disposed = 0;
        private CancellationTokenSource _loadCts;
        private ProjectSelectionPanel _cachedSelectionPanel;

        public SVNLoad(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        // === FIX Ś1: snapshot inputów na main thread (metoda publiczna — może
        // być wołana z dowolnego kontekstu); dopiero potem async rdzeń.
        public void LoadRepoPathAndRefresh()
        {
            if (Volatile.Read(ref _disposed) == 1) return;

            if (Interlocked.CompareExchange(ref _isBusy, 1, 0) == 1)
            {
                SVNLogBridge.LogLine("<color=orange>Another operation is running. Please wait.</color>");
                return;
            }

            string path = svnUI.LoadDestFolderInput != null
                ? svnUI.LoadDestFolderInput.text.Trim()
                : string.Empty;

            string manualUrl = svnUI.LoadRepoUrlInput != null
                ? svnUI.LoadRepoUrlInput.text.Trim()
                : string.Empty;

            string keyPath = string.Empty;
            if (svnUI.LoadPrivateKeyInput != null && !string.IsNullOrWhiteSpace(svnUI.LoadPrivateKeyInput.text))
                keyPath = svnUI.LoadPrivateKeyInput.text.Trim();
            else if (svnUI.SettingsSshKeyPathInput != null && !string.IsNullOrWhiteSpace(svnUI.SettingsSshKeyPathInput.text))
                keyPath = svnUI.SettingsSshKeyPathInput.text.Trim();
            else
                keyPath = SvnRunner.KeyPath ?? string.Empty;

            // === FIX K1: delayed dispose starego CTS — natychmiastowy Cancel+Dispose
            // potrafił rzucić ObjectDisposedException w biegnącym checkoucie (token
            // zarejestrowany w SvnRunner), co wyglądało jak błąd operacji, nie cancel.
            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _loadCts, newCts);
            if (oldCts != null)
            {
                try { oldCts.Cancel(); } catch { }
                _ = Task.Delay(1000).ContinueWith(_ => { try { oldCts.Dispose(); } catch { } });
            }

            var token = newCts.Token;

            _ = LoadRepoPathAndRefreshAsync(path, manualUrl, keyPath, token).ContinueWith(t =>
            {
                Interlocked.Exchange(ref _isBusy, 0);

                var currentCts = Interlocked.CompareExchange(ref _loadCts, null, newCts);
                if (ReferenceEquals(currentCts, newCts))
                {
                    _ = Task.Delay(1000).ContinueWith(_ => { try { newCts.Dispose(); } catch { } });
                }

                if (t.IsFaulted)
                {
                    var baseEx = t.Exception?.GetBaseException();
                    if (baseEx is not OperationCanceledException)
                    {
                        UnityMainThreadDispatcher.Enqueue(() =>
                            SVNLogBridge.LogError($"[SVNLoad] Operation failed: {baseEx?.Message ?? "Unknown"}"));
                    }
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }

        private async Task LoadRepoPathAndRefreshAsync(string path, string manualUrl, string keyPath, CancellationToken token)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Invalid destination path!");
                return;
            }

            // === FIX K2: walidacja scheme — Uri.TryCreate akceptował file://,
            // ftp:// i dowolne śmieci; checkout startował i padał w połowie.
            if (!string.IsNullOrEmpty(manualUrl) && !IsValidSvnUrl(manualUrl))
            {
                SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Invalid SVN URL. Expected svn://, svn+ssh://, http:// or https://.");
                return;
            }

            SVNLogBridge.LogLine($"<b>Processing path:</b> <color=green>{path}</color>", append: false);

            try
            {
                token.ThrowIfCancellationRequested();

                // === FIX K3: normalizacja trailing slasha — Path.GetFileName("D:/Repo/")
                // zwracał PUSTY string → projekt bez nazwy w liście projektów.
                string normalizedPath = path.Replace("\\", "/").TrimEnd('/');

                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    if (svnUI.LoadDestFolderInput != null)
                        svnUI.LoadDestFolderInput.text = normalizedPath;
                });

                bool hasSvnFolder = Directory.Exists(Path.Combine(normalizedPath, ".svn"));

                if (!hasSvnFolder && string.IsNullOrEmpty(manualUrl))
                {
                    SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Path is not a repository and no URL provided!");
                    return;
                }

                if (!hasSvnFolder)
                {
                    bool isFolderEmpty = false;
                    try
                    {
                        isFolderEmpty = !Directory.EnumerateFileSystemEntries(normalizedPath).Any();
                    }
                    catch (Exception ex)
                    {
                        SVNLogBridge.LogLine($"<color=orange>Warning:</color> Cannot read directory: {ex.Message}");
                        isFolderEmpty = false;
                    }

                    string forceFlag = isFolderEmpty ? "" : " --force";
                    if (!isFolderEmpty)
                        SVNLogBridge.LogLine("<color=orange>Note:</color> Folder not empty. Merging with existing files...");

                    SVNLogBridge.LogLine("<color=yellow>Starting Checkout...</color>");
                    await SvnRunner.RunAsync($"checkout \"{manualUrl}\" .{forceFlag}", normalizedPath, token: token).ConfigureAwait(false);
                    SVNLogBridge.LogLine("<color=green>Checkout completed!</color>");
                }
                else
                {
                    await svnManager.RefreshRepositoryInfo().ConfigureAwait(false);
                }

                token.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(svnManager.RepositoryUrl) && !string.IsNullOrEmpty(manualUrl))
                    svnManager.RepositoryUrl = manualUrl;

                string urlSnapshot = svnManager.RepositoryUrl ?? string.Empty;
                string keySnapshot = keyPath;

                var project = new SVNProject
                {
                    projectName = Path.GetFileName(normalizedPath),
                    repoUrl = urlSnapshot,
                    workingDir = normalizedPath,
                    privateKeyPath = keySnapshot
                };

                // === FIX Ś2: LoadProject (po fixie martwej kopii) zwraca Task<bool> —
                // false = odmowa (brak .svn / martwa kopia). Wcześniej kod leciał
                // dalej: ustawiał stan, register, SUCCESS i ZAMYKAŁ panel mimo to,
                // że projekt się nie załadował.
                bool loaded = await svnManager.LoadProject(project).ConfigureAwait(false);
                if (!loaded)
                {
                    SVNLogBridge.LogLine("<color=#FFAA00>Error:</color> Project could not be loaded (working copy invalid).");
                    return;
                }

                svnManager.WorkingDir = normalizedPath;
                svnManager.CurrentKey = keyPath;
                SvnRunner.KeyPath = keyPath;

                UnityMainThreadDispatcher.Enqueue(() =>
                {
                    RegisterProjectInList(normalizedPath, keySnapshot, urlSnapshot);

                    if (_cachedSelectionPanel == null)
                    {
                        _cachedSelectionPanel = UnityEngine.Object.FindAnyObjectByType<ProjectSelectionPanel>(
                            FindObjectsInactive.Exclude);
                    }

                    _cachedSelectionPanel?.RefreshList();

                    if (svnUI.LoadRepoUrlInput != null)
                        svnUI.LoadRepoUrlInput.text = urlSnapshot;
                });

                SVNLogBridge.LogLine("<color=green>SUCCESS:</color> System synchronized.");

                if (svnManager.PanelHandler != null)
                {
                    await Task.Delay(300, token).ConfigureAwait(false);
                    UnityMainThreadDispatcher.Enqueue(() => svnManager.PanelHandler?.Button_CloseLoad());
                }
            }
            catch (OperationCanceledException)
            {
                SVNLogBridge.LogLine("<color=orange>[SVNLoad] Operation canceled.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogToOutput($"<color=#FFAA00>Operation Failed:</color> {ex.Message}");
                SVNLogBridge.LogErrorToOutput($"[SVN] Load Error: {ex}");
            }
        }

        private void RegisterProjectInList(string path, string key, string url)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            // === S1: atomowe
            ProjectSettings.AddOrUpdateProject(path, (p, created) =>
            {
                if (created)
                    p.projectName = Path.GetFileName(path.Replace("\\", "/").TrimEnd('/'));

                p.repoUrl = url;
                p.privateKeyPath = key;
                p.lastOpened = DateTime.UtcNow;
            });

            string normalizedPath = path.Replace("\\", "/").TrimEnd('/');
            PlayerPrefs.SetString("SVN_LastOpenedProjectPath", normalizedPath);
            PlayerPrefs.Save();
        }

        public void UpdateUIFromManager()
        {
            if (svnManager == null) return;

            UnityMainThreadDispatcher.Enqueue(() =>
            {
                if (svnUI.LoadRepoUrlInput != null)
                    svnUI.LoadRepoUrlInput.text = svnManager.RepositoryUrl ?? string.Empty;

                if (svnUI.LoadDestFolderInput != null)
                    svnUI.LoadDestFolderInput.text = svnManager.WorkingDir ?? string.Empty;

                if (svnUI.LoadPrivateKeyInput != null)
                    svnUI.LoadPrivateKeyInput.text = svnManager.CurrentKey ?? string.Empty;
            });
        }

        public void ClearSceneReferences()
        {
            _cachedSelectionPanel = null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            var cts = Interlocked.Exchange(ref _loadCts, null);
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                // === FIX Ś4: delayed dispose (spójnie z K1).
                _ = Task.Delay(1000).ContinueWith(_ => { try { cts.Dispose(); } catch { } });
            }
        }
    }
}
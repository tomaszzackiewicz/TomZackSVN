using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    public class SVNAdd : SVNBase
    {
        private CancellationTokenSource _activeCTS;
        private const int MaxLogLines = 50;
        private List<string> _logBuffer = new List<string>();

        public SVNAdd(SVNUI ui, SVNManager manager) : base(ui, manager) { }

        public async Task AddAll()
        {
            if (IsProcessing) return;

            CancellationToken token = PrepareNewOperation();

            try
            {
                string root = svnManager.WorkingDir;
                if (string.IsNullOrEmpty(root))
                {
                    SVNLogBridge.LogError("Working directory is null or empty.");
                    return;
                }

                ClearAndLog("<b>[Recursive Add]</b> Scanning for unversioned items...");

                string rawStatus = await SvnRunner.RunAsync("status", root, true, token);

                if (string.IsNullOrWhiteSpace(rawStatus) || !rawStatus.Contains("?"))
                {
                    UpdateLightLog("<color=yellow>Nothing to add. All items are already tracked or ignored.</color>");
                    return;
                }

                UpdateLightLog("Adding files to SVN (Local operation)...");

                await SvnRunner.RunAsync("add * --force --parents --depth infinity", root, true, token);


                IsProcessing = false;

                UpdateLightLog("<color=#4FC3F7>Rebuilding tree...</color>");

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    statusModule.ClearSVNTreeView();
                    statusModule.ClearCurrentData();
                    statusModule.ShowOnlyModified();
                }

                UpdateLightLog("\n<color=green><b>[SUCCESS]</b> Items marked as 'Added'.</color>");
                UpdateLightLog("<color=white>Note: You still need to <b>Commit</b> to upload them to the server.</color>");
            }
            catch (OperationCanceledException)
            {
                UpdateLightLog("<color=orange>Operation cancelled by user.</color>");
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"\n<color=red>Error during AddAll: {ex.Message}</color>");
                IsProcessing = false;
            }
            finally
            {
                CleanUpOperation(token);
            }
        }

        private void ClearAndLog(string initialMessage)
        {
            _logBuffer.Clear();
            _logBuffer.Add(initialMessage);
            SyncBufferWithUI();
        }

        private void UpdateLightLog(string message)
        {
            _logBuffer.Add(message);

            if (_logBuffer.Count > MaxLogLines)
            {
                _logBuffer.RemoveAt(1);
            }

            SyncBufferWithUI();
        }

        private void SyncBufferWithUI()
        {
            if (svnUI.CommitConsoleContent == null) return;

            StringBuilder sb = new StringBuilder();
            foreach (var line in _logBuffer)
            {
                sb.AppendLine(line);
            }

            SVNLogBridge.UpdateUIField(svnUI.CommitConsoleContent, sb.ToString(), append: false);
        }

        private CancellationToken PrepareNewOperation()
        {
            if (_activeCTS != null) { _activeCTS.Cancel(); _activeCTS.Dispose(); }
            _activeCTS = new CancellationTokenSource();
            IsProcessing = true;
            if (svnUI.OperationProgressBar != null)
            {
                svnUI.OperationProgressBar.gameObject.SetActive(true);
                svnUI.OperationProgressBar.value = 0.1f;
            }
            return _activeCTS.Token;
        }

        private void CleanUpOperation(CancellationToken token)
        {
            if (_activeCTS != null && _activeCTS.Token == token)
            {
                IsProcessing = false;
                _activeCTS.Dispose();
                _activeCTS = null;
                HideProgressWithDelay(1.5f);
            }
        }

        private async void HideProgressWithDelay(float delay)
        {
            await Task.Delay((int)(delay * 1000));
            if (!IsProcessing && svnUI.OperationProgressBar != null)
                svnUI.OperationProgressBar.gameObject.SetActive(false);
        }

        public async void AddSingleItem(SvnTreeElement element)
        {
            if (IsProcessing || element == null) return;

            string root = svnManager.WorkingDir;
            if (string.IsNullOrEmpty(root)) return;

            CancellationToken token = PrepareNewOperation();
            ClearAndLog($"<b>[Add]</b> Adding item: {element.Name}...");

            try
            {
                await SvnRunner.RunAsync($"add \"{element.FullPath}\"", root, true, token);

                UpdateLightLog($"<color=green>Successfully added:</color> {element.Name}");

                IsProcessing = false;

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    UpdateLightLog("<color=#4FC3F7>Rebuilding tree...</color>");

                    statusModule.ShowOnlyModified();
                }
            }
            catch (Exception ex)
            {
                SVNLogBridge.LogError($"<color=red>Add Error: {ex.Message}</color>");

                IsProcessing = false;
            }
            finally
            {
                CleanUpOperation(token);
            }
        }
    }
}
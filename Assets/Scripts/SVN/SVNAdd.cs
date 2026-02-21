using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

        public async void AddAll()
        {
            if (IsProcessing) return;
            CancellationToken token = PrepareNewOperation();

            try
            {
                string root = svnManager.WorkingDir;
                ClearAndLog("<b>[Recursive Add]</b> Scanning...");

                string rawStatus = await SvnRunner.RunAsync("status", root, true, token);
                if (!rawStatus.Contains("?"))
                {
                    UpdateLightLog("<color=yellow>Nothing to add.</color>");
                    return;
                }

                UpdateLightLog("Adding files to SVN...");
                await Task.Run(() => SvnRunner.RunAsync("add . --force --parents --depth infinity", root, true, token));

                IsProcessing = false;

                UpdateLightLog("<color=#4FC3F7>Rebuilding tree...</color>");

                var statusModule = svnManager.GetModule<SVNStatus>();
                if (statusModule != null)
                {
                    statusModule.ClearSVNTreeView();
                    statusModule.ClearCurrentData();
                    await statusModule.ExecuteRefreshWithAutoExpand();
                }

                UpdateLightLog("\n<color=green><b>[SUCCESS]</b> Items added and UI refreshed.</color>");
            }
            catch (Exception ex)
            {
                UpdateLightLog($"\n<color=red>Error: {ex.Message}</color>");
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
    }
}
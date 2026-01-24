using UnityEngine;

namespace SVN.Core
{
    public class SVNBase
    {
        protected SVNManager svnManager;
        protected SVNUI svnUI;

        protected bool IsProcessing
        {
            get => svnManager.IsProcessing;
            set => svnManager.IsProcessing = value;
        }

        public SVNBase(SVNUI ui, SVNManager manager)
        {
            this.svnUI = ui;
            this.svnManager = manager;

            if (ui == null || manager == null)
            {
                UnityEngine.Debug.LogError($"{this.GetType().Name}: UI or Manager is NULL!");
            }
        }
    }
}

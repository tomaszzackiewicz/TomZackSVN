namespace SVN.Core
{
    public class SVNProjectInfoSnapshot
    {
        public string ProjectName;

        public string Revision;
        public string RemoteRevision;

        public string Author;
        public string Date;

        public string Branch;
        public string Server;

        public string RepoRoot;
        public string Url;
        public string RelativeUrl;

        public string WorkingCopySize;

        public bool IsOutdated;

        public bool IsValid;

        public string AppVersion;
        public string SvnVersion;

        public string CurrentUser;

        public bool IsUpdating;
        public bool IsCanceled;
        public bool HasChanges;

        public bool WasCanceled;
        public string LastOperation;
        public double DurationSeconds;
    }
}
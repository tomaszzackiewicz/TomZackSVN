using System;
using System.Collections.Generic;

namespace SVN.Core
{
    [Serializable]
    public class SVNProject
    {
        public string projectId;
        public string projectName;
        public string repoUrl;
        public string workingDir;
        public string privateKeyPath;
        public string mergeToolPath;
        public DateTime lastOpened;
    }

    [Serializable]
    public class SVNProjectList
    {
        public List<SVNProject> projects = new List<SVNProject>();
    }
}
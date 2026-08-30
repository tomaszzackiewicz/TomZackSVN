using System;
using System.Collections.Generic;
using UnityEngine;

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
        public string resolveToolPath;
        public string diffToolPath;
        public string blameToolPath;
        public string sshOptions;

        [SerializeField]
        private string lastOpenedString;

        public DateTime lastOpened
        {
            get
            {
                if (DateTime.TryParse(
                        lastOpenedString,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out DateTime dt))
                {
                    return dt;
                }
                return default(DateTime);
            }
            set
            {
                lastOpenedString = value.ToString("o");
            }
        }
    }

    [Serializable]
    public class SVNProjectList
    {
        public List<SVNProject> projects = new List<SVNProject>();
    }
}
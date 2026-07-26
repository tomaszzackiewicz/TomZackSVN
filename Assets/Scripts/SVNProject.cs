using System;
using System.Collections.Generic;
using UnityEngine; // Dodane dla atrybutu [SerializeField]

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

        // To pole zostanie zapisane do JSONa przez Unity
        [SerializeField]
        private string lastOpenedString;

        // Reszta Twojego kodu (ProjectSelectionPanel.cs) nadal używa "lastOpened" tak jak wcześniej
        public DateTime lastOpened
        {
            get
            {
                if (DateTime.TryParse(lastOpenedString, out DateTime dt))
                {
                    return dt;
                }
                return default(DateTime); // Zwraca 0001-01-01, jeśli string jest pusty/błędny
            }
            set
            {
                // Zapisujemy w formacie ISO 8601 ("o"), który jest najbezpieczniejszy przy konwersjach daty i czasu
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
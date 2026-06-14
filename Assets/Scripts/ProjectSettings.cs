using SVN.Core;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SVN.Core
{
    public static class ProjectSettings
    {
        private static string FilePath => Path.Combine(Application.persistentDataPath, "projects.json");

        public static void SaveProjects(List<SVNProject> projects)
        {
            string json = JsonUtility.ToJson(new SVNProjectList { projects = projects }, true);
            File.WriteAllText(FilePath, json);
        }

        public static List<SVNProject> LoadProjects()
        {
            if (!File.Exists(FilePath)) return new List<SVNProject>();
            string json = File.ReadAllText(FilePath);
            return JsonUtility.FromJson<SVNProjectList>(json).projects;
        }

        public static void DeleteProject(string workingDir)
        {
            List<SVNProject> projects = LoadProjects();
            projects.RemoveAll(p => p.workingDir == workingDir);
            SaveProjects(projects);
        }
    }
}
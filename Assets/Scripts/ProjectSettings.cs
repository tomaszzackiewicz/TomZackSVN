using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace SVN.Core
{
    public static class ProjectSettings
    {
        private static string FilePath => Path.Combine(SVNPrefs.PersistentDataPath, "projects.json");

        private static readonly object _fileLock = new();

        // ===================================================================
        //  ATOMIC HIGH-LEVEL API (S1) — używać zamiast Load→modify→Save!
        //  Cały read-modify-write pod JEDNYM lockiem — koniec lost-update
        //  między modułami (checkout/terminal/load/manager/settings/panel).
        // ===================================================================

        /// <summary>
        /// Atomowo: znajdź projekt po workingDir lub utwórz nowy, potem mutuj.
        /// mutate dostaje (projekt, czyUtworzony). Bezpieczne z dowolnego wątku.
        /// </summary>
        public static SVNProject AddOrUpdateProject(string workingDir, Action<SVNProject, bool> mutate = null)
        {
            lock (_fileLock)
            {
                var projects = LoadFrom(FilePath);
                string normalized = NormalizeDir(workingDir);

                var existing = FindByDir(projects, normalized);
                bool created = false;
                SVNProject project;

                if (existing != null)
                {
                    project = existing;
                }
                else
                {
                    created = true;
                    project = new SVNProject
                    {
                        workingDir = normalized,
                        projectName = Path.GetFileName(normalized),
                        lastOpened = DateTime.UtcNow
                    };
                    projects.Add(project);
                }

                mutate?.Invoke(project, created);
                SaveTo(FilePath, projects);
                return project;
            }
        }

        /// <summary>
        /// Atomowo mutuj ISTNIEJĄCY projekt (bez tworzenia). Zwraca false, gdy nie znaleziono.
        /// </summary>
        public static bool UpdateProject(string workingDir, Action<SVNProject> mutate)
        {
            if (mutate == null || string.IsNullOrWhiteSpace(workingDir)) return false;

            lock (_fileLock)
            {
                var projects = LoadFrom(FilePath);
                var existing = FindByDir(projects, NormalizeDir(workingDir));
                if (existing == null) return false;

                mutate(existing);
                SaveTo(FilePath, projects);
                return true;
            }
        }

        // ===================================================================
        //  BASIC API — semantyka bez zmian (dla pozostałych callerów)
        // ===================================================================

        public static void SaveProjects(List<SVNProject> projects)
        {
            lock (_fileLock)
            {
                SaveTo(FilePath, projects ?? new List<SVNProject>());
            }
        }

        public static List<SVNProject> LoadProjects()
        {
            lock (_fileLock)
            {
                return LoadFrom(FilePath);
            }
        }

        public static void DeleteProject(string workingDir)
        {
            lock (_fileLock)
            {
                var projects = LoadFrom(FilePath);
                string normalized = NormalizeDir(workingDir);
                projects.RemoveAll(p =>
                    string.Equals(NormalizeDir(p.workingDir), normalized, StringComparison.OrdinalIgnoreCase));
                SaveTo(FilePath, projects);
            }
        }

        // ===================================================================
        //  Internals — lock trzymany przez wywołującego (Monitor reentrant,
        //  więc zagnieżdżone wywołania publicznych metod też są bezpieczne).
        // ===================================================================

        private static List<SVNProject> LoadFrom(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    string bak = path + ".bak";
                    if (File.Exists(bak))
                    {
                        try { return Parse(File.ReadAllText(bak)); } catch { }
                    }
                    return new List<SVNProject>();
                }

                try
                {
                    return Parse(File.ReadAllText(path));
                }
                catch
                {
                    string bak = path + ".bak";
                    if (File.Exists(bak))
                    {
                        try { return Parse(File.ReadAllText(bak)); } catch { }
                    }
                    return new List<SVNProject>();
                }
            }
            catch
            {
                return new List<SVNProject>();
            }
        }

        private static List<SVNProject> Parse(string json)
        {
            var wrapper = JsonUtility.FromJson<SVNProjectList>(json);
            return wrapper?.projects ?? new List<SVNProject>();
        }

        private static void SaveTo(string path, List<SVNProject> projects)
        {
            string json = JsonUtility.ToJson(new SVNProjectList { projects = projects }, true);

            string tmpPath = path + ".tmp";
            try
            {
                File.WriteAllText(tmpPath, json);
                if (File.Exists(path))
                    File.Replace(tmpPath, path, path + ".bak");
                else
                    File.Move(tmpPath, path);
            }
            catch
            {
                try { if (File.Exists(tmpPath) && File.Exists(path)) File.Delete(tmpPath); } catch { }
                throw;
            }
        }

        private static string NormalizeDir(string dir) =>
            string.IsNullOrWhiteSpace(dir) ? "" : dir.Replace("\\", "/").Trim().TrimEnd('/');

        private static SVNProject FindByDir(List<SVNProject> projects, string normalizedDir)
        {
            if (string.IsNullOrWhiteSpace(normalizedDir)) return null;
            return projects.Find(p =>
                string.Equals(NormalizeDir(p.workingDir), normalizedDir, StringComparison.OrdinalIgnoreCase));
        }

        // Prywatna zagnieżdżona wersja wrappera — legalnie współistnieje z publiczną
        // SVNProjectList z SVNProject.cs (zasłanianie w zakresie klasy).
        [Serializable]
        private class SVNProjectList
        {
            public List<SVNProject> projects;
        }
    }
}
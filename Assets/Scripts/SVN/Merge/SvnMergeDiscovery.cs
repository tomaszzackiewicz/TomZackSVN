using System;
using System.Threading;
using System.Threading.Tasks;

namespace SVN.Core
{
    public static class SvnMergeDiscovery
    {
        public static async Task<string[]> FetchAvailableBranchesAsync(SVNMerge merge, bool force = false)
        {
            if (!merge.IsReady())
            {
                merge.LogInfo("[Branches] Project not ready yet — returning cached or empty.");
                return merge._cachedBranches ?? Array.Empty<string>();
            }

            if (merge._isFetchingBranchesFlag == 1)
            {
                merge.LogInfo("[Branches] Fetch already in progress → returning cache.");
                return merge._cachedBranches ?? Array.Empty<string>();
            }

            if (!force && merge._branchesCacheValid && merge._cachedBranches != null)
            {
                merge.LogInfo("[Cache] Using cached branches.");
                return merge._cachedBranches;
            }

            if (!merge.TryStart()) return merge._cachedBranches ?? Array.Empty<string>();

            if (Interlocked.CompareExchange(ref merge._isFetchingBranchesFlag, 1, 0) != 0)
            {
                merge.End();
                return merge._cachedBranches ?? Array.Empty<string>();
            }

            try
            {
                // FIX: merge.svnManager -> merge.SVNManager
                await merge.SVNManager.CancelBackgroundTasksAsync().ConfigureAwait(false);

                string repoRoot = merge.EnsureRepoRoot();
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    string rootOutput = await SvnRunner.RunAsync("info --show-item repos-root-url", merge.SVNManager.WorkingDir, false, CancellationToken.None).ConfigureAwait(false);
                    repoRoot = rootOutput?.Trim().TrimEnd('/');
                    if (string.IsNullOrWhiteSpace(repoRoot))
                    {
                        merge.LogErrorLocal("[Critical Error] Repo root missing.");
                        return Array.Empty<string>();
                    }
                }

                string branchesUrl = $"{repoRoot}/branches";
                merge.LogInfo($"[Debug] Scanning branches at: {branchesUrl}");

                var branchList = await merge.GetRepoListAsync(branchesUrl, CancellationToken.None).ConfigureAwait(false);
                if (branchList.Length == 0)
                {
                    merge.LogInfo("[FetchAvailableBranches] No branches found (folder may be empty or not exist yet).");
                    merge._cachedBranches = Array.Empty<string>();
                    merge._branchesCacheValid = true;
                    return merge._cachedBranches;
                }

                merge._cachedBranches = branchList;
                merge._branchesCacheValid = true;
                merge.LogSuccess($"Found {branchList.Length} branch(es).");
                return branchList;
            }
            catch (Exception ex)
            {
                merge.LogErrorLocal($"[Critical Error] Scan failed: {ex.Message}");
                return Array.Empty<string>();
            }
            finally
            {
                Interlocked.Exchange(ref merge._isFetchingBranchesFlag, 0);
                merge.End();
            }
        }

        public static async Task<string[]> FetchAvailableTagsAsync(SVNMerge merge, bool force = false)
        {
            if (!merge.IsReady())
            {
                merge.LogInfo("[Tags] Project not ready yet — returning cached or empty.");
                return merge._cachedTags ?? Array.Empty<string>();
            }

            if (merge._isFetchingTagsFlag == 1)
            {
                merge.LogInfo("[Tags] Fetch already in progress → returning cache.");
                return merge._cachedTags ?? Array.Empty<string>();
            }

            if (!force && merge._tagsCacheValid && merge._cachedTags != null)
            {
                merge.LogInfo("[Cache] Using cached tags.");
                return merge._cachedTags;
            }

            if (!merge.TryStart()) return merge._cachedTags ?? Array.Empty<string>();

            if (Interlocked.CompareExchange(ref merge._isFetchingTagsFlag, 1, 0) != 0)
            {
                merge.End();
                return merge._cachedTags ?? Array.Empty<string>();
            }

            try
            {
                using var cts = new CancellationTokenSource();
                CancellationToken token = cts.Token;

                string repoRoot = await merge.GetRepoRootSafeAsync(token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(repoRoot))
                {
                    merge.LogErrorLocal("[Tags] Repo root not found.");
                    return Array.Empty<string>();
                }

                string tagsUrl = $"{repoRoot}/tags";
                merge.LogInfo($"[Tags] Scanning at: {tagsUrl}");

                var tagList = await merge.GetRepoListAsync(tagsUrl, token).ConfigureAwait(false);
                if (tagList.Length == 0)
                {
                    merge.LogInfo("[Tags] No tags found (folder may be empty or not exist yet).");
                    merge._cachedTags = Array.Empty<string>();
                    merge._tagsCacheValid = true;
                    return merge._cachedTags;
                }

                merge._cachedTags = tagList;
                merge._tagsCacheValid = true;
                merge.LogSuccess($"[Tags] Found {tagList.Length} tag(s).");
                return tagList;
            }
            catch (Exception ex)
            {
                merge.LogErrorLocal($"[Tags Error] {ex.Message}");
                return Array.Empty<string>();
            }
            finally
            {
                Interlocked.Exchange(ref merge._isFetchingTagsFlag, 0);
                merge.End();
            }
        }

        public static async Task RefreshIfEmptyAsync(SVNMerge merge)
        {
            if (!merge.IsReady())
            {
                merge.LogInfo("[RefreshIfEmpty] Not ready — skipped.");
                return;
            }

            if (merge._cachedBranches == null || !merge._branchesCacheValid)
            {
                merge.LogInfo("[RefreshIfEmpty] Branches cache empty/invalid — fetching...");
                await FetchAvailableBranchesAsync(merge, force: false).ConfigureAwait(false);
            }
            else
            {
                merge.LogInfo("[RefreshIfEmpty] Branches cache valid — skipped.");
            }

            if (merge._cachedTags == null || !merge._tagsCacheValid)
            {
                merge.LogInfo("[RefreshIfEmpty] Tags cache empty/invalid — fetching...");
                await FetchAvailableTagsAsync(merge, force: false).ConfigureAwait(false);
            }
            else
            {
                merge.LogInfo("[RefreshIfEmpty] Tags cache valid — skipped.");
            }
        }
    }
}
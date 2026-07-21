# TomZackSVN
Light SVN Client
TomZackSVN was created because there are currently no modern, free SVN clients that properly support the svn+ssh protocol. Most existing tools are outdated, limited, or require paid licenses. This project fills that gap by providing a simple and accessible solution for everyone.

The application relies on the TortoiseSVN command‑line interface (CLI), so TortoiseSVN must be installed on the system. Additionally, OpenSSH needs to be added to the system’s environment variables, and users must generate SSH keys for authentication in order for the client to work correctly.

You only need to download the project, open it in Unity, and build the Windows executable to start using the client.


Installation Checklist for Using the Application:

- Install TortoiseSVN (make sure to select Command Line Tools) or SlikSVN (which includes only CLI tools for Windows). 
- Generate SSH keys (the application requires a private key for authentication). 
- Ensure you have write permissions for the folder you choose as your Working Directory.

If Windows SmartScreen prevents the app from starting:
1. Click on "More info" in the blue Windows popup.
2. Select "Run anyway" to launch the application.

This warning appears simply because the app is new and hasn't 
built up a "reputation" with Microsoft yet. We guarantee the 
file is safe and virus-free.


Third-Party Libraries

This project utilizes the following open-source library to handle native file and folder dialogues:

    Unity Standalone File Browser

        Author: Gökhan Gökçe (gkngkc)

        License: MIT

        Description: Used for providing native Windows/Mac/Linux file selection interfaces within the SVN Client.

# TomZackSVN Documentation

## 1. Switch Project
The **Switch Project** panel is your central hub for managing all your working copies. It displays a list of your saved projects and provides sub‑panels for adding new ones, loading existing folders, and relocating repository addresses.  
Each saved project appears with its name and three action buttons:
- **Select** – loads the project and makes it the active working copy.
- **Delete** – removes the project from the list. Your local folder and files are not touched; only the entry in the application is deleted.
- **Relocate** – opens a small panel where you can change the repository URL of the working copy without downloading everything again. The current URL is pre‑filled; type a new one and press **Confirm**, or **Cancel** to close the panel. This is useful when the server address or protocol changes.

### Add New Project (sub‑panel)
Opens when you click the **Add New Project** button. You need to provide:
- **Project Name** – a friendly label for the list. If left empty, the name is automatically generated from the Repository URL.
- **Repository URL** – the address of the SVN server (e.g., `svn+ssh://...` or `https://...`).
- **Local Folder Path** – the directory where the working copy already exists or will be placed. Use the **Browse** button to select it.
- **Private Key** – required if the repository uses `svn+ssh://` connections. You can type the path or use the **Browse** button.

Click **Save Project** to register the project. It immediately appears in the main list and is automatically loaded as the active working copy.

## 2. Checkout
The **Checkout** panel is where you download a repository for the first time, export a clean copy without version-control metadata, or resume an interrupted download. It gives you full control over long‑running transfers with pause, resume, and cancel.

**Fields you must fill:**
- **Repository URL** – the address of the SVN server (e.g., `svn+ssh://...`, `https://...`).
- **Destination Folder** – the local folder where the project will be placed. It must be empty or non‑existent.
- **Private Key** – required if your repository uses `svn+ssh://`. You can type the path directly or use the **Browse** button to select it.

**Action buttons:**
- **Project Info** – before starting, you can click this button to analyze the repository. It shows the remote size, the required disk space, and the structure (trunk, branches, tags) so you know exactly what you are about to download.
- **Checkout** – starts the download of the entire repository (HEAD revision). A live progress panel appears, showing current status (Downloading, Paused, etc.), percentage complete, estimated time remaining, total size already on disk, and download speed.
- **Pause** – temporarily stops the checkout. The already downloaded files are kept, and you can resume later.
- **Resume** – continues a paused checkout. It only works when the destination folder already contains a partial working copy (`.svn` folder present). If the checkout was cancelled, Resume is not available.
- **Cancel** – permanently aborts the operation. The downloaded files remain on disk, but the working copy will be incomplete and cannot be resumed. Use Cancel only when you are sure you want to start over. And if you change your mind, you can use **Update** to download the rest of the project.

**Typical workflow:**
1. Enter the repository URL, a new folder path, and (if needed) the private key.
2. Click **Project Info** to see how much space you need.
3. Click **Checkout** and watch the progress.
4. If the connection is slow, you can **Pause** and later **Resume** without losing the downloaded data.
5. Once finished, the project is automatically loaded into the application and appears in the Switch Project list.

### Add Repo (quick registration of an existing folder)
This function lets you bring an already existing local folder under the application's management without filling in a custom name. Click the **Add Repo** button to open its sub‑panel, then provide:
- **Repository URL** – the full address of your SVN server. This is optional if the folder is already a working copy; if the folder is not yet under SVN control and you supply a URL, the application will checkout the repository into that folder.
- **Local Folder Path** – the directory that contains (or will contain) the working copy. Use the **Browse** button to select it.
- **Private Key** (optional) – the SSH private key if the repository uses `svn+ssh://`. You can type the path or use the **Browse** button.

Click **Load Repo** to register the folder. The project immediately appears in the main list and is set as the active working copy. If a project with the same local path already exists in your list, it is updated instead of duplicated.  
This is ideal when you already have a checked‑out working copy on disk and simply want to start working with it, or when you need to quickly add several repositories without manually entering names.

## Update
Sync local files with the server. Use this daily before starting work. Shows per‑file progress and a final report with counts of Updated, Added, Deleted, and Conflicts. The report includes the old and new revision numbers and the duration of the update.

## Cancel Update
Stops an ongoing Update operation immediately. Use this if the download seems stuck or you need to work offline. Partial files will be preserved, and the working copy will remain in a consistent state. You can press **Update** again later to continue downloading the remaining changes.

## Refresh
Triggers a complete manual scan of your local directory. It updates the file statuses in the main tree, rebuilds the commit panel (if it is active), refreshes lock information, and recalculates all statistics (modified, added, deleted, etc.). Use this after making changes outside the application, or whenever the display seems out of sync with the actual file state.

## Clean
The **Clean** panel is your maintenance and repair toolbox. When the SVN database gets locked after a crash, or your working copy behaves strangely, use the buttons here to fix it. Each button performs a specific repair operation and writes its progress to the panel's dedicated console.

**Available Repair Operations:**
- **Clean** – runs the standard `svn cleanup` command. It releases any internal SVN locks that are blocking your working copy (common after an interrupted update or commit). If the basic cleanup fails, it automatically retries with `--include-externals`. Use this first whenever you see "locked" or "working copy locked" errors.
- **Discard Untracked** – deletes all unversioned files and folders from your working copy. These are items marked with the `?` status – files that exist on disk but are not tracked by SVN. After the operation the tree view is cleared, and a fresh status scan runs automatically. **Warning:** the deleted items cannot be recovered; use only when you are sure you no longer need them.
- **Vacuum** – performs a deep database optimization (`svn cleanup --vacuum-pristines`). It reclaims disk space that is wasted inside the internal `wc.db` database and can noticeably speed up SVN operations on large projects. If your SVN client is too old to support vacuum, it falls back to a normal cleanup. This operation may take a while for big working copies.
- **Deep Repair** – a multi‑step, advanced repair that combines cleanup, timestamp refresh, and automatic conflict resolution. It runs three stages: (1) Basic Cleanup – removes database locks; (2) Timestamp repair – forces an update to fix broken file‑time records; (3) Conflict resolution – automatically resolves every conflict by accepting the server version (`theirs-full`). Use this when a normal cleanup is not enough, but be aware that any local changes you made in conflicted files will be discarded.
- **Hard Reset** – reverts everything in your working copy to match the latest server revision (HEAD). It first discards all local modifications (`svn revert -R .`), then permanently deletes all unversioned files, and finally forces an update that replaces every file with the server version. This operation is **irreversible** – after it finishes, your working copy is an exact mirror of the server. Use it only when you want a completely fresh start without deleting the whole folder.
- **Repair Structure** – re‑aligns the working copy with the repository URL, fixing deep structural issues. It reads the current repository URL, then performs a cleanup, a switch to the same URL (which repairs missing metadata), and a full update that restores all files. Finally, it resolves any remaining conflicts by accepting the server version. Use this when your working copy has lost track of its repository structure (e.g., after a failed switch or a partial checkout).

All operations automatically refresh the file tree after completion, so you immediately see the result. If an operation is cancelled, the console shows a warning and the working copy is left in a consistent state.

## Resolve
The **Resolve** panel is your conflict‑resolution centre. Conflicts happen when two people modify the same part of a file, or when a file that exists locally has been added remotely (or vice‑versa). The panel shows every conflicted file together with buttons that let you fix them one by one, or all at once.

**What the panel contains:**
- A console that logs every resolve action so you can track what was done.
- A list of conflict items – each item shows the file path and its conflict type (Text, Manual, or Tree). For every item you have the following action buttons:
  - **Resolve as Mine** – keep your local version and discard the server changes.
  - **Resolve as Theirs** – accept the server version and discard your own changes.
  - **Open in Editor** – open the file in your configured merge tool (from Settings) so you can combine both versions manually.
  - **Mark as Resolved** – tell SVN that you have fixed the file (only works after conflict markers like `<<<<<<<`, `=======`, `>>>>>>>` have been removed).
- Global buttons at the top (or bottom) that affect all conflicts at once:
  - **Resolve All (Mine)** – keep your local version for every conflict.
  - **Resolve All (Theirs)** – discard all your changes and accept the server version everywhere.
  - **Delete All Obstructions** – batch‑resolve every tree conflict by removing the obstructing local files.
  - **Mark as Resolved** – attempts to mark all conflicts as resolved (only works if no conflict markers remain).
  - **Open in Editor** – opens the merge tool for a specific file. You can type the file path in the input field, or leave it blank to open the first pending conflict. The **Browse** button next to the input field lets you select a file from a file dialog.

**How to use it:**
1. After an Update or Merge, open the Resolve panel. The list automatically refreshes and shows every file with a conflict.
2. Decide how to fix each file:
   - If you want to keep your work and ignore the other change, click **Resolve as Mine** on that file's row.
   - If the other change is the correct one and you want to discard your modifications, click **Resolve as Theirs**.
   - If you need to combine both changes, click **Open in Editor**. This launches your external merge tool (configured in Settings). After you have saved the merged file, click **Mark as Resolved** to let SVN know the conflict is over.
3. For tree conflicts (a file exists locally but was added on the server, or vice‑versa):
   - Use the global **Delete All Obstructions** button to remove all obstructing local files at once and accept the server versions.
   - If you prefer to keep a local file, use **Resolve as Mine** on that specific item.
4. When all conflicts are fixed, you can commit your changes as usual. The panel automatically refreshes after every action, so you always see the current state.

The global buttons let you resolve everything in one step – for example, after a large merge you can click **Resolve All (Theirs)** to accept all incoming changes, or **Delete All Obstructions** to clear every tree conflict at once. The console log helps you track exactly what was done.

## Branch/Tag
The **Branch/Tag** panel lets you create, switch, compare, delete and inspect branches and tags directly from the server.

**Layout:**
- A type selector (branches or tags).
- A text field for the name of the new branch or tag.
- An optional revision field – if filled, the new branch/tag will be created from that exact revision; otherwise HEAD is used.
- Two dropdown lists: one for branches (always contains **trunk** plus all branches), and one for tags.

**Buttons:**
- **Create Branch from Trunk** – creates a new branch or tag from trunk. The name is taken from the input field.
- **Create Branch from Selected** – creates a new branch or tag from the currently selected branch/tag in the dropdown.
- **Show Details** – displays the creator, creation date, and source branch for the selected branch/tag.
- **Switch to Branch** – switches your working copy to the selected branch. You will be warned if you have uncommitted local changes.
- **Switch to Tag** – switches your working copy to the selected tag. Same warning applies.
- **Diff with Current Branch** – compares your current working copy with the selected branch. Differences are written to a temporary text file and opened in your default text editor, with a summary in the console.
- **Diff with Current Tag** – same as above, but compares with the selected tag.
- **Delete Branch** – permanently removes the selected branch from the server (requires double‑click within 5 seconds to confirm). **trunk** cannot be deleted.
- **Delete Tag** – permanently removes the selected tag from the server, also with double‑click confirmation.

## Merge
The **Merge** panel provides all the tools you need to merge changes between branches, simulate merges, and undo them.

**Layout:**
- A dropdown list of available branches (including trunk). The selected branch is automatically copied to the source input field.
- A set of action buttons.
- After a dry‑run, a list of files that would be affected appears below the console.

**Buttons:**
- **Cancel Merge** – immediately stops any running merge operation.
- **Refresh Branch Dropdown** – reloads the list of branches from the server.
- **Compare with Trunk** – analyses the difference between your current branch and trunk, showing how many revisions your branch is behind (incoming) and ahead (outgoing).
- **Sync with Trunk** – performs a live merge of trunk into your current branch.
- **Repair Merge History** – fixes incomplete reintegrate merge information on trunk. No files are changed, only SVN metadata. You must commit after running this.
- **Force Merge from Trunk** – merges trunk changes into your branch while ignoring ancestry. Use only when the standard merge fails with an "ancestry" error. Commit immediately after.
- **Dry Run Merge** – simulates the merge without modifying any files, showing exactly what would be added, updated, deleted or conflicted.
- **Confirm Merge** – executes the actual merge. Review the changes and commit them.
- **Cancel Local Merge** – reverts only the changes introduced by the last merge, keeping other local modifications.
- **Revert to HEAD** – discards all local changes and restores your working copy to the latest server revision (requires double‑click within 5 seconds).
- **Undo Merge** – reverts a merge that has already been committed, creating a new commit that reverses the changes. Your working copy must be clean.

All merge operations handle common errors automatically (e.g., ancestry issues trigger an automatic retry with `--ignore-ancestry`) and update the working copy status afterwards.

## Diff
The **Diff** panel lets you compare your local changes in a single file with the version stored in the repository.

**How to use it – from the main file tree:**
- Double‑click any file that has a modified status (`M`, `A`, etc.). The first double‑click shows a coloured, line‑by‑line preview directly inside the application. Added lines are green, removed lines are red, and context lines are white. A summary header displays the file paths and counts of added, removed, and unchanged lines.
- Double‑click the same file a second time to open the comparison in your external diff/merge tool (configured in Settings). The external tool receives a temporary file with the diff output, enriched with line numbers for easier navigation.

**How to use it – from the Diff panel itself:**  
The panel contains an input field and two buttons:
- **Browse** – opens a file dialog starting from your working copy root. When you select a file, its relative path is automatically placed into the input field.
- **Diff** – runs `svn diff` on the file whose path is in the input field. The application then:
  1. Displays the full, colour‑formatted diff directly in the panel's console. To keep the interface responsive and prevent TextMeshPro from being overloaded, the preview is limited to the first 500 lines. If the diff is longer, a message is shown at the bottom: *"… Diff truncated. Full diff opened in external editor."*
  2. Shows a one‑line summary in the console, e.g., *"Diff Summary: +12 lines added, -3 lines removed"*.
  3. Opens the complete diff in your external diff tool so you can inspect every change without restrictions.

If no file is selected, or the working directory is missing, an appropriate warning is displayed in the console. For binary files, the preview is not available; instead, Windows Explorer opens with the file selected.

**When to use it:**
- Before committing – check that all changes are intentional.
- After an update or merge – verify that conflicts were resolved correctly.
- While reviewing a colleague's work – quickly see what was modified in a specific file.

The Diff panel combines a quick, in‑app preview with a full external comparison, giving you both speed and depth when reviewing changes.

## Blame
The **Blame** panel shows who last changed each line of a text file, together with the revision number and the author.

**How to use it:**
- **Browse** – opens a file dialog starting from your working copy root. The selected file's relative path is placed into the input field.
- **Blame** – runs `svn blame --xml` on the file in the input field and:
  1. Displays a colour‑coded report directly in the panel. Each line shows the line number, revision, author, and content. To keep the interface responsive, the preview is limited to the first 500 lines. If the file has more, a message appears at the bottom, and the full report is opened in your external editor (as configured in Settings).
  2. Simultaneously saves a plain‑text version of the same report and opens it in your external editor, so you can review, print, or share the complete history.
  3. For binary files (such as `.uasset`, images, etc.) the panel simply shows a clear message: *"Binary file – blame not available."* No external editor is opened in this case.
- From the main file tree – when you click the per‑file **Blame** button, the result is shown directly in the main application log console (with the same formatting and truncation limits).

The blame output is cached – running Blame again on the same unchanged file reuses the previous result, saving time.

**When to use it:**
- To find out who introduced a specific change (e.g., a bug or configuration update).
- Before modifying a file – to see who else has been working on the same lines.
- During code review – to quickly check the authorship of recent modifications.

## Shelve
Opens the **Shelve** (Stash) panel. It lets you temporarily save your uncommitted changes, revert your working copy to a clean state, and restore the changes later.

**Panel layout:**
- An input field for an optional shelf name (auto‑generated if left empty).
- **Stash** – saves all local modifications as a patch, reverts every changed file, and adds the shelf to the list.
- **Refresh** – reloads the list of saved shelves.

Each shelf in the list shows its name, creation date, number of changed files, and patch size. Two action buttons are available:
- **Restore** – applies the saved patch back to your working copy and deletes the shelf.
- **Delete** – permanently removes the shelf without applying it.

**Workflow example:**
1. You need to switch tasks quickly. Click **Stash** – your working copy is reverted to the latest server revision.
2. Fix the urgent issue, commit, then return.
3. Click **Restore** on your shelf to get your previous changes back.

Shelf files are stored locally and are not shared with the server.

## Settings
The **Settings** panel lets you configure the essential connection and tool parameters for your current working copy. Each setting is saved in two places:
- Per‑project – the value is stored in the project list (the same JSON file that the Switch Project panel uses), so it stays linked to this specific working copy.
- Global fallback – the value is also stored in Unity's PlayerPrefs, providing a default for operations where no project is active.

**Available settings:**
- **Repository URL** – the address of your SVN server (e.g., `svn+ssh://...` or `https://...`). Press the adjacent **Save** button to store the value. It updates both the current project and the global default.
- **Working Directory** – the local folder of your working copy. Press the adjacent **Save** button to switch the application to this folder. If the folder exists and is not yet in your project list, it will be added automatically. After saving, the application refreshes the repository information and file tree for the new location.
- **SSH Private Key** – the path to your private key file for `svn+ssh://` connections. Press the adjacent **Save** button to store the path. It updates the current project and the global key setting.
- **Merge Tool path** – the external program used for resolving text conflicts and comparing files (e.g., the path to your diff tool or text editor). Press the adjacent **Save** button to store the path. It updates the current project and the global merge‑tool setting.

When you open the Settings panel, it automatically fills all fields with the values currently held in memory for the active project – so you always see what is actually being used.

## Show Ignored
Displays all files and folders currently excluded from version control (marked with the **"I"** status), together with the active ignore rules.

When opened, the panel shows:
- System information (working directory, `.svnignore` file path, whether the file exists).
- Active ignore rules – patterns from the local `.svnignore` file (marked **[FILE]**) and from SVN properties (marked **[SVN]**).
- Currently ignored files – up to 200 files matching the active rules.

**Buttons:**
- **Refresh Rules** – rescans the working copy and updates the display.
- **Load New Rules** – reloads the ignore patterns from the local `.svnignore` file into the application's cache.
- **Apply New Rules** – writes the contents of your local `.svnignore` file to the `svn:global-ignores` property on the root of the working copy. After this, you must commit the root folder so that other team members also get the same ignore rules.

Common workflow: edit `.svnignore`, click **Load New Rules**, verify with **Refresh Rules**, then **Apply New Rules** and commit.

## Explore
Opens the root folder of your current working copy directly in Windows Explorer.

## Revision Graph
The **Revision Graph** visualises the complete history of your repository – every commit, its author, date, message, and changed files. It is an interactive timeline that helps you understand the evolution of the project.

**What you see:**
- A list of commits, newest at the top.
- Each commit row contains:
  - A lane diagram on the left – coloured symbols and lines that represent different branches and merges. **Trunk** is a square, branches are circles, tags are diamonds, and merges have special composite symbols. Each branch gets its own distinct, consistent colour.
  - The revision number, author, date, and commit message.
  - A collapsible list of changed files – click the small arrow to see exactly what was modified, added, or deleted.
  - A small **R** button next to your own commits (active only when you are the author of that commit).

**Filtering:**
- **Filter field** – type a branch name, part of a commit message, an author name, a revision number, or a file path. Only matching revisions remain visible.
- **Rename field** – a permanent input field used to change the message of your own commits (see below).

**Buttons:**
- **Load Graph** – reloads the full revision history from the server. Use this when you know new commits have been made.
- **Collapse All** – collapses the file list of every commit, giving you a compact overview.
- **Export History** – saves the complete graph (with revision details, dates, authors, messages, and changed paths) to a text file and opens it in your default text editor.

**Renaming a commit message (R button):**  
Next to each commit row, there is an **R** button. It is active only for your own commits. When you click **R**, the current commit message is automatically loaded into the **Rename field** at the top of the panel. You can then edit the message freely in that field. Press Enter to save the new message – the graph refreshes and the updated message appears immediately. This allows you to correct typos or improve descriptions of your changes without rewriting history.

**When to use it:**
- To understand the branching structure and merge history.
- To track when a specific change was introduced – filter by file path and see all related commits.
- To prepare release notes or documentation.
- To fix or clarify your own commit messages directly from the graph.

## Help
Opens this help documentation inside the application.

## Exit
Safely closes the application. Any running SVN operations are cancelled before shutdown.

## File Tree Toolbar
The buttons located near the file tree give you quick access to everyday actions on your local changes.

- **Check Remote** – quickly shows which files on the server have been changed by others, without downloading anything.
- **Commit** – opens the Commit panel, where you can review and send your local changes to the repository. The panel itself contains the following buttons:
  - **Refresh** – rescans the working copy and rebuilds the list of files ready to commit.
  - **Commit All** – executes a full, four‑step process: (1) cleans up the database, (2) scans and fixes missing files, (3) adds all new unversioned items, and (4) sends everything to the server.
  - **Commit Selected** – commits only the files you have checked in the main tree.
  - **Cancel Commit** – safely aborts a running commit and runs a cleanup to keep the database healthy.
- **Add** – tells SVN to start tracking unversioned items (`?`). Works globally or per‑file.
- **Revert All Missing** – restores files deleted outside of SVN (marked `!`).
- **Fix Missing** – removes SVN records of physically deleted files, clearing the `!` status.
- **Discard Untracked** – permanently deletes all untracked (`?`) files.
- **Revert** – discards all local modifications in the entire working copy. A double‑click confirmation is required.
- **Cancel Revert** – immediately stops an ongoing Revert operation.

All actions automatically refresh the file tree after completion.

## Lock Toolbar
The **Lock** toolbar helps you manage file locks on the repository.

**Main buttons:**
- **Show Locks** – fetches and displays all active locks currently set on the repository.
- **Lock All Modified** – locks every file that you have locally modified (`M` status).
- **Unlock All Modified** – releases all locks that you currently hold.
- **Break All Locks** – removes internal database lock records locally without affecting the server.
- **Clear Locks View** – clears the list of displayed lock info from the panel.
- **Steal Locks** – opens a dedicated panel for forcefully taking over locks that belong to other users.

**Steal Locks panel:**  
When you open **Steal Locks**, a new panel appears showing all locks. It has its own **Refresh Locks** button, and each lock entry has a **Steal** button. Clicking **Steal** forcefully transfers that lock to you, allowing you to commit changes to a file that was previously locked by someone else.

## Utility Toolbar
Located directly below the Lock Toolbar, the **Utility Toolbar** gives you immediate access to log management, connection testing, revision‑based operations, and the built‑in terminal.

**First row of buttons:**
- **Show Log** – opens the dedicated log viewer.
- **Clear Log** – erases all content from the internal application log console.
- **Open Log File** – opens the current raw session log file in your default text editor.
- **Test Connection** – verifies that the configured repository URL and SSH key can reach the server.
- **Update to Revision** – updates your working copy to a specific revision number. Leave empty for HEAD. Double‑click confirmation required for non‑HEAD updates.
- **Export Revision** – exports a clean copy (without `.svn`) of a specific revision. Uses the same revision input field.

**Second row of buttons (Terminal):**
- **Run CLI Command** – executes the raw SVN command typed in the terminal input field.
- **Cancel CLI Command** – immediately stops the currently running terminal command.

## Per‑File Action Buttons
Each file row in the tree displays small, single‑letter buttons depending on the file's status. Hovering over any button shows a tooltip.

- **Add (A)** – visible for unversioned files (`?`). Tracks the file with SVN.
- **Revert (R)** – visible for modified/replaced files. Discards local changes.
- **Log (L)** – opens the SVN Log for that file.
- **Blame (B)** – shows who last modified each line.
- **Explore (E)** – opens the file's location in Windows Explorer.
- **Lock / Unlock (K / O / U)** – indicates lock state:
  - **K** (green) – you own the lock, click to unlock.
  - **O** (red) – locked by another user.
  - **U** (grey) – unlocked, click to lock.
- **Resolve (C)** – visible for conflicted files. Opens the Resolve panel.
- **Commit (V)** – visible for committable files. Commits only this single file.

## Notifications
When the application detects a new commit from another user, it plays a sound and shows a notification pop‑up with the author, revision, and commit message. Automatic checking happens when the app regains focus (cooldown: 3 minutes).

## File Status Legend (Understanding the Icons)
- **? (Unversioned)** – New file not yet tracked by SVN. Use **Add**.
- **M (Modified)** – Local changes not yet committed.
- **A (Added)** – Scheduled for addition; will be uploaded on next commit.
- **! (Missing)** – Deleted manually outside SVN. Blocks commits. Use **Fix Missing** or **Revert**.
- **D (Deleted)** – Officially scheduled for deletion.
- **C (Conflicted)** – Both you and someone else modified the same lines. Must be resolved.
- **I (Ignored)** – Excluded from repository.
- **R (Replaced)** – Replaced by merge/switch. Commit or update to clear.
- **K (Locked by me)** – You have locked this file.
- **O (Locked by other)** – Locked by another user; cannot commit until unlocked.

## Terminal & Custom SSH Keys

Most SVN commands that communicate with the remote server (`checkout`, `update`, `commit`, `log`, `diff`, `status`, `list`, `merge`, `switch`, `export`, `import`, `lock`/`unlock`, and `blame`) automatically use the default SSH key configured in your application settings. You do not need to specify it manually for every operation.

If you ever need to override the default and use a different key for a single command, simply add the `--key` option right after the command name.

General syntax:
> command --key "C:/Users/[YourUsername]/.ssh/[KeyFile]" [rest of the arguments].

Examples include checking out with a specific key:
> checkout --key "C:/Users/PC/.ssh/project_key" svn+ssh://user@server/srv/svn/repo "D:\Projects\Repo";

updating using a different key:
> update --key "C:/Users/PC/.ssh/admin_key";

showing log with a custom key:
> log --key "C:/Users/PC/.ssh/readonly_key" -l 10;

and committing with a temporary key:
> commit --key "C:/Users/PC/.ssh/committer_key" -m "Message".

Remember: if you omit the --key parameter, the terminal will fall back to the key configured under Settings > SSH Private Key. Use --key only when you need to temporarily override it.

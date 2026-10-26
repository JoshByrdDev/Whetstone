# Setting up Whetstone on a new machine

Read this once, top to bottom, before the first run. It is written for a person or an agent doing the
install: every step says how to check it worked, because several of the things below fail
**silently** when they are missing.

Whetstone runs on Windows only.

Once it is running, **System → Setup check** checks most of this list for you: the agent CLIs your
roles use, git and its identity in each project's repo, Node and npx, the narrator, and why the
heartbeat last refused to start, and whether each project folder has a remote. Press **Check again** after installing something, and restart Whetstone
first, because neither it nor the heartbeat sees a program installed after it started.

## 1. Required

| What | Why | Check |
|---|---|---|
| **.NET 9 SDK** | Runs the app | `dotnet --list-sdks` shows a `9.` line |
| **Windows PowerShell 5.1** (`powershell`, not `pwsh`) | The heartbeat, every worker, the guard hook, notifications | `powershell -NoProfile -Command '$PSVersionTable.PSVersion'` |
| **Git for Windows** | Worktrees, branches, diffs | `git --version` |
| **A git identity** | Workers commit in their worktrees. Nothing checks this up front: without it the first implement run fails with git's "tell me who you are" | `git config --global user.name` and `user.email` both print something |
| **At least one agent CLI, signed in**: `claude` (the default for every role), `codex` or `gemini` | Every pipeline phase, the steward, Classroom analyses | `claude --version`, then `claude auth login` once |
| **Node.js (with `npx`)** | QA and PR review drive a browser through `npx @playwright/mcp`, which fetches itself and its browser on first use. Without Node, QA tickets go to **Blocked** after two failed tries, and nothing warns you sooner | `node --version`, `npx --version` |

The PowerShell scripts are all started with `-ExecutionPolicy Bypass`, so you do not need to change the
execution policy, unless Group Policy enforces one on this machine.

Long paths: worktrees nest deep. If a checkout fails with "Filename too long", run
`git config --global core.longpaths true`.

## 2. Optional, by feature

| Feature | Needs | Without it |
|---|---|---|
| **Classroom walkthrough voice-over** | `npm install` once in `data/narrator/`. The first voiced walkthrough also downloads a ~90MB voice model (Kokoro), so the first one is slow and needs internet | Walkthroughs play with captions only. The Classroom shows a "Voice-over is not set up" notice above each walkthrough. Walkthroughs written before the install stay silent until you press **rewrite** |
| Semantic code search for workers | `auggie` (Augment CLI) on `PATH`, signed in | Workers fall back to grep. Nothing is logged |
| Codex or Gemini as an agent | That CLI on `PATH` and signed in (`~/.codex/auth.json`; gemini login or `GEMINI_API_KEY`) | The heartbeat refuses to start (see §5). A missing login only shows up in the heartbeat log |
| "Create PR" on a GitHub remote | `gh` on `PATH`, signed in | Falls back to opening GitHub's compare page, and says so |
| TFVC projects | `tf.exe` (Visual Studio Team Explorer), or `tfPath` in `data/config.json` | Check-in fails with a clear "TF.exe was not found" |
| A project's local stack (Start/Stop, QA) | Whatever that project's `stack` commands call, usually Docker Desktop | The stack chip shows the failing command's output |
| Slack / ntfy notifications | A webhook URL or topic in `data/config.json` | No notifications. Settings → Notifications has a **Send test** button |
| Jira / Azure Boards links | A token in that project's `project.json` | The issue picker says the tracker is not configured |
| Editing the client | `npm ci` in `ClientApp/` | Nothing breaks: the built client is committed in `wwwroot/` |

## 3. First run

```bash
dotnet run --project Whetstone.csproj
```

Or double-click `Start-Whetstone.bat`, which also opens the browser. The app listens on
http://localhost:5338. The database (`data/dashboard.db`) is created and migrated on first start.

Then:

1. **Settings → Projects → Add**: a name and the path to a git repo. This creates
   `data/projects/<key>/` with its config and skills copied from `data/skill-templates/`.
   A project whose code is several repos side by side takes the folder they are in as its path,
   and lists them under `repos` in `project.defaults.json` (see `docs/multi-repo-plan.md`). A path
   that is not a git repo and has no `repos` is refused: none of its tickets are claimed.
2. Give that folder a private remote and push it. Adding the project already made the folder a git
   repo, so this is the one step left. See [§4](#4-each-project-folder-is-its-own-repo).
3. Hand-edit `project.defaults.json` for what the form does not cover: `stack`, `phaseResources`,
   `memoryDir`. Also in `data/README.md`.
4. Optional: copy `data/config.example.json` to `data/config.json` for notifications, extra Claude
   accounts, `tfPath`. Every key you leave out falls back to `data/config.defaults.json`.
5. Write a ticket, then start the heartbeat from the header.

## 4. Each project folder is its own repo

`data/projects/<key>/` holds everything Whetstone knows about one project: the skill files that tell
each phase's worker what to do, the project's settings, and its own guard rules. You will edit the
skills as you learn what the project needs, and they are worth keeping.

They are deliberately **not** in the Whetstone repository. Whetstone's `.gitignore` excludes
`data/projects/*/`, because a project folder describes one machine's repos, paths and infrastructure,
and nobody else's clone should receive it. So each folder is a git repo of its own, nested inside the
Whetstone checkout, where the parent repo cannot see it: no submodule, and nothing shows up in
Whetstone's `git status`.

**What Add project does for you.** It writes a `.gitignore` into the folder, runs `git init`, and
commits everything as a baseline. The `.gitignore` keeps out `project.json` (your repo path, tracker
token and QA credentials) and the files the fleet writes at runtime. The commit uses your git identity,
or a stand-in if you have none. The Settings page shows **No remote** on the project until the next
step is done.

**What you do: give it a private remote.** Create an **empty, private** repository wherever you keep
code (GitHub, Azure Repos, a bare repo on another disk). Name it something like `<key>-whetstone`, and
don't let the host add a README, so the first push is not refused. Then:

```bash
git -C data/projects/<key> remote add origin <the empty repo's URL>
```

```bash
git -C data/projects/<key> push -u origin HEAD
```

After that, commit and push the folder as you would any repo. Whetstone makes no commits there after
the baseline. When you edit a skill, or merge a template change into one from the Skills page, the
files change and committing them is up to you. The Setup check on the System page flags a folder with
no history or no remote.

**On a second computer**, these repos are not cloned back into `data/projects/`. Paths and stacks
differ between machines, so each computer adds its own projects and gets its own folders. Copying
skills across is a deliberate edit, not a sync.

**A project folder made before this existed** may have no repo, or may still be tracked by an older
Whetstone commit. `data/migrate-projects-to-nested-repos.ps1` handles the second case, on the
machine that owns the folder (run it with `-DryRun` first). For the first, create the repo by hand.
The `.gitignore` must be in place **before** the first `git add`:

```bash
git -C data/projects/<key> init
```

Then copy the ignore list from [`Services/ProjectRepo.cs`](../Services/ProjectRepo.cs) (`GitIgnore`)
into `data/projects/<key>/.gitignore`, run `git add -A` and commit there, and add the remote as above.

## 5. Ports

| Port | What |
|---|---|
| 5338 | The app |
| 5173 | Vite dev server, proxying to 5338 (only when editing the client) |
| 5340 / 5341 | An isolated QA copy (`data/qa-copy.ps1`), for proving changes to Whetstone itself |

The API has no authentication. Reaching it from another device (the phone view, a worker host) is done
through a private network or a firewall rule scoped to that one address. Never open 5338 to the LAN as a
whole. Worker hosts have their own guide: [`worker-host-setup.md`](worker-host-setup.md).

## 6. Checking it works

| Check | Where |
|---|---|
| The app bar's usage meter shows numbers, not "no credentials" or "expired" | The Claude login is good |
| System → **Setup check** shows nothing missing | Agent CLIs, git identity, Node, the narrator, project folders backed up, each project's path a git repo or a set of them |
| The heartbeat stays **Running** after you start it | If it flips back to Stopped, the reason is shown beside the Start button on the Heartbeat page and at the top of the Setup check. Usually it is an agent CLI missing from `PATH` |
| Settings → Notifications → **Send test** | Notifications |
| A first small ticket gets through QA, not Blocked | Node and Playwright |

## 7. What never leaves this machine

`data/config.json`, every `project.json`, and `data/projects/*/` are gitignored and are this machine's
alone. Each project folder is its own nested repo with its own private remote. A second computer gets
its own projects, set up again from [§3](#3-first-run).

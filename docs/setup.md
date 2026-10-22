# Setting up Whetstone on a new machine

Read this once, top to bottom, before the first run. It is written for a person or an agent doing the
install: every step says how to check it worked, because several of the things below fail
**silently** when they are missing.

Whetstone runs on Windows only.

Once it is running, **System → Setup check** checks most of this list for you: the agent CLIs your
roles use, git and its identity in each project's repo, Node and npx, the narrator, and why the
heartbeat last refused to start. Press **Check again** after installing something, and restart Whetstone
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
2. Give that folder its own git history (the form does not yet). See "Adding a project" in
   [`data/README.md`](../data/README.md), and copy a `.gitignore` in first so `project.json` and its
   secrets are never committed.
3. Hand-edit `project.defaults.json` for what the form does not cover: `stack`, `phaseResources`,
   `memoryDir`. Also in `data/README.md`.
4. Optional: copy `data/config.example.json` to `data/config.json` for notifications, extra Claude
   accounts, `tfPath`. Every key you leave out falls back to `data/config.defaults.json`.
5. Write a ticket, then start the heartbeat from the header.

## 4. Ports

| Port | What |
|---|---|
| 5338 | The app |
| 5173 | Vite dev server, proxying to 5338 (only when editing the client) |
| 5340 / 5341 | An isolated QA copy (`data/qa-copy.ps1`), for proving changes to Whetstone itself |

The API has no authentication. Reaching it from another device (the phone view, a worker host) is done
through a private network or a firewall rule scoped to that one address. Never open 5338 to the LAN as a
whole. Worker hosts have their own guide: [`worker-host-setup.md`](worker-host-setup.md).

## 5. Checking it works

| Check | Where |
|---|---|
| The app bar's usage meter shows numbers, not "no credentials" or "expired" | The Claude login is good |
| System → **Setup check** shows nothing missing | Agent CLIs, git identity, Node, the narrator |
| The heartbeat stays **Running** after you start it | If it flips back to Stopped, the reason is shown beside the Start button on the Heartbeat page and at the top of the Setup check. Usually it is an agent CLI missing from `PATH` |
| Settings → Notifications → **Send test** | Notifications |
| A first small ticket gets through QA, not Blocked | Node and Playwright |

## 6. What never leaves this machine

`data/config.json`, every `project.json`, and `data/projects/*/` are gitignored and are this machine's
alone. Each project folder is its own nested repo with its own private remote. A second computer gets
its own projects, set up again from step 3.

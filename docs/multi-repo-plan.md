# Multi-repo projects: one project, several repos

Implementation plan, phase 1. A **multi-repo project** is one project, with one tracker and one
board, whose code lives in several git repos side by side under a folder that is not itself a
repo. Each of those repos is a **member**. A ticket can change any of them. What it delivers is
one pull request per member it changed, all on the same branch name.

Phase 1 covers the Delivery track on the board host, for projects that deliver by pull request.
The Review track, the steward, worker hosts, `mergeInsteadOfPr` and TFVC are each refused for a
multi-repo project until a later phase designs them (see "Refused in phase 1").

## What it is for

Some products are split across repos that only make sense together: a web client, the API it
calls, and the infrastructure both run on. A ticket for a feature in that product reads the API to
know what the client can ask for, and often changes both. Pointing Whetstone at one of the repos
hides the others from the agent. Pointing it at three separate projects imports the same tracker
three times and splits one feature into tickets that cannot see each other.

Pointing a project at the parent folder today is worse than either. `Resolve-Worktree` finds no
`.git` there, returns `deferred`, and the worker runs **directly in the human's own checkouts**,
with no worktree, no branch and no isolation. Phase 1 closes that hole as well as opening the
intended path.

## What stays exactly as it is

Every project that exists today. A project with no `repos` key is a set of one member, whose
folder is `projectPath` itself and whose worktree *is* the ticket's workspace, at the same path as
now. The single-repo case is not a second code path. It is the loop below, run once, and the tests
for it are today's tests, unchanged.

Also unchanged: claims, leases, phases, the MCP tools, the tracker, the stack lease, the local-only
rule and the guard. A multi-repo run reaches nothing a single-repo run could not.

## Shape of the solution

```
projectPath (not a repo)               worktreeRoot/<branch-dir>/  = the ticket's workspace
-----------------------------          ------------------------------------------------------
C:\Projects\Alpha\                     C:\Projects\.worktrees\alpha\jb-PROJ-123\
  alpha-web\    (repo, hub)  -------->   alpha-web\    worktree of alpha-web,   branch jb/PROJ-123
  alpha-api\    (repo, hub)  -------->   alpha-api\    worktree of alpha-api,   branch jb/PROJ-123
  alpha-infra\  (repo, hub)  -------->   alpha-infra\  worktree of alpha-infra, branch jb/PROJ-123
                                         CLAUDE.md     copied from data/projects/alpha/workspace/
```

**The workspace has the parent folder's layout.** The agent starts at the workspace root and finds
each member under the same folder name the human sees. A path written in a note, a skill or a
comment (`alpha-api/src/...`) means the same thing in both places.

**The workspace root is a plain folder, not a repo.** No superproject and no submodules. Git
worktrees handle submodules poorly. A superproject would also be a repo nobody else on the
project's team has, and the delivery would still have to be split into one PR per member at the
end.

**A member is in the ticket once it is touched.** Touched means the member's branch has commits
ahead of its base ref, or its worktree has uncommitted changes. A member nobody touched gets no
leftover commit, no squash, no rebase, no push and no PR. It exists in the workspace only so the
agent can read it.

## The pieces

### 1. Config (`Services/ProjectRegistry.cs`)

`repos` in `project.defaults.json` (shared) or `project.json` (machine-local), the latter winning
per member as usual:

```json
"repos": [
  { "dir": "alpha-web",   "worktreeSetup": ["pnpm install --frozen-lockfile"], "worktreeCopyFiles": [".env.local"] },
  { "dir": "alpha-api",   "worktreeSetup": ["dotnet restore"] },
  { "dir": "alpha-infra", "baseBranch": "main" }
]
```

| Field | Per member | Falls back to |
|---|---|---|
| `dir` | required, a folder directly under `projectPath` | - |
| `baseBranch` | yes | the project's `baseBranch`, then the global one |
| `worktreeSetup`, `worktreeCopyFiles` | yes | nothing (the project-level keys belong to the set-of-one case) |
| `branchPrefix`, `mergeInsteadOfPr`, `repoType` | **no**: one branch name, one way of delivering | - |

`ProjectConfig` gains `Repos` (`IReadOnlyList<RepoMember>`), always at least one entry, and
`IsMultiRepo`. With no `repos` key, the single member has `Dir = ""` and the project-level base,
setup and copy keys. Order is config order, and everything below that lists members uses it.

**Config errors are refusals, not warnings.** A multi-repo project with `repoType: tfvc` or
`mergeInsteadOfPr: true`, a `dir` that is not a git repo, or two members naming one folder is
reported as misconfigured by the Setup check, and nothing is claimed for it.

**The silent fallback is closed for every project.** A `projectPath` that is not a repo, has no
`repos` key and has git repos directly beneath it is reported by the Setup check as "this looks
like a multi-repo project", and claims for it are refused. A path with no repo at all and nothing
beneath it keeps today's behaviour (open question 6).

`ResolvedWorktreeRoot` is unchanged: a sibling of `projectPath`, which is now the parent folder.
`Stack.WorkingDir` already resolves against `projectPath`, so a stack in one member is
`"workingDir": "alpha-api"`.

Tests (`Whetstone.Tests`, filter `MultiRepo`): set-of-one equivalence for every existing key,
per-member fallbacks, each refusal, and the "looks like multi-repo" detection.

### 2. The member loop (`data/worker-repos.ps1`, dot-sourced by `worker.ps1`)

Built and tested first, against temporary repos and with no fleet involved:
`data/worker-repos.tests.ps1`, in the style of `worker-board.tests.ps1`.

- `Get-RepoMembers` reads the claim's `project.repos` and returns each member's hub path, worktree
  path, base branch, setup and copy lists. A claim with no `repos` returns the set of one.
- `Resolve-Workspace` creates the workspace folder, then calls today's `Resolve-Worktree` once per
  member, with that member's hub as `-RepoDir` and the workspace folder as `-WorktreeRoot`, so each
  member's worktree is `<workspace>\<dir>`. A member's own `dir` is used for the folder name, not
  the branch. Each call takes that hub's own `hub-<path>` mutex, one at a time and never nested.
- **Every member gets the branch, at speculate.** Creating a branch costs nothing, and an agent that
  had to ask for a member before it could edit it would ask in the middle of a change. Each member
  forks from its own `Resolve-BaseRef`.
- **A later phase that finds a member without the branch creates it from that member's base**,
  rather than failing as a single-repo project does. That happens when a member is added to `repos`
  mid-ticket, and an untouched member's branch is the base anyway. At least one member must have
  the branch outside speculate, or it is still the upstream error it is today.
- **Untouched members are brought up to their base at the start of every phase**, with
  `Update-MergedBranch`. A member with nothing of its own has nothing to lose, and the agent should
  read the API as it is now, not as it was when the ticket was speculated.
- `Get-TouchedMembers` returns the members with commits ahead of their base ref or a dirty
  worktree. This rule is written twice, here and in `TicketGitService` (piece 4), and the two must
  agree, the same way the base-ref rule does. The tests check both sides against the same repos.
- A member whose setup fails blocks the ticket, naming the member. The worktrees already made stay,
  and the next run re-enters them, as `Resolve-Worktree` already does for one.

Setup runs member by member, in config order, in the same `Initialize-Worktree` as today. Running
it in parallel, or only for members that get touched, is open question 1.

`worker-base.tests.ps1` gains cases with two members on different bases, to prove `Resolve-BaseRef`
is asked per member and that no member's answer leaks into another's.

### 3. Worker (`data/worker.ps1`)

- `$workDir` is the workspace root. `Set-TicketBranch` records the one branch name and the
  workspace path, so `Ticket.WorktreePath` keeps meaning "where the agent ran".
- The hand-off steps run once per touched member, in config order: `Save-WorkerLeftovers`,
  `Compress-TicketCommits` (one ticket, one commit **per member**), `Update-BranchFromBase`. A
  conflict in one member is reported and undone for that member only. The others' clean rebases
  stand, because they are separate PRs. Refinement's existing rule for a conflicted hand-off
  applies if any member conflicted.
- Every comment these steps write names the member: "`alpha-api` was 3 commits behind
  origin/main ...". A comment that says "the branch" on a multi-repo ticket is a bug.
- A touched member with uncommitted changes at the start of a phase blocks, as today. A dirty
  untouched member cannot exist: dirty is one of the two ways to be touched.
- `Get-ConventionsFile` is asked against the workspace root, where the copied workspace file
  (piece 6) sits. Each member's own `CLAUDE.md` / `AGENTS.md` stays in its member, where the
  providers already find it when the agent works in that folder.
- auggie's `-w` is the workspace root, so semantic search covers every member.
- The prompt gains `{{REPOS_NOTE}}` (piece 5). `{{BASE_REF}}` becomes one line per member on a
  multi-repo run (`alpha-api: origin/main`), and stays the bare ref on a single-repo run.

A claim with no `repos` takes the set-of-one path, which is today's code. That is what
`worker-repos.tests.ps1` proves first.

### 4. API (`Services/TicketGitService*.cs`, `Api/GitEndpoints.cs`, `Api/TicketEndpoints.cs`)

**The claim** carries `project.repos`: each member's `dir`, `path`, `baseBranch`, `worktreeSetup`
and `worktreeCopyFiles`. The project-level `baseBranch`, `worktreeSetup` and `worktreeCopyFiles`
stay in the claim for the set of one.

**One repo per call, chosen by `?repo=<dir>`.** Each public method in `TicketGitService` that
starts from `cfg.ProjectPath` takes the member instead: checkout, preflight, base check, rebase,
force-push and PR create. With no `repo` on a single-repo project it means the one member, so the
routes stay the same for every existing caller. On a multi-repo project a missing `repo` is a 400.

**The PR preflight answers for the whole ticket.** `GET /git/pr` returns `repos: [...]`, one entry
per member, each with `touched` and its own preflight, base check, commits and files. A single-repo
project returns a list of one, which is the shape the client reads for both.

**Files are named with their member as the first segment** (`alpha-api/src/Foo.cs`), in
`ChangedFilesAsync`, `FileDiffAsync` and `FileAtAsync`. That is also the path a review comment is
anchored to, so comments, the diff list and the steward's later file checks need no schema change.
`FileDiffAsync` splits off the first segment to find the member. On a single-repo project there is
no prefix, so every stored comment keeps its path.

**PR create stays one PR per call.** The dialog (piece 7) calls it once per touched member. Each
description ends with a block naming the other touched members and the shared branch, so a
reviewer opening one PR knows the others exist. Azure DevOps creation stays the prefilled web form
it is today, so the dialog opens one form per member. A real `az repos pr create`, which could link
the PRs to each other and to the work item, is open question 3.

**`BaseRefAsync` and the touched rule** are asked per member. Their agreement with piece 2 is
tested from both sides.

Refused with a clear error on a multi-repo project: `/git/merge`, every `/git/checkin*` route and
`/tfvc/sync`. Each belongs to a delivery mode the config already refuses, so reaching one is a bug.

Tests (`Whetstone.Tests`, filter `MultiRepo`): preflight over two members with one touched, the
prefixed diff and file reads, rebase and push choosing the right member, the 400 for a missing
`repo`, the refused routes, and the set-of-one case returning today's answers.

### 5. Prompt and skills

- `data/heartbeat-prompt.md` gains `{{REPOS_NOTE}}`. On a single-repo run it says nothing. On a
  multi-repo run it says: this workspace holds these repos, each its own git repo, all on branch
  `<branch>`. Run git inside a member (`git -C alpha-api ...`), never at the root, which is not a
  repo. Commit in every member you change. A member you did not change is delivered as nothing,
  so do not commit to it to "keep it in step".
- The rule "commit at the end, one ticket one commit" says "per repo you changed" where it appears.
  That is in the prompt and in the implement and refine skills, the same three places the
  attribution rule lives, and they stay in step.
- **The skills get one shared paragraph, "Several repos".** It is a pipeline rule, so it goes into
  every template and every project copy verbatim (see "A skill exists more than once" in
  `CLAUDE.md`). It is conditional ("when `{{REPOS_NOTE}}` lists more than one repo") so a
  single-repo project reads past it. It says: `<base-ref>` is per member; diff, log and test each
  member you touched; name the member in every outcome and comment.
- What order the members' PRs should merge in (an API change before the client that calls it) is
  the project's, and goes in that project's skills, never here.

### 6. Around the edges

- **`WorktreeReaper`** removes each member's worktree from its own hub, then deletes the workspace
  folder once it is empty. A member it cannot remove keeps the folder and the recorded path, as a
  single worktree does today.
- **`AnalysisRunner`**: the "Repo root" line becomes "Workspace root" with the member list, and
  the run's directory is the workspace, which it already is through `WorktreePath`.
- **`QaIsolation`** checks every member's path against the data root, not only `projectPath`.
- **`data/qa-copy.ps1`** gains a multi-repo placeholder project (`bravo`, two members), so the PR
  dialog and the diff can be exercised on the QA copy.
- **The workspace file.** `data/projects/<key>/workspace/` is copied into the root of every new
  workspace: a `CLAUDE.md` (or `AGENTS.md`) that maps the members for the agent. It lives in the
  project's folder because what the members are, and how they talk to each other, is the
  project's.
- **The notes store.** There is no repo at the root to hold `.claude/memory`, so a multi-repo
  project sets an absolute `memoryDir` in its own project folder, the same as a project that is
  this repository does. Reflection commits there. A human working from the parent folder can
  junction the Claude Code auto-memory path for that folder to the same store, so interactive
  sessions read the same notes as the fleet.
- **`docs/setup.md` and `SetupCheck`** get no new install row: nothing here needs a tool a fresh
  clone lacks. The Setup check gains the per-member rows from piece 1.

### 7. UI

- **`PrModal`** draws one section per member, touched first, with untouched members collapsed under
  "not changed". Each touched section has its own base check, rebase, force-push and Create PR,
  and a "Create all" button runs them in config order.
- **`DiffReviewList`** groups files under a member heading, taken from the first path segment.
- **`TicketGitActions` and `ProjectGitBar`** show each member's ahead/behind count on a multi-repo
  ticket, and today's single line on a single-repo one.
- Every new surface uses the motion layer's classes (`orbit-pick` for a member row, `orbit-swap`
  for the collapse) and passes the 390px check in `CLAUDE.md`.

### 8. `CLAUDE.md`

A short section, "A project can be several repos", beside "The base is not always origin's". It
says that the touched rule is written twice (`Get-TouchedMembers` and `TicketGitService`) and must
agree, that a file path on a multi-repo ticket starts with its member, and which tests to run.

## Refused in phase 1

Each refusal names itself where it fires, so nothing is silently skipped.

| What | Why not yet | Where it is refused |
|---|---|---|
| Review track | A review target would need to name its member (`ReviewTarget.Repo`, a migration), and the workspace would need that member detached at their branch and the others at their bases | review ticket creation |
| Steward | Its checks read one worktree and one base (`StewardService`, `TicketGitService.Steward`) | the mode reads as `off` |
| Worker hosts | The branch round trip and the project kit are one repo each | a `Host` claim skips the project |
| `mergeInsteadOfPr` | A local merge across members is not atomic: one could land and another refuse | config (piece 1) |
| TFVC | Out of scope | config (piece 1) |

## Order of work

Each step is its own commit, and steps 1 to 4 change nothing for an existing project:

1. Config, refusals and Setup check rows + tests (piece 1)
2. Member loop + `worker-repos.tests.ps1`, and the two-member cases in `worker-base.tests.ps1`
   (piece 2)
3. Board side: claim, per-member routes, prefixed paths + tests (piece 4)
4. Worker wiring (piece 3)
5. Reaper, analysis, QaIsolation, the QA copy's placeholder project (piece 6)
6. UI (piece 7), proven on the QA copy against the placeholder project
7. Prompt, skill templates, every project's skill copies, `CLAUDE.md` (pieces 5 and 8)
8. First live run: a ticket that touches one member, then one that touches two. Its PRs are
   created by hand from the dialog, and the untouched member is checked to have nothing pushed.

Step 8 is the only step that cannot be proven on the QA copy, which refuses agent runs by design.

## Open questions (the owner's)

Phase 1 will be built on the planned answer to each of these unless the owner says otherwise.

1. **When each member's setup runs.** Planned: every member, in order, when its worktree is made.
   That is simple and always ready, but a ticket that only changes the API still waits for the web
   client's package install. The alternatives are running members' setup in parallel (each is its
   own process in its own folder already), or setting a member up only once it is touched. The
   second needs the agent to be able to ask for it mid-run.
2. **Should a member be able to sit out a project?** Planned: every member is in every workspace.
   An infrastructure repo that no ticket should change could instead be marked `readOnly`, made as
   a detached worktree with no branch, so it cannot be touched at all.
3. **Real PRs on Azure DevOps.** Planned: the prefilled web form, one per member, as today. `az repos
   pr create` would make real PRs that link to each other and the work item, but it is a new CLI
   to install and sign in (a `docs/setup.md` row and a Setup check row).
4. **Merge order between members.** Planned: the board lists members in config order and says
   nothing about order. The project's skills tell the agent, and the PR description tells the
   reviewer. Should the board know an order, and warn when a later member's PR is merged first?
5. **A ticket that touches no member.** Planned: the same as a single-repo branch with no commits
   today, with the same refinement rule. Confirm that "nothing to deliver" reads correctly when
   there are three repos it could have been in.
6. **A `projectPath` that is not a repo and has none beneath it.** Planned: today's behaviour (run
   in the folder, with a warning in the log). Should that refuse too, now that the multi-repo
   case is caught separately?
7. **Phase 2 order.** Planned: the Review track first, because reviewing colleagues' PRs in one
   member is everyday work on a project like this, then the steward, then worker hosts.

---
name: whetstone-sync
description: Bring this machine's Whetstone main level with origin - fetch, rebase the local commits on top, rebuild one wwwroot, port every changed skill template into each project's copy and record its baseline, test, and push. Use when the user types /whetstone-sync, or says "pull latest main and push our changes", "sync whetstone", "merge origin and update the skills".
---

# Sync Whetstone main with origin

The repo is pushed from more than one machine, so local `main` and `origin/main` drift apart. This
brings them together and makes sure every project on this machine reads the skill text the merged
templates now say. The user asking for this is the permission to push `main` - not the project
folders, which have their own remotes and are never pushed from here.

Keep a task list: check state, rebase, skills, wwwroot, test, push.

## 1. See where things stand

```bash
git fetch origin && git status -sb && git log --oneline origin/main..main && git log --oneline main..origin/main
```

- Nothing behind: skip to step 3 only if the user still wants the skills checked; otherwise push and stop.
- Uncommitted changes: look at them before anything else. Stash what is noise (a lockfile that only
  lost `"peer": true` lines is npm drift) with a message saying what it is; ask about real work.
  Leave untracked runtime files (`data/heartbeat.wake`, `data/shots/`) alone.
- Take a safety branch: `git branch backup/pre-sync-<date> main`. Delete it once the push lands.

## 2. Rebase, skipping the wwwroot builds

```bash
git rebase origin/main
```

`wwwroot:` commits are build output with hashed file names, and every one of them conflicts. Skip
each (`git rebase --skip` when `REBASE_HEAD`'s subject starts with `wwwroot:`); step 4 makes one fresh
build. Any other conflict is real: read both sides and resolve it, never `--skip` a source commit.

Migrations: two machines may each add one. Check the snapshot came out whole, after a build:

```bash
dotnet build -c Release
dotnet ef migrations has-pending-model-changes --configuration Release --no-build
```

It must say no changes. If it does not, the `DashboardDbContextModelSnapshot.cs` merge lost one
side - fix the snapshot by hand; do not generate a new migration to paper over it.

## 3. Port the skill templates - every copy, both directions

Read CLAUDE.md's "A skill exists more than once" first. The templates that changed:

```bash
git diff --stat <old-origin>..origin/main -- data/skill-templates   # what origin brought
git diff --stat <old-origin>..HEAD~0 -- data/skill-templates        # what this machine had
ls -d data/projects/*/                                              # every project here, in heartbeatProjects or not
```

For each project and each changed template, decide whether its copy already carries the change
(grep a distinctive new sentence). Our own side's changes are usually ported already; origin's
never are. To port, turn the template diff into a patch on the copies and apply it three-way:

```bash
git diff <old-origin>..origin/main -- data/skill-templates > "$SCRATCH/t.patch"
sed "s#data/skill-templates/#skills/#g" "$SCRATCH/t.patch" > "$SCRATCH/p.patch"
(cd data/projects/<key> && git apply -p1 --3way "$SCRATCH/p.patch")
```

Resolve each conflict on its merits:

- **A pipeline rule** goes in verbatim.
- **The project's own local text** (its compose command, its database, its examples) stays, with the
  new rule placed beside it.
- **A section the copy never had** that the board now enforces in code (a gate, a refused move) is
  older drift: take the template's section whole and say so in the report.

Then check, per copy: no conflict markers left, every new rule's sentence present, and `diff` against
the template shows only that project's own text.

Record the baselines, so the Skills page stops reporting those copies as behind:

```bash
pwsh -NoProfile -File .claude/skills/whetstone-sync/record-baseline.ps1 -Project <key> -Phase qa-test,refine
```

Commit in each project folder (`git -C data/projects/<key> add skills template-base`). If
`template-base/` already had uncommitted changes the board wrote for earlier ports, they belong in
the same commit. Message: what the rule is for, and `(template change, pipeline rule)` or whichever of
the three kinds it is. No AI attribution trailer. Never push a project folder.

## 4. One fresh wwwroot

```bash
cd ClientApp && npm run build && cd .. && git add wwwroot && git commit -m "wwwroot: the client with <what changed on both sides>"
```

## 5. Test

Run the .NET suite in the background while the PowerShell suites for whatever the merged commits
touched run in the foreground (CLAUDE.md lists them by area; `data/*.tests.ps1`):

```bash
dotnet test Whetstone.Tests -c Release
```

A failure stops the sync: report it with its output, do not push.

## 6. Check and push

The project-name check over everything about to leave the machine, with every key and display
name from `data/projects/*/project*.json`:

```bash
git diff origin/main..HEAD -- . ':!wwwroot' | grep '^+' | grep -niE '<keys and display names>'
git push origin main
```

Delete the backup branch, and mention any stash you made.

## 7. Report

What came from origin, what was rebased on top, which wwwroot commits were dropped for the fresh
build, each project's skill ports and any conflict resolved by judgment, the test counts, and the
push. The live board (5338) still serves the old build: offer *Stop, restart Whetstone, resume*
(`POST /api/heartbeat/restart-after-drain`), which drains and kills nothing - never `dotnet run` it.

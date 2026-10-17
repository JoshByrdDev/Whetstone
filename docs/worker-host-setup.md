# Setting up a worker host

A checklist for giving a board berths on a second machine. The design is in
`docs/worker-hosts-plan.md`. Each step ends with a check: don't start the next step until the check
passes.

Names used below. Replace them with yours:

| Placeholder | Means |
|---|---|
| `BOARD` | the board host's machine name; `192.168.1.20` is its LAN address |
| `WORKER` | the worker host's machine name; `192.168.1.30` is its LAN address |
| `bravo-host` | the name the worker host goes by in the board's config |
| `alpha` | a project key; `C:\src\alpha` is its repo on the board host |

Phase 1 sends a worker host only `speculate`, `implement` and `refine`. QA stays on the board host.

## On the router

1. **Give both machines a fixed address**, with a DHCP reservation for each. The firewall rule and
   `dashboardUrl` both name an address, and an address that changes breaks both without a word.

   Check: `ipconfig` on each machine shows its reserved address after a reboot.

## On the board host

2. **Listen on the LAN.** In `data/config.json` add:

   ```json
   "lanUrl": "http://192.168.1.20:5338"
   ```

   It has to be an explicit address and port, never `0.0.0.0` or `*`. Restart the board from
   Explorer with `Start-Whetstone.bat`, never from a Claude session (see `CLAUDE.md`). Restarting
   kills the board host's in-flight workers, so do it while the fleet is idle.

   Check: the console prints `Listening for worker hosts on: http://localhost:5338, http://192.168.1.20:5338`.

3. **Let in the worker host, and nothing else.** In an elevated PowerShell:

   ```powershell
   New-NetFirewallRule -DisplayName "Whetstone from bravo-host" -Direction Inbound -Protocol TCP `
       -LocalPort 5338 -RemoteAddress 192.168.1.30 -Profile Private -Action Allow
   ```

   The API has no authentication. This rule is what keeps every other device on the network out
   of it.

   Check: from the worker host, `curl http://192.168.1.20:5338/api/hosts` answers `[]`. From
   any other device, it times out.

4. **Share the project repos.** Make a local account for the share (say `whetstone-git`). Share the
   folder that holds the project repos, and the Whetstone checkout itself, read/write for that
   account only.

   Check: from the worker host, `git ls-remote \\BOARD\src\alpha` lists the branches. If git
   answers `dubious ownership`, run this on the worker host and try again:
   `git config --global --add safe.directory "%(prefix)///BOARD/src/alpha"`

5. **Name the worker host.** In `data/config.json` add:

   ```json
   "workerHosts": [
     { "name": "bravo-host", "enabled": false, "slots": 4, "projects": ["alpha"] }
   ]
   ```

   Its slots are numbered from 5. `skills` defaults to `speculate`, `implement` and `refine`.
   Leave it `enabled: false` until step 12.

   Check: `GET /api/hosts` lists `bravo-host` with `online: false`.

## On the worker host

6. **Tools.** Install git, and the agent CLI logged in to this machine's own account, plus whatever
   the project's implement skill builds and tests with (its SDKs, Node, and so on). It needs no
   .NET, because this machine runs no board.

   Check: the CLI answers a one-line prompt. A project build runs in a scratch clone.

7. **Credentials for the share**, stored for the account the runner will run as:

   ```powershell
   cmdkey /add:BOARD /user:BOARD\whetstone-git /pass
   ```

   Check: `dir \\BOARD\src` works from a fresh PowerShell without asking for anything.

8. **A Whetstone checkout from the board host's share,** so the two are on the same commit:

   ```powershell
   git clone \\BOARD\Whetstone C:\Whetstone
   ```

   The board host gives this machine no work while its commit differs from the board host's.
   After every merge on the board host, run `git -C C:\Whetstone pull` here.

   Check: `git -C C:\Whetstone rev-parse HEAD` matches the same command on the board host.

9. **Point it at the board.** `C:\Whetstone\data\config.json`:

   ```json
   { "dashboardUrl": "http://192.168.1.20:5338" }
   ```

   Add `claudeAccount` and `claudeAccounts` only if this machine has more than one login.

10. **A clone per project:**

    ```powershell
    git clone -o board \\BOARD\src\alpha D:\src\alpha
    git -C D:\src\alpha checkout --detach
    ```

    The remote is called `board`, and the main checkout stays detached, because its base branch is
    a mirror the runs force-update. Copy across by hand, once, any gitignored files the project's
    `worktreeCopyFiles` names (an `.env`, say). They are not in git, so a clone does not have them.

    Check: `git -C D:\src\alpha remote` prints `board`, and `git -C D:\src\alpha status` says
    `HEAD detached`.

11. **Name this machine.** `C:\Whetstone\data\host.json` (gitignored):

    ```json
    {
      "name": "bravo-host",
      "projects": { "alpha": { "path": "D:\\src\\alpha" } }
    }
    ```

    `worktreeRoot` per project is optional. It defaults to `D:\src\.worktrees\alpha`, beside the
    clone, as on the board host.

    Check: `powershell -ExecutionPolicy Bypass -File C:\Whetstone\data\runner.ps1 -Once` exits 0.
    The board's `GET /api/hosts` then shows `bravo-host` with `online: true` and
    `commitMatches: true`.

12. **Start it at logon.** In Task Scheduler, create a task:
    - Trigger: at log on of this user.
    - Action: `powershell.exe -WindowStyle Hidden -ExecutionPolicy Bypass -File C:\Whetstone\data\runner.ps1`
    - Select "Run only when user is logged on". The CLI's login and the share's stored credential
      are this user's.
    - Clear "Stop the task if it runs longer than...".

    Never start the runner from a Claude session. Whatever owns its tree can kill every worker
    under it.

    Then on the board host, set `"enabled": true` for `bravo-host`. The orchestrator reads the hosts
    on every pass, so no restart is needed.

    Check: the runner's log (`C:\Whetstone\data\logs\heartbeat-<date>.log`) says
    `Runner 'bravo-host' on commit ...`, and the board's fleet view shows its berths.

## The first live run

13. Set `slots: 1` and a single project. File a small, low-stakes ticket and let it go through
    `implement` on the worker host. Then watch it come back: QA runs on the board host, against
    the branch the worker host handed back. Then send it back to implementation once by hand, so it
    goes worker host to board host to worker host.

    Check: the ticket's comments show no hand-back failure. The board host's branch has the worker
    host's commits. The worker host's log says `Handed '<branch>' back to the board host`.

## QA on the worker host (phase 2)

Optional. Leave it out and QA stays on the board host.

14. **Tools for QA, on the worker host.** Install Docker Desktop, plus whatever the project's stack
    needs to start there (its images, its seed data, the gitignored files its compose reads). The QA
    browser runs through `npx`, so Node too. Then bring the stack up by hand once, from the clone,
    the way the project's qa-test skill does.

    Check: the stack starts, and the app answers on the port the skill expects.

    For review runs, the clone also wants a real `origin` (fetch only):
    `git -C D:\src\alpha remote add origin <the project's remote URL>`. Without it, a review sees the
    branch as of the board host's last fetch.

15. **Let it take QA.** On the board host, give the host's entry the testing skills and, if the
    machine can carry more than one copy of a project's stack, `qaLeases`:

    ```json
    { "name": "bravo-host", "slots": 4, "qaLeases": 2, "projects": ["alpha"],
      "skills": ["speculate", "implement", "refine", "qa-test", "review-pr"] }
    ```

    `qaLeases` above 1 applies only to a project whose `project.json` says `"parallelStacks": true`.
    Only set that once the project's conventions say how a run on stack slot N
    (`WHETSTONE_STACK_SLOT`) picks its own compose project name and ports. Without it, a second
    QA run on the same machine would start the same stack on the same ports.

    Check: send one ticket through QA on the worker host. Its screenshots appear on the ticket, and
    the worker host's log shows `Captures mirrored to the board host as they land`. QA on the board
    host can run at the same time.

## Day to day

- **Stop taking new work:** set `"enabled": false` on the board host. Runs already out finish.
- **Stop the runner:** create `C:\Whetstone\data\runner.stop`. It takes nothing new and exits once
  its runs are done.
- **Stop everything now:** the dashboard's hard Stop. It also orders every worker host run killed.
- **After a merge on the board host:** `git -C C:\Whetstone pull` on the worker host. Until then the
  host is shown as on another commit and gets no work.
- **If the worker host goes off:** after 10 minutes without a check-in its runs are given up on,
  and their tickets go back to the queue. When it comes back, the runner kills anything it was
  still holding without handing it back.
- **A hand-back that failed** blocks its ticket with the reason. The work is committed on the worker
  host, and nowhere else, until the next run there hands it back. Fix the cause and unblock.

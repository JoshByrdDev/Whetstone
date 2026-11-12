# Machine limits: what each machine has, how loaded it is, and how many berths it should take

Agreed 2026-11-11: suggestions are driven by machine strain, the board host collects its own in
C#, inventory gets a Machines section on the System page, and automatic mode is one global switch.
Slot counts are hand-set today (`heartbeatWorkers`, `qaLeases`, and each
worker host's `slots` / `qaLeases`). This adds three things, in this order:

1. an **inventory** of every machine's hardware, kept as a history of changes;
2. a **load sample** per machine per minute, beside `CapacitySamples`;
3. a **suggested** worker-slot and QA-slot count beside each hand-set one, with its reason and how
   sure it is - advisory only, with an automatic-mode switch that exists and is off.

## What the run history already says (2026-11-11)

Read from a copy of `dashboard.db`, implement runs on Opus since 2026-11-01. `api` is
`ApiDurationSeconds / NumTurns`, `machine` is `(DurationSeconds - ApiDurationSeconds) / NumTurns`.

| | api s/turn | machine s/turn |
|---|---|---|
| Board host, 11-02 (fleet small) | 17.5 | 1.4 |
| Board host, 11-07 / 11-08 (fleet at its largest) | 54.1 | 1.0 |
| Worker host, 11-05 | 20.3 | 1.2 |
| Worker host, 11-11 | 77.4 | 4.0 |

And by how many runs overlapped anywhere in the fleet (time-weighted), api s/turn goes
31 -> 34 -> 38 -> 41 -> 44 -> 50 from 1 to 7 others.

Three things follow, and they change the brainstormed algorithm:

- **Almost all of a run's wall time is model time, and that is what grew.** The board host ran one
  or two workers the whole time and its time per turn still tripled, while the time spent outside
  the API stayed near a second per turn. A per-host "slowdown against local overlap" (the first
  idea for rule B) would have blamed the worker host's hardware for something its CPU was not doing.
- **It tracks the fleet, not the machine.** Overlap on *other* machines slows a run about as much
  as overlap on its own. That points at something shared: the Claude account(s), the API itself, or
  the network the machines share. The worker host's `pnpm install` timeouts on 11-11 fell on its
  worst api day, which makes the network a real suspect - so load samples include network throughput.
- **The machine-bound part is real but small, and it is growing on the worker host**: prep (worktree
  setup) median 10s -> 47s, hand-off 2s -> 27s, machine s/turn 1 -> 4 over the same week. That is
  the part more or fewer berths on one machine can actually change.

So the suggestion for a machine is driven by **what the machine measurably did** (CPU, memory,
swap, pressure, its own setup/hand-off times, timeouts), and the run-speed curve is shown as evidence
split into its model and machine parts. Model-time slowdown is reported once, fleet-wide, as its own
finding: fewer berths on one host would not fix it.

## 1. Inventory

Two collectors with one shape (decided 2026-11-11): a worker host's runner uses
`data/machine-info.ps1` (pwsh 7 on Windows and macOS, pure ASCII, every external command behind one
`Invoke-Probe` the tests replace); the board host reads its own in C# (`Services/MachineProbe.cs`,
Win32 calls on Windows and the same commands as below on macOS, every command behind an injectable
runner). Both send the JSON shape of `MachineInventoryReport` / `MachineLoadReport`, and the tests
on each side parse the same captured command output, so the two cannot quietly mean different things.

| Field | Windows | macOS |
|---|---|---|
| CPU model | `Win32_Processor.Name` | `sysctl -n machdep.cpu.brand_string` |
| Physical / logical cores | `NumberOfCores` / `NumberOfLogicalProcessors` | `hw.physicalcpu` / `hw.logicalcpu` |
| Performance / efficiency cores | the board host: each core's efficiency class from `GetLogicalProcessorInformationEx`; a worker host on Windows: null (null is an honest answer) | `hw.perflevel0.physicalcpu` / `hw.perflevel1.physicalcpu` |
| RAM | `Win32_ComputerSystem.TotalPhysicalMemory` | `hw.memsize` |
| OS, version, arch | `[RuntimeInformation]` + `Win32_OperatingSystem.Caption` | `sw_vers` + `uname -m` |
| Worktree drive: kind, size | `Get-PhysicalDisk.MediaType` for the volume holding the data root | `diskutil info` (`Solid State`) |
| Docker CPUs / memory | `docker info --format '{{.NCPU}} {{.MemTotal}}'` | same - on a Mac this is the VM, the limit QA actually meets |
| Tool versions | `node`, `pnpm`, `dotnet`, `pwsh`, `git`, `docker`, `claude` `--version` | same |

**Stored only on change.** Table `MachineInventories`: `Place` (`@board` or the host's name),
`FirstSeen`, `LastSeen`, `Hash` of the normalised JSON, the JSON, and the fields worth searching
promoted to columns (CPU model, cores, perf/eff cores, RAM, OS, arch, disk kind, Docker CPUs and
memory). A report with the same hash moves `LastSeen`; a different one opens a new row - that row
*is* the upgrade event. Free space is load, not inventory.

**When.** A worker host sends it on its first check-in and then hourly (inside the runner loop, not
the supervisor, so no manual restart on each host). The board host collects its own from a small
background service at start and hourly.

## 2. Load, once a minute

Same collector, `-Load`. Cheap enough to run every minute (no `Get-Counter` sampling window over a
second; no `docker stats` every minute).

| Field | Windows | macOS |
|---|---|---|
| CPU % | runner: `Win32_Processor.LoadPercentage`; board: `GetSystemTimes` between minutes | `top -l 2 -n 0 -s 1`, second sample's idle |
| Memory available | runner: `Win32_PerfFormattedData_PerfOS_Memory.AvailableMBytes`; board: `GlobalMemoryStatusEx` | `vm_stat`: free + inactive + purgeable + speculative - *not* free alone, which is always near zero |
| Swap / pagefile in use | runner: `Win32_PageFileUsage.CurrentUsage`; board: `NtQuerySystemInformation` page files | `sysctl vm.swapusage` |
| Pressure | derived: available < 10% of RAM or pages/s high -> warn | `sysctl kern.memorystatus_vm_pressure_level` (1 normal, 2 warn, 4 critical) |
| Worktree drive free | `DriveInfo` | `df -k` |
| Network in/out | .NET `NetworkInterface` byte counters, delta since the last sample (both OSes, both collectors) | same |
| Docker memory in use | `docker stats --no-stream`, every 5th minute only | same |

**Who adds the berths.** A host reports only what it measured. The board already knows which
berths were busy and in which phase at that minute (`CapacitySnapshot`), so `CapacitySampler` writes
the load row beside the capacity row it already writes: table `MachineLoadSamples` (`At`, `Place`,
the fields above, `Busy`, `PhasesJson` like `{"implement":3,"qa-test":1}`). A worker host's latest
reading rides on its check-in and is held in `WorkerHostService` like its runs; the board host's own
comes from the same background service as its inventory. Kept 30 days, pruned like capacity samples.

## 3. Suggestions

Computed on request (`GET /api/machines/suggestions`), never stored: the inputs are stored, and a
better rule should apply to old data. Each suggestion carries `value`, `basis` (`hardware` /
`learned`), `confidence` (`low` / `medium` / `high`), and `reasons[]` - short sentences with their
numbers, which is what the page shows.

**A. From hardware alone** - a new machine, or one with too few samples.
Per-berth cost is configuration, not code (`machineLimits` in `data/config.json`, defaults below),
because it depends on the projects being run:

- `reserveGb`: max(4, 15% of RAM) kept for the OS, the board itself and the browser.
- `berthGb` 2.5 and `berthCores` 1 for a worker; a performance core counts 1, an efficiency core 0.5.
- `qaStackGb` 3 per QA lease, set against Docker's memory, not the machine's.

workers = min((RAM - reserve) / berthGb, effective cores - 1); QA = DockerMem / qaStackGb.
For the coming Mac mini (48GB; assuming 4P + 6E): min(16, 6) = **6 workers**; QA from whatever
Docker's VM is given - at Docker Desktop's default of half the RAM, 24GB, that is 8, capped by the
worker count. Confidence: low.

**B. Learned from the machine's own minutes** - once there are enough samples (for example 6 hours
of busy minutes spread over at least 3 different busy counts). For each busy count n, take the p90
CPU and the p10 available memory over the minutes at n; fit each as a straight line in n (a berth
costs about this much CPU and this much memory here); suggested = the largest n where CPU stays
under 85% and available memory above the reserve, held to twice the busiest count seen (a straight
line is only trusted a little past where it was drawn). QA leases are learned the same way from
Docker's memory on the minutes it was read, against how many stack phases were running. Fitting
host memory per phase is left for later: the samples record the phases, so it needs no new data.

Shown beside it, as evidence rather than as an input: the run-speed curve from `CycleRuns` on that
host, by time-weighted local overlap, split into model time and machine time, with the two points the
user asked for -

- *most work per day*: where one more berth adds under 12% throughput;
- *each ticket fastest*: where time per turn passes 2x its solo value -

each marked "machine-bound" or "model-bound" by which part grew. Only a machine-bound knee may
lower B's number.

**C. Ceilings from warning signs** - each caps A and B, whatever they say, at one below the busy
count it happened at, for 7 days after the last time it happened:

- swap growing for 5+ minutes, or pressure at warn/critical (macOS) for 5+ minutes;
- a setup or install timeout on that host (`pnpm install` hitting its limit, the case of 11-11);
- Docker memory in use above 90% of its limit during QA (caps QA slots);
- the worktree drive under 20GB free (caps at the current busy count and says why).

**The model-time finding** is fleet-wide and separate: "model time per turn rises about X% per run
running anywhere in the fleet". Not a per-host number; a later automatic mode might use it as a
fleet cap.

## 4. Where it shows

- **Heartbeat page, host panel** (`WorkerHostsPanel.tsx`) and **Settings** (`SettingsPanel.tsx`),
  next to each slot control: `Worker slots 6 (set by you) · suggested 4`, the reasons under a
  disclosure, and the confidence. No "apply" that writes behind the owner's back: an *Use 4* button
  sets the field the owner would have typed in, and saving is still the owner's.
- **System page**: a Machines section - each machine's current inventory, its change history, and a
  small chart of the last day's CPU and memory against busy berths. Searching the inventory is the
  history table plus a text filter; nothing more for now.
- Theme tokens and orbit-* classes only; the suggestion folds under the field at 390px.

## 5. Automatic mode, off

`machineLimits.auto` in `data/config.json`, default `false`, with a switch in Settings that says it
does nothing yet. Its design is fixed now so it can be built later without changing anything above:

- it would change **how many berths dispatch fills**, never `slots`: a host keeps its block of ten
  and its slot numbers, so turning it on never renumbers a berth under a live run;
- it would only ever *lower* the fill below the hand-set count (the owner's number stays the ceiling)
  and read the suggestion through `GET /api/hosts/dispatch`, which the orchestrator already reads
  every pass.

Later candidates for the same treatment: heavy checks at once, test threads per run, which phases a
host takes, dispatch order, setup timeouts.

Where each piece lives: `Services/MachineInfo.cs` (report shapes, parsers, store-on-change, the
latest-load book), `Services/MachineProbe.cs` (the board host's collector), `Services/MachineLimits.cs`
(the rules and `GET /api/machines`), `data/machine-info.ps1` (a worker host's collector),
`ClientApp/src/components/MachinesSection.tsx` and `SlotSuggestionNote.tsx` (the pages).

## Tests

- `data/machine-info.tests.ps1`: the collector on mocked Windows and macOS probe output, including
  missing tools (no Docker, no pnpm) and Intel-Mac / non-hybrid CPUs where perf levels are absent.
- `Whetstone.Tests`: inventory store-on-change, the check-in carrying inventory and load, rule A
  arithmetic, rule B's fit on synthetic samples, each C ceiling, model- vs machine-bound labelling,
  and the suggestions route.
- Proven on the QA copy: its board host reports its own inventory and load; a fake worker host
  checking in with a macOS-shaped payload shows up with suggestions on the Heartbeat page and Settings.

`docs/setup.md` and `SetupCheck`: no new dependency - every probe is a built-in OS command, and a
missing Docker or pnpm is reported as absent, not as a failure.

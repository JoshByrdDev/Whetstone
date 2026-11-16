# The iPhone simulator for the fleet

A project with a phone app can already have its QA runs drive an Android emulator on this
machine. This plan gives the same to an iPhone app: a worker on a Mac boots an iOS simulator,
installs the branch's build, drives it and captures what it shows, so a ticket that changes
the app is proven on both phones before a person sees it.

What is specific to one project stays in its folder, as always: which phases take the
simulator, for which paths, which device, and the commands that build that project's app.
This repository adds what any project with an iPhone app needs: the machine check, the guard,
the rule in the prompt and the skills, and sending such a run only to a machine that can do it.

## What exists today, for Android

| Piece | Where | What it does |
|---|---|---|
| The lease | a project's `phaseResources` names `android:emulator`; `resourcePaths` gives it only to a run whose branch changes the app's folder (`ProjectRegistry.ResourcePaths`) | One run at a time drives the device. The colon makes it the machine's, not the project's, so another project's run waits on the same lease |
| On a worker host | `WorkerHostService.HostResource`: `android:emulator@<host>` | Each machine's device is its own |
| The guard | `data/worker-guard.ps1`, "A phone app" section | Denies Expo's cloud (`eas build/submit/update`, `expo start --tunnel`) and any adb target that is not `emulator-<port>` |
| The rule | `data/heartbeat-prompt.md` (the first hard rule: "no device but an emulator running here") and every skill's "Local only" section | A phone on a cable or the network is another machine |
| How to drive it | the project's own qa-test and implement skills ("The phone app") | Not here |

## What is needed

### 1. The machine has it, and says so

An iOS simulator needs macOS, Xcode with its licence accepted, at least one iOS runtime, and
`xcrun simctl`. None of that is on a fresh Mac, and none of it exists on Windows.

- **Setup check** (`Services/SetupCheck.cs`) and **`docs/setup.md`** each gain a row:
  - On macOS it checks `xcode-select -p`, `xcrun simctl list runtimes` (at least one iOS
    runtime available) and the licence (`xcodebuild -checkFirstLaunchStatus`).
  - The row matters only when an enabled project's resources name `ios:simulator`. Anywhere
    else it reads as not needed, never as a failure.
  - On Windows it says the simulator is macOS-only.
- Say what fixes each gap. Installing Xcode and accepting its licence need the owner's
  password, so the check says the command rather than running it.

### 2. Only a Mac is sent a run that needs it

A worker host on Windows cannot satisfy `ios:simulator@<host>`, and neither can a board host
on Windows. Today a lease is only ever *waited for*. Here, a run would wait for a device that
can never exist on that machine.

- A machine says which machine-wide devices it has. For a worker host that is part of its
  check-in (the runner already reports its logins and capacity). For the board host, the board
  works it out itself.
- Dispatch (`GET /api/hosts/dispatch` and the claim) skips a machine that lacks a device a
  run needs, the same way it skips a host without the Claude account a phase needs
  (`ClaudeSmartSwitch.ChooseForHost`).
- If no machine has the device, the run does not start. The board shows why, in the ticket and
  in the capacity strip, as "waiting for an iPhone simulator, which only a Mac has". It is not
  claimed and then failed.
- Write the rule once, in C#, and have the PowerShell side ask for it rather than repeat it.
  If a copy in `heartbeat-common.ps1` is unavoidable, the two must agree, as the slot numbering
  does.

### 3. The lease

- **`ios:simulator`**, machine-wide like `android:emulator`, and `ios:simulator@<host>` on a
  worker host. Nothing in C# should special-case the name: the lease machinery is generic, and
  a project's config is what names it.
- One simulator per machine first, as with Android.
  - Later, one per stack copy: `simctl clone` gives a device per slot, and the project's folder
    says which device slot N uses.
  - Leave room for that, but do not build it now.
- **Hygiene.** A run killed mid-flight leaves a booted simulator with its build installed.
  - The next holder of the lease starts from a known state: shut down, then erase or
    reinstall. That is the project skill's first step, but the reaper should also shut down a
    simulator whose lease it frees.
  - A booted simulator is headless (`simctl boot` opens no window), so it needs no desktop
    session and no keep-awake beyond what the fleet already holds.

### 4. The guard

Add to the phone-app section of `data/worker-guard.ps1`, with tests in
`data/worker-guard.tests.ps1` that encode what a run legitimately does every day.

**Allow:**
- `xcrun simctl` on a simulator: `list`, `boot`, `shutdown`, `erase`, `install`, `launch`,
  `terminate`, `io <udid> screenshot`/`recordVideo`, `openurl` to a localhost or app-scheme URL,
  `privacy`, `status_bar`, and `spawn` for logs.
- `xcodebuild` with `-sdk iphonesimulator` or `-destination 'platform=iOS Simulator,...'`.
- `npx expo prebuild`, and `npx expo run:ios` aimed at a simulator.
- `pod install`. CocoaPods fetches packages, like `pnpm install`.

**Deny:**
- `xcrun devicectl` (a real device).
- `ios-deploy`.
- `xcodebuild` with a `platform=iOS` destination that is not the simulator, `-allowProvisioningUpdates` (it signs in to Apple) or `-exportArchive`.
- `xcrun altool` and `xcrun notarytool`.
- `expo run:ios --device`.
- `fastlane`.
- Anything that reads the login keychain's signing identities.

`eas build/submit/update` are already denied: check that the tests cover each of them.

The guard is claude-only, so the prompt below is the whole rule on codex and gemini, as with
everything else in this section.

### 5. The words: prompt and skills

"No device but an emulator running here" becomes "no device but an emulator or simulator
running here", together with what it rules out: a phone on a cable or the network, and Apple's
or Expo's cloud.

- It goes in `data/heartbeat-prompt.md` and in the shared "Local only" section of every
  template and every project copy. That is a pipeline rule, word for word: list the set with
  `ls data/skill-templates/*/skill.md data/projects/*/skills/*/skill.md`.
- One commit here, plus one in each project folder it touches.
- Record the baselines with the Skills page's mark-synced.

How to build and drive a particular app (which scheme, which device, which deep links) is the
project's qa-test and implement skills, not the templates. The templates may gain one general
paragraph: an iPhone check uses the simulator lease when it has it, and says so when it does
not.

### 6. Captures

A QA run's screenshots from `simctl io ... screenshot` go where its other captures go, under
the data root at the same path on every machine (`HostDataMirror`), so a worker host's captures
come back as they land. Check that the capture path a skill is handed works with a simulator's
output, PNG by default. No new route should be needed.

### 7. The QA copy

The QA copy (`data/qa-copy.ps1`) starts no agents, so it starts no simulator. If any new route
boots or drives one, it belongs in `QaIsolation`'s table.

## Not in this plan

- Anything one project needs: its `phaseResources` and `resourcePaths` entries, its device
  name, and its skills' "The phone app" section for iPhone. The project's own folder holds
  those, written once this lands.
- Physical devices, TestFlight, signing and store builds: the owner's, never the fleet's.
- The Claude desktop app's own iOS simulator panel, which is for an interactive session, not
  for workers.

## Done when

- On a Mac with Xcode, a project that names `ios:simulator` gets QA runs that hold that
  lease, boot a simulator headless and capture it.
- A Windows worker host is never handed such a run. The board says why such a run waits when
  no Mac is free.
- The guard tests cover every allow and deny above.
- The Setup check reports the Mac's Xcode state truthfully.
- The prompt and every skill copy say "emulator or simulator".

```bash
pwsh -NoProfile -ExecutionPolicy Bypass -File data/worker-guard.tests.ps1
```

```bash
pwsh -NoProfile -ExecutionPolicy Bypass -File data/heartbeat-hosts.tests.ps1
```

```bash
dotnet test Whetstone.Tests -c Release --filter "FullyQualifiedName~WorkerHost|FullyQualifiedName~ResourceLease|FullyQualifiedName~SetupCheck"
```

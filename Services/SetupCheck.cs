using System.Diagnostics;
using Whetstone.Services.Walkthrough;

namespace Whetstone.Services;

/// <summary>One thing a fresh install needs, and whether this machine has it.</summary>
/// <param name="State">ok, missing (it will fail), or warn (it will work less well, or needs a restart).</param>
/// <param name="Required">False for a feature that degrades without it rather than stopping.</param>
/// <param name="Fix">What to run or change, or null when there is nothing to do.</param>
public sealed record SetupItem(string Key, string Label, string State, bool Required, string Detail, string? Fix);

/// <summary>
/// The setup check on the System page: the dependencies a new machine gets wrong and that fail
/// quietly when they do. Each item says what it is for, so a missing one reads as a consequence,
/// not a bare "not found". <c>docs/setup.md</c> is the same list for a person reading before the
/// first run; the two should name the same things.
/// <para>
/// Everything is looked up the way the fleet will look it up: the dashboard's own PATH, which the
/// heartbeat and every worker inherit. A tool installed after the dashboard started is on the
/// user's PATH but not that one, so it is reported as needing a restart rather than as missing.
/// </para>
/// </summary>
public class SetupCheckService
{
    /// <summary>The ways this check looks at the machine, swapped out in tests.</summary>
    public sealed record Probe(
        Func<string, string?> FindOnFleetPath,
        Func<string, string?> FindOnUserPath,
        Func<string?, string, string?> GitConfig,
        Func<string?> NarratorUnavailable,
        Func<string?> HeartbeatStartupError);

    private static readonly Dictionary<string, (string Name, string Install)> Clis = new()
    {
        ["claude"] = ("Claude Code", "npm install -g @anthropic-ai/claude-code, then: claude auth login"),
        ["codex"] = ("Codex CLI", "npm install -g @openai/codex, then sign in with: codex"),
        ["gemini"] = ("Gemini CLI", "npm install -g @google/gemini-cli, then sign in with: gemini"),
    };

    private readonly ProjectRegistry _registry;
    private readonly DashboardPaths _paths;
    private readonly Probe _probe;

    public SetupCheckService(ProjectRegistry registry, DashboardPaths paths, Narrator narrator, HeartbeatManager heartbeat)
        : this(registry, paths, new Probe(
            name => FindOnPath(name, Environment.GetEnvironmentVariable("PATH")),
            name => FindOnPath(name, UserPath()),
            RunGitConfig,
            narrator.Unavailable,
            () => heartbeat.StartupError))
    { }

    public SetupCheckService(ProjectRegistry registry, DashboardPaths paths, Probe probe) =>
        (_registry, _paths, _probe) = (registry, paths, probe);

    public IReadOnlyList<SetupItem> Run()
    {
        var cfg = _registry.GetGlobalConfig();
        // Every role, the dashboard's own two included: an analysis on a missing CLI fails as surely
        // as a worker does, it just does not stop the heartbeat.
        var roles = AgentRoles.All
            .Select(r => (Role: r, Provider: AgentRoles.Resolve(cfg, r).Provider))
            .ToList();
        var configs = _registry.Projects
            .Select(k => (Key: k, Config: SafeConfig(k)))
            .Where(p => p.Config is not null)
            .ToList();
        // Every repo a worker commits in, so a project of several is asked about each of them.
        var projects = configs
            .SelectMany(p => p.Config!.Repos.Select(m =>
                (Key: m.Dir.Length == 0 ? p.Key : $"{p.Key}/{m.Dir}", Path: m.HubPath(p.Config.ProjectPath))))
            .Where(p => !string.IsNullOrEmpty(p.Path))
            .ToList();
        var folders = _registry.Projects
            .Select(k => (Key: k, Repo: ProjectRepo.Read(_paths.ProjectDir(k))))
            .ToList();
        var layouts = configs
            .Select(p => (p.Key, Problems: RepoLayout.Problems(p.Config!, RepoLayout.Disk), Pending: RepoLayout.NotYetRunnable(p.Config!)))
            .ToList();
        return Evaluate(_probe, roles, projects, folders, layouts);
    }

    /// <summary>The check itself, over whatever the probe reports. Public for tests.</summary>
    public static IReadOnlyList<SetupItem> Evaluate(Probe probe,
        IReadOnlyList<(string Role, string Provider)> roles, IReadOnlyList<(string Key, string Path)> projects,
        IReadOnlyList<(string Key, ProjectRepo.State Repo)>? folders = null,
        IReadOnlyList<(string Key, IReadOnlyList<string> Problems, string? Pending)>? layouts = null)
    {
        var items = new List<SetupItem>();

        // ---- the heartbeat's own refusal: the reason the board says Stopped and nothing else ----
        if (probe.HeartbeatStartupError() is { } refused)
            items.Add(new SetupItem("heartbeat", "Heartbeat", "missing", true,
                "The last start was refused: " + refused,
                "Fix what it names, then start the heartbeat again."));

        // ---- agent CLIs, one per provider some role runs on ----
        foreach (var group in roles.GroupBy(r => r.Provider))
        {
            var (name, install) = Clis.TryGetValue(group.Key, out var c) ? c : (group.Key, $"Install the {group.Key} CLI");
            var needs = string.Join(", ", group.Select(g => g.Role));
            items.Add(Tool(probe, group.Key, $"{name} ({group.Key})", required: true,
                found: $"Runs: {needs}.",
                missing: $"These roles run on it and will fail: {needs}. The heartbeat refuses to start without it.",
                install + ". Or move those roles to another provider in Settings."));
        }

        // ---- git, and who its commits are by ----
        var git = Tool(probe, "git", "git", required: true,
            found: "Worktrees, branches and diffs.",
            missing: "Nothing in the pipeline works without it.",
            "Install Git for Windows: https://git-scm.com/download/win");
        items.Add(git);
        if (git.State == "ok") items.Add(Identity(probe, projects));

        // ---- Node: QA's browser, and the narrator's runtime ----
        var node = Tool(probe, "node", "Node.js", required: true,
            found: "QA and PR review drive a browser through npx.",
            missing: "QA and PR review cannot open a browser, so QA tickets go to Blocked. Walkthroughs cannot be voiced.",
            "Install Node.js LTS: https://nodejs.org");
        items.Add(node);
        if (node.State == "ok")
        {
            var npx = Tool(probe, "npx", "npx", required: true,
                found: "Fetches the Playwright browser server (@playwright/mcp) on first use.",
                missing: "Node is here but npx is not, so QA cannot start its browser.",
                "Reinstall Node.js with npm included.");
            if (npx.State != "ok") items.Add(npx);
        }

        // ---- the narrator ----
        var narrator = probe.NarratorUnavailable();
        items.Add(narrator is null
            ? new SetupItem("narrator", "Walkthrough voice-over", "ok", false, "Classroom walkthroughs are voiced.", null)
            : new SetupItem("narrator", "Walkthrough voice-over", "warn", false,
                "Walkthroughs play with captions only: " + narrator,
                "Run npm install once in data/narrator (the first voiced walkthrough also downloads a ~90MB voice model)."));

        // ---- each project folder's own repo: its skills and config exist nowhere else ----
        if (folders is { Count: > 0 }) items.Add(Folders(folders));

        // ---- where each project's code is: a repo, or a set of them ----
        if (layouts is { Count: > 0 }) items.Add(Layouts(layouts));

        return items;
    }

    /// <summary>
    /// Whether each project's code is somewhere a worktree can be made of it. Required, because a
    /// project with a problem here has its tickets passed over by every claim until it is fixed,
    /// and a passed-over ticket says nothing on the board.
    /// </summary>
    private static SetupItem Layouts(IReadOnlyList<(string Key, IReadOnlyList<string> Problems, string? Pending)> layouts)
    {
        const string label = "Project repos";
        var broken = layouts.Where(l => l.Problems.Count > 0).ToList();
        if (broken.Count > 0)
            return new SetupItem("project-repos", label, "missing", true,
                string.Join(" ", broken.SelectMany(b => b.Problems.Select(p => $"{b.Key}: {p}"))) +
                " Until then no ticket of theirs is claimed.",
                $"Fix projectPath or repos in data/projects/{broken[0].Key}/project.json.");

        var pending = layouts.Where(l => l.Pending is not null).ToList();
        if (pending.Count > 0)
            return new SetupItem("project-repos", label, "warn", false,
                string.Join(" ", pending.Select(p => $"{p.Key}: {p.Pending}")), null);

        return new SetupItem("project-repos", label, "ok", true,
            "Every project points at a git repo, or a set of them.", null);
    }

    private static SetupItem Folders(IReadOnlyList<(string Key, ProjectRepo.State Repo)> folders)
    {
        const string label = "Project folders backed up";
        var unversioned = folders.Where(f => !f.Repo.Versioned).Select(f => f.Key).ToList();
        var local = folders.Where(f => f.Repo.Versioned && f.Repo.Remote is null).Select(f => f.Key).ToList();
        if (unversioned.Count == 0 && local.Count == 0)
            return new SetupItem("project-folders", label, "ok", false,
                "Every project folder is its own repo with a remote.", null);

        var detail = new List<string>();
        if (unversioned.Count > 0)
            detail.Add($"No git history of their own: {string.Join(", ", unversioned)}.");
        if (local.Count > 0)
            detail.Add($"No remote, so this machine holds the only copy of their skills: {string.Join(", ", local)}.");
        var key = (unversioned.Count > 0 ? unversioned : local)[0];
        var fix = unversioned.Count > 0
            ? "See \"Each project folder is its own repo\" in docs/setup.md."
            : $"Create an empty private repo, then: git -C data/projects/{key} remote add origin <its URL>, and git -C data/projects/{key} push -u origin HEAD";
        return new SetupItem("project-folders", label, "warn", false, string.Join(" ", detail), fix);
    }

    private static SetupItem Tool(Probe probe, string exe, string label, bool required, string found, string missing, string fix)
    {
        if (probe.FindOnFleetPath(exe) is { } at)
            return new SetupItem(exe, label, "ok", required, $"{found} {at}", null);
        if (probe.FindOnUserPath(exe) is { } later)
            return new SetupItem(exe, label, "warn", required,
                $"Installed at {later}, but after Whetstone started, so neither it nor the heartbeat can see it yet.",
                "Restart Whetstone (Start-Whetstone.bat). Stop the heartbeat first.");
        return new SetupItem(exe, label, required ? "missing" : "warn", required, missing, fix);
    }

    private static SetupItem Identity(Probe probe, IReadOnlyList<(string Key, string Path)> projects)
    {
        const string fix = "git config --global user.name \"Your Name\" and git config --global user.email you@example.com";
        // Workers commit in worktrees of each project's repo, so the identity that counts is the one
        // git resolves there - a repo-level setting is as good as a global one.
        if (projects.Count == 0)
        {
            var name = probe.GitConfig(null, "user.name");
            var email = probe.GitConfig(null, "user.email");
            return name is not null && email is not null
                ? new SetupItem("git-identity", "git identity", "ok", true, $"Commits as {name} <{email}>.", null)
                : new SetupItem("git-identity", "git identity", "missing", true,
                    "No global user.name / user.email. Every worker commit will fail with \"tell me who you are\".", fix);
        }

        var lacking = projects
            .Where(p => probe.GitConfig(p.Path, "user.name") is null || probe.GitConfig(p.Path, "user.email") is null)
            .Select(p => p.Key)
            .ToList();
        if (lacking.Count == 0)
        {
            var first = projects[0];
            return new SetupItem("git-identity", "git identity", "ok", true,
                $"Commits as {probe.GitConfig(first.Path, "user.name")} <{probe.GitConfig(first.Path, "user.email")}>.", null);
        }
        return new SetupItem("git-identity", "git identity", "missing", true,
            $"No user.name / user.email in {string.Join(", ", lacking)}. Worker commits there will fail with \"tell me who you are\".", fix);
    }

    private ProjectConfig? SafeConfig(string key)
    {
        try { return _registry.GetProjectConfig(key); }
        catch { return null; }
    }

    // ---- the real machine ----

    /// <summary>
    /// Where a bare command name resolves on a PATH-style list, the way PowerShell's Get-Command does
    /// for the heartbeat: each directory in order, each PATHEXT extension in order. Null when nowhere.
    /// </summary>
    public static string? FindOnPath(string name, string? searchPath, string? pathExt = null)
    {
        var exts = (pathExt ?? Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(".ps1");
        foreach (var dir in (searchPath ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var ext in exts)
            {
                try
                {
                    // PATHEXT is upper case; the files on disk almost never are, and this is shown.
                    var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(dir), name + ext.ToLowerInvariant());
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* a malformed PATH entry is just not the one */ }
            }
        }
        return null;
    }

    /// <summary>The PATH a new process would get now: machine then user, as saved in the registry.</summary>
    private static string UserPath()
    {
        if (!OperatingSystem.IsWindows()) return Environment.GetEnvironmentVariable("PATH") ?? "";
        return string.Join(Path.PathSeparator, new[]
        {
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
        }.Where(p => !string.IsNullOrEmpty(p)));
    }

    /// <summary>One git config value as git resolves it in a repo (or globally, for null), or null.</summary>
    private static string? RunGitConfig(string? repo, string key)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (repo is null) psi.ArgumentList.Add("config"); else { psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(repo); psi.ArgumentList.Add("config"); }
            if (repo is null) psi.ArgumentList.Add("--global");
            psi.ArgumentList.Add("--get");
            psi.ArgumentList.Add(key);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch { return null; }
    }
}

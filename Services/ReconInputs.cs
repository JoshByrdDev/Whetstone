using System.Text.Json;

namespace Whetstone.Services;

/// <summary>
/// What the Recon marts need that <c>refresh.py</c> cannot read for itself, because the rules for it
/// live here (docs/recon-tab-plan.md, "Loading"): each project's areas and roles from the merged
/// project config, and each repo's resolved base ref. Written to
/// <c>&lt;data root&gt;/analytics/recon-inputs.json</c> before every refresh the board starts. A refresh
/// started by hand without it loads no Recon inputs and builds the Recon marts empty.
/// <para>
/// Staleness is measured against the <b>project's</b> base - the ref the product is shown from -
/// not any ticket's, so a slice's or a feature's own base never enters here. The base ref is asked
/// of <see cref="TicketGitService.ResolveBaseRefAsync"/>, the rule the board already applies.
/// Reads only this machine's project folders and their git, so on a QA copy it reads the copy's
/// placeholder projects and needs nothing in <see cref="QaIsolation"/>'s table.
/// </para>
/// </summary>
public sealed class ReconInputs
{
    public delegate Task<string?> BaseRefResolver(string repo, ProjectConfig cfg, string baseBranch, CancellationToken ct);

    private readonly ProjectRegistry _registry;
    private readonly BaseRefResolver _baseRef;

    public ReconInputs(ProjectRegistry registry, TicketGitService git) : this(registry, git.ResolveBaseRefAsync) { }

    public ReconInputs(ProjectRegistry registry, BaseRefResolver baseRef) => (_registry, _baseRef) = (registry, baseRef);

    public const string FileName = "recon-inputs.json";

    public sealed record Area(string Name, string[] Pages, string[] Code);
    public sealed record Repo(string Dir, string Path, string? BaseRef, bool ReadOnly);
    public sealed record Project(string Key, Area[] Areas, string[] Roles, Repo[] Repos);
    public sealed record Inputs(DateTime WrittenAt, Project[] Projects);

    /// <summary>Every enabled project with a <c>discovery</c> block, in the board's order.</summary>
    public async Task<Inputs> BuildAsync(CancellationToken ct = default)
    {
        var projects = new List<Project>();
        foreach (var key in _registry.Projects)
        {
            var cfg = _registry.GetProjectConfig(key);
            if (!cfg.Discovery.Present) continue;
            var repos = new List<Repo>();
            foreach (var m in cfg.Repos)
            {
                var hub = m.HubPath(cfg.ProjectPath);
                string? baseRef = null;
                if (hub.Length > 0 && Directory.Exists(hub) && m.BaseBranch.Length > 0)
                {
                    try { baseRef = await _baseRef(hub, cfg, m.BaseBranch, ct); }
                    catch (Exception e) when (e is not OperationCanceledException) { baseRef = null; }
                }
                repos.Add(new Repo(m.Dir, hub, baseRef, m.ReadOnly));
            }
            projects.Add(new Project(key,
                cfg.Discovery.Areas.Select(a => new Area(a.Name, a.Pages, a.Code)).ToArray(),
                cfg.Discovery.Roles, repos.ToArray()));
        }
        return new Inputs(DateTime.UtcNow, projects.ToArray());
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Write the file into <paramref name="outDir"/>, whole or not at all.</summary>
    public async Task WriteAsync(string outDir, CancellationToken ct = default)
    {
        var inputs = await BuildAsync(ct);
        Directory.CreateDirectory(outDir);
        var file = System.IO.Path.Combine(outDir, FileName);
        var tmp = file + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(inputs, Json), ct);
        File.Move(tmp, file, overwrite: true);
    }
}

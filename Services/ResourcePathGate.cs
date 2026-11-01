using System.Collections.Concurrent;
using Whetstone.Models;

namespace Whetstone.Services;

/// <summary>
/// Decides, for the claim, whether a ticket's run needs a resource its project takes only for some
/// branches (<see cref="ProjectConfig.ResourcePaths"/>): it does when the branch changes something
/// under one of the resource's paths.
/// <para>
/// Every doubt reads as "needed". A ticket with no branch, a branch the board cannot read, a diff
/// that fails: each takes the resource, because a run that needs one it was not given starts it on
/// top of somebody else's - the collision leases exist to prevent - while one given a resource it
/// does not need only waits a little longer.
/// </para>
/// <para>
/// A branch's file list is kept for a minute. The claim runs every ten seconds per free berth while
/// the fleet is busy, and asks for every QA candidate each time; the branch only moves when a
/// phase that writes it runs, and that phase holds the ticket, so it is not a candidate meanwhile.
/// </para>
/// </summary>
public class ResourcePathGate
{
    public static readonly TimeSpan Keep = TimeSpan.FromMinutes(1);

    private readonly Func<string, string, Task<IReadOnlyList<string>?>> _changedFiles;
    private readonly Func<string, ProjectConfig> _config;
    private readonly Func<DateTime> _now;
    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<string>? Files)> _cache = new();

    public ResourcePathGate(TicketGitService git, ProjectRegistry registry)
        : this(async (project, branch) =>
        {
            var (files, error) = await git.ChangedFilesAsync(project, branch);
            return error is null ? files.Select(f => f.Path).ToList() : null;
        }, registry.GetProjectConfig, () => DateTime.UtcNow) { }

    /// <param name="changedFiles">The files a branch changes against its base, or null when they cannot be read.</param>
    public ResourcePathGate(Func<string, string, Task<IReadOnlyList<string>?>> changedFiles,
        Func<string, ProjectConfig> config, Func<DateTime> now)
        => (_changedFiles, _config, _now) = (changedFiles, config, now);

    /// <param name="resourceKey">As the claim holds it: fully qualified, and host-qualified on a worker host.</param>
    public async Task<bool> NeededAsync(Ticket ticket, string resourceKey)
    {
        var plain = WorkerHostConfig.SplitHostResource(resourceKey)?.Resource ?? resourceKey;
        if (_config(ticket.ProjectKey).PathsFor(plain) is not { } paths) return true;
        if (string.IsNullOrWhiteSpace(ticket.Branch)) return true;

        var key = $"{ticket.ProjectKey}|{ticket.Branch}";
        var now = _now();
        if (!_cache.TryGetValue(key, out var hit) || now - hit.At > Keep)
        {
            IReadOnlyList<string>? files;
            try { files = await _changedFiles(ticket.ProjectKey, ticket.Branch!); }
            catch { files = null; }
            hit = (now, files);
            _cache[key] = hit;
        }
        return hit.Files is null || ProjectConfig.Touches(hit.Files, paths);
    }
}

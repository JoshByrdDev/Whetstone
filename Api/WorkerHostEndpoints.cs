using System.Text.Json;
using Whetstone.Services;

namespace Whetstone.Api;

/// <summary>What a runner reports on every check-in: everything it is holding, every time.</summary>
/// <param name="WaitSeconds">How long the check-in may wait for an order when none is queued (0..25).</param>
/// <param name="DataRoot">The runner's own data root - where that machine keeps a ticket's files (see HostDataMirror).</param>
public record HostCheckinRequest(string? Commit, HostRun[]? Runs, DateTime? UsageLimitUntil, int? WaitSeconds, string? DataRoot = null);

/// <summary>An order the orchestrator places for one of a host's slots.</summary>
public record HostOrderRequest(string Kind, int Slot, int RunId, JsonElement? Claim);

/// <summary>A worker host handing a ticket's branch back. See <see cref="TicketGitService.AcceptWorkerBranchAsync"/>.</summary>
public record BranchAcceptRequest(int RunId, string Branch, string Sha, string IncomingRef, string? ExpectedOld);

/// <summary>
/// The worker host routes (docs/worker-hosts-plan.md, piece 2). A runner checks in and receives its
/// orders; the orchestrator places orders and reads every host's state; a worker on a worker host
/// hands its branch back and fetches its project's kit.
/// <para>
/// None of this is authentication. A host is named in the path and the firewall rule on the board
/// host is what keeps other machines out - see the plan, piece 7.
/// </para>
/// </summary>
public static class WorkerHostEndpoints
{
    public static void MapWorkerHostEndpoints(this WebApplication app)
    {
        app.MapGet("/api/hosts", (WorkerHostService hosts) => Results.Ok(hosts.Snapshot()));

        // A runner's check-in. The response also names the runs it reported whose lease has gone -
        // the orchestrator gave up on them while this host was unreachable - which the runner kills
        // without handing anything back, because their tickets may already be running elsewhere.
        app.MapPost("/api/hosts/{name}/checkin", async (string name, HostCheckinRequest req,
            WorkerHostService hosts, ResourceLeaseService leases, CancellationToken ct) =>
        {
            var runs = req.Runs ?? Array.Empty<HostRun>();
            var live = await leases.LiveRunsAsync(runs.Where(r => !r.Exited).Select(r => r.RunId).ToList());
            var revoked = runs.Where(r => !r.Exited && !live.Contains(r.RunId)).Select(r => r.RunId).ToArray();

            var wait = TimeSpan.FromSeconds(Math.Clamp(req.WaitSeconds ?? 0, 0, (int)WorkerHostService.MaxWait.TotalSeconds));
            var orders = await hosts.CheckInAsync(name, req.Commit, runs, req.UsageLimitUntil, wait, ct, req.DataRoot);
            return orders is null
                ? Results.NotFound(new { error = $"'{name}' is not a worker host in this board's config (workerHosts)." })
                : Results.Ok(new { orders, revoked });
        });

        app.MapPost("/api/hosts/{name}/orders", (string name, HostOrderRequest req, WorkerHostService hosts) =>
        {
            if (req.Kind is not ("start" or "kill"))
                return Results.BadRequest(new { error = $"Unknown order '{req.Kind}'." });
            var host = hosts.Find(name);
            if (host is null) return Results.NotFound(new { error = $"'{name}' is not a worker host." });
            if (!host.OwnsSlot(req.Slot))
                return Results.BadRequest(new { error = $"Slot {req.Slot} is not one of {name}'s ({host.FirstSlot}..{host.FirstSlot + host.Slots - 1})." });
            if (req.Kind == "start" && req.Claim is null)
                return Results.BadRequest(new { error = "A start order carries the claim." });
            hosts.Enqueue(name, new HostOrder(req.Kind, req.Slot, req.RunId, req.Claim));
            return Results.Accepted();
        });

        // The board host's half of the hand-back. The lease is checked first: a run that is no
        // longer the ticket's run must not move its branch, whatever git would allow.
        app.MapPost("/api/tickets/{id:int}/branch/accept", async (int id, BranchAcceptRequest req,
            TicketService tickets, ResourceLeaseService leases, TicketGitService git, CancellationToken ct) =>
        {
            var ticket = await tickets.GetAsync(id);
            if (ticket is null) return Results.NotFound(new { error = $"Ticket #{id} not found." });
            if (!await leases.IsLiveRunAsync(req.RunId, id))
                return Results.Conflict(new { error = $"Run {req.RunId} no longer holds ticket #{id} - it was released, and the ticket may be running elsewhere." });

            var r = await git.AcceptWorkerBranchAsync(ticket.ProjectKey, req.Branch, req.IncomingRef, req.Sha, req.ExpectedOld, ct);
            return r.Ok ? Results.Ok(new { accepted = true, moved = r.Moved }) : Results.Conflict(new { error = r.Why });
        });

        // A ticket's own files - its owner's attachments and the linked issue's - for a worker host
        // to keep at the same paths under its own root. See HostDataMirror.
        app.MapGet("/api/hosts/ticket-data/{id:int}", (int id, DashboardPaths paths) =>
            Results.File(HostDataMirror.BuildTicketData(paths, id), "application/zip", $"ticket-{id}.zip"));

        // A QA capture from a worker host's run, as it lands, so set_qa_shots finds it here when the
        // worker names it by its own path. Only for the run that holds the ticket.
        app.MapPut("/api/hosts/{name}/qa-captures/{id:int}/{**file}", async (string name, int id, string file, int runId,
            HttpRequest request, WorkerHostService hosts, ResourceLeaseService leases, DashboardPaths paths) =>
        {
            if (hosts.Find(name) is null) return Results.NotFound(new { error = $"'{name}' is not a worker host." });
            if (HostDataMirror.SafeCaptureName(file) is not { } safe)
                return Results.BadRequest(new { error = $"'{file}' is not a capture (an image, by a plain relative path)." });
            if (!await leases.IsLiveRunAsync(runId, id))
                return Results.Conflict(new { error = $"Run {runId} no longer holds ticket #{id}." });
            if (request.ContentLength is > HostDataMirror.MaxCaptureBytes)
                return Results.BadRequest(new { error = "A capture over 10MB is not a screenshot." });

            var dest = Path.Combine(new[] { paths.QaCaptureTicketDir(id) }.Concat(safe.Split('/')).ToArray());
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var temp = dest + ".part";
            await using (var output = File.Create(temp))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await request.Body.ReadAsync(buffer)) > 0)
                {
                    total += read;
                    if (total > HostDataMirror.MaxCaptureBytes) { output.Close(); File.Delete(temp); return Results.BadRequest(new { error = "A capture over 10MB is not a screenshot." }); }
                    await output.WriteAsync(buffer.AsMemory(0, read));
                }
            }
            // Renamed into place whole, so a set_qa_shots waiting for it never copies half a file.
            File.Move(temp, dest, overwrite: true);
            return Results.Ok(new { stored = safe });
        });

        app.MapGet("/api/hosts/project-kit/{key}", (string key, ProjectRegistry registry, DashboardPaths paths) =>
        {
            if (!registry.AllProjects.Contains(key, StringComparer.OrdinalIgnoreCase))
                return Results.NotFound(new { error = $"No project '{key}'." });
            var cfg = registry.GetProjectConfig(key);
            var zip = ProjectKit.Build(paths.ProjectDir(key), cfg.ResolvedMemoryDir);
            return Results.File(zip, "application/zip", $"{key}-kit.zip");
        });
    }
}

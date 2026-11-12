using Microsoft.EntityFrameworkCore;
using Whetstone.Persistence;
using Whetstone.Services;

namespace Whetstone.Api;

/// <summary>
/// Each machine's hardware, load and suggested slot counts (docs/machine-limits-plan.md). Read-only:
/// a suggestion is applied by its owner typing it into the field it sits beside, never from here.
/// </summary>
public static class MachineEndpoints
{
    public static void MapMachineEndpoints(this WebApplication app)
    {
        app.MapGet("/api/machines", async (MachineLimitsService limits, CancellationToken ct) =>
            Results.Ok(await limits.ReadAsync(DateTime.UtcNow, ct)));

        // One machine's minutes, for the System page's chart: load against busy berths.
        app.MapGet("/api/machines/{place}/load", async (string place, int? hours, DashboardDbContext db, CancellationToken ct) =>
        {
            var from = DateTime.UtcNow.AddHours(-Math.Clamp(hours ?? 24, 1, 24 * 14));
            var rows = await db.MachineLoadSamples.AsNoTracking()
                .Where(s => s.Place == place && s.At >= from).OrderBy(s => s.At)
                .Select(s => new { s.At, s.CpuPct, s.MemAvailMb, s.MemTotalMb, s.SwapUsedMb, s.Pressure, s.Busy, s.PhasesJson, s.NetInKbps, s.NetOutKbps, s.DockerMemUsedMb, s.DockerMemLimitMb })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });
    }
}

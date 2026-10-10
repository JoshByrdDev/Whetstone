using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Whetstone.Persistence;

/// <summary>
/// Used only by the `dotnet ef` tooling so it doesn't have to build/run the web
/// app to discover the model. The connection string here is irrelevant for
/// `migrations add` (which reads the model, not a live DB); runtime migration
/// uses the real path from DashboardPaths.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DashboardDbContext>
{
    public DashboardDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<DashboardDbContext>()
            .UseSqlite("Data Source=dashboard.design.db")
            .Options;
        return new DashboardDbContext(options);
    }
}

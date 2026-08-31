using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Yemekhane.Infrastructure.Persistence;

public sealed class YemekhaneDbContextFactory : IDesignTimeDbContextFactory<YemekhaneDbContext>
{
    public YemekhaneDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("YEMEKHANE_DESIGN_CONNECTION")
            ?? LocalDatabaseConnection.Resolve(null);
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>()
            .UseSqlite(connectionString)
            .Options;

        return new YemekhaneDbContext(options);
    }
}

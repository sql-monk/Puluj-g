using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Puluj.Infrastructure.Persistence;

/// <summary>Used by `dotnet ef` only. No live database is needed to add migrations.</summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PulujDbContext>
{
    public PulujDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__Puluj")
            ?? "Host=localhost;Port=5442;Database=puluj;Username=puluj;Password=puluj";
        var options = new DbContextOptionsBuilder<PulujDbContext>();
        DependencyInjection.ConfigureDbContext(options, cs);
        return new PulujDbContext(options.Options);
    }
}

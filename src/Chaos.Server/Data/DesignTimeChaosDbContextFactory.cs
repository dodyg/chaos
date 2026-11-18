using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Chaos.Server.Data;

/// <summary>
/// Used only by <c>dotnet ef</c> to build the model for <c>migrations add</c> and
/// <c>migrations script</c>, neither of which opens a connection. Running the service never uses it.
/// </summary>
public sealed class DesignTimeChaosDbContextFactory : IDesignTimeDbContextFactory<ChaosDbContext>
{
    public ChaosDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<ChaosDbContext>();
        ChaosDbContext.Configure(builder, "Host=localhost;Database=chaos_design_time");
        return new ChaosDbContext(builder.Options);
    }
}

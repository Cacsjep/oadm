using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Oadm.Core.Persistence;

/// <summary>
/// Used only by <c>dotnet ef migrations add</c>. Add a migration with:
/// <c>dotnet tool restore</c> then
/// <c>dotnet ef migrations add &lt;Name&gt; --project src/Oadm.Core --output-dir Persistence/Migrations</c>.
/// </summary>
public sealed class OadmDbContextDesignTimeFactory : IDesignTimeDbContextFactory<OadmDbContext>
{
    public OadmDbContext CreateDbContext(string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), "oadm-design-time.db");
        var options = new DbContextOptionsBuilder<OadmDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return new OadmDbContext(options);
    }
}

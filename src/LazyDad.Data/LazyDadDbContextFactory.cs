using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace LazyDad.Data;

/// <summary>
/// Used only by EF Core CLI tools (dotnet ef migrations add/update).
/// Not used at runtime — the app registers the DbContext via Program.cs.
/// </summary>
public class LazyDadDbContextFactory : IDesignTimeDbContextFactory<LazyDadDbContext>
{
    public LazyDadDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LazyDadDbContext>()
            .UseSqlServer("Server=design-time-placeholder;Database=design-time-placeholder;")
            .Options;

        return new LazyDadDbContext(options);
    }
}

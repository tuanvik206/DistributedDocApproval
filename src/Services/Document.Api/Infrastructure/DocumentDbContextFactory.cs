using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Document.Api.Infrastructure;

public sealed class DocumentDbContextFactory : IDesignTimeDbContextFactory<DocumentDbContext>
{
    public DocumentDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DocumentDb")
            ?? throw new InvalidOperationException("Set ConnectionStrings__DocumentDb before running EF Core design-time commands.");
        var options = new DbContextOptionsBuilder<DocumentDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new DocumentDbContext(options);
    }
}

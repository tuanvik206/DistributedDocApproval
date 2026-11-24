using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Approval.Api.Infrastructure;

public sealed class ApprovalDbContextFactory : IDesignTimeDbContextFactory<ApprovalDbContext>
{
    public ApprovalDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__ApprovalDb")
            ?? "Host=localhost;Database=ApprovalDesign;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<ApprovalDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new ApprovalDbContext(options);
    }
}

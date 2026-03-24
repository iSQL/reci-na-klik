using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ReciNaKlik.Infrastructure.Persistence;

namespace ReciNaKlik.Component.Tests.Fixtures;

internal static class TestDbContextFactory
{
    public static ReciNaKlikDbContext Create(string? databaseName = null)
    {
        var options = new DbContextOptionsBuilder<ReciNaKlikDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new ReciNaKlikDbContext(options);
    }
}

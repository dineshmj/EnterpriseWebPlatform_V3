using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

using EnterpriseWebPlatform.IdentityServer.Customers;
using EnterpriseWebPlatform.IdentityServer.Data;
using EnterpriseWebPlatform.IdentityServer.Data.Entities;

namespace EnterpriseWebPlatform.IdentityServer.Tests.Support;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ Same approach as MfaTestDatabase: the IDP's own model on an in-memory SQLite database,
// │ because a code is claimed and a failure counted with ONE conditional UPDATE each - SQL
// │ that EF Core's in-memory provider cannot run. One test-only change: SQLite cannot
// │ compare DateTimeOffset values in SQL (PostgreSQL can), so they are stored as UTC ticks
// │ here - the IDP always writes UTC, so the order is the same.
// └──────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// An in-memory IDP database for the customers' sign-in, with a clock the test moves. Each
/// Codes() / Logins() call is a new request: a new DbContext.
/// </summary>
internal sealed class CustomerTestDatabase : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public CustomerTestDatabase()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 22, 9, 0, 15, TimeSpan.Zero));

    public CustomerOneTimeCodes Codes() => new(NewContext(), Clock);

    public CustomerLoginStore Logins() => new(NewContext(), Clock);

    /// <summary>Camilla's registration code, as the Register page issues it.</summary>
    public Task<(Guid Id, string Code)> IssueRegistrationCodeAsync() =>
        Codes().IssueAsync(new CustomerOneTimeCode
        {
            Purpose = CustomerCodePurpose.Registration,
            CustomerNumber = "CUST-100001",
            Email = "camilla@example.com",
            DisplayName = "Camilla",
            MobileEnding = "678"
        }, CancellationToken.None);

    /// <summary>The code's row as the Code page finds it (null once spent).</summary>
    public Task<CustomerOneTimeCode?> OpenAsync(Guid id) =>
        Codes().FindOpenAsync(id, CustomerCodePurpose.Registration, CancellationToken.None);

    /// <summary>Camilla registered online with this password.</summary>
    public async Task<CustomerLogin> RegisterCamillaAsync(string password)
    {
        var login = new CustomerLogin { SubjectId = Guid.NewGuid(), CustomerNumber = "CUST-100001", DisplayName = "Camilla", MobileEnding = "678" };
        if (!await Logins().CreateAsync(login, password, CancellationToken.None))
            throw new InvalidOperationException("Registration failed in the test set-up.");
        return login;
    }

    public CustomerLogin Stored(Guid subjectId)
    {
        using var db = NewContext();
        return db.CustomerLogins.AsNoTracking().Single(c => c.SubjectId == subjectId);
    }

    public IdentityDbContext NewContext() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>()
            .UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, UtcTicksModelCustomizer>()
            .Options);

    public void Dispose() => _connection.Dispose();
}

/// <summary>The IDP's model, with every DateTimeOffset column stored as UTC ticks (SQLite only).</summary>
internal sealed class UtcTicksModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        var converter = new ValueConverter<DateTimeOffset, long>(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
                property.SetValueConverter(converter);
        }
    }
}
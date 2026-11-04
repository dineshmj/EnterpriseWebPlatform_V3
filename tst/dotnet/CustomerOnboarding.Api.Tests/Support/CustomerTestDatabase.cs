using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;

using EnterpriseWebPlatform.Common.WebUtilities.Configuration;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Outbox;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Persistence.Repositories;
using EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Privacy;

namespace EnterpriseWebPlatform.CustomerOnboarding.Api.Tests.Support;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ Same approach as the IDP's test databases: Customer Onboarding's OWN model and unit of
// │ work on an in-memory SQLite database, so a test can read the raw columns and see what
// │ really reaches the database - ciphertext or not. PostgreSQL-only parts (sequences,
// │ ORDER BY on timestamps) are not used by these tests.
// └──────────────────────────────────────────────────────────────────────────────────────
/// <summary>An in-memory Customer database; each Context() call is a new request (a new DbContext).</summary>
internal sealed class CustomerTestDatabase : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private readonly WorkflowContextAccessor _workflowContext = new(
        new HttpContextAccessor(),
        ClientIds.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ClientIds:OnboardingOutcomeSubscriber"] = "test.outcome-subscriber" })
            .Build()));

    public CustomerTestDatabase()
    {
        _connection.Open();
        using var db = Context();
        db.Database.EnsureCreated();
    }

    /// <summary>The API's master keys for this database (a new pair per test database).</summary>
    public PersonalDataCipher Cipher { get; } = new(new PersonalDataProtectionOptions
    {
        MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        BlindIndexKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
    });

    public CustomerDbContext Context() =>
        new(new DbContextOptionsBuilder<CustomerDbContext>()
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, SqliteOutboxModelCustomizer>()
                .Options,
            _workflowContext,
            Cipher);

    public static CustomerRepository Repository(CustomerDbContext db, PersonalDataCipher cipher) =>
        new(db, new CustomerPersonalData(db, cipher));

    /// <summary>One column of one row, exactly as stored.</summary>
    public string? Raw(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar() as string;
    }

    /// <summary>Runs SQL directly against the database (what erasure or an attacker with SQL access would do).</summary>
    public void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>Test-only: SQLite has no jsonb, so the Outbox payload is stored as its JSON text.</summary>
internal sealed class SqliteOutboxModelCustomizer(ModelCustomizerDependencies dependencies) : RelationalModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        modelBuilder.Entity<OutboxMessage>()
            .Property(x => x.Payload)
            .HasColumnType("TEXT")
            .HasConversion(value => value.RootElement.GetRawText(), value => JsonDocument.Parse(value, default));
    }
}
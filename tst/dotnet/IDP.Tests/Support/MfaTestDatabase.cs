using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

using EnterpriseWebPlatform.IdentityServer.Data;
using EnterpriseWebPlatform.IdentityServer.Data.Entities;
using EnterpriseWebPlatform.IdentityServer.Security;

namespace EnterpriseWebPlatform.IdentityServer.Tests.Support;

// ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
// │ The IDP's own model on an in-memory SQLite database, one per test. MfaService claims a
// │ code with ONE conditional UPDATE (ExecuteUpdate): a real relational database is needed
// │ to run that SQL, so EF Core's in-memory provider would prove nothing here. Production
// │ runs PostgreSQL; the SQL is the same single-row UPDATE ... WHERE.
// └──────────────────────────────────────────────────────────────────────────────────────
/// <summary>
/// An in-memory IDP database with one user (Daniel), a clock the test moves, and MfaService
/// built the way the IDP builds it. Each Service() call is a new request: a new DbContext.
/// </summary>
internal sealed class MfaTestDatabase : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();

    public MfaTestDatabase()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
        db.Users.Add(new User
        {
            SubjectId = Guid.NewGuid(),
            FirstName = "Daniel",
            LastName = "Ops",
            Email = "daniel.ops@ewp.example",
            UserName = "daniel.ops",
            HashedPassword = "not-used-here",
            IsActive = true,
            CreatedAt = Clock.GetUtcNow(),
            UpdatedAt = Clock.GetUtcNow()
        });
        db.SaveChanges();
    }

    /// <summary>The middle of a 30-second time step, so ±1 step of drift stays inside the test.</summary>
    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 22, 9, 0, 15, TimeSpan.Zero));

    public byte[] Secret { get; } = Totp.NewSecret();

    public MfaService Service() => new(NewContext(), _dataProtection, Clock);

    /// <summary>Daniel as the sign-in page reads him: a fresh copy, with his lockout as stored.</summary>
    public User Daniel()
    {
        using var db = NewContext();
        return db.Users.AsNoTracking().Single();
    }

    public IdentityDbContext NewContext() =>
        new(new DbContextOptionsBuilder<IdentityDbContext>().UseSqlite(_connection).Options);

    /// <summary>The code Daniel's authenticator shows now, or <paramref name="stepsFromNow"/> steps away.</summary>
    public string CodeNow(int stepsFromNow = 0) => Totp.CodeAt(Secret, Totp.TimeStepOf(Clock.GetUtcNow()) + stepsFromNow);

    /// <summary>Daniel enrols with the current code; returns his recovery codes.</summary>
    public async Task<IReadOnlyList<string>> EnrolAsync()
    {
        var codes = await Service().ConfirmEnrolmentAsync(Daniel(), Secret, CodeNow(), CancellationToken.None);
        return codes ?? throw new InvalidOperationException("Enrolment failed in the test set-up.");
    }

    public void Dispose() => _connection.Dispose();
}

/// <summary>A clock the test sets: MfaService reads "now" only through TimeProvider.</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
using Chaos.Server.Auth;
using Chaos.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Tests;

/// <summary>PLAN2 §8.4 step 5: a provider login never joins an existing account without an email code.</summary>
[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class AccountServiceTests(PostgresServer postgres)
{
    private async Task<(ChaosDbContext Db, AccountService Service)> OpenAsync()
    {
        var db = ServerDatabase.Open(await ServerDatabase.CreateMigratedAsync(postgres));
        return (db, new AccountService(db, TimeProvider.System));
    }

    [Test]
    public async Task ExternalLogin_WithAnEmailOfAnExistingAccount_NeedsConfirmationAndLinksNothing()
    {
        var (db, service) = await OpenAsync();
        await using var _ = db;
        await service.SignInWithEmailAsync("person@example.com", CancellationToken.None);

        var result = await service.SignInExternalAsync(LoginProviders.Google, "g-1", "Person@Example.com", emailVerified: true, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ExternalLoginOutcome.NeedsEmailConfirmation);
        await Assert.That(await db.AccountLogins.CountAsync(l => l.Provider == LoginProviders.Google)).IsEqualTo(0);
        await Assert.That(await db.Accounts.CountAsync()).IsEqualTo(1);
    }

    [Test]
    public async Task ExternalLogin_UnverifiedEmailOfAnExistingAccount_StillNeedsConfirmation()
    {
        var (db, service) = await OpenAsync();
        await using var _ = db;
        await service.SignInWithEmailAsync("person@example.com", CancellationToken.None);

        var result = await service.SignInExternalAsync(LoginProviders.Microsoft, "m-1", "person@example.com", emailVerified: false, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ExternalLoginOutcome.NeedsEmailConfirmation);
    }

    [Test]
    public async Task ExternalLogin_AfterConfirmation_LinksAndThenSignsInDirectly()
    {
        var (db, service) = await OpenAsync();
        await using var _ = db;
        var account = (await service.SignInWithEmailAsync("person@example.com", CancellationToken.None))!;

        var linked = await service.LinkExternalAsync("person@example.com", LoginProviders.Google, "g-1", CancellationToken.None);
        var again = await service.SignInExternalAsync(LoginProviders.Google, "g-1", null, emailVerified: false, CancellationToken.None);

        await Assert.That(linked!.Id).IsEqualTo(account.Id);
        await Assert.That(again.Outcome).IsEqualTo(ExternalLoginOutcome.SignedIn);
        await Assert.That(again.Account!.Id).IsEqualTo(account.Id);
    }

    [Test]
    public async Task ExternalLogin_NewVerifiedEmail_CreatesAnAccount()
    {
        var (db, service) = await OpenAsync();
        await using var _ = db;

        var result = await service.SignInExternalAsync(LoginProviders.GitHub, "gh-1", "new@example.com", emailVerified: true, CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(ExternalLoginOutcome.SignedIn);
        await Assert.That(result.Account!.PrimaryEmail).IsEqualTo("new@example.com");
        await Assert.That(await db.AccountLogins.CountAsync(l => l.AccountId == result.Account.Id)).IsEqualTo(1);
    }

    [Test]
    public async Task ExternalLogin_NewUnverifiedOrMissingEmail_IsRejected()
    {
        var (db, service) = await OpenAsync();
        await using var _ = db;

        var unverified = await service.SignInExternalAsync(LoginProviders.Microsoft, "m-1", "new@example.com", emailVerified: false, CancellationToken.None);
        var missing = await service.SignInExternalAsync(LoginProviders.GitHub, "gh-1", null, emailVerified: false, CancellationToken.None);

        await Assert.That(unverified.Outcome).IsEqualTo(ExternalLoginOutcome.Rejected);
        await Assert.That(missing.Outcome).IsEqualTo(ExternalLoginOutcome.Rejected);
        await Assert.That(await db.Accounts.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task EmailSignIn_ForADeletedAccount_IsRefused()
    {
        var (db, service) = await OpenAsync();
        await using var _ = db;
        var account = (await service.SignInWithEmailAsync("gone@example.com", CancellationToken.None))!;
        await db.Accounts.Where(a => a.Id == account.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, DateTimeOffset.UtcNow));
        db.ChangeTracker.Clear();

        await Assert.That(await service.SignInWithEmailAsync("gone@example.com", CancellationToken.None)).IsNull();
    }
}

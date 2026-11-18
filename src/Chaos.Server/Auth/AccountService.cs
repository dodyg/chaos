using Chaos.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Auth;

public enum ExternalLoginOutcome
{
    SignedIn,

    /// <summary>The provider's email belongs to an existing account: the person must confirm with an email code first (PLAN2 §8.4 step 5).</summary>
    NeedsEmailConfirmation,

    /// <summary>The provider gave no verified email, so no account can be made from it.</summary>
    Rejected,
}

public sealed record ExternalLoginResult(ExternalLoginOutcome Outcome, Account? Account);

/// <summary>Finds, creates and links accounts. Accounts are created on first sign-in.</summary>
public sealed class AccountService(ChaosDbContext db, TimeProvider clock)
{
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    /// <summary>A sign-in proved by an email code. Null when the account was deleted.</summary>
    public async Task<Account?> SignInWithEmailAsync(string email, CancellationToken ct)
    {
        email = NormalizeEmail(email);
        var account = await db.Accounts.Include(a => a.Logins).FirstOrDefaultAsync(a => a.PrimaryEmail == email, ct);
        if (account is { DeletedAt: not null })
        {
            return null;
        }

        account ??= CreateAccount(email);
        // The code proved the address, so the email login exists on every account that owns it.
        if (!account.Logins.Any(l => l.Provider == LoginProviders.Email && l.Subject == email))
        {
            account.Logins.Add(NewLogin(account.Id, LoginProviders.Email, email, email));
        }

        RecordSignIn(account.Id);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two first sign-ins may have raced; if the other one made the account, use it.
            db.ChangeTracker.Clear();
            if (!await db.Accounts.AsNoTracking().AnyAsync(a => a.PrimaryEmail == email, CancellationToken.None))
            {
                throw;
            }

            return await SignInWithEmailAsync(email, ct);
        }

        return account;
    }

    public async Task<ExternalLoginResult> SignInExternalAsync(string provider, string subject, string? email, bool emailVerified, CancellationToken ct)
    {
        var existing = await db.AccountLogins.AsNoTracking().FirstOrDefaultAsync(l => l.Provider == provider && l.Subject == subject, ct);
        if (existing is not null)
        {
            var linked = await db.Accounts.FirstOrDefaultAsync(a => a.Id == existing.AccountId && a.DeletedAt == null, ct);
            if (linked is null)
            {
                return new(ExternalLoginOutcome.Rejected, null);
            }

            RecordSignIn(linked.Id);
            await db.SaveChangesAsync(ct);
            return new(ExternalLoginOutcome.SignedIn, linked);
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            return new(ExternalLoginOutcome.Rejected, null);
        }

        email = NormalizeEmail(email);
        // Whether or not the provider vouches for the address, an existing account is never joined without an email code.
        if (await db.Accounts.AsNoTracking().AnyAsync(a => a.PrimaryEmail == email, ct))
        {
            return new(ExternalLoginOutcome.NeedsEmailConfirmation, null);
        }

        if (!emailVerified)
        {
            return new(ExternalLoginOutcome.Rejected, null);
        }

        var account = CreateAccount(email);
        account.Logins.Add(NewLogin(account.Id, provider, subject, email));
        RecordSignIn(account.Id);
        await db.SaveChangesAsync(ct);
        return new(ExternalLoginOutcome.SignedIn, account);
    }

    /// <summary>Adds a provider login to the account that owns <paramref name="email"/>, after the email code was accepted.</summary>
    public async Task<Account?> LinkExternalAsync(string email, string provider, string subject, CancellationToken ct)
    {
        var account = await SignInWithEmailAsync(email, ct);
        if (account is null)
        {
            return null;
        }

        if (!await db.AccountLogins.AnyAsync(l => l.Provider == provider && l.Subject == subject, ct))
        {
            db.AccountLogins.Add(NewLogin(account.Id, provider, subject, NormalizeEmail(email)));
            await db.SaveChangesAsync(ct);
        }

        return account;
    }

    private Account CreateAccount(string email)
    {
        var account = new Account { Id = Guid.NewGuid(), CreatedAt = clock.GetUtcNow(), PrimaryEmail = email };
        db.Accounts.Add(account);
        return account;
    }

    private AccountLogin NewLogin(Guid accountId, string provider, string subject, string? email) =>
        new() { AccountId = accountId, Provider = provider, Subject = subject, Email = email, LinkedAt = clock.GetUtcNow() };

    private void RecordSignIn(Guid accountId) =>
        db.AuditEvents.Add(new AuditEvent { AccountId = accountId, Kind = AuditKinds.SignIn, At = clock.GetUtcNow() });
}

using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chaos.Server.Auth;
using Chaos.Server.Data;
using Chaos.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Sync;

public sealed record CreateSpaceRequest(Guid SpaceId, string? Name, string? ColorKey, string? Icon);
public sealed record MemberRoleRequest(short Role);
public sealed record MemberProfileRequest(string? DisplayName, string? GitHubLogin);
public sealed record CreateInviteRequest(string? Email, short Role, DateTimeOffset? ExpiresAt, int? MaxUses);
public sealed record AcceptInviteRequest(string? Token);
public sealed record MoveRequest(Guid MoveId, Guid ContainerId);

public sealed record SpaceDto(Guid Id, string Name, short Role);
public sealed record MemberDto(Guid MemberId, string Name, short Role, short Kind, DateTimeOffset JoinedAt, DateTimeOffset? RemovedAt);
public sealed record InviteDto(Guid Id, string? Email, short Role, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt,
    int? MaxUses, int Uses, DateTimeOffset? RevokedAt);
public sealed record CreatedInviteDto(InviteDto Invite, string? Link);
public sealed record MoveDto(Guid MoveId, Guid SpaceId, int EntityCount);

public sealed record SpaceOperation<T>(T? Value, string? Error = null, string? Detail = null)
{
    public bool Succeeded => Error is null;
    public static SpaceOperation<T> Success(T value) => new(value);
    public static SpaceOperation<T> Refuse(string code, string detail) => new(default, code, detail);
}

/// <summary>Authoritative shared-space membership, invites, server changes and moves.</summary>
public sealed class SpaceService(
    ChaosDbContext db,
    TimeProvider clock,
    ServerChangeWriter changes,
    SpaceMembershipCache memberships,
    SyncBroadcaster broadcaster,
    IEmailSender mail,
    IOptions<SharingOptions> options,
    ILogger<SpaceService> log)
{
    private readonly TimeSpan _emailExpiry = TimeSpan.FromDays(14);

    public async Task<SpaceOperation<SpaceDto>> CreateAsync(Guid accountId, CreateSpaceRequest request, Guid deviceId,
        CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (request.SpaceId == Guid.Empty || string.IsNullOrEmpty(name) || name.EnumerateRunes().Count() > 200)
        {
            return SpaceOperation<SpaceDto>.Refuse("invalid-space", "The space needs an ID and a name of at most 200 characters.");
        }

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var account = await db.Accounts.FromSqlInterpolated($"""SELECT * FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""")
                .FirstOrDefaultAsync(ct);
            if (account is null || account.DeletedAt is not null)
            {
                return SpaceOperation<SpaceDto>.Refuse("account-unavailable", "This account was deleted.");
            }
            if (account.ProfileId is not { } profile)
            {
                return SpaceOperation<SpaceDto>.Refuse("setup-required", "Connect and bind this account on a desktop first.");
            }
            var existing = await db.SyncStreams.AsNoTracking().SingleOrDefaultAsync(s => s.Id == request.SpaceId, ct);
            if (existing is not null)
            {
                var mine = await db.SpaceMembers.AsNoTracking().SingleOrDefaultAsync(
                    m => m.SpaceId == request.SpaceId && m.MemberId == profile && m.RemovedAt == null, ct);
                return mine is not null && existing.DeletedAt is null
                    ? SpaceOperation<SpaceDto>.Success(new(request.SpaceId, SpaceName(existing.Id), mine.Role))
                    : SpaceOperation<SpaceDto>.Refuse("space-id-in-use", "That space ID is already in use.");
            }
            var owned = await db.SpaceMembers.CountAsync(m =>
                m.AccountId == accountId && m.Role == SpaceMember.OwnerRole && m.RemovedAt == null, ct);
            if (owned >= options.Value.MaxSpacesPerAccount)
            {
                return SpaceOperation<SpaceDto>.Refuse("space-limit", "This account has reached its shared-space limit.");
            }

            var now = clock.GetUtcNow();
            var stream = new SyncStream
            {
                Id = request.SpaceId, Kind = SyncStream.SpaceKind, SpaceId = request.SpaceId, CreatedAt = now,
            };
            db.SyncStreams.Add(stream);
            db.SpaceMembers.Add(new SpaceMember
            {
                SpaceId = request.SpaceId, MemberId = profile, MemberKind = SpaceMember.PersonKind, AccountId = accountId,
                Role = SpaceMember.OwnerRole, DisplayName = account.DisplayName ?? "A member", JoinedAt = now,
            });
            account.MembershipVersion++;
            await db.SaveChangesAsync(ct);

            var spaceKey = request.SpaceId.ToString("D");
            var hlc = await changes.NextHlcAsync(request.SpaceId, "space", spaceKey, ct);
            await changes.AppendAsync(request.SpaceId, profile, SyncChange.Upsert(Guid.NewGuid().ToString("D"), "space", spaceKey,
                Groups(hlc, new Dictionary<string, Dictionary<string, object?>>
                {
                    ["create"] = new() { ["OwnerProfileId"] = profile.ToString("D"), ["Kind"] = 2L, ["CreatedAtUtc"] = Instant(now) },
                    ["name"] = new() { ["Name"] = name },
                    ["look"] = new() { ["ColorKey"] = request.ColorKey, ["Icon"] = request.Icon },
                    ["settings"] = Settings(),
                    ["deleted"] = new() { ["DeletedAtUtc"] = null },
                })), ct);
            await AppendMemberAsync(stream, profile, null, account, SpaceMember.OwnerRole, now, ct);
            db.AuditEvents.Add(new AuditEvent
            {
                AccountId = accountId, DeviceId = deviceId, Kind = AuditKinds.SpaceCreated, At = now,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await NotifyStreamAsync(request.SpaceId, ct);
            await NotifyMembershipAsync(accountId, account.MembershipVersion, ct);
            return SpaceOperation<SpaceDto>.Success(new(request.SpaceId, name, SpaceMember.OwnerRole));
        });
    }

    public async Task<SpaceOperation<IReadOnlyList<MemberDto>>> ListMembersAsync(Guid accountId, Guid spaceId, CancellationToken ct)
    {
        var member = await CurrentMemberAsync(accountId, spaceId, ct);
        if (member is null)
        {
            return SpaceOperation<IReadOnlyList<MemberDto>>.Refuse(SyncRejection.NotMember, "You are not a member of this space.");
        }
        var rows = await db.SpaceMembers.AsNoTracking().Where(m => m.SpaceId == spaceId).OrderBy(m => m.JoinedAt)
            .Select(m => new MemberDto(m.MemberId, m.DisplayName, m.Role, m.MemberKind, m.JoinedAt, m.RemovedAt))
            .ToListAsync(ct);
        return SpaceOperation<IReadOnlyList<MemberDto>>.Success(rows);
    }

    public async Task<SpaceOperation<MemberDto>> ChangeRoleAsync(Guid accountId, Guid deviceId, Guid spaceId, Guid memberId,
        short role, CancellationToken ct)
    {
        if (role is not (SpaceMember.EditorRole or SpaceMember.ViewerRole or SpaceMember.OwnerRole))
        {
            return SpaceOperation<MemberDto>.Refuse("invalid-role", "The role must be owner, editor or viewer.");
        }

        return await strategy(async tx =>
        {
            var stream = await SpaceStreamForUpdateAsync(spaceId, ct);
            await LockSpaceMembersAsync(spaceId, ct);
            var current = await CurrentMemberAsync(accountId, spaceId, ct);
            if (current is null)
            {
                return SpaceOperation<MemberDto>.Refuse(SyncRejection.NotMember, "You are not a member of this space.");
            }
            if (current.Role != SpaceMember.OwnerRole)
            {
                return SpaceOperation<MemberDto>.Refuse(SyncRejection.Forbidden, "Only an owner can change roles.");
            }
            var target = await db.SpaceMembers.SingleOrDefaultAsync(m => m.SpaceId == spaceId
                && m.MemberId == memberId && m.RemovedAt == null, ct);
            if (target is null)
            {
                return SpaceOperation<MemberDto>.Refuse("member-not-found", "That member is not in this space.");
            }
            if (target.Role == SpaceMember.OwnerRole && role != SpaceMember.OwnerRole
                && await db.SpaceMembers.CountAsync(m => m.SpaceId == spaceId && m.Role == SpaceMember.OwnerRole && m.RemovedAt == null, ct) == 1)
            {
                return SpaceOperation<MemberDto>.Refuse("last-owner", "The space must keep an owner.");
            }
            if (target.Role == role)
            {
                return SpaceOperation<MemberDto>.Success(ToDto(target));
            }

            target.Role = role;
            await AppendMemberGroupAsync(stream, current.MemberId, target, "role", new() { ["Role"] = (long)role }, ct);
            db.AuditEvents.Add(new AuditEvent { AccountId = accountId, DeviceId = deviceId, Kind = AuditKinds.MemberRole, At = clock.GetUtcNow() });
            if (target.AccountId is { } affected)
            {
                await BumpMembershipAsync(affected, ct);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await NotifyStreamAsync(spaceId, ct);
            if (target.AccountId is { } changedAccount)
            {
                await NotifyCurrentMembershipAsync(changedAccount, ct);
            }
            return SpaceOperation<MemberDto>.Success(ToDto(target));
        });
    }

    public async Task<SpaceOperation<MemberDto>> RemoveAsync(Guid accountId, Guid deviceId, Guid spaceId, Guid memberId,
        CancellationToken ct)
    {
        return await strategy(async tx =>
        {
            var stream = await SpaceStreamForUpdateAsync(spaceId, ct);
            await LockSpaceMembersAsync(spaceId, ct);
            var current = await CurrentMemberAsync(accountId, spaceId, ct);
            var target = current is not null && current.MemberId == memberId
                ? current
                : await db.SpaceMembers.SingleOrDefaultAsync(m => m.SpaceId == spaceId
                    && m.MemberId == memberId && m.RemovedAt == null, ct);
            if (current is null || (current.MemberId != memberId && current.Role != SpaceMember.OwnerRole) || target is null)
            {
                return SpaceOperation<MemberDto>.Refuse(current is null ? SyncRejection.NotMember : SyncRejection.Forbidden,
                    "Only an owner may remove another member.");
            }
            if (target.Role == SpaceMember.OwnerRole
                && await db.SpaceMembers.CountAsync(m => m.SpaceId == spaceId && m.Role == SpaceMember.OwnerRole && m.RemovedAt == null, ct) == 1)
            {
                return SpaceOperation<MemberDto>.Refuse("last-owner", "The space must keep an owner.");
            }

            var now = clock.GetUtcNow();
            target.RemovedAt = now;
            await AppendMemberGroupAsync(stream, current.MemberId, target, "state", new() { ["RemovedAtUtc"] = Instant(now) }, ct);
            await changes.TombstoneAssignmentsAsync(spaceId, target.MemberId, current.MemberId, now, ct);
            if (target.AccountId is { } affected)
            {
                await TombstoneOverlaysAsync(affected, spaceId, current.MemberId, ct);
                await BumpMembershipAsync(affected, ct);
            }
            db.AuditEvents.Add(new AuditEvent { AccountId = accountId, DeviceId = deviceId, Kind = AuditKinds.MemberRemoved, At = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await NotifyStreamAsync(spaceId, ct);
            if (target.AccountId is { } changedAccount)
            {
                await NotifyCurrentMembershipAsync(changedAccount, ct);
            }
            return SpaceOperation<MemberDto>.Success(ToDto(target));
        });
    }

    public async Task<SpaceOperation<MemberDto>> UpdateProfileAsync(Guid accountId, Guid spaceId, MemberProfileRequest request,
        CancellationToken ct)
    {
        var displayName = request.DisplayName?.Trim();
        var githubLogin = request.GitHubLogin?.Trim().TrimStart('@');
        if (displayName is not null && (displayName.Length == 0 || displayName.EnumerateRunes().Count() > 200))
        {
            return SpaceOperation<MemberDto>.Refuse("invalid-member", "The display name must be 1 to 200 characters.");
        }
        if (githubLogin is not null && (githubLogin.Length == 0 || githubLogin.Length > 39
            || githubLogin.Any(c => !(char.IsLetterOrDigit(c) || c == '-'))))
        {
            return SpaceOperation<MemberDto>.Refuse("invalid-member", "The GitHub login is not valid.");
        }

        return await strategy(async tx =>
        {
            var stream = await SpaceStreamForUpdateAsync(spaceId, ct);
            await LockSpaceMembersAsync(spaceId, ct);
            var member = await CurrentMemberAsync(accountId, spaceId, ct);
            if (member is null)
            {
                return SpaceOperation<MemberDto>.Refuse(SyncRejection.NotMember, "You are not a member of this space.");
            }
            if (displayName is not null)
            {
                member.DisplayName = displayName;
            }
            if (request.GitHubLogin is not null)
            {
                member.GitHubLogin = githubLogin;
            }
            await AppendMemberGroupAsync(stream, member.MemberId, member, "profile",
                new() { ["DisplayName"] = member.DisplayName, ["GitHubLogin"] = member.GitHubLogin }, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await NotifyStreamAsync(spaceId, ct);
            return SpaceOperation<MemberDto>.Success(ToDto(member));
        });
    }

    public async Task<SpaceOperation<MemberDto>> RemoveCurrentAsync(Guid accountId, Guid deviceId, Guid spaceId,
        CancellationToken ct)
    {
        var member = await CurrentMemberAsync(accountId, spaceId, ct);
        return member is null
            ? SpaceOperation<MemberDto>.Refuse(SyncRejection.NotMember, "You are not a member of this space.")
            : await RemoveAsync(accountId, deviceId, spaceId, member.MemberId, ct);
    }

    public async Task<SpaceOperation<CreatedInviteDto>> CreateInviteAsync(Guid accountId, Guid deviceId, Guid spaceId,
        CreateInviteRequest request, string baseUrl, CancellationToken ct)
    {
        if (request.Role is not (SpaceMember.EditorRole or SpaceMember.ViewerRole))
        {
            return SpaceOperation<CreatedInviteDto>.Refuse("invalid-role", "An invite role must be editor or viewer.");
        }
        string? email = null;
        if (request.Email is not null)
        {
            email = AccountService.NormalizeEmail(request.Email);
            if (!MailAddress.TryCreate(email, out _))
            {
                return SpaceOperation<CreatedInviteDto>.Refuse("invalid-email", "Enter a valid email address.");
            }
        }
        if (request.MaxUses is <= 0 || request.ExpiresAt <= clock.GetUtcNow())
        {
            return SpaceOperation<CreatedInviteDto>.Refuse("invalid-invite", "The invite expiry and use count are not valid.");
        }

        var token = Token();
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        SpaceInvite? invite = null;
        string inviterName = string.Empty;
        string spaceName = "Shared space";
        var result = await strategy(async tx =>
        {
            var stream = await SpaceStreamForUpdateAsync(spaceId, ct);
            await LockSpaceMembersAsync(spaceId, ct);
            var owner = await CurrentMemberAsync(accountId, spaceId, ct);
            if (owner is null)
            {
                return SpaceOperation<CreatedInviteDto>.Refuse(SyncRejection.NotMember, "You are not a member of this space.");
            }
            if (owner.Role != SpaceMember.OwnerRole)
            {
                return SpaceOperation<CreatedInviteDto>.Refuse(SyncRejection.Forbidden, "Only an owner can invite people.");
            }
            var active = await db.SpaceInvites.CountAsync(i => i.SpaceId == spaceId && i.RevokedAt == null
                && (i.ExpiresAt == null || i.ExpiresAt > clock.GetUtcNow())
                && (i.MaxUses == null || i.Uses < i.MaxUses), ct);
            if (active >= options.Value.MaxOpenInvitesPerSpace)
            {
                return SpaceOperation<CreatedInviteDto>.Refuse("invite-limit", "This space has reached its open-invite limit.");
            }
            if (email is not null)
            {
                var recent = await db.SpaceInvites.CountAsync(i => i.Email != null
                    && db.SpaceMembers.Any(m => m.SpaceId == i.SpaceId && m.MemberId == i.CreatedByMemberId
                        && m.AccountId == accountId)
                    && i.CreatedAt >= clock.GetUtcNow().AddDays(-1), ct);
                if (recent >= options.Value.MaxInviteEmailsPerAccountPerDay)
                {
                    return SpaceOperation<CreatedInviteDto>.Refuse("invite-email-limit", "This account has sent too many invite emails today.");
                }
            }

            var now = clock.GetUtcNow();
            invite = new SpaceInvite
            {
                Id = Guid.NewGuid(), SpaceId = spaceId, TokenHash = tokenHash, Email = email, Role = request.Role,
                CreatedByMemberId = owner.MemberId, CreatedAt = now,
                ExpiresAt = email is null ? request.ExpiresAt : now + _emailExpiry,
                MaxUses = email is null ? request.MaxUses : 1,
            };
            db.SpaceInvites.Add(invite);
            inviterName = owner.DisplayName;
            spaceName = SpaceName(spaceId);
            db.AuditEvents.Add(new AuditEvent { AccountId = accountId, DeviceId = deviceId, Kind = AuditKinds.InviteCreated, At = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            var dto = ToDto(invite);
            return SpaceOperation<CreatedInviteDto>.Success(new(dto, email is null ? InviteLink(baseUrl, token) : null));
        });
        if (!result.Succeeded || email is null)
        {
            return result;
        }

        var link = InviteLink(baseUrl, token);
        var text = $"{inviterName} invited you to join {spaceName} in Chaos.\n\n{link}\n\nThe invitation works for 14 days.";
        var html = $"<p>{Encode(inviterName)} invited you to join <strong>{Encode(spaceName)}</strong> in Chaos.</p><p><a href=\"{Encode(link)}\">Join {Encode(spaceName)}</a></p><p>The invitation works for 14 days.</p>";
        try
        {
            await mail.SendHtmlAsync(email, $"Join {spaceName} in Chaos", text, html, ct);
            return result;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await db.SpaceInvites.Where(i => i.Id == result.Value!.Invite.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAt, clock.GetUtcNow()), CancellationToken.None);
            log.LogWarning("An invite email could not be sent ({ErrorType})", error.GetType().Name);
            return SpaceOperation<CreatedInviteDto>.Refuse("email-unavailable", "The invite email could not be sent.");
        }
    }

    public async Task<SpaceOperation<IReadOnlyList<InviteDto>>> ListInvitesAsync(Guid accountId, Guid spaceId, CancellationToken ct)
    {
        var current = await CurrentMemberAsync(accountId, spaceId, ct);
        if (current is null)
        {
            return SpaceOperation<IReadOnlyList<InviteDto>>.Refuse(SyncRejection.NotMember, "You are not a member of this space.");
        }
        if (current.Role != SpaceMember.OwnerRole)
        {
            return SpaceOperation<IReadOnlyList<InviteDto>>.Refuse(SyncRejection.Forbidden, "Only an owner can list invites.");
        }
        var rows = await db.SpaceInvites.AsNoTracking().Where(i => i.SpaceId == spaceId).OrderByDescending(i => i.CreatedAt)
            .Select(i => new InviteDto(i.Id, i.Email, i.Role, i.CreatedAt, i.ExpiresAt, i.MaxUses, i.Uses, i.RevokedAt))
            .ToListAsync(ct);
        return SpaceOperation<IReadOnlyList<InviteDto>>.Success(rows);
    }

    public async Task<SpaceOperation<bool>> RevokeInviteAsync(Guid accountId, Guid deviceId, Guid spaceId, Guid inviteId,
        CancellationToken ct)
    {
        return await strategy(async tx =>
        {
            await SpaceStreamForUpdateAsync(spaceId, ct);
            await LockSpaceMembersAsync(spaceId, ct);
            var current = await CurrentMemberAsync(accountId, spaceId, ct);
            if (current is null)
            {
                return SpaceOperation<bool>.Refuse(SyncRejection.NotMember, "You are not a member of this space.");
            }
            if (current.Role != SpaceMember.OwnerRole)
            {
                return SpaceOperation<bool>.Refuse(SyncRejection.Forbidden, "Only an owner can revoke invites.");
            }
            var invite = await db.SpaceInvites.SingleOrDefaultAsync(i => i.SpaceId == spaceId && i.Id == inviteId, ct);
            if (invite is null)
            {
                return SpaceOperation<bool>.Refuse("invite-not-found", "That invite does not exist.");
            }
            invite.RevokedAt ??= clock.GetUtcNow();
            db.AuditEvents.Add(new AuditEvent { AccountId = accountId, DeviceId = deviceId, Kind = AuditKinds.InviteRevoked, At = invite.RevokedAt.Value });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return SpaceOperation<bool>.Success(true);
        });
    }

    public async Task<SpaceOperation<SpaceDto>> AcceptAsync(Guid accountId, Guid? deviceId, string token, CancellationToken ct)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return await strategy(async tx =>
        {
            var invite = await db.SpaceInvites.FromSqlInterpolated(
                $"""SELECT * FROM "SpaceInvites" WHERE "TokenHash" = {hash} FOR UPDATE""").SingleOrDefaultAsync(ct);
            if (invite is null)
            {
                return SpaceOperation<SpaceDto>.Refuse("invite-invalid", "That invitation is not valid.");
            }
            var now = clock.GetUtcNow();
            if (invite.RevokedAt is not null)
            {
                return SpaceOperation<SpaceDto>.Refuse("invite-revoked", "That invitation has been turned off.");
            }
            if (invite.ExpiresAt <= now)
            {
                return SpaceOperation<SpaceDto>.Refuse("invite-expired", "That invitation has expired.");
            }
            if (invite.MaxUses is { } uses && invite.Uses >= uses)
            {
                return SpaceOperation<SpaceDto>.Refuse("invite-used", "That invitation has already been used.");
            }
            var stream = await SpaceStreamForUpdateAsync(invite.SpaceId, requireDeleted: false, ct);
            if (stream is null)
            {
                return SpaceOperation<SpaceDto>.Refuse("invite-invalid", "That space is no longer available.");
            }
            await LockSpaceMembersAsync(invite.SpaceId, ct);
            var account = await db.Accounts.FromSqlInterpolated($"""SELECT * FROM "Accounts" WHERE "Id" = {accountId} FOR UPDATE""")
                .SingleOrDefaultAsync(ct);
            if (account?.ProfileId is not { } profile || account.DeletedAt is not null)
            {
                return SpaceOperation<SpaceDto>.Refuse("setup-required", "Connect and bind this account on a desktop first.");
            }
            if (invite.Email is not null && !await HasVerifiedEmailAsync(account, invite.Email, ct))
            {
                return SpaceOperation<SpaceDto>.Refuse("invite-email-mismatch", "Sign in with the email address named by this invitation.");
            }

            var member = await db.SpaceMembers.SingleOrDefaultAsync(m => m.SpaceId == invite.SpaceId && m.MemberId == profile, ct);
            if (member is not null && member.AccountId != accountId)
            {
                return SpaceOperation<SpaceDto>.Refuse("member-account-mismatch",
                    "This member identity cannot be attached to this account.");
            }
            var reactivating = member is { RemovedAt: not null };
            var higher = member is null || reactivating
                ? invite.Role
                : HigherRole(member.Role, invite.Role);
            if (member is null || reactivating)
            {
                var people = await db.SpaceMembers.CountAsync(m => m.SpaceId == invite.SpaceId
                    && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null, ct);
                if (people >= options.Value.MaxPeoplePerSpace)
                {
                    return SpaceOperation<SpaceDto>.Refuse("member-limit", "This space has reached its people limit.");
                }
                member ??= new SpaceMember
                {
                    SpaceId = invite.SpaceId, MemberId = profile, MemberKind = SpaceMember.PersonKind, AccountId = accountId,
                    Role = invite.Role, DisplayName = account.DisplayName ?? "A member",
                    JoinedAt = now, InvitedByMemberId = invite.CreatedByMemberId,
                };
                if (reactivating)
                {
                    member.Role = invite.Role;
                    member.AccountId = accountId;
                    member.DisplayName = account.DisplayName ?? "A member";
                    member.JoinedAt = now;
                    member.InvitedByMemberId = invite.CreatedByMemberId;
                    member.RemovedAt = null;
                    await AppendMemberGroupAsync(stream, profile, member, "profile",
                        new() { ["DisplayName"] = member.DisplayName, ["GitHubLogin"] = member.GitHubLogin }, ct);
                    await AppendMemberGroupAsync(stream, profile, member, "role",
                        new() { ["Role"] = (long)member.Role }, ct);
                    await AppendMemberGroupAsync(stream, profile, member, "state",
                        new() { ["RemovedAtUtc"] = null }, ct);
                }
                else
                {
                    db.SpaceMembers.Add(member);
                    await db.SaveChangesAsync(ct);
                    await AppendMemberAsync(stream, profile, invite.CreatedByMemberId, account, invite.Role, now, ct);
                }
                account.MembershipVersion++;
            }
            else
            {
                member.AccountId = accountId;
                member.RemovedAt = null;
                member.DisplayName = member.DisplayName == "Former member" ? account.DisplayName ?? "A member" : member.DisplayName;
                if (member.Role != higher)
                {
                    member.Role = higher;
                    await AppendMemberGroupAsync(stream, profile, member, "role", new() { ["Role"] = (long)higher }, ct);
                }
                await AppendMemberGroupAsync(stream, profile, member, "state", new() { ["RemovedAtUtc"] = null }, ct);
                account.MembershipVersion++;
            }
            invite.Uses++;
            invite.LastUsedAt = now;
            db.AuditEvents.Add(new AuditEvent { AccountId = accountId, DeviceId = deviceId, Kind = AuditKinds.InviteUsed, At = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await NotifyStreamAsync(invite.SpaceId, ct);
            await NotifyMembershipAsync(accountId, account.MembershipVersion, ct);
            return SpaceOperation<SpaceDto>.Success(new(invite.SpaceId, SpaceName(invite.SpaceId), higher));
        });
    }

    public async Task<SpaceOperation<bool>> DeleteAsync(Guid accountId, Guid deviceId, Guid spaceId, CancellationToken ct)
    {
        return await strategy(async tx =>
        {
            var stream = await SpaceStreamForUpdateAsync(spaceId, ct);
            await LockSpaceMembersAsync(spaceId, ct);
            var current = await db.SpaceMembers.SingleOrDefaultAsync(m => m.SpaceId == spaceId
                && m.AccountId == accountId && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null, ct);
            if (current is null)
            {
                return SpaceOperation<bool>.Refuse(SyncRejection.NotMember, "You are not a member of this space.");
            }
            if (current.Role != SpaceMember.OwnerRole)
            {
                return SpaceOperation<bool>.Refuse(SyncRejection.Forbidden, "Only an owner can delete this space.");
            }
            if (stream.DeletedAt is not null)
            {
                return SpaceOperation<bool>.Success(true);
            }
            var now = clock.GetUtcNow();
            stream.DeletedAt = now;
            stream.PurgeAfter = now.AddDays(30);
            var owner = current;
            var hlc = await changes.NextHlcAsync(spaceId, "space", spaceId.ToString("D"), ct);
            await changes.AppendAsync(spaceId, owner.MemberId, SyncChange.Upsert(Guid.NewGuid().ToString("D"), "space",
                spaceId.ToString("D"), new Dictionary<string, SyncGroupState>
                {
                    ["deleted"] = new(hlc, new SyncGroupValues(new Dictionary<string, object?> { ["DeletedAtUtc"] = Instant(now) })),
                }), ct);
            var live = await db.SpaceMembers.Where(m => m.SpaceId == spaceId && m.RemovedAt == null
                && m.AccountId != null).Select(m => m.AccountId!.Value).ToListAsync(ct);
            foreach (var affected in live)
            {
                await TombstoneOverlaysAsync(affected, spaceId, owner.MemberId, ct);
                await BumpMembershipAsync(affected, ct);
            }
            db.AuditEvents.Add(new AuditEvent { AccountId = accountId, DeviceId = deviceId, Kind = AuditKinds.SpaceDeleted, At = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await NotifyStreamAsync(spaceId, ct);
            foreach (var affected in live)
            {
                await NotifyCurrentMembershipAsync(affected, ct);
            }
            return SpaceOperation<bool>.Success(true);
        });
    }

    public async Task<SpaceOperation<bool>> RestoreAsync(Guid accountId, Guid deviceId, Guid spaceId, CancellationToken ct)
    {
        return await strategy(async tx =>
        {
            var stream = await SpaceStreamForUpdateAsync(spaceId, requireDeleted: true, ct);
            await LockSpaceMembersAsync(spaceId, ct);
            var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId, ct);
            var member = await db.SpaceMembers.SingleOrDefaultAsync(m => m.SpaceId == spaceId && m.AccountId == accountId
                && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null, ct);
            if (member is null)
            {
                return SpaceOperation<bool>.Refuse(SyncRejection.NotMember, "You are not a member of this space.");
            }
            if (member.Role != SpaceMember.OwnerRole || account is null || stream?.PurgeAfter is null)
            {
                return SpaceOperation<bool>.Refuse(SyncRejection.Forbidden, "Only an owner can restore this space.");
            }
            var writes = await db.SyncChanges.AsNoTracking()
                .Where(c => c.AccountId == spaceId && c.Kind == "space" && c.EntityKey == spaceId.ToString("D")
                    && c.Op == "upsert")
                .OrderByDescending(c => c.Seq)
                .Select(c => new { c.ActorId, c.Groups })
                .ToListAsync(ct);
            var lastDeletion = writes.FirstOrDefault(write =>
                SyncJson.ReadGroups(write.Groups).GetValueOrDefault("deleted")?.Values.Values
                    .GetValueOrDefault("DeletedAtUtc") is not null);
            if (lastDeletion?.ActorId != member.MemberId)
            {
                return SpaceOperation<bool>.Refuse(SyncRejection.Forbidden,
                    "Only the owner who deleted this space can restore it.");
            }
            if (stream.PurgeAfter <= clock.GetUtcNow())
            {
                return SpaceOperation<bool>.Refuse("restore-expired", "The restore period has ended.");
            }
            stream.DeletedAt = null;
            stream.PurgeAfter = null;
            var hlc = await changes.NextHlcAsync(spaceId, "space", spaceId.ToString("D"), ct);
            await changes.AppendAsync(spaceId, member.MemberId, SyncChange.Upsert(Guid.NewGuid().ToString("D"), "space",
                spaceId.ToString("D"), new Dictionary<string, SyncGroupState>
                {
                    ["deleted"] = new(hlc, new SyncGroupValues(new Dictionary<string, object?> { ["DeletedAtUtc"] = null })),
                }), ct);
            foreach (var affected in await db.SpaceMembers.Where(m => m.SpaceId == spaceId && m.RemovedAt == null
                && m.AccountId != null).Select(m => m.AccountId!.Value).Distinct().ToListAsync(ct))
            {
                await BumpMembershipAsync(affected, ct);
            }
            db.AuditEvents.Add(new AuditEvent { AccountId = accountId, DeviceId = deviceId, Kind = AuditKinds.SpaceRestored, At = clock.GetUtcNow() });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await NotifyStreamAsync(spaceId, ct);
            foreach (var affected in await db.SpaceMembers.AsNoTracking().Where(m => m.SpaceId == spaceId && m.RemovedAt == null
                && m.AccountId != null).Select(m => m.AccountId!.Value).Distinct().ToListAsync(ct))
            {
                await NotifyCurrentMembershipAsync(affected, ct);
            }
            return SpaceOperation<bool>.Success(true);
        });
    }

    public async Task<SpaceOperation<MoveDto>> MoveAsync(Guid accountId, Guid deviceId, Guid spaceId, MoveRequest request,
        CancellationToken ct)
    {
        return await strategy(async tx =>
        {
            var streamIds = new[] { accountId, spaceId }.Order().ToArray();
            var locked = await db.SyncStreams.FromSqlInterpolated(
                $"""SELECT * FROM "SyncStreams" WHERE "Id" = ANY({streamIds}) ORDER BY "Id" FOR UPDATE""").ToListAsync(ct);
            await LockSpaceMembersAsync(spaceId, ct);
            var current = await CurrentMemberAsync(accountId, spaceId, ct);
            if (current?.Role is not (SpaceMember.OwnerRole or SpaceMember.EditorRole))
            {
                return SpaceOperation<MoveDto>.Refuse(current is null ? SyncRejection.NotMember : SyncRejection.Forbidden,
                    "Only an owner or editor can move a list into this space.");
            }
            var accountStream = locked.SingleOrDefault(s => s.Id == accountId);
            var targetStream = locked.SingleOrDefault(s => s.Id == spaceId && s.DeletedAt == null);
            if (accountStream is null || targetStream is null)
            {
                return SpaceOperation<MoveDto>.Refuse("space-unavailable", "The target space is not available.");
            }
            if (await db.SyncChanges.AsNoTracking().AnyAsync(c => c.AccountId == accountId && c.ChangeId == request.MoveId, ct))
            {
                return SpaceOperation<MoveDto>.Success(new(request.MoveId, spaceId, 0));
            }

            var account = await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == accountId, ct);
            var root = await db.SyncEntities.SingleOrDefaultAsync(e =>
                e.AccountId == accountId && e.Kind == "container" && e.EntityKey == request.ContainerId.ToString("D")
                && e.PurgedAt == null && e.MovedTo == null, ct);
            if (root is null)
            {
                return SpaceOperation<MoveDto>.Refuse("container-not-found", "That list is no longer in your Personal space.");
            }
            var rootGroups = SyncJson.ReadGroups(root.Groups);
            if (rootGroups["create"].Values.Values.GetValueOrDefault("IsInbox") is 1L
                || request.ContainerId == account.InboxId)
            {
                return SpaceOperation<MoveDto>.Refuse("inbox-cannot-move", "The Inbox cannot move into a shared space.");
            }

            var all = await db.SyncEntities.Where(e => e.AccountId == accountId && e.PurgedAt == null && e.MovedTo == null)
                .ToListAsync(ct);
            var parsed = all.ToDictionary(e => (e.Kind, e.EntityKey), e => SyncJson.ReadGroups(e.Groups));
            var included = new HashSet<(string Kind, string Key)> { ("container", root.EntityKey) };
            bool changed;
            do
            {
                changed = false;
                foreach (var entity in all)
                {
                    var key = (entity.Kind, entity.EntityKey);
                    if (included.Contains(key) || SyncKinds.Find(entity.Kind) is not { } kind)
                    {
                        continue;
                    }
                    if (kind.Route.Kind == SyncRouteKind.Parent)
                    {
                        var parent = RouteParent(kind.Route, entity.EntityKey, parsed[key]);
                        if (parent is { } p && included.Contains(p))
                        {
                            included.Add(key);
                            changed = true;
                        }
                    }
                    else if (kind.Route.Kind == SyncRouteKind.OwnSpace && ReferencesIncluded(parsed[key], included))
                    {
                        included.Add(key);
                        changed = true;
                    }
                    else if (kind.Kind == "attachment" && parsed[key].Values.SelectMany(g => g.Values.Values)
                        .Any(pair => pair.Key == "OwnerId" && pair.Value is string owner && included.Any(i => i.Key == owner)))
                    {
                        included.Add(key);
                        changed = true;
                    }
                }
            }
            while (changed);

            var now = clock.GetUtcNow();
            var moving = included.OrderBy(pair => SyncKinds.Find(pair.Kind)?.ApplyOrder ?? int.MaxValue).ToList();
            foreach (var (kind, key) in moving)
            {
                var old = all.Single(e => e.Kind == kind && e.EntityKey == key);
                var info = SyncKinds.Find(kind)!;
                var groups = SyncJson.ReadGroups(old.Groups);
                if (info.HasSpace && groups.TryGetValue("create", out var create))
                {
                    var values = create.Values.Values.ToDictionary();
                    values["SpaceId"] = spaceId.ToString("D");
                    var hlc = await changes.NextHlcAsync(spaceId, kind, key, ct);
                    groups["create"] = new SyncGroupState(hlc, new SyncGroupValues(values));
                }
                if (kind == "task" && groups.TryGetValue("status", out var status)
                    && status.Values.Values.GetValueOrDefault("CustomStateId") is not null)
                {
                    // States belong to their space and do not move with a list. Keep the base
                    // status, and issue a clock that also wins over the mover's local status.
                    var values = status.Values.Values.ToDictionary();
                    values["CustomStateId"] = null;
                    var hybrid = new HybridClock(ServerChangeWriter.DeviceId, clock);
                    hybrid.Receive(status.Hlc);
                    groups["status"] = new SyncGroupState(hybrid.TickLocal(), new SyncGroupValues(values));
                }
                await changes.AppendAsync(spaceId, current.MemberId,
                    SyncChange.Upsert(Guid.NewGuid().ToString("D"), kind, key, groups), ct);
                if (kind == "attachment" && groups.TryGetValue("file", out var file))
                {
                    // It comes from the mover's own stream, so the mover's blob holds its bytes.
                    await SyncService.BindAttachmentAsync(db, spaceId, key, file, accountId, ct);
                }

                old.MovedTo = spaceId;
                old.Groups = "{}";
                old.LastSeq = accountStream.NextSeq;
                old.UpdatedAt = now;
                db.SyncChanges.Add(new SyncChangeRow
                {
                    AccountId = accountId, Seq = accountStream.NextSeq++,
                    ChangeId = kind == "container" && key == root.EntityKey ? request.MoveId : Guid.NewGuid(),
                    Kind = kind, EntityKey = key, Op = "moved",
                    Groups = JsonSerializer.Serialize(new { to = spaceId }), ActorId = current.MemberId, CreatedAt = now,
                });
            }
            db.AuditEvents.Add(new AuditEvent { AccountId = accountId, DeviceId = deviceId, Kind = AuditKinds.Move, At = now });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await NotifyStreamAsync(accountId, ct);
            await NotifyStreamAsync(spaceId, ct);
            return SpaceOperation<MoveDto>.Success(new(request.MoveId, spaceId, moving.Count));
        });
    }

    private async Task<SpaceOperation<T>> strategy<T>(Func<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction,
        Task<SpaceOperation<T>>> body)
    {
        var execution = db.Database.CreateExecutionStrategy();
        return await execution.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync();
            return await body(tx);
        });
    }

    private async Task<SpaceMember?> CurrentMemberAsync(Guid accountId, Guid spaceId, CancellationToken ct) =>
        await db.SpaceMembers.SingleOrDefaultAsync(m => m.SpaceId == spaceId && m.AccountId == accountId
            && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null
            && db.SyncStreams.Any(s => s.Id == spaceId && s.DeletedAt == null), ct);

    private async Task LockSpaceMembersAsync(Guid spaceId, CancellationToken ct) =>
        await db.SpaceMembers.FromSqlInterpolated($"""
            SELECT * FROM "SpaceMembers" WHERE "SpaceId" = {spaceId}
            ORDER BY "MemberId" FOR UPDATE
            """).ToListAsync(ct);

    private async Task<SyncStream> SpaceStreamForUpdateAsync(Guid spaceId, CancellationToken ct) =>
        (await db.SyncStreams.FromSqlInterpolated($"""
            SELECT * FROM "SyncStreams" WHERE "Id" = {spaceId} FOR UPDATE
            """).ToListAsync(ct)).Single();

    private async Task<SyncStream?> SpaceStreamForUpdateAsync(Guid spaceId, bool requireDeleted, CancellationToken ct)
    {
        var rows = requireDeleted
            ? await db.SyncStreams.FromSqlInterpolated($"""
                SELECT * FROM "SyncStreams" WHERE "Id" = {spaceId} AND "DeletedAt" IS NOT NULL FOR UPDATE
                """).ToListAsync(ct)
            : await db.SyncStreams.FromSqlInterpolated($"""
                SELECT * FROM "SyncStreams" WHERE "Id" = {spaceId} AND "DeletedAt" IS NULL FOR UPDATE
                """).ToListAsync(ct);
        return rows.SingleOrDefault();
    }

    private async Task AppendMemberAsync(SyncStream stream, Guid actor, Guid? invitedBy, Account account, short role,
        DateTimeOffset now, CancellationToken ct)
    {
        var member = await db.SpaceMembers.SingleAsync(m => m.SpaceId == stream.Id && m.MemberId == account.ProfileId, ct);
        member.InvitedByMemberId = invitedBy;
        var key = MemberKey(stream.Id, member.MemberId);
        var hlc = await changes.NextHlcAsync(stream.Id, "space_member", key, ct);
        await changes.AppendAsync(stream.Id, actor, SyncChange.Upsert(Guid.NewGuid().ToString("D"), "space_member", key,
            Groups(hlc, new Dictionary<string, Dictionary<string, object?>>
            {
                ["create"] = new()
                {
                    ["SpaceId"] = stream.Id.ToString("D"), ["MemberId"] = member.MemberId.ToString("D"),
                    ["MemberKind"] = (long)member.MemberKind, ["OwnerMemberId"] = null, ["JoinedAtUtc"] = Instant(now),
                },
                ["profile"] = new() { ["DisplayName"] = member.DisplayName, ["GitHubLogin"] = member.GitHubLogin },
                ["role"] = new() { ["Role"] = (long)role },
                ["state"] = new() { ["RemovedAtUtc"] = null },
            })), ct);
    }

    private async Task AppendMemberGroupAsync(SyncStream stream, Guid actor, SpaceMember member, string group,
        Dictionary<string, object?> values, CancellationToken ct)
    {
        var key = MemberKey(stream.Id, member.MemberId);
        var hlc = await changes.NextHlcAsync(stream.Id, "space_member", key, ct);
        await changes.AppendAsync(stream.Id, actor, SyncChange.Upsert(Guid.NewGuid().ToString("D"), "space_member", key,
            new Dictionary<string, SyncGroupState> { [group] = new(hlc, new SyncGroupValues(values)) }), ct);
    }

    private async Task TombstoneOverlaysAsync(Guid accountId, Guid spaceId, Guid actorId, CancellationToken ct)
    {
        var ids = (await db.SyncEntities.AsNoTracking().Where(e => e.AccountId == spaceId && e.PurgedAt == null)
            .Select(e => e.EntityKey).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var entity in await db.SyncEntities.Where(e => e.AccountId == accountId && e.PurgedAt == null).ToListAsync(ct))
        {
            if (SyncKinds.Find(entity.Kind) is not { OverlayRefs.Count: > 0 } kind)
            {
                continue;
            }
            var groups = SyncJson.ReadGroups(entity.Groups);
            var references = kind.OverlayRefs.Any(reference => reference.KeyPart is { } part
                ? entity.EntityKey.Split('|').ElementAtOrDefault(part) is { } key && ids.Contains(key)
                : reference.Column is { } column && groups.GetValueOrDefault(reference.Group ?? "create")?
                    .Values.Values.GetValueOrDefault(column)?.ToString() is { } id && ids.Contains(id));
            if (!references || !TryDeletedGroup(entity.Kind, out var group, out var column))
            {
                continue;
            }
            if (group == "purge")
            {
                await changes.AppendPurgedOverlayAsync(accountId, actorId, entity.Kind, entity.EntityKey, ct);
                continue;
            }
            var hlc = await changes.NextHlcAsync(accountId, entity.Kind, entity.EntityKey, ct);
            await changes.AppendDeletedOverlayAsync(accountId, actorId, entity.Kind, entity.EntityKey, group, column, hlc, ct);
        }
    }

    private static bool TryDeletedGroup(string kind, out string group, out string column)
    {
        if (kind == "day_plan_item")
        {
            group = "removed"; column = "RemovedAtUtc"; return true;
        }
        if (kind is "week_assignment" or "space_notification_setting" or "view_alert_setting")
        {
            group = "purge"; column = string.Empty; return true;
        }
        group = "deleted";
        column = "DeletedAtUtc";
        return kind is "calendar_event_link" or "reminder_rule" or "waiting_detail" or "follow_up"
            or "week_goal_task" or "task_tag" or "subtask_tag" or "pad_tag" or "page_tag";
    }

    private async Task BumpMembershipAsync(Guid accountId, CancellationToken ct)
    {
        await db.Accounts.Where(a => a.Id == accountId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.MembershipVersion, a => a.MembershipVersion + 1), ct);
        memberships.Invalidate(accountId);
    }

    private async Task NotifyCurrentMembershipAsync(Guid accountId, CancellationToken ct)
    {
        var version = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId)
            .Select(a => a.MembershipVersion).SingleAsync(ct);
        await NotifyMembershipAsync(accountId, version, ct);
    }

    private async Task NotifyStreamAsync(Guid streamId, CancellationToken ct)
    {
        var stream = await db.SyncStreams.AsNoTracking().Where(s => s.Id == streamId)
            .Select(s => new { s.Id, Seq = s.NextSeq - 1 }).FirstOrDefaultAsync(ct);
        if (stream is null)
        {
            return;
        }
        var accounts = await db.SpaceMembers.AsNoTracking()
            .Where(m => m.SpaceId == streamId && m.MemberKind == SpaceMember.PersonKind && m.RemovedAt == null && m.AccountId != null)
            .Select(m => m.AccountId!.Value).ToListAsync(ct);
        if (accounts.Count == 0)
        {
            accounts.Add(streamId);
        }
        foreach (var accountId in accounts.Distinct())
        {
            broadcaster.PublishStream(accountId, streamId, stream.Seq);
        }
        var payload = SyncNotificationListener.Payload(streamId, stream.Seq);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_notify({SyncNotificationListener.Channel}, {payload})", CancellationToken.None);
    }

    private async Task NotifyMembershipAsync(Guid accountId, long version, CancellationToken ct)
    {
        memberships.Invalidate(accountId);
        broadcaster.PublishMembership(accountId, version);
        var payload = SyncNotificationListener.Payload(accountId, version);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_notify({SyncNotificationListener.MembershipChannel}, {payload})", CancellationToken.None);
    }

    private async Task<bool> HasVerifiedEmailAsync(Account account, string email, CancellationToken ct) =>
        account.PrimaryEmail == email
        || await db.AccountLogins.AsNoTracking().AnyAsync(l => l.AccountId == account.Id && l.Email == email, ct);

    private string SpaceName(Guid spaceId)
    {
        var json = db.SyncEntities.AsNoTracking().Where(e => e.AccountId == spaceId && e.Kind == "space")
            .Select(e => e.Groups).FirstOrDefault();
        return json is null ? "Shared space" : SyncJson.ReadGroups(json).GetValueOrDefault("name")?
            .Values.Values.GetValueOrDefault("Name")?.ToString() ?? "Shared space";
    }

    private static (string Kind, string Key)? RouteParent(SyncRoute route, string entityKey, Dictionary<string, SyncGroupState> groups)
    {
        var kind = route.ParentKind;
        if (route.DiscriminatorColumn is { } discriminator
            && groups.GetValueOrDefault(route.Group ?? "create")?.Values.Values.GetValueOrDefault(discriminator)?.ToString() is { } actual)
        {
            kind = actual;
        }
        var key = route.KeyPart is { } part ? entityKey.Split('|').ElementAtOrDefault(part)
            : groups.GetValueOrDefault(route.Group ?? "create")?.Values.Values.GetValueOrDefault(route.Column ?? string.Empty)?.ToString();
        return kind is null || key is null ? null : (kind, key);
    }

    private static bool ReferencesIncluded(Dictionary<string, SyncGroupState> groups, HashSet<(string Kind, string Key)> included) =>
        groups.Values.SelectMany(g => g.Values.Values)
            .Any(pair => pair.Key != "AuthorProfileId" && pair.Key != "OwnerProfileId"
                && pair.Value is string id && included.Any(item => item.Key == id));

    private static short HigherRole(short current, short invited) => current < invited ? current : invited;
    private static MemberDto ToDto(SpaceMember member) => new(member.MemberId, member.DisplayName, member.Role,
        member.MemberKind, member.JoinedAt, member.RemovedAt);
    private static InviteDto ToDto(SpaceInvite invite) => new(invite.Id, invite.Email, invite.Role, invite.CreatedAt,
        invite.ExpiresAt, invite.MaxUses, invite.Uses, invite.RevokedAt);
    private static string MemberKey(Guid spaceId, Guid memberId) =>
        NameBasedId.Create($"spacemember:{spaceId:D}:{memberId:D}").ToString("D");
    private static string Instant(DateTimeOffset value) => value.UtcDateTime.ToString("o");
    private static string Token() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string InviteLink(string baseUrl, string token) => baseUrl.TrimEnd('/') + "/invite/" + token;
    private static string Encode(string value) => WebUtility.HtmlEncode(value);
    private static Dictionary<string, SyncGroupState> Groups(Hlc hlc,
        Dictionary<string, Dictionary<string, object?>> groups) => groups.ToDictionary(
            pair => pair.Key, pair => new SyncGroupState(hlc, new SyncGroupValues(pair.Value)));
    private static Dictionary<string, object?> Settings() => new()
    {
        ["TagsOwnerOnly"] = 0L, ["AgentsAllowed"] = 0L, ["ClaimMinutes"] = 120L,
        ["DefaultDeadlineReminderJson"] = null, ["AllowAgentsToClose"] = 0L,
    };
}

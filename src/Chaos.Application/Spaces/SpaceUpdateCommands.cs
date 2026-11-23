using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.People;
using Chaos.Application.Reminders;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Sync;

namespace Chaos.Application.Spaces;

public sealed record PostSpaceUpdateCommand(string OperationId, string SpaceId, DateOnly ForDate, string Markdown);
public sealed record EditSpaceUpdateCommand(string OperationId, string UpdateId, long ExpectedVersion, string Markdown);
public sealed record DeleteSpaceUpdateCommand(string OperationId, string UpdateId, long ExpectedVersion);

/// <param name="Item">The standup as the Overview shows it.</param>
public sealed record SpaceUpdateMutationResult(SpaceUpdateItem Item, bool Replayed);

/// <summary>
/// Standups (PLAN3 9.12): a member posts a Markdown update to a shared space from the evening summary,
/// after reviewing and editing the suggested text; nothing is posted by itself. The author alone edits a
/// standup; the author or an owner deletes it. Each change is a recorded operation of the local person,
/// and the service checks every rule again on push.
/// </summary>
public sealed class SpaceUpdateCommands(IApplicationStore store, IIdGenerator ids, TimeProvider clock)
{
    /// <summary>The most standups the Overview lists at a time; the rest stay in the space.</summary>
    public const int MaxList = 100;
    private const int OwnerRole = 1;

    /// <summary>Live standups of a shared space, newest first, with each author's name in the space.</summary>
    public async Task<IReadOnlyList<SpaceUpdateItem>> ListAsync(string spaceId, CancellationToken ct = default) =>
        await store.QueryAsync<IReadOnlyList<SpaceUpdateItem>>(async (session, token) =>
        {
            var space = await session.Spaces.GetAsync(spaceId, token) ?? throw AppException.NotFound("That space is no longer on this computer.");
            var profile = await ReminderCommands.RequireProfileAsync(session, token);
            var people = await session.People.ListAsync(space.Id, token);
            return [.. (await session.SpaceUpdates.ListAsync(space.Id, token)).Take(MaxList).Select(row => Item(row, people, profile.Id))];
        }, ct);

    /// <summary>Posts the text as it stands as the person's standup for the date.</summary>
    public async Task<SpaceUpdateMutationResult> PostAsync(PostSpaceUpdateCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        var normalized = command with { Markdown = Markdown(command.Markdown) };
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (item, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, normalized.OperationId, "spaceUpdate.post", normalized, now, async inner =>
            {
                var space = await session.Spaces.GetAsync(normalized.SpaceId, inner);
                if (space is null || space.Kind != SpaceKind.Shared)
                {
                    throw AppException.Validation("spaceId", "Standups are posted to a shared space.");
                }

                var people = await session.People.ListAsync(space.Id, inner);
                _ = Me(people, profile.Id);
                var row = new SpaceUpdateRecord
                {
                    Id = ids.NewId().ToString(),
                    SpaceId = space.Id,
                    AuthorMemberId = profile.Id,
                    ForDate = normalized.ForDate,
                    RawMarkdown = normalized.Markdown,
                    Version = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                await session.SpaceUpdates.AddAsync(row, inner);
                return Item(row, people, profile.Id);
            }, token);
            return new SpaceUpdateMutationResult(item, replayed);
        }, ct);
    }

    public async Task<SpaceUpdateMutationResult> EditAsync(EditSpaceUpdateCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        var normalized = command with { Markdown = Markdown(command.Markdown) };
        return await ChangeAsync(normalized.OperationId, "spaceUpdate.edit", normalized, normalized.UpdateId, normalized.ExpectedVersion, authorOnly: true,
            (row, now) => row.RawMarkdown == normalized.Markdown ? null : row with { RawMarkdown = normalized.Markdown, EditedAtUtc = now }, ct);
    }

    /// <summary>Deletes the standup for everyone in the space; a tombstone stays so other devices drop it too.</summary>
    public async Task<SpaceUpdateMutationResult> DeleteAsync(DeleteSpaceUpdateCommand command, CancellationToken ct = default)
    {
        ReminderCommands.RequireOperation(command.OperationId);
        return await ChangeAsync(command.OperationId, "spaceUpdate.delete", command, command.UpdateId, command.ExpectedVersion, authorOnly: false,
            (row, now) => row.DeletedAtUtc is null ? row with { DeletedAtUtc = now } : null, ct);
    }

    private async Task<SpaceUpdateMutationResult> ChangeAsync<T>(
        string operationId, string commandKind, T payload, string updateId, long expectedVersion, bool authorOnly,
        Func<SpaceUpdateRecord, DateTimeOffset, SpaceUpdateRecord?> change, CancellationToken ct)
        where T : notnull
    {
        return await InWriteAsync(async (session, profile, now, token) =>
        {
            var (item, replayed) = await TaskMutationSupport.ReplayOrRunAsync(session, profile.Id, operationId, commandKind, payload, now, async inner =>
            {
                var row = await session.SpaceUpdates.GetIncludingDeletedAsync(updateId, inner)
                    ?? throw AppException.NotFound("That standup no longer exists. Refresh and try again.");
                if (authorOnly && row.DeletedAtUtc is not null)
                {
                    throw AppException.Validation("updateId", "This standup was deleted.");
                }

                var people = await session.People.ListAsync(row.SpaceId, inner);
                var me = Me(people, profile.Id);
                var author = string.Equals(row.AuthorMemberId, profile.Id, StringComparison.OrdinalIgnoreCase);
                var allowed = author || (!authorOnly && me.Role == OwnerRole);
                if (!allowed)
                {
                    throw AppException.Validation("updateId", authorOnly
                        ? "Only the author can edit this standup."
                        : "Only the author or a space owner can delete this standup.");
                }

                if (row.Version != expectedVersion)
                {
                    throw AppException.VersionConflict("This standup changed elsewhere while you were working. Refresh and try again.");
                }

                if (change(row, now) is not { } updated)
                {
                    return Item(row, people, profile.Id); // nothing to change
                }

                var applied = await session.SpaceUpdates.UpdateAsync(updated with { Version = row.Version + 1, UpdatedAtUtc = now }, row.Version, inner)
                    ?? throw AppException.VersionConflict("This standup changed elsewhere while you were working. Refresh and try again.");
                return Item(applied, people, profile.Id);
            }, token);
            return new SpaceUpdateMutationResult(item, replayed);
        }, ct);
    }

    /// <summary>The person's live membership row of the space, whatever its role: a viewer may post a standup too.</summary>
    private static SpacePerson Me(IReadOnlyList<SpacePerson> people, string profileId) =>
        people.FirstOrDefault(member => !member.Former && string.Equals(member.ActorId, $"person:{profileId}", StringComparison.OrdinalIgnoreCase))
            ?? throw AppException.Validation("spaceId", "You are no longer a member of this space.");

    private static SpaceUpdateItem Item(SpaceUpdateRecord row, IReadOnlyList<SpacePerson> people, string profileId)
    {
        var author = people.FirstOrDefault(member => string.Equals(member.ActorId, $"person:{row.AuthorMemberId}", StringComparison.OrdinalIgnoreCase))?.DisplayName;
        return new SpaceUpdateItem(row, string.IsNullOrWhiteSpace(author) ? "Unknown person" : author, string.Equals(row.AuthorMemberId, profileId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The standup text as posted: the notes limit applies, blank text is refused, and the ends are trimmed (an editor often leaves a trailing newline).</summary>
    private static string Markdown(string? raw)
    {
        var text = TaskFieldValidation.Notes(raw, "markdown").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw AppException.Validation("markdown", "Write something in the standup first.");
        }

        return text.Length > SpaceUpdates.MaxMarkdownLength
            ? throw AppException.Validation("markdown", $"A standup holds at most {SpaceUpdates.MaxMarkdownLength} characters.")
            : text;
    }

    private async Task<T> InWriteAsync<T>(Func<IDataSession, ProfileRecord, DateTimeOffset, CancellationToken, Task<T>> body, CancellationToken ct) =>
        await TaskMutationSupport.InWriteTransactionAsync(store, clock, ids, body, ct);
}

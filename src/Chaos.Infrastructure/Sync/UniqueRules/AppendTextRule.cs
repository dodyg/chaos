using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Sync;

namespace Chaos.Infrastructure.Sync.UniqueRules;

/// <summary>Daily pages (§7.6): the loser's text is appended to the winner, then it is tombstoned and its tags move.</summary>
internal sealed class AppendTextRule : IUniqueRule
{
    public static readonly AppendTextRule Instance = new();

    private AppendTextRule()
    {
    }

    public MergeResult ResolveLoser(UniqueConflict conflict, MergeResult merge)
    {
        var (connection, localKey, incomingKey, wall) = (conflict.Connection, conflict.LocalKey, conflict.IncomingKey, conflict.Wall);
        var separator = $"\n\nFrom another device, {wall.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}\n";
        string? incomingText = merge.Winners.TryGetValue("text", out var text) && text.Values.Values.TryGetValue("RawMarkdown", out var markdown) ? markdown as string : null;

        if (conflict.IncomingWins)
        {
            // The local row is the loser: its text is appended to the incoming winner when that is
            // written, and the local row is tombstoned with its links moved.
            var localText = SyncSql.Text(connection, $"SELECT RawMarkdown FROM Pages WHERE Id = {SyncSql.Literal(localKey)};") ?? string.Empty;
            var winners = new Dictionary<string, SyncGroupState>(merge.Winners, StringComparer.Ordinal);
            if (winners.TryGetValue("text", out var winnerText))
            {
                var values = new Dictionary<string, object?>(winnerText.Values.Values, StringComparer.Ordinal)
                {
                    ["RawMarkdown"] = (winnerText.Values.Values.TryGetValue("RawMarkdown", out var incoming) ? incoming as string ?? string.Empty : string.Empty)
                        + separator
                        + localText,
                };
                winners["text"] = winnerText with { Values = new SyncGroupValues(values) };
            }

            merge = merge with { Winners = winners };

            // The loser's page tags become the winner's: each active link is copied to the winner
            // (reviving a tombstoned winner link) and the loser's row tombstoned, so link keys and
            // clocks stay stable. The loser's rows keep their own key: re-keying them to the
            // winner would collide with the copies on the (PageId, TagId) key.
            using var links = connection.CreateCommand();
            links.CommandText = $"""
                INSERT INTO PageTags (PageId, TagId) SELECT $winner, TagId FROM PageTags WHERE PageId = $loser AND DeletedAtUtc IS NULL
                ON CONFLICT (PageId, TagId) DO UPDATE SET DeletedAtUtc = NULL;
                UPDATE PageTags SET DeletedAtUtc = $wall WHERE PageId = $loser AND DeletedAtUtc IS NULL;
                INSERT INTO SyncClocks (Kind, EntityKey, GroupName, Hlc)
                    SELECT 'page_tag', $winner || '|' || TagId, 'deleted', $hlc FROM PageTags WHERE PageId = $winner AND DeletedAtUtc IS NULL
                {SyncSql.ClockConflict(onlyIfNewer: true)};
                """;
            links.Parameters.AddWithValue("$winner", incomingKey);
            links.Parameters.AddWithValue("$loser", localKey);
            links.Parameters.AddWithValue("$wall", StorageFormats.Instant(wall));
            links.Parameters.AddWithValue("$hlc", conflict.ClockText);
            links.ExecuteNonQuery();

            using var tombstone = connection.CreateCommand();
            tombstone.CommandText = "UPDATE Pages SET DeletedAtUtc = $now, Version = Version + 1, UpdatedAtUtc = $now WHERE Id = $loser AND DeletedAtUtc IS NULL;";
            tombstone.Parameters.AddWithValue("$loser", localKey);
            tombstone.Parameters.AddWithValue("$now", StorageFormats.Instant(wall));
            tombstone.ExecuteNonQuery();
            return merge;
        }

        // The local row is the winner: the incoming loser's text is appended here, and the loser
        // row itself is inserted tombstoned by WriteIncomingLoserAsync on both devices.
        using var append = connection.CreateCommand();
        append.CommandText = "UPDATE Pages SET RawMarkdown = RawMarkdown || $tail, Version = Version + 1, UpdatedAtUtc = $wall WHERE Id = $winner;";
        append.Parameters.AddWithValue("$winner", localKey);
        append.Parameters.AddWithValue("$tail", separator + (incomingText ?? string.Empty));
        append.Parameters.AddWithValue("$wall", StorageFormats.Instant(wall));
        append.ExecuteNonQuery();
        return merge;
    }

    public Task WriteIncomingLoserAsync(UniqueConflict conflict, MergeResult merge, CancellationToken ct) =>
        RetiredLoser.WriteAsync(conflict, merge, ct);
}

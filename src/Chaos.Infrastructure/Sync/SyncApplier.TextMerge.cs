using Chaos.Domain.Text;
using Chaos.Sync;
using Microsoft.Data.Sqlite;
using static Chaos.Infrastructure.Sync.SyncRows;

namespace Chaos.Infrastructure.Sync;

/// <summary>
/// Three-way merge of shared text (PLAN3 §9.9, decision E2). A remote replacement of a pad's text, a page's
/// text or a task's notes is merged with an unsent local edit against <c>TextBase</c>, the text last agreed
/// with the server, instead of letting the later clock discard one of the two.
/// </summary>
public sealed partial class SyncApplier
{
    private sealed record TextPlan(string Group, string RemoteText, string Hlc, bool Merged);

    internal static (string Group, string Column)? TextGroupOf(string kind) => kind switch
    {
        "pad" => ("text", "RawMarkdown"),
        "page" => ("text", "RawMarkdown"),
        "task" => ("notes", "Notes"),
        _ => null,
    };

    /// <summary>
    /// When the incoming change carries the text group and wins, decides whether it must be merged with an
    /// unsent local edit. A merged text replaces the winner's value, and a new local change is queued so the
    /// result travels under a fresh clock. Returns the bookkeeping to do once the row is written.
    /// </summary>
    private TextPlan? PlanText(
        SqliteConnection connection, SyncTable table, IReadOnlyDictionary<string, string> key, SyncChange change,
        IReadOnlyDictionary<string, SyncGroupState>? stored, bool exists, ref MergeResult merge)
    {
        if (TextGroupOf(table.Kind) is not { } text || !change.Groups.TryGetValue(text.Group, out var incoming)) return null;
        var remote = incoming.Values.Values.GetValueOrDefault(text.Column) as string ?? string.Empty;
        var wins = merge.Winners.TryGetValue(text.Group, out var winner);
        var plan = new TextPlan(text.Group, remote, incoming.Hlc.Encode(), false);
        if (!exists || stored is null || !stored.TryGetValue(text.Group, out var local)) return wins ? plan : null;

        var current = local.Values.Values.GetValueOrDefault(text.Column) as string ?? string.Empty;
        if (current == remote || ReadTextBase(connection, table.Kind, change.EntityKey) is not { } baseRow) return wins ? plan : null;
        var @base = baseRow.Text;
        if (@base == current) return wins ? plan : null;

        // A remote text with an older clock is still unseen when it is newer than the base: two people edited
        // at once, and each must keep the other's lines. One that is not newer than the base is a replay.
        if (!wins && (incoming.Hlc <= baseRow.Hlc || remote == @base)) return null;

        var space = SpaceOf(connection, table, key);
        var localFirst = local.Hlc <= incoming.Hlc;
        var localLabel = Label(connection, space, LocalMemberId(connection), local.Hlc);
        var remoteLabel = Label(connection, space, ActorId.TryParse(change.ActorId, out var actor) ? actor.Id.ToString("D") : null, incoming.Hlc);
        var merged = localFirst
            ? Diff3.Merge(@base, current, remote, localLabel, remoteLabel)
            : Diff3.Merge(@base, remote, current, remoteLabel, localLabel);
        if (merged == remote) return wins ? plan : null;
        if (merged == current && !wins) return plan with { Merged = false };

        var source = wins ? winner! : local with { Hlc = local.Hlc };
        var values = new Dictionary<string, object?>(source.Values.Values, StringComparer.OrdinalIgnoreCase) { [text.Column] = merged };
        var winners = new Dictionary<string, SyncGroupState>(merge.Winners, StringComparer.Ordinal)
        {
            [text.Group] = source with { Values = new SyncGroupValues(values) },
        };
        merge = merge with { Winners = winners };
        return plan with { Merged = true };
    }

    /// <summary>
    /// A drawing edited on two devices keeps both sets of strokes: the stored and incoming drawings are united by
    /// stroke ID. True when a merged drawing replaced the winner and must travel as a new local change.
    /// </summary>
    private static bool PlanDrawing(
        SyncTable table, SyncChange change, IReadOnlyDictionary<string, SyncGroupState>? stored, bool exists, ref MergeResult merge)
    {
        if (table.Kind != "pad_drawing" || !exists || stored is null
            || !change.Groups.TryGetValue("strokes", out var incoming) || !stored.TryGetValue("strokes", out var local)) return false;
        var remote = incoming.Values.Values.GetValueOrDefault("StrokesJson") as string ?? string.Empty;
        var current = local.Values.Values.GetValueOrDefault("StrokesJson") as string ?? string.Empty;
        if (remote == current || DrawingMerge.Merge(current, remote) is not { } merged) return false;
        var wins = merge.Winners.TryGetValue("strokes", out var winner);
        // One side already holds everything the other has: the later clock's text is the union.
        if (merged == DrawingMerge.Canonical(wins ? remote : current)) return false;
        var source = wins ? winner! : local;
        var values = new Dictionary<string, object?>(source.Values.Values, StringComparer.OrdinalIgnoreCase) { ["StrokesJson"] = merged };
        var winners = new Dictionary<string, SyncGroupState>(merge.Winners, StringComparer.Ordinal)
        {
            ["strokes"] = source with { Values = new SyncGroupValues(values) },
        };
        merge = merge with { Winners = winners };
        return true;
    }

    /// <summary>After the winners are written: the remote text becomes the base, and a merged result is queued as a local change.</summary>
    private void FinishText(SqliteConnection connection, SyncTable table, IReadOnlyDictionary<string, string> key, SyncChange change, TextPlan plan)
    {
        WriteTextBase(connection, table.Kind, change.EntityKey, plan.Hlc, plan.RemoteText);
        if (!plan.Merged) return;
        SyncSql.Execute(connection,
            $"INSERT OR IGNORE INTO SyncPending (Kind, EntityKey, GroupName) VALUES ({SyncSql.Literal(table.Kind)}, {SyncSql.Literal(change.EntityKey)}, {SyncSql.Literal(plan.Group)});");
        if (table.Kind != "page") return;

        // The remote text stays as a version next to the replaced local one.
        var page = SyncSql.Literal(key["Id"]);
        using var revision = connection.CreateCommand();
        revision.CommandText = $"""
            INSERT INTO PageRevisions (PageId, Number, Title, RawMarkdown, CreatedAtUtc, UpdatedAtUtc)
            SELECT {page}, COALESCE((SELECT MAX(Number) FROM PageRevisions WHERE PageId = {page}), 0) + 1,
                   (SELECT Title FROM Pages WHERE Id = {page}), $text, $now, $now;
            """;
        revision.Parameters.AddWithValue("$text", plan.RemoteText);
        revision.Parameters.AddWithValue("$now", Chaos.Application.Abstractions.StorageFormats.Instant(clock.GetUtcNow()));
        revision.ExecuteNonQuery();
    }

    private static (Hlc Hlc, string Text)? ReadTextBase(SqliteConnection connection, string kind, string entityKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Hlc, Text FROM TextBase WHERE Kind = $kind AND EntityKey = $key;";
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", entityKey);
        using var reader = command.ExecuteReader();
        return reader.Read() && Hlc.TryParse(reader.GetString(0), out var hlc) ? (hlc, reader.GetString(1)) : null;
    }

    /// <summary>Records the text agreed with the server; an older clock never replaces a newer base.</summary>
    internal static void WriteTextBase(SqliteConnection connection, string kind, string entityKey, string hlc, string text)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO TextBase (Kind, EntityKey, Hlc, Text) VALUES ($kind, $key, $hlc, $text)
            ON CONFLICT (Kind, EntityKey) DO UPDATE SET Hlc = excluded.Hlc, Text = excluded.Text WHERE excluded.Hlc >= TextBase.Hlc;
            """;
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$key", entityKey);
        command.Parameters.AddWithValue("$hlc", hlc);
        command.Parameters.AddWithValue("$text", text);
        command.ExecuteNonQuery();
    }

    private static string? SpaceOf(SqliteConnection connection, SyncTable table, IReadOnlyDictionary<string, string> key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT SpaceId FROM {SyncSql.Quote(table.Table)} WHERE {Where(table, key)};";
        BindKey(command, table, key);
        return command.ExecuteScalar() as string;
    }

    private static string? LocalMemberId(SqliteConnection connection) =>
        SyncSql.Text(connection, "SELECT AccountId FROM SyncState WHERE Id = 1;");

    /// <summary>The author's display name, or a short device mark. Both devices resolve the same label for the same side.</summary>
    private static string Label(SqliteConnection connection, string? space, string? member, Hlc hlc)
    {
        if (space is not null && member is not null)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DisplayName FROM SpaceMembers WHERE SpaceId = $space AND MemberId = $member COLLATE NOCASE LIMIT 1;";
            command.Parameters.AddWithValue("$space", space);
            command.Parameters.AddWithValue("$member", member);
            if (command.ExecuteScalar() is string name && !string.IsNullOrWhiteSpace(name)) return name.Replace('\n', ' ').Trim();
        }

        return hlc.DeviceId.Length >= 8 ? "device " + hlc.DeviceId[..8] : "another device";
    }
}

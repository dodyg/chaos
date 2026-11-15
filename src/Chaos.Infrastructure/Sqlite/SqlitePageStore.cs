using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Pages;
using Chaos.Domain.Tags;
using Chaos.Infrastructure.Search;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>
/// Pages, their versions and task backlinks (SPEC Pages). Every add/update refreshes the page's
/// search document and its front-matter tags on the same connection and transaction as the row.
/// </summary>
public sealed class SqlitePageStore(SqliteConnection connection) : IPageStore
{
    public async Task<PageRecord?> GetAsync(string pageId, CancellationToken ct) =>
        (await ReadAsync("SELECT * FROM Pages WHERE Id = $id AND DeletedAtUtc IS NULL;", ct, ("$id", pageId))).FirstOrDefault();

    public async Task<PageRecord?> GetIncludingDeletedAsync(string pageId, CancellationToken ct) =>
        (await ReadAsync("SELECT * FROM Pages WHERE Id = $id;", ct, ("$id", pageId))).FirstOrDefault();

    public async Task<IReadOnlyList<PageRecord>> ListBySpaceAsync(string spaceId, CancellationToken ct) =>
        await ReadAsync("SELECT * FROM Pages WHERE SpaceId = $space AND DeletedAtUtc IS NULL ORDER BY Position, Id;", ct, ("$space", spaceId));

    public async Task<IReadOnlyList<PageRecord>> ListDeletedBySpaceAsync(string spaceId, CancellationToken ct) =>
        await ReadAsync("SELECT * FROM Pages WHERE SpaceId = $space AND DeletedAtUtc IS NOT NULL ORDER BY DeletedAtUtc DESC, Id;", ct, ("$space", spaceId));

    public async Task<PageRecord?> FindDailyAsync(string spaceId, DateOnly date, CancellationToken ct) =>
        (await ReadAsync("SELECT * FROM Pages WHERE SpaceId = $space AND DailyDate = $date AND DeletedAtUtc IS NULL;", ct, ("$space", spaceId), ("$date", StorageFormats.Date(date)))).FirstOrDefault();

    public async Task AddAsync(PageRecord page, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Pages (Id, SpaceId, ParentPageId, Title, RawMarkdown, Position, DailyDate, DraftVersion, Version, CreatedAtUtc, UpdatedAtUtc, DeletedAtUtc)
            VALUES ($id, $space, $parent, $title, $markdown, $position, $daily, $draft, $version, $created, $updated, $deleted);
            """;
        command.Parameters.AddWithValue("$id", page.Id);
        command.Parameters.AddWithValue("$space", page.SpaceId);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(page.CreatedAtUtc));
        Bind(command, page);
        await command.ExecuteNonQueryAsync(ct);
        await SyncDerivedAsync(page, ct);
    }

    public async Task<PageRecord?> UpdateAsync(PageRecord updated, long expectedVersion, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Pages SET ParentPageId = $parent, Title = $title, RawMarkdown = $markdown, Position = $position, DailyDate = $daily,
                DraftVersion = $draft, Version = $version, UpdatedAtUtc = $updated, DeletedAtUtc = $deleted
            WHERE Id = $id AND Version = $expected;
            """;
        command.Parameters.AddWithValue("$id", updated.Id);
        command.Parameters.AddWithValue("$expected", expectedVersion);
        Bind(command, updated);
        if (await command.ExecuteNonQueryAsync(ct) != 1)
        {
            return null;
        }

        await SyncDerivedAsync(updated, ct);
        return await GetIncludingDeletedAsync(updated.Id, ct);
    }

    public async Task<long> GetNextPositionAsync(string spaceId, string? parentPageId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(MAX(Position), -1024) + 1024 FROM Pages
            WHERE SpaceId = $space AND DeletedAtUtc IS NULL AND DailyDate IS NULL AND COALESCE(ParentPageId, '') = COALESCE($parent, '');
            """;
        command.Parameters.AddWithValue("$space", spaceId);
        command.Parameters.AddWithValue("$parent", (object?)parentPageId ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    public async Task<IReadOnlyList<PageRevisionRecord>> ListRevisionsAsync(string pageId, CancellationToken ct) =>
        await ReadRevisionsAsync("SELECT * FROM PageRevisions WHERE PageId = $id ORDER BY Number DESC;", ct, ("$id", pageId));

    public async Task<PageRevisionRecord?> GetRevisionAsync(string pageId, long number, CancellationToken ct) =>
        (await ReadRevisionsAsync("SELECT * FROM PageRevisions WHERE PageId = $id AND Number = $number;", ct, ("$id", pageId), ("$number", number))).FirstOrDefault();

    public async Task AddRevisionAsync(PageRevisionRecord revision, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PageRevisions (PageId, Number, Title, RawMarkdown, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($page, $number, $title, $markdown, $created, $updated);
            """;
        BindRevision(command, revision);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task UpdateRevisionAsync(PageRevisionRecord revision, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PageRevisions SET Title = $title, RawMarkdown = $markdown, UpdatedAtUtc = $updated WHERE PageId = $page AND Number = $number;";
        BindRevision(command, revision);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task AddTaskLinkAsync(PageTaskLinkRecord link, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PageTaskLinks (PageId, TaskId, SourceRevision, StartIndex, EndIndex, Quote, CreatedAtUtc, ContextBefore, ContextAfter)
            VALUES ($page, $task, $revision, $start, $end, $quote, $created, $before, $after);
            """;
        command.Parameters.AddWithValue("$page", link.PageId);
        command.Parameters.AddWithValue("$task", link.TaskId);
        command.Parameters.AddWithValue("$revision", link.SourceRevision);
        command.Parameters.AddWithValue("$start", link.StartIndex);
        command.Parameters.AddWithValue("$end", link.EndIndex);
        command.Parameters.AddWithValue("$quote", link.Quote);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(link.CreatedAtUtc));
        command.Parameters.AddWithValue("$before", SqliteProfileStore.OptionalValue(link.ContextBefore));
        command.Parameters.AddWithValue("$after", SqliteProfileStore.OptionalValue(link.ContextAfter));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<PageTaskLinkRecord>> ListTaskLinksAsync(string pageId, CancellationToken ct) =>
        await ReadLinksAsync("SELECT * FROM PageTaskLinks WHERE PageId = $id ORDER BY CreatedAtUtc, TaskId;", pageId, ct);

    public async Task<PageTaskLinkRecord?> FindTaskLinkAsync(string taskId, CancellationToken ct) =>
        (await ReadLinksAsync("SELECT * FROM PageTaskLinks WHERE TaskId = $id;", taskId, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<string>> SearchAsync(string spaceId, string text, int limit, CancellationToken ct)
    {
        var match = SearchText.ToMatchExpression(text);
        if (match is null)
        {
            return [];
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH Matches AS MATERIALIZED (
                SELECT rowid AS RowId, bm25(SearchIndex, 10.0, 1.0) AS Rank FROM SearchIndex WHERE SearchIndex MATCH $match)
            SELECT p.Id FROM Matches m
            JOIN SearchDocuments d ON d.RowId = m.RowId AND d.TargetKind = 5
            JOIN Pages p ON p.Id = d.TargetId
            WHERE p.SpaceId = $space AND p.DeletedAtUtc IS NULL
            ORDER BY m.Rank, p.Id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$match", match);
        command.Parameters.AddWithValue("$space", spaceId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    public async Task<IReadOnlyList<PageRecord>> ListContainingAsync(string spaceId, IReadOnlyList<string> needles, CancellationToken ct)
    {
        if (needles.Count == 0)
        {
            return [];
        }

        var (clause, parameters) = Contains("RawMarkdown", needles);
        return await ReadAsync($"SELECT * FROM Pages WHERE SpaceId = $space AND DeletedAtUtc IS NULL AND ({clause}) ORDER BY UpdatedAtUtc DESC, Id;", ct,
            [("$space", spaceId), .. parameters]);
    }

    public async Task<IReadOnlyList<(string Id, string Title, string Notes)>> ListTasksContainingAsync(IReadOnlyList<string> needles, CancellationToken ct)
    {
        if (needles.Count == 0)
        {
            return [];
        }

        var (clause, parameters) = Contains("Notes", needles);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT Id, Title, Notes FROM Tasks WHERE DeletedAtUtc IS NULL AND ({clause}) ORDER BY UpdatedAtUtc DESC, Id LIMIT 200;";
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<(string, string, string)>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    public async Task<IReadOnlyList<string>> ListTagIdsAsync(string pageId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT l.TagId FROM PageTags l JOIN Tags t ON t.Id = l.TagId
            WHERE l.PageId = $id AND l.DeletedAtUtc IS NULL AND t.DeletedAtUtc IS NULL ORDER BY t.NormalizedName, l.TagId;
            """;
        command.Parameters.AddWithValue("$id", pageId);
        using var reader = await command.ExecuteReaderAsync(ct);
        var ids = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    public async Task<IReadOnlyList<PageRecord>> ListByTagsAsync(IReadOnlyCollection<string> tagIds, CancellationToken ct)
    {
        if (tagIds.Count == 0)
        {
            return [];
        }

        var names = tagIds.Select((_, index) => $"$t{index}").ToList();
        return await ReadAsync(
            $"SELECT * FROM Pages WHERE DeletedAtUtc IS NULL AND Id IN (SELECT PageId FROM PageTags WHERE TagId IN ({string.Join(", ", names)}) AND DeletedAtUtc IS NULL) ORDER BY Title COLLATE NOCASE, Id;",
            ct, [.. tagIds.Select((id, index) => ($"$t{index}", (object)id))]);
    }

    private static (string Clause, (string Name, object Value)[] Parameters) Contains(string column, IReadOnlyList<string> needles)
    {
        // instr() on lower() is exact for the ASCII IDs links use, and close enough for titles.
        var parameters = needles.Select((needle, index) => ($"$n{index}", (object)needle.ToLowerInvariant())).ToArray();
        return (string.Join(" OR ", parameters.Select(parameter => $"instr(lower({column}), {parameter.Item1}) > 0")), parameters);
    }

    private static void Bind(SqliteCommand command, PageRecord page)
    {
        command.Parameters.AddWithValue("$parent", (object?)page.ParentPageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", page.Title);
        command.Parameters.AddWithValue("$markdown", page.RawMarkdown);
        command.Parameters.AddWithValue("$position", page.Position);
        command.Parameters.AddWithValue("$daily", page.DailyDate is { } date ? StorageFormats.Date(date) : DBNull.Value);
        command.Parameters.AddWithValue("$draft", page.DraftVersion);
        command.Parameters.AddWithValue("$version", page.Version);
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(page.UpdatedAtUtc));
        command.Parameters.AddWithValue("$deleted", SqliteProfileStore.OptionalInstant(page.DeletedAtUtc));
    }

    private static void BindRevision(SqliteCommand command, PageRevisionRecord revision)
    {
        command.Parameters.AddWithValue("$page", revision.PageId);
        command.Parameters.AddWithValue("$number", revision.Number);
        command.Parameters.AddWithValue("$title", revision.Title);
        command.Parameters.AddWithValue("$markdown", revision.RawMarkdown);
        command.Parameters.AddWithValue("$created", StorageFormats.Instant(revision.CreatedAtUtc));
        command.Parameters.AddWithValue("$updated", StorageFormats.Instant(revision.UpdatedAtUtc));
    }

    /// <summary>The search document and front-matter tags of a page, rewritten from its row.</summary>
    private async Task SyncDerivedAsync(PageRecord page, CancellationToken ct)
    {
        await SearchIndexWriter.SyncAsync(connection, SearchTargetKind.Page, page.Id, null, page.Title, page.RawMarkdown, page.DeletedAtUtc is null, ct);

        // Page tags are rewritten from the front matter; removed ones are tombstoned so the
        // removal syncs (PLAN2 §7.2). The page save time stamps them deterministically.
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "UPDATE PageTags SET DeletedAtUtc = $at WHERE PageId = $id AND DeletedAtUtc IS NULL;";
            clear.Parameters.AddWithValue("$id", page.Id);
            clear.Parameters.AddWithValue("$at", StorageFormats.Instant(page.UpdatedAtUtc));
            await clear.ExecuteNonQueryAsync(ct);
        }

        var wanted = PageFrontMatter.Parse(page.RawMarkdown).Tags;
        if (wanted.Count == 0)
        {
            return;
        }

        var paths = await TagPathsAsync(page.SpaceId, ct);
        foreach (var path in wanted.Distinct(StringComparer.Ordinal))
        {
            if (paths.TryGetValue(NormalizePath(path), out var tagId))
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO PageTags (PageId, TagId, DeletedAtUtc) VALUES ($page, $tag, NULL)
                    ON CONFLICT (PageId, TagId) DO UPDATE SET DeletedAtUtc = NULL WHERE PageTags.DeletedAtUtc IS NOT NULL;
                    """;
                insert.Parameters.AddWithValue("$page", page.Id);
                insert.Parameters.AddWithValue("$tag", tagId);
                await insert.ExecuteNonQueryAsync(ct);
            }
        }
    }

    /// <summary>Normalized slash paths of the space owner's active tags, mapped to tag IDs.</summary>
    private async Task<Dictionary<string, string>> TagPathsAsync(string spaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.Id, t.NormalizedName, t.ParentTagId FROM Tags t
            JOIN Spaces s ON s.OwnerProfileId = t.OwnerProfileId
            WHERE s.Id = $space AND t.DeletedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$space", spaceId);
        var tags = new Dictionary<string, (string Name, string? Parent)>(StringComparer.Ordinal);
        using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                tags[reader.GetString(0)] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, _) in tags)
        {
            var segments = new List<string>();
            var current = id;
            for (var guard = 0; current is not null && guard < 64 && tags.TryGetValue(current, out var tag); guard++)
            {
                segments.Add(tag.Name);
                current = tag.Parent;
            }

            segments.Reverse();
            paths.TryAdd(string.Join('/', segments), id);
        }

        return paths;
    }

    private static string NormalizePath(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(TagName.Normalize));

    private async Task<IReadOnlyList<PageRecord>> ReadAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<PageRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PageRecord
            {
                Id = reader.S("Id"),
                SpaceId = reader.S("SpaceId"),
                ParentPageId = reader.Sn("ParentPageId"),
                Title = reader.S("Title"),
                RawMarkdown = reader.S("RawMarkdown"),
                Position = reader.L("Position"),
                DailyDate = reader.Dn("DailyDate"),
                DraftVersion = reader.L("DraftVersion"),
                Version = reader.L("Version"),
                CreatedAtUtc = reader.I("CreatedAtUtc"),
                UpdatedAtUtc = reader.I("UpdatedAtUtc"),
                DeletedAtUtc = reader.In("DeletedAtUtc"),
            });
        }

        return rows;
    }

    private async Task<IReadOnlyList<PageRevisionRecord>> ReadRevisionsAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<PageRevisionRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PageRevisionRecord(reader.S("PageId"), reader.L("Number"), reader.S("Title"), reader.S("RawMarkdown"), reader.I("CreatedAtUtc"), reader.I("UpdatedAtUtc")));
        }

        return rows;
    }

    private async Task<IReadOnlyList<PageTaskLinkRecord>> ReadLinksAsync(string sql, string id, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<PageTaskLinkRecord>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new PageTaskLinkRecord(
                reader.S("PageId"),
                reader.S("TaskId"),
                reader.L("SourceRevision"),
                (int)reader.L("StartIndex"),
                (int)reader.L("EndIndex"),
                reader.S("Quote"),
                reader.I("CreatedAtUtc"),
                reader.Sn("ContextBefore"),
                reader.Sn("ContextAfter")));
        }

        return rows;
    }
}

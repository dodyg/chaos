using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Transfer;
using Chaos.Domain.Tasks;
using Chaos.Application.Contracts;

namespace Chaos.Infrastructure.Transfer;

/// <summary>
/// Merge mode for a Chaos JSON archive (PLAN §14): its active tasks become an import draft that
/// keeps their IDs, lists, tags (by full path), subtasks and diary entries. Plans, reminders,
/// history and settings are only brought back by a full restore; the preview warns about that.
/// </summary>
public static class ArchiveMerge
{
    public static ImportDraft ToDraft(ArchiveContent archive, string sourceName, string fileFingerprint)
    {
        var warnings = new List<ImportIssue>();
        var links = Rows(archive, "GitHubIssueLinks").Where(row => row.Text("TaskId") is not null)
            .ToDictionary(row => row.Text("TaskId")!, ReadIssueLink, StringComparer.Ordinal);
        var writes = Rows(archive, "GitHubIssueWrites").ToDictionary(row => row.Text("IssueLinkId")!, row =>
            new GitHubIssueWriteRecord(row.Text("IssueLinkId")!, row.Text("PayloadJson")!, StorageFormats.ParseInstant(row.Text("CreatedAtUtc"))!.Value), StringComparer.Ordinal);
        var checklist = Rows(archive, "GitHubChecklistItems").ToDictionary(row => row.Text("SubtaskId")!, row => new GitHubChecklistItemRecord
        {
            SubtaskId = row.Text("SubtaskId")!, IssueLinkId = row.Text("IssueLinkId")!, BaseOrdinal = row.Long("BaseOrdinal"),
            BaseText = row.Text("BaseText")!, BaseChecked = row.Long("BaseChecked") == 1,
        }, StringComparer.Ordinal);
        var comments = Rows(archive, "GitHubCommentLinks").ToDictionary(row => row.Text("DiaryEntryId")!, row => new GitHubCommentLinkRecord
        {
            DiaryEntryId = row.Text("DiaryEntryId")!, IssueLinkId = row.Text("IssueLinkId")!,
            CommentId = row.Text("CommentId") is null ? null : row.Long("CommentId"), AuthorLogin = row.Text("AuthorLogin"),
            HtmlUrl = row.Text("HtmlUrl"), Origin = (int)row.Long("Origin"), State = (int)row.Long("State"),
            BaseBody = row.Text("BaseBody"), BaseRemoteUpdatedUtc = StorageFormats.ParseInstant(row.Text("BaseRemoteUpdatedUtc")),
        }, StringComparer.Ordinal);
        var containers = Rows(archive, "Containers").Where(row => row.Text("DeletedAtUtc") is null).ToDictionary(row => row.Text("Id")!, StringComparer.Ordinal);
        var tagRows = Rows(archive, "Tags").Where(row => row.Text("DeletedAtUtc") is null).ToDictionary(row => row.Text("Id")!, StringComparer.Ordinal);
        var tagPaths = tagRows.Keys.ToDictionary(id => id, id => TagPath(tagRows, id), StringComparer.Ordinal);
        var taskTags = Rows(archive, "TaskTags").Where(row => tagPaths.ContainsKey(row.Text("TagId")!))
            .ToLookup(row => row.Text("TaskId")!, row => tagPaths[row.Text("TagId")!]);
        var subtasks = Rows(archive, "Subtasks").Where(row => row.Text("DeletedAtUtc") is null)
            .OrderBy(row => row.Long("Position")).ThenBy(row => row.Text("Id"), StringComparer.Ordinal)
            .ToLookup(row => row.Text("TaskId")!, row => new ImportSubtask(row.Text("Title")!, row.Text("Notes") ?? "", (TaskItemStatus)row.Long("Status"), row.Text("Id"), checklist.GetValueOrDefault(row.Text("Id")!)));
        var diary = Rows(archive, "DiaryEntries").Where(row => row.Text("DeletedAtUtc") is null)
            .OrderBy(row => row.Text("EntryAtUtc"), StringComparer.Ordinal)
            .ToLookup(row => row.Text("TaskId")!, row => new ImportDiaryEntry(StorageFormats.ParseInstant(row.Text("EntryAtUtc"))!.Value, row.Text("RawMarkdown") ?? "", comments.GetValueOrDefault(row.Text("Id")!)));

        var tasks = new List<ImportTask>();
        var line = 0;
        foreach (var row in Rows(archive, "Tasks").Where(row => row.Text("DeletedAtUtc") is null).OrderBy(row => row.Text("ContainerId"), StringComparer.Ordinal).ThenBy(row => row.Long("Position")))
        {
            line++;
            var id = row.Text("Id")!;
            if (!containers.TryGetValue(row.Text("ContainerId")!, out var container))
            {
                continue; // inside a deleted list
            }

            var isInbox = container.Long("IsInbox") == 1;
            var status = (TaskItemStatus)row.Long("Status");
            tasks.Add(new ImportTask
            {
                Line = line,
                SourceKey = id,
                HasStableKey = true,
                PreservedId = id,
                Title = row.Text("Title")!,
                Notes = row.Text("Notes") ?? "",
                ListName = isInbox ? null : container.Text("Name"),
                PreservedListId = isInbox ? null : container.Text("Id"),
                Status = status,
                Priority = (TaskPriority)row.Long("Priority"),
                EstimateMinutes = row.Text("EstimateMinutes") is null ? null : (int)row.Long("EstimateMinutes"),
                Category = (CapacityCategory)row.Long("CapacityCategory"),
                Planned = Date(row, "Planned"),
                Due = Date(row, "Due"),
                ClosedAtUtc = StorageFormats.ParseInstant(row.Text(status == TaskItemStatus.Cancelled ? "CancelledAtUtc" : "CompletedAtUtc")),
                TagPaths = [.. taskTags[id].Distinct(StringComparer.Ordinal)],
                Subtasks = [.. subtasks[id]],
                Diary = [.. diary[id]],
                GitHubLink = links.GetValueOrDefault(id),
                GitHubIssueWrite = links.TryGetValue(id, out var link) ? writes.GetValueOrDefault(link.Id) : null,
            });
        }

        if (Rows(archive, "RecurrenceSeries").Any() || Rows(archive, "ReminderRules").Any() || Rows(archive, "WeekPlans").Any() || Rows(archive, "ScratchPadItems").Any())
        {
            warnings.Add(new ImportIssue(0, "file", "Merging adds tasks with their subtasks, tags and diary. Recurrence, reminders, plans, the Personal pad and history come back only with Restore."));
        }

        return new ImportDraft(ImportSourceKind.ChaosJson, sourceName, fileFingerprint, tasks, [], warnings);
    }

    private static GitHubIssueLinkRecord ReadIssueLink(ArchiveRow row) => new()
    {
        Id = row.Text("Id")!, RepositorySubscriptionId = row.Text("RepositorySubscriptionId")!, ConnectionId = row.Text("ConnectionId")!,
        TaskId = row.Text("TaskId"), IssueId = row.Long("IssueId"), IssueNodeId = row.Text("IssueNodeId")!, IssueNumber = row.Long("IssueNumber"),
        HtmlUrl = row.Text("HtmlUrl")!, State = (int)row.Long("State"), BaseJson = row.Text("BaseJson")!,
        BaseRemoteUpdatedUtc = StorageFormats.ParseInstant(row.Text("BaseRemoteUpdatedUtc"))!.Value,
        PlacementManaged = row.Long("PlacementManaged") == 1, LocalDirty = row.Long("LocalDirty") == 1,
        LocalDirtySinceUtc = StorageFormats.ParseInstant(row.Text("LocalDirtySinceUtc")),
        LocalAssigneeIntent = row.Text("LocalAssigneeIntent") is null ? null : (int)row.Long("LocalAssigneeIntent"),
        ConflictNoticeUtc = StorageFormats.ParseInstant(row.Text("ConflictNoticeUtc")),
        CreatedAtUtc = StorageFormats.ParseInstant(row.Text("CreatedAtUtc"))!.Value,
        UpdatedAtUtc = StorageFormats.ParseInstant(row.Text("UpdatedAtUtc"))!.Value,
    };

    private static ImportDate? Date(ArchiveRow row, string prefix)
    {
        var date = StorageFormats.ParseDate(row.Text(prefix + "Date"));
        return date is null ? null : new ImportDate(date.Value, StorageFormats.ParseTime(row.Text(prefix + "Time")), row.Text(prefix + "ZoneId"));
    }

    private static string TagPath(Dictionary<string, ArchiveRow> tags, string id)
    {
        var segments = new List<string>();
        var current = tags[id];
        for (var guard = 0; guard < 64; guard++)
        {
            segments.Add(current.Text("DisplayName")!);
            if (current.Text("ParentTagId") is not { } parent || !tags.TryGetValue(parent, out var next))
            {
                break;
            }

            current = next;
        }

        segments.Reverse();
        return string.Join('/', segments);
    }

    private static IEnumerable<ArchiveRow> Rows(ArchiveContent archive, string table)
    {
        var data = archive.Table(table);
        if (data is null)
        {
            yield break;
        }

        var columns = data.Columns.Select((name, index) => (name, index)).ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);
        foreach (var values in data.Rows)
        {
            yield return new ArchiveRow(columns, values);
        }
    }

    private readonly record struct ArchiveRow(Dictionary<string, int> Columns, object?[] Values)
    {
        public string? Text(string column) =>
            Columns.TryGetValue(column, out var index) && index < Values.Length && Values[index] is { } value ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

        public long Long(string column) =>
            Columns.TryGetValue(column, out var index) && index < Values.Length && Values[index] is long value ? value : 0;
    }
}

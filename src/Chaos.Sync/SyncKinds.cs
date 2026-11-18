namespace Chaos.Sync;

/// <summary>One field group of a synced kind and the client schema version in which it first appeared (PLAN2 §7.12 forward compatibility).</summary>
public sealed record SyncGroupInfo(string Name, int Since);

/// <summary>A synced kind as the service needs to know it: its key parts, whether its create group names a space, and its groups.</summary>
public sealed record SyncKindInfo(string Kind, int KeyParts, int ApplyOrder, bool HasSpace, IReadOnlyList<SyncGroupInfo> Groups)
{
    public SyncKindInfo(string kind, int keyParts, int applyOrder, bool hasSpace, string[] groups)
        : this(kind, keyParts, applyOrder, hasSpace, [.. groups.Select(name => new SyncGroupInfo(name, 1))])
    {
    }

    public SyncGroupInfo? Group(string name) => Groups.FirstOrDefault(group => string.Equals(group.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// The service's copy of the client sync catalog's kind and group list (PLAN2 P13 item 1). The service
/// references no client project, so the list lives here, and the client test
/// <c>SyncKinds_MatchTheClientCatalog</c> fails when the two differ: a catalog change that forgets
/// this file cannot ship. Listed in apply order, parents first.
/// </summary>
public static class SyncKinds
{
    public static readonly IReadOnlyList<SyncKindInfo> All =
    [
        new("profile", 1, 10, false, ["create", "name", "zone", "locale", "dates", "planning", "reminders", "quiet", "deleted"]),
        new("space", 1, 20, false, ["create", "name", "deleted"]),
        new("container", 1, 30, true, ["create", "name", "position", "deadline", "archived", "category", "deleted"]),
        new("task_template", 1, 35, true, ["create", "name", "payload", "deleted"]),
        new("section", 1, 40, false, ["create", "title", "position", "deleted"]),
        new("tag", 1, 50, false, ["create", "name", "parent", "color", "position", "deleted"]),
        new("google_account", 1, 54, false, ["create", "deleted"]),
        new("board_setting", 1, 55, false, ["create", "grouping", "limits", "deleted"]),
        new("saved_view", 1, 56, true, ["create", "name", "look", "pinned", "position", "definition", "layout", "alerts", "deleted"]),
        new("calendar_source", 1, 57, false, ["create", "name", "look", "busy", "enabled", "owner", "position", "deleted"]),
        new("task", 1, 60, true, ["create", "title", "notes", "status", "place", "position", "priority", "estimate", "category", "planned", "due", "deleted"]),
        new("calendar_event_link", 1, 65, false, ["create", "deleted"]),
        new("subtask", 1, 70, false, ["create", "title", "notes", "status", "position", "deleted"]),
        new("task_tag", 2, 80, false, ["deleted"]),
        new("subtask_tag", 2, 90, false, ["deleted"]),
        new("diary_entry", 1, 100, false, ["create", "entry", "deleted"]),
        new("task_history", 1, 110, false, ["record"]),
        new("pad", 1, 120, true, ["create", "text", "position", "archived", "deleted"]),
        new("pad_drawing", 1, 125, false, ["strokes", "preview"]),
        new("scratch_task_link", 2, 130, false, ["anchor"]),
        new("pad_tag", 2, 140, false, ["deleted"]),
        new("recurrence_series", 1, 160, true, ["create", "rule", "ended", "deleted"]),
        new("recurrence_occurrence", 1, 170, false, ["create", "task", "state", "edit", "series", "deleted"]),
        new("recurrence_exception", 2, 180, false, ["exception"]),
        new("reminder_rule", 1, 190, false, ["create", "rule", "deleted"]),
        new("waiting_detail", 1, 200, false, ["create", "person", "deleted"]),
        new("follow_up", 1, 210, false, ["create", "kind", "schedule", "resolved", "deleted"]),
        new("day_plan", 1, 220, false, ["create", "seed", "initialized"]),
        new("day_plan_item", 1, 230, false, ["create", "position", "removed"]),
        new("week_plan", 1, 240, false, ["create", "state"]),
        new("week_goal", 1, 250, false, ["create", "title", "position", "project", "outcome"]),
        new("week_goal_task", 2, 260, false, ["deleted"]),
        new("week_assignment", 1, 270, false, ["create", "placement"]),
        new("availability_rule", 2, 280, false, ["minutes"]),
        new("week_availability_override", 2, 290, false, ["minutes"]),
        new("summary", 1, 300, false, ["record"]),
        new("import_batch", 1, 310, false, ["record"]),
        new("imported_item", 1, 320, false, ["record"]),
        new("github_connection", 1, 330, true, ["create", "settings", "state", "deleted"]),
        new("github_repository", 1, 340, false, ["create", "settings", "state", "deleted"]),
        new("github_issue_link", 1, 350, false, ["create", "base", "state"]),
        new("github_checklist_item", 1, 360, false, ["base"]),
        new("github_comment_link", 1, 370, false, ["create", "state"]),
        new("github_label_tag", 2, 380, false, ["mapping"]),
        new("github_milestone_project", 2, 390, false, ["mapping"]),
        new("page", 1, 400, true, ["create", "title", "text", "tree", "position", "deleted"]),
        new("page_task_link", 2, 420, false, ["anchor"]),
        new("page_tag", 2, 430, false, ["deleted"]),
        new("attachment", 1, 440, true, ["create", "name", "file", "deleted"]),
    ];

    private static readonly Dictionary<string, SyncKindInfo> ByKind = All.ToDictionary(kind => kind.Kind, StringComparer.Ordinal);

    public static SyncKindInfo? Find(string kind) => ByKind.GetValueOrDefault(kind);
}

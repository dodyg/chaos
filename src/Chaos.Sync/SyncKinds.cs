namespace Chaos.Sync;

/// <summary>One field group of a synced kind and the client schema version in which it first appeared (PLAN2 §7.12 forward compatibility).</summary>
public enum SyncWrite { Owner, Editor, Commenter, Author, Agent, Server }
public enum SyncRouteKind { Account, OwnSpace, Parent, FollowsTag, ServerOnly }
public sealed record SyncRoute(SyncRouteKind Kind, string? ParentKind = null, string? Column = null, int? KeyPart = null, string? Group = null, string? DiscriminatorColumn = null);
public sealed record OverlayRef(string Kind, string? Column = null, int? KeyPart = null, string? Group = null);

public sealed record SyncGroupInfo(string Name, int Since, SyncWrite Write = SyncWrite.Editor);

/// <summary>A synced kind as the service needs to know it: its key parts, whether its create group owns a space, and its groups.</summary>
public sealed record SyncKindInfo(string Kind, int KeyParts, int ApplyOrder, bool HasSpace, IReadOnlyList<SyncGroupInfo> Groups)
{
    public SyncRoute Route { get; init; } = new(SyncRouteKind.Account);
    public IReadOnlyList<OverlayRef> OverlayRefs { get; init; } = [];
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
    private static readonly IReadOnlyList<SyncKindInfo> Definitions =
    [
        new("profile", 1, 10, false, new SyncGroupInfo[] { new("create", 1), new("name", 1), new("zone", 1), new("locale", 1), new("dates", 1), new("planning", 1), new("reminders", 1), new("quiet", 1), new("appearance", 2), new("day_background", 2), new("deleted", 1) }),
        new("space", 1, 20, false, new SyncGroupInfo[] { new("create", 1), new("name", 1), new("look", 4), new("settings", 4), new("deleted", 1) }),
        new("space_member", 1, 21, true, new SyncGroupInfo[] { new("create", 4), new("profile", 4), new("role", 4), new("state", 4) }),
        new("container", 1, 30, true, ["create", "name", "position", "deadline", "archived", "category", "deleted"]),
        new("task_template", 1, 35, true, ["create", "name", "payload", "deleted"]),
        new("section", 1, 40, false, ["create", "title", "position", "deleted"]),
        new("space_state", 1, 45, true, new SyncGroupInfo[] { new("create", 7), new("name", 7), new("base", 7), new("position", 7), new("deleted", 7) }),
        new("tag", 1, 50, false, ["create", "name", "parent", "color", "position", "deleted"]),
        new("google_account", 1, 54, false, ["create", "deleted"]),
        new("board_setting", 1, 55, false, ["create", "grouping", "limits", "deleted"]),
        new("saved_view", 1, 56, true, ["create", "name", "look", "pinned", "position", "definition", "layout", "alerts", "deleted"]),
        new("calendar_source", 1, 57, false, ["create", "name", "look", "busy", "enabled", "owner", "position", "deleted"]),
        new("space_notification_setting", 1, 58, false, new SyncGroupInfo[] { new("create", 6), new("settings", 6) }),
        new("view_alert_setting", 1, 59, false, new SyncGroupInfo[] { new("create", 8), new("settings", 8) }),
        new("task", 1, 60, true, new SyncGroupInfo[] { new("create", 1), new("title", 1), new("notes", 1), new("status", 1), new("assignment", 5), new("place", 1), new("position", 1), new("priority", 1), new("estimate", 1), new("category", 1), new("planned", 1), new("due", 1), new("deleted", 1) }),
        new("task_assignee", 2, 61, false, new SyncGroupInfo[] { new("create", 5), new("done", 5), new("deleted", 5) }),
        new("task_delegation", 1, 62, false, new SyncGroupInfo[] { new("create", 10), new("brief", 10), new("state", 10), new("deleted", 10) }),
        new("nudge", 1, 63, false, new SyncGroupInfo[] { new("record", 10) }),
        new("calendar_event_link", 1, 65, false, ["create", "deleted"]),
        new("subtask", 1, 70, false, ["create", "title", "notes", "status", "position", "deleted"]),
        new("task_tag", 2, 80, false, ["deleted"]),
        new("subtask_tag", 2, 90, false, ["deleted"]),
        new("diary_entry", 1, 100, false, ["create", "entry", "deleted"]),
        new("space_update", 1, 101, true, new SyncGroupInfo[] { new("create", 11), new("entry", 11), new("deleted", 11) }),
        new("task_history", 1, 110, false, ["record"]),
        new("pad", 1, 120, true, ["create", "text", "position", "archived", "deleted"]),
        new("pad_drawing", 1, 125, false, ["strokes", "preview"]),
        new("scratch_task_link", 2, 130, false, ["anchor"]),
        new("pad_tag", 2, 140, false, ["deleted"]),
        new("recurrence_series", 1, 160, true, new SyncGroupInfo[] { new("create", 1), new("rule", 1), new("rotation", 9), new("ended", 1), new("deleted", 1) }),
        new("recurrence_occurrence", 1, 170, false, ["create", "task", "state", "edit", "series", "deleted"]),
        new("recurrence_exception", 2, 180, false, ["exception"]),
        new("reminder_rule", 1, 190, false, ["create", "rule", "deleted"]),
        new("reminder_acknowledgement", 1, 195, false, ["decision"]),
        new("waiting_detail", 1, 200, false, ["create", "person", "deleted"]),
        new("follow_up", 1, 210, false, new SyncGroupInfo[] { new("create", 1), new("kind", 1), new("schedule", 1), new("resolved", 1), new("deleted", 1), new("delegation", 10) }),
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
        new("github_connection", 1, 330, true, new SyncGroupInfo[] { new("create", 1), new("settings", 1), new("runner", 3), new("state", 1), new("deleted", 1) }),
        new("github_repository", 1, 340, false, ["create", "settings", "state", "deleted"]),
        new("github_issue_link", 1, 350, false, new SyncGroupInfo[] { new("create", 1), new("base", 1), new("state", 1), new("assignee_intent", 3) }),
        new("github_pull_request_link", 2, 355, false, new SyncGroupInfo[] { new("create", 12), new("state", 12) }),
        new("github_checklist_item", 1, 360, false, ["base"]),
        new("github_comment_link", 1, 370, false, ["create", "state"]),
        new("github_label_tag", 2, 380, false, ["mapping"]),
        new("github_milestone_project", 2, 390, false, ["mapping"]),
        new("page", 1, 400, true, ["create", "title", "text", "tree", "position", "deleted"]),
        new("page_task_link", 2, 420, false, ["anchor"]),
        new("page_tag", 2, 430, false, ["deleted"]),
        new("attachment", 1, 440, true, ["create", "name", "file", "deleted"]),
    ];

    public static readonly IReadOnlyList<SyncKindInfo> All = [.. Definitions.Select(Describe)];

    private static SyncKindInfo Describe(SyncKindInfo kind)
    {
        var route = kind.Kind switch
        {
            "profile" or "view_alert_setting" or "space_notification_setting" or "google_account" or "calendar_source" or "calendar_event_link" or "reminder_rule" or "reminder_acknowledgement" or "waiting_detail" or "follow_up" or "day_plan" or "day_plan_item" or "week_plan" or "week_goal" or "week_goal_task" or "week_assignment" or "availability_rule" or "week_availability_override" or "summary" or "import_batch" or "imported_item" => new SyncRoute(SyncRouteKind.Account),
            "space" => new SyncRoute(SyncRouteKind.OwnSpace, KeyPart: 0),
            "space_member" => new SyncRoute(SyncRouteKind.ServerOnly),
            "tag" => new SyncRoute(SyncRouteKind.OwnSpace, Column: "SpaceId", Group: "create"),
            "space_state" or "container" or "task_template" or "saved_view" or "task" or "pad" or "recurrence_series" or "page" or "attachment" or "github_connection" or "space_update" => new SyncRoute(SyncRouteKind.OwnSpace, Column: "SpaceId", Group: "create"),
            "section" => Parent("container", "ContainerId"),
            "subtask" or "diary_entry" => Parent("task", "TaskId"),
            "task_assignee" => KeyParent("task", 0),
            "task_delegation" => Parent("task", "TaskId"),
            "nudge" => Parent("task", "TaskId", "record"),
            "task_history" => Parent("task", "TaskId", "record"),
            "github_issue_link" => Parent("task", "TaskId", "state"),
            "pad_drawing" or "scratch_task_link" => KeyParent("pad", 0),
            "recurrence_occurrence" => Parent("recurrence_series", "SeriesId"),
            "recurrence_exception" => KeyParent("recurrence_series", 0),
            "page_task_link" => KeyParent("page", 0),
            "github_repository" => Parent("github_connection", "ConnectionId"),
            "github_pull_request_link" => Parent("github_issue_link", "IssueLinkId"),
            "github_checklist_item" => Parent("github_issue_link", "IssueLinkId", "base"),
            "github_comment_link" => KeyParent("diary_entry", 0),
            "github_label_tag" or "github_milestone_project" => KeyParent("github_repository", 0),
            "board_setting" => new SyncRoute(SyncRouteKind.Parent, Column: "TargetId", Group: "create", DiscriminatorColumn: "TargetKind"),
            "task_tag" or "subtask_tag" or "pad_tag" or "page_tag" => new SyncRoute(SyncRouteKind.FollowsTag, ParentKind: "tag", KeyPart: 1),
            _ => throw new InvalidOperationException($"Missing sync route for {kind.Kind}."),
        };
        IReadOnlyList<OverlayRef> overlays = kind.Kind switch
        {
            "space_notification_setting" => [new("space", "SpaceId", Group: "create")],
            "view_alert_setting" => [new("saved_view", "ViewId", Group: "create")],
            "calendar_event_link" or "reminder_rule" or "waiting_detail" or "follow_up" or "day_plan_item" or "week_assignment" => [new("task", "TaskId", Group: "create")],
            "week_goal_task" or "task_tag" => [new("task", KeyPart: kind.Kind == "week_goal_task" ? 1 : 0)],
            "subtask_tag" => [new("subtask", KeyPart: 0)],
            "pad_tag" => [new("pad", KeyPart: 0)],
            "page_tag" => [new("page", KeyPart: 0)],
            _ => [],
        };
        return kind with { Route = route, OverlayRefs = overlays, Groups = [.. kind.Groups.Select(group => group with { Write = WriteFor(kind.Kind, group.Name) })] };
    }

    private static SyncRoute Parent(string kind, string column, string group = "create") => new(SyncRouteKind.Parent, kind, column, Group: group);
    private static SyncRoute KeyParent(string kind, int part) => new(SyncRouteKind.Parent, kind, KeyPart: part);
    private static SyncWrite WriteFor(string kind, string group) => kind switch
    {
        "space" or "space_state" => SyncWrite.Owner,
        "space_member" => SyncWrite.Server,
        "diary_entry" or "space_update" when group == "create" => SyncWrite.Commenter,
        "diary_entry" or "space_update" => SyncWrite.Author, // the author named in the create group; SyncService checks, owners may delete
        "task_delegation" when group == "brief" => SyncWrite.Author,
        "task_delegation" when group == "state" => SyncWrite.Author, // who, depends on the transition: SyncService checks the table
        "nudge" => SyncWrite.Author, // only the delegator nudges; SyncService also applies the rate limit
        _ when kind.StartsWith("github_", StringComparison.Ordinal) => SyncWrite.Owner,
        _ => SyncWrite.Editor,
    };

    private static readonly Dictionary<string, SyncKindInfo> ByKind = All.ToDictionary(kind => kind.Kind, StringComparer.Ordinal);

    public static SyncKindInfo? Find(string kind) => ByKind.GetValueOrDefault(kind);
}

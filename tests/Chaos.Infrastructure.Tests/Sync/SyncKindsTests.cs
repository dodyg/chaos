using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Migrations;
using Chaos.Sync;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests.Sync;

/// <summary>The service copies the catalog's kind and group list into Chaos.Sync (PLAN2 P13 item 1); they must not drift.</summary>
public sealed class SyncKindsTests
{
    [Test]
    public async Task SyncKinds_RouteColumnsExist()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chaos-route-{Guid.NewGuid():N}.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        await connection.OpenAsync();
        await SqliteMigrationRunner.MigrateAsync(connection, CancellationToken.None);
        foreach (var table in SyncCatalog.Tables.Where(table => table.Mode != SyncMode.LocalOnly))
        {
            var route = SyncKinds.Find(table.Kind)!.Route;
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT name FROM pragma_table_info($table)";
            query.Parameters.AddWithValue("$table", table.Table);
            using var reader = await query.ExecuteReaderAsync();
            var columns = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
            if (route.KeyPart is int part)
            {
                await Assert.That(part >= 0 && part < table.Key.Count).IsTrue();
                await Assert.That(columns.Contains(table.Key[part])).IsTrue();
            }
            if (route.Column is not null && table.Kind != "tag")
            {
                var group = table.Groups.Single(g => g.Name == route.Group);
                await Assert.That(group.Columns.Contains(route.Column)).IsTrue();
                await Assert.That(columns.Contains(route.Column)).IsTrue();
                if (route.DiscriminatorColumn is not null)
                {
                    await Assert.That(group.Columns.Contains(route.DiscriminatorColumn)).IsTrue();
                    await Assert.That(columns.Contains(route.DiscriminatorColumn)).IsTrue();
                }
            }
            foreach (var overlay in SyncKinds.Find(table.Kind)!.OverlayRefs)
            {
                if (overlay.KeyPart is int overlayPart)
                    await Assert.That(overlayPart >= 0 && overlayPart < table.Key.Count).IsTrue();
                if (overlay.Column is not null)
                    await Assert.That(table.Groups.Single(g => g.Name == overlay.Group).Columns.Contains(overlay.Column)).IsTrue();
            }
        }
    }

    [Test]
    public async Task SyncKinds_OverlayAndPermissionMetadata_IsComplete()
    {
        string[] overlayKinds = ["calendar_event_link", "reminder_rule", "waiting_detail", "follow_up", "day_plan_item", "week_goal_task", "week_assignment", "task_tag", "subtask_tag", "pad_tag", "page_tag"];
        foreach (var name in overlayKinds)
            await Assert.That(SyncKinds.Find(name)!.OverlayRefs.Count > 0).IsTrue();
        foreach (var kind in SyncKinds.All)
            foreach (var group in kind.Groups)
                await Assert.That(Enum.IsDefined(group.Write)).IsTrue();
        await Assert.That(SyncKinds.Find("github_issue_link")!.Route.Group).IsEqualTo("state");
    }
    [Test]
    public async Task GitHubRunnerGroups_RequireSchemaThree()
    {
        await Assert.That(SyncKinds.Find("github_connection")!.Group("runner")!.Since).IsEqualTo(3);
        await Assert.That(SyncKinds.Find("github_issue_link")!.Group("assignee_intent")!.Since).IsEqualTo(3);
    }

    [Test]
    public async Task SyncKinds_MatchTheClientCatalog()
    {
        var catalog = SyncCatalog.Tables.Where(table => table.Mode != SyncMode.LocalOnly).OrderBy(table => table.ApplyOrder).ToList();
        string[] parentKinds = ["section", "subtask", "diary_entry", "task_history", "github_issue_link", "pad_drawing", "scratch_task_link", "recurrence_occurrence", "recurrence_exception", "page_task_link", "github_repository", "github_checklist_item", "github_comment_link", "github_label_tag", "github_milestone_project", "board_setting"];
        string[] tagLinks = ["task_tag", "subtask_tag", "pad_tag", "page_tag"];
        await Assert.That(SyncKinds.All.Select(kind => kind.Kind)).IsEquivalentTo(catalog.Select(table => table.Kind));
        foreach (var table in catalog)
        {
            var kind = SyncKinds.Find(table.Kind)!;
            await Assert.That(kind.KeyParts).IsEqualTo(table.Key.Count);
            await Assert.That(kind.ApplyOrder).IsEqualTo(table.ApplyOrder);
            await Assert.That(kind.Groups.Select(group => group.Name)).IsEquivalentTo(table.Groups.Select(group => group.Name));
            await Assert.That(kind.HasSpace).IsEqualTo(table.Groups.Any(group => group.Name == "create" && group.Columns.Contains("SpaceId")));
            var route = table.Kind == "space_member" ? SyncRouteKind.ServerOnly
                : parentKinds.Contains(table.Kind) ? SyncRouteKind.Parent
                : tagLinks.Contains(table.Kind) ? SyncRouteKind.FollowsTag
                : kind.HasSpace || table.Kind is "space" or "tag" ? SyncRouteKind.OwnSpace
                : SyncRouteKind.Account;
            await Assert.That(kind.Route.Kind).IsEqualTo(route);
        }
    }
}

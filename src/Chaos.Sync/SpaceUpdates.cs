namespace Chaos.Sync;

/// <summary>
/// Standups (PLAN3 9.12): a member's post in a shared space, shown on the space Overview newest
/// first. The author alone edits a standup; an author or an owner deletes it. The limit is shared
/// by the client schema (migration 047) and the desktop commands.
/// </summary>
public static class SpaceUpdates
{
    /// <summary>The most characters one standup holds.</summary>
    public const int MaxMarkdownLength = 20_000;
}

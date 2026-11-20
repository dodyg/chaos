namespace Chaos.Application.People;

/// <summary>A space member, retained after departure so past contributions keep their attribution.</summary>
public sealed record SpacePerson(string ActorId, string DisplayName, int Role, bool Former, string? GitHubLogin);

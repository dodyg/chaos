namespace Chaos.Domain.Tasks;

public enum TaskPriority
{
    None = 0,
    Low = 1,
    Normal = 2,
    High = 3,
    Urgent = 4,
}

/// <summary>Work vs personal capacity category (PLAN §10). A personal capacity category, not a space.</summary>
public enum CapacityCategory
{
    Personal = 0,
    Work = 1,
}

/// <summary>Lists and projects are alternative containers; Inbox is the special capture container (PLAN §1 decision 6).</summary>
public enum ContainerKind
{
    Inbox = 0,
    List = 1,
    Project = 2,
}

public enum SpaceKind
{
    Personal = 1,
}

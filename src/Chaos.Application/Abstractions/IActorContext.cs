using Chaos.Application.Activity;

namespace Chaos.Application.Abstractions;

/// <summary>Resolves an operation's trusted actor, falling back to the local profile.</summary>
public interface IActorContext
{
    string CurrentActor(string localProfileId);
}

public sealed class AmbientActorContext : IActorContext
{
    public static AmbientActorContext Instance { get; } = new();

    public string CurrentActor(string localProfileId) => ActivityScope.Current?.ActorId ?? $"person:{localProfileId}";
}

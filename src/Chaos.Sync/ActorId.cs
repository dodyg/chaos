namespace Chaos.Sync;

/// <summary>A person or agent identity in history, independent of the device issuing a change.</summary>
public readonly record struct ActorId(ActorKind Kind, Guid Id)
{
    public override string ToString() => $"{(Kind == ActorKind.Person ? "person" : "agent")}:{Id:D}";

    public static bool TryParse(string? value, out ActorId actor)
    {
        actor = default;
        if (value is null) return false;
        var separator = value.IndexOf(':');
        if (separator < 0 || !Guid.TryParseExact(value[(separator + 1)..], "D", out var id)) return false;
        var kind = value[..separator] switch
        {
            "person" => ActorKind.Person,
            "agent" => ActorKind.Agent,
            _ => (ActorKind?)null,
        };
        if (kind is null) return false;
        actor = new ActorId(kind.Value, id);
        return true;
    }
}

public enum ActorKind { Person, Agent }

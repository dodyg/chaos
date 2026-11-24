using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Chaos.Sync;

namespace Chaos.Application.People;

/// <summary>A member as retained on the device, including members who have left.</summary>
public sealed record DirectoryMember(string ActorId, string DisplayName);

public sealed record PersonDisplay(string ActorId, string DisplayName, string Initials, int ColorIndex, ActorKind? Kind, bool IsMe, int? Role = null, bool Former = false, string? GitHubLogin = null);

/// <summary>Resolves actor identities without treating absent or former members as errors.</summary>
public static class PeopleDirectory
{
    public static PersonDisplay Resolve(string? actorId, string? myActorId, IEnumerable<DirectoryMember> members)
    {
        var id = actorId ?? string.Empty;
        var parsed = ActorId.TryParse(id, out var actor);
        var member = parsed ? members.FirstOrDefault(m => string.Equals(m.ActorId, id, StringComparison.OrdinalIgnoreCase)) : null;
        var name = string.IsNullOrWhiteSpace(member?.DisplayName) ? "Unknown person" : member.DisplayName.Trim();
        var words = name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var initials = string.Concat(words.Take(2).Select(word => StringInfo.GetNextTextElement(word).ToUpperInvariant()));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id.ToLowerInvariant()));
        return new PersonDisplay(id, name, initials, hash[0] % 8, parsed ? actor.Kind : null,
            parsed && string.Equals(id, myActorId, StringComparison.OrdinalIgnoreCase));
    }
}

using Chaos.Domain.Tags;

namespace Chaos.Domain.GitHub;

public static class LabelNames
{
    public static IReadOnlyList<TagSegment> ToTagPath(string label) => TagName.Path(label);

    public static string FromTagPath(IEnumerable<string> segments) =>
        string.Join('/', segments.Select(segment => TagName.Segment(segment).DisplayName));

    public static bool Matches(string label, string tagPath) =>
        string.Equals(TagName.Normalize(label), TagName.Normalize(tagPath), StringComparison.Ordinal);
}

using Chaos.Application.Activity;
using Chaos.Domain.Text;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record TextMerge3Request(string Base, string Local, string Remote, string? LocalLabel, string? RemoteLabel);
public sealed record TextMerge3Response(bool Ok, string Text, bool Conflicted);

/// <summary>
/// The three-way line merge the sync applier uses (PLAN3 §9.9), exposed so an editor merges its own unsaved text
/// with a remote replacement by the same rules and the logic is not written twice.
/// </summary>
[NeoRpcService("text", Version = 1)]
public sealed class TextMergeRpcService
{
    private const int MaxLabel = 80;

    [NeoRpcMethod("merge3")]
    public ValueTask<TextMerge3Response> Merge3Async(TextMerge3Request request, CancellationToken ct)
    {
        using var rpcScope = ActivityScope.Rpc("text.merge3");
        var merged = Diff3.Merge(request.Base, request.Local, request.Remote, Label(request.LocalLabel, "yours"), Label(request.RemoteLabel, "remote"));
        return ValueTask.FromResult(new TextMerge3Response(true, merged, Diff3.HasConflictMarkers(merged)));
    }

    private static string Label(string? label, string fallback)
    {
        var clean = (label ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return clean.Length == 0 ? fallback : clean.Length > MaxLabel ? clean[..MaxLabel] : clean;
    }
}

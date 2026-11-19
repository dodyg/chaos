using Chaos.Application.Abstractions;
using Chaos.Application.Activity;
using Chaos.Application.Sites;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

public sealed record SiteAddRequest(string OperationId, string Url);
public sealed record SiteAddResponse(bool Ok, PadItemResponse? Item, TaskErrorResponse? Error);

/// <summary>"New site" on the scratch pad: the note is stored at once; reading and summarizing the page continue in the background.</summary>
[NeoRpcService("site", Version = 1)]
public sealed class SiteRpcService(SiteNoteCommands sites)
{
    [NeoRpcMethod("add")]
    public ValueTask<SiteAddResponse> AddAsync(SiteAddRequest request, CancellationToken ct) =>
        Rpc.Run<SiteAddResponse>("site.add", async () => {
            var result = await sites.AddAsync(new(request.OperationId, request.Url), ct);
            return new(true, PadRpcService.Map(new(result.Item, [], [])), null);
        },
            error => new(false, null, error));
}

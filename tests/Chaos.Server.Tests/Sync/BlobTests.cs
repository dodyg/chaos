using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chaos.Server.Blobs;
using Chaos.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Chaos.Server.Tests.Sync;

/// <summary>Flags a file that contains the word EICAR, and can be made unavailable. Stands in for ClamAV in the unit-level tests.</summary>
internal sealed class FakeScanner : IMalwareScanner
{
    public bool Unavailable { get; set; }
    public int Scans;

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken ct)
    {
        Interlocked.Increment(ref Scans);
        if (Unavailable)
        {
            throw new IOException("The scanner is down.");
        }

        using var all = new MemoryStream();
        await content.CopyToAsync(all, ct);
        return Encoding.ASCII.GetString(all.ToArray()).Contains("EICAR", StringComparison.Ordinal) ? new ScanResult(false, "Fake.Eicar") : ScanResult.Ok;
    }

    public Task CheckAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>PLAN2 P16 item 3, server side: resumable upload, hash check, scanning, encryption, range download, quota, isolation, housekeeping.</summary>
[ClassDataSource<PostgresServer>(Shared = SharedType.PerTestSession)]
public sealed class BlobTests(PostgresServer postgres)
{
    private sealed record Caller(string Token, Guid Device, Guid Account);

    private sealed class Rig : IAsyncDisposable
    {
        public required TestServer Server { get; init; }
        public required FakeScanner Scanner { get; init; }
        public required string BlobDirectory { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Server.DisposeAsync();
            try
            {
                Directory.Delete(BlobDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task<Rig> StartAsync(long? quota = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "chaos-blob-tests", Guid.NewGuid().ToString("N"));
        var scanner = new FakeScanner();
        var settings = new Dictionary<string, string?>
        {
            ["Blob:Directory"] = Path.Combine(directory, "objects"),
            ["KeyVault:MasterKeyPath"] = Path.Combine(directory, "master.key"),
        };
        if (quota is { } q)
        {
            settings["Limits:AccountQuotaBytes"] = q.ToString();
        }

        var server = await TestServer.StartAsync(postgres, settings, services =>
        {
            services.RemoveAll<IMalwareScanner>();
            services.AddSingleton<IMalwareScanner>(scanner);
        });
        return new Rig { Server = server, Scanner = scanner, BlobDirectory = directory };
    }

    private static async Task<Caller> SignInAsync(TestServer server, string email)
    {
        var flow = new DesktopFlow(server);
        var tokens = await flow.SignInAsync(email);
        using var http = server.Factory.CreateClient();
        using var register = DesktopFlow.Api(HttpMethod.Post, "/v1/devices", tokens.AccessToken, new { deviceId = flow.DeviceId, name = "Test", platform = "windows", clientVersion = "1", clientSchema = 1 });
        (await http.SendAsync(register)).EnsureSuccessStatusCode();
        await using var db = ServerDatabase.Open(server.ConnectionString);
        return new Caller(tokens.AccessToken, flow.DeviceId, db.Accounts.Single(a => a.PrimaryEmail == email).Id);
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Random(int size) => RandomNumberGenerator.GetBytes(size);

    private static async Task<HttpResponseMessage> SendAsync(TestServer server, Caller caller, HttpMethod method, string path, HttpContent? content = null, Action<HttpRequestMessage>? tweak = null, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        var http = server.Factory.CreateClient();
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new("Bearer", caller.Token);
        tweak?.Invoke(request);
        return await http.SendAsync(request, completion);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<HttpResponseMessage> StartUploadAsync(TestServer server, Caller caller, string sha, long size) =>
        await SendAsync(server, caller, HttpMethod.Post, $"/v1/blobs/{sha}", new StringContent($"{{\"size\":{size}}}", Encoding.UTF8, "application/json"));

    private static async Task<HttpResponseMessage> PutPartAsync(TestServer server, Caller caller, string sha, int index, byte[] bytes, int partSize = 1024 * 1024)
    {
        var slice = bytes.AsMemory(index * partSize, Math.Min(partSize, bytes.Length - (index * partSize))).ToArray();
        return await SendAsync(server, caller, HttpMethod.Put, $"/v1/blobs/{sha}?part={index}", new ByteArrayContent(slice));
    }

    private static Task<HttpResponseMessage> CompleteAsync(TestServer server, Caller caller, string sha) =>
        SendAsync(server, caller, HttpMethod.Post, $"/v1/blobs/{sha}/complete");

    /// <summary>The whole upload in order; returns the completion answer.</summary>
    private static async Task<HttpResponseMessage> UploadAsync(TestServer server, Caller caller, byte[] bytes)
    {
        var sha = Sha(bytes);
        (await StartUploadAsync(server, caller, sha, bytes.Length)).EnsureSuccessStatusCode();
        var parts = (bytes.Length + (1024 * 1024) - 1) / (1024 * 1024);
        for (var i = 0; i < parts; i++)
        {
            (await PutPartAsync(server, caller, sha, i, bytes)).EnsureSuccessStatusCode();
        }

        return await CompleteAsync(server, caller, sha);
    }

    private static string[] StoredObjects(Rig rig) =>
        Directory.Exists(Path.Combine(rig.BlobDirectory, "objects", "blobs")) ? Directory.GetFiles(Path.Combine(rig.BlobDirectory, "objects", "blobs"), "*", SearchOption.AllDirectories) : [];

    [Test]
    public async Task An_upload_round_trips_and_is_stored_encrypted()
    {
        await using var rig = await StartAsync();
        var caller = await SignInAsync(rig.Server, "a@example.com");
        var bytes = Random((2 * 1024 * 1024) + 12_345);
        var sha = Sha(bytes);

        var done = await UploadAsync(rig.Server, caller, bytes);
        await Assert.That(done.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await JsonAsync(done)).GetProperty("state").GetString()).IsEqualTo("Clean");

        using var head = await SendAsync(rig.Server, caller, HttpMethod.Head, $"/v1/blobs/{sha}");
        await Assert.That(head.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(head.Content.Headers.ContentLength).IsEqualTo(bytes.Length);
        await Assert.That(head.Headers.GetValues(BlobEndpoints.StateHeader).Single()).IsEqualTo("Clean");

        using var get = await SendAsync(rig.Server, caller, HttpMethod.Get, $"/v1/blobs/{sha}");
        await Assert.That(await get.Content.ReadAsByteArrayAsync()).IsEquivalentTo(bytes);

        // What is on disk is neither the file nor contains any long stretch of it.
        var stored = await File.ReadAllBytesAsync(StoredObjects(rig).Single());
        await Assert.That(stored.Length).IsEqualTo((int)BlobCrypto.EncryptedSize(bytes.Length));
        await Assert.That(stored.AsSpan().IndexOf(bytes.AsSpan(1000, 64))).IsEqualTo(-1);

        await using var db = ServerDatabase.Open(rig.Server.ConnectionString);
        await Assert.That((await db.Accounts.SingleAsync(a => a.Id == caller.Account)).StorageBytes).IsEqualTo(bytes.Length);
        await Assert.That(await db.BlobParts.CountAsync()).IsEqualTo(0);
        await Assert.That(Directory.Exists(Path.Combine(rig.BlobDirectory, "objects", "parts", caller.Account.ToString("N"), sha))).IsFalse();
    }

    [Test]
    public async Task An_interrupted_upload_resumes_from_the_parts_the_service_holds()
    {
        await using var rig = await StartAsync();
        var caller = await SignInAsync(rig.Server, "a@example.com");
        var bytes = Random((3 * 1024 * 1024) - 5);
        var sha = Sha(bytes);

        (await StartUploadAsync(rig.Server, caller, sha, bytes.Length)).EnsureSuccessStatusCode();
        (await PutPartAsync(rig.Server, caller, sha, 0, bytes)).EnsureSuccessStatusCode();
        (await PutPartAsync(rig.Server, caller, sha, 2, bytes)).EnsureSuccessStatusCode();

        using var early = await CompleteAsync(rig.Server, caller, sha);
        await Assert.That(early.StatusCode).IsEqualTo(HttpStatusCode.Conflict);

        // The device starts again after a restart and is told what to send.
        var resumed = await JsonAsync(await StartUploadAsync(rig.Server, caller, sha, bytes.Length));
        await Assert.That(resumed.GetProperty("received").EnumerateArray().Select(e => e.GetInt32()).ToArray()).IsEquivalentTo(new[] { 0, 2 });
        await Assert.That(resumed.GetProperty("partCount").GetInt32()).IsEqualTo(3);
        (await PutPartAsync(rig.Server, caller, sha, 1, bytes)).EnsureSuccessStatusCode();
        // Sending a part twice is harmless.
        (await PutPartAsync(rig.Server, caller, sha, 1, bytes)).EnsureSuccessStatusCode();

        using var done = await CompleteAsync(rig.Server, caller, sha);
        await Assert.That(done.StatusCode).IsEqualTo(HttpStatusCode.OK);
        using var get = await SendAsync(rig.Server, caller, HttpMethod.Get, $"/v1/blobs/{sha}");
        await Assert.That(await get.Content.ReadAsByteArrayAsync()).IsEquivalentTo(bytes);

        // Completing again, and starting again, both report the finished blob.
        using var again = await CompleteAsync(rig.Server, caller, sha);
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await JsonAsync(await StartUploadAsync(rig.Server, caller, sha, bytes.Length))).GetProperty("state").GetString()).IsEqualTo("Clean");
    }

    [Test]
    public async Task A_part_of_the_wrong_size_or_position_is_refused()
    {
        await using var rig = await StartAsync();
        var caller = await SignInAsync(rig.Server, "a@example.com");
        var bytes = Random((2 * 1024 * 1024) + 10);
        var sha = Sha(bytes);
        (await StartUploadAsync(rig.Server, caller, sha, bytes.Length)).EnsureSuccessStatusCode();

        using var tooShort = await SendAsync(rig.Server, caller, HttpMethod.Put, $"/v1/blobs/{sha}?part=0", new ByteArrayContent(bytes[..1000]));
        using var tooLong = await SendAsync(rig.Server, caller, HttpMethod.Put, $"/v1/blobs/{sha}?part=2", new ByteArrayContent(bytes[..2000]));
        using var outOfRange = await SendAsync(rig.Server, caller, HttpMethod.Put, $"/v1/blobs/{sha}?part=3", new ByteArrayContent(bytes[..10]));
        using var noUpload = await SendAsync(rig.Server, caller, HttpMethod.Put, $"/v1/blobs/{Sha([1])}?part=0", new ByteArrayContent([1]));
        await Assert.That(tooShort.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(tooLong.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(outOfRange.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(noUpload.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        using var badHash = await StartUploadAsync(rig.Server, caller, "ABC", 10);
        await Assert.That(badHash.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task Bytes_that_do_not_match_their_hash_are_rejected_and_discarded()
    {
        await using var rig = await StartAsync();
        var caller = await SignInAsync(rig.Server, "a@example.com");
        var real = Random(1500);
        var claimed = Sha(real);
        var other = Random(1500);

        (await StartUploadAsync(rig.Server, caller, claimed, other.Length)).EnsureSuccessStatusCode();
        (await SendAsync(rig.Server, caller, HttpMethod.Put, $"/v1/blobs/{claimed}?part=0", new ByteArrayContent(other))).EnsureSuccessStatusCode();
        using var done = await CompleteAsync(rig.Server, caller, claimed);
        await Assert.That(done.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
        await Assert.That((await JsonAsync(done)).GetProperty("code").GetString()).IsEqualTo("hash-mismatch");

        using var head = await SendAsync(rig.Server, caller, HttpMethod.Head, $"/v1/blobs/{claimed}");
        await Assert.That(head.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await using var db = ServerDatabase.Open(rig.Server.ConnectionString);
        await Assert.That(await db.BlobUploads.CountAsync()).IsEqualTo(0);
        await Assert.That(await db.Blobs.CountAsync()).IsEqualTo(0);
    }

    [Test]
    public async Task A_file_the_scanner_flags_is_blocked_and_never_served()
    {
        await using var rig = await StartAsync();
        var caller = await SignInAsync(rig.Server, "a@example.com");
        var bytes = Encoding.ASCII.GetBytes("EICAR test file, harmless");
        var sha = Sha(bytes);

        using var done = await UploadAsync(rig.Server, caller, bytes);
        await Assert.That((await JsonAsync(done)).GetProperty("state").GetString()).IsEqualTo("Blocked");

        using var head = await SendAsync(rig.Server, caller, HttpMethod.Head, $"/v1/blobs/{sha}");
        await Assert.That(head.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(head.Headers.GetValues(BlobEndpoints.StateHeader).Single()).IsEqualTo("Blocked");
        using var get = await SendAsync(rig.Server, caller, HttpMethod.Get, $"/v1/blobs/{sha}");
        await Assert.That(get.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await JsonAsync(get)).GetProperty("code").GetString()).IsEqualTo("blocked");

        await Assert.That(StoredObjects(rig)).IsEmpty();
        await using var db = ServerDatabase.Open(rig.Server.ConnectionString);
        await Assert.That((await db.Accounts.SingleAsync(a => a.Id == caller.Account)).StorageBytes).IsEqualTo(0L);
    }

    [Test]
    public async Task An_unavailable_scanner_never_means_clean()
    {
        await using var rig = await StartAsync();
        var caller = await SignInAsync(rig.Server, "a@example.com");
        var bytes = Random(2000);
        var sha = Sha(bytes);
        rig.Scanner.Unavailable = true;

        using var failed = await UploadAsync(rig.Server, caller, bytes);
        await Assert.That(failed.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        using var head = await SendAsync(rig.Server, caller, HttpMethod.Head, $"/v1/blobs/{sha}");
        await Assert.That(head.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // The parts are still there, so the retry needs no re-upload.
        rig.Scanner.Unavailable = false;
        using var done = await CompleteAsync(rig.Server, caller, sha);
        await Assert.That(done.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_range_is_served_across_chunk_boundaries()
    {
        await using var rig = await StartAsync();
        var caller = await SignInAsync(rig.Server, "a@example.com");
        var bytes = Random((3 * BlobCrypto.ChunkSize) + 777);
        var sha = Sha(bytes);
        (await UploadAsync(rig.Server, caller, bytes)).EnsureSuccessStatusCode();

        async Task CheckAsync(string range, int from, int to, string contentRange)
        {
            using var response = await SendAsync(rig.Server, caller, HttpMethod.Get, $"/v1/blobs/{sha}", tweak: r => r.Headers.Range = RangeHeaderValue.Parse(range));
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.PartialContent);
            await Assert.That(response.Content.Headers.ContentRange!.ToString()).IsEqualTo(contentRange);
            await Assert.That(await response.Content.ReadAsByteArrayAsync()).IsEquivalentTo(bytes[from..(to + 1)]);
        }

        var n = bytes.Length;
        await CheckAsync("bytes=0-99", 0, 99, $"bytes 0-99/{n}");
        await CheckAsync($"bytes={BlobCrypto.ChunkSize - 50}-{BlobCrypto.ChunkSize + 49}", BlobCrypto.ChunkSize - 50, BlobCrypto.ChunkSize + 49, $"bytes {BlobCrypto.ChunkSize - 50}-{BlobCrypto.ChunkSize + 49}/{n}");
        await CheckAsync($"bytes={(2 * BlobCrypto.ChunkSize) + 5}-", (2 * BlobCrypto.ChunkSize) + 5, n - 1, $"bytes {(2 * BlobCrypto.ChunkSize) + 5}-{n - 1}/{n}");
        await CheckAsync("bytes=-1000", n - 1000, n - 1, $"bytes {n - 1000}-{n - 1}/{n}");

        using var beyond = await SendAsync(rig.Server, caller, HttpMethod.Get, $"/v1/blobs/{sha}", tweak: r => r.Headers.TryAddWithoutValidation("Range", $"bytes={n + 10}-"));
        await Assert.That(beyond.StatusCode).IsEqualTo(HttpStatusCode.RequestedRangeNotSatisfiable);
    }

    [Test]
    public async Task A_tampered_stored_chunk_is_not_delivered_as_a_complete_file()
    {
        await using var rig = await StartAsync();
        var caller = await SignInAsync(rig.Server, "a@example.com");
        var bytes = Random((2 * BlobCrypto.ChunkSize) + 100);
        var sha = Sha(bytes);
        (await UploadAsync(rig.Server, caller, bytes)).EnsureSuccessStatusCode();

        var path = StoredObjects(rig).Single();
        var stored = await File.ReadAllBytesAsync(path);
        stored[BlobCrypto.ChunkSize + BlobCrypto.Overhead + 500] ^= 0xFF; // inside the second chunk
        await File.WriteAllBytesAsync(path, stored);

        var failed = false;
        try
        {
            using var response = await SendAsync(rig.Server, caller, HttpMethod.Get, $"/v1/blobs/{sha}");
            failed = (await response.Content.ReadAsByteArrayAsync()).Length != bytes.Length;
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            failed = true;
        }

        await Assert.That(failed).IsTrue();

        // The first chunk is untouched and still reads.
        using var first = await SendAsync(rig.Server, caller, HttpMethod.Get, $"/v1/blobs/{sha}", tweak: r => r.Headers.Range = RangeHeaderValue.Parse("bytes=0-99"));
        await Assert.That(await first.Content.ReadAsByteArrayAsync()).IsEquivalentTo(bytes[..100]);
    }

    [Test]
    public async Task Chunk_encryption_round_trips_and_rejects_swapped_or_tampered_chunks()
    {
        var key = BlobCrypto.NewDataKey();
        var plain = Random((2 * BlobCrypto.ChunkSize) + 10);
        using var sealedStream = new MemoryStream();
        var written = await BlobCrypto.EncryptAsync(new MemoryStream(plain), sealedStream, key, CancellationToken.None);
        await Assert.That(written).IsEqualTo(plain.Length);
        var stored = sealedStream.ToArray();

        async Task<byte[]> DecryptAsync(byte[] data, long start, long end)
        {
            using var output = new MemoryStream();
            await BlobCrypto.DecryptRangeAsync(offset => Task.FromResult<Stream?>(new MemoryStream(data[(int)offset..])), key, plain.Length, start, end, output, CancellationToken.None);
            return output.ToArray();
        }

        await Assert.That(await DecryptAsync(stored, 0, plain.Length - 1)).IsEquivalentTo(plain);

        // Two chunks swapped places: each is authentic on its own, but not at the other's index.
        var stride = BlobCrypto.ChunkSize + BlobCrypto.Overhead;
        var swapped = (byte[])stored.Clone();
        Array.Copy(stored, 0, swapped, stride, stride);
        Array.Copy(stored, stride, swapped, 0, stride);
        await Assert.That(async () => await DecryptAsync(swapped, 0, plain.Length - 1)).Throws<CryptographicException>();

        var truncated = stored[..^500];
        await Assert.That(async () => await DecryptAsync(truncated, 0, plain.Length - 1)).Throws<CryptographicException>();

        // A wrong key is the same failure.
        using var output = new MemoryStream();
        await Assert.That(async () => await BlobCrypto.DecryptRangeAsync(offset => Task.FromResult<Stream?>(new MemoryStream(stored[(int)offset..])), BlobCrypto.NewDataKey(), plain.Length, 0, 10, output, CancellationToken.None))
            .Throws<CryptographicException>();
    }

    [Test]
    public async Task The_quota_is_enforced_and_the_client_is_told_why()
    {
        await using var rig = await StartAsync(quota: 3000);
        var caller = await SignInAsync(rig.Server, "a@example.com");

        var first = Random(2000);
        using var ok = await UploadAsync(rig.Server, caller, first);
        await Assert.That(ok.StatusCode).IsEqualTo(HttpStatusCode.OK);

        var second = Random(2000);
        using var over = await StartUploadAsync(rig.Server, caller, Sha(second), second.Length);
        await Assert.That(over.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That((await JsonAsync(over)).GetProperty("code").GetString()).IsEqualTo("quota");

        // A file already on the account needs no room.
        using var again = await StartUploadAsync(rig.Server, caller, Sha(first), first.Length);
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.OK);

        await using var db = ServerDatabase.Open(rig.Server.ConnectionString);
        await Assert.That(await db.AuditEvents.CountAsync(a => a.AccountId == caller.Account && a.Kind == AuditKinds.Quota)).IsEqualTo(1);
    }

    [Test]
    public async Task A_file_larger_than_the_per_file_limit_is_refused()
    {
        await using var rig = await StartAsync();
        var caller = await SignInAsync(rig.Server, "a@example.com");
        using var response = await StartUploadAsync(rig.Server, caller, Sha([1]), 101L * 1024 * 1024);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That((await JsonAsync(response)).GetProperty("code").GetString()).IsEqualTo("too-large");
    }

    [Test]
    public async Task Another_account_cannot_see_read_or_finish_a_blob()
    {
        await using var rig = await StartAsync();
        var owner = await SignInAsync(rig.Server, "owner@example.com");
        var stranger = await SignInAsync(rig.Server, "stranger@example.com");
        var bytes = Random(5000);
        var sha = Sha(bytes);
        (await UploadAsync(rig.Server, owner, bytes)).EnsureSuccessStatusCode();

        using var head = await SendAsync(rig.Server, stranger, HttpMethod.Head, $"/v1/blobs/{sha}");
        using var get = await SendAsync(rig.Server, stranger, HttpMethod.Get, $"/v1/blobs/{sha}");
        await Assert.That(head.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(get.StatusCode).IsEqualTo(HttpStatusCode.NotFound);

        // The stranger uploads the same bytes on their own: stored apart, so deleting one account never touches the other.
        using var theirs = await UploadAsync(rig.Server, stranger, bytes);
        await Assert.That(theirs.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await using var db = ServerDatabase.Open(rig.Server.ConnectionString);
        await Assert.That(await db.Blobs.CountAsync(b => b.Sha256 == sha)).IsEqualTo(2);

        // And without a token there is nothing.
        using var anonymous = await rig.Server.Factory.CreateClient().GetAsync($"/v1/blobs/{sha}");
        await Assert.That(anonymous.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    private static async Task ReferenceAsync(TestServer server, Guid account, string sha, bool deleted = false)
    {
        await using var db = ServerDatabase.Open(server.ConnectionString);
        var groups = new Dictionary<string, object> { ["file"] = new { hlc = "0", values = new { Sha256 = sha } } };
        if (deleted)
        {
            groups["deleted"] = new { hlc = "0", values = new { DeletedAtUtc = "2026-11-01T00:00:00Z" } };
        }

        db.SyncEntities.Add(new SyncEntityRow { AccountId = account, Kind = "attachment", EntityKey = Guid.NewGuid().ToString(), Groups = JsonSerializer.Serialize(groups), LastSeq = 1, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task A_blob_nothing_references_goes_after_seven_days_and_a_referenced_one_stays()
    {
        await using var rig = await StartAsync();
        var caller = await SignInAsync(rig.Server, "a@example.com");
        var kept = Random(3000);
        var dropped = Random(4000);
        var removedLater = Random(1000);
        foreach (var bytes in new[] { kept, dropped, removedLater })
        {
            (await UploadAsync(rig.Server, caller, bytes)).EnsureSuccessStatusCode();
        }

        await ReferenceAsync(rig.Server, caller.Account, Sha(kept));
        await ReferenceAsync(rig.Server, caller.Account, Sha(removedLater), deleted: true); // a deleted attachment does not count

        async Task<(int Blobs, int Uploads)> SweepAsync()
        {
            await using var scope = rig.Server.Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<BlobService>().SweepAsync(CancellationToken.None);
        }

        await Assert.That((await SweepAsync()).Blobs).IsEqualTo(0);
        rig.Server.Clock.Advance(TimeSpan.FromDays(6));
        await Assert.That((await SweepAsync()).Blobs).IsEqualTo(0);

        // A reference that appears within the grace period saves the blob.
        await ReferenceAsync(rig.Server, caller.Account, Sha(removedLater));
        rig.Server.Clock.Advance(TimeSpan.FromDays(2));
        await Assert.That((await SweepAsync()).Blobs).IsEqualTo(1);

        await using var db = ServerDatabase.Open(rig.Server.ConnectionString);
        var left = await db.Blobs.Select(b => b.Sha256).ToListAsync();
        await Assert.That(left.Order().ToArray()).IsEquivalentTo(new[] { Sha(kept), Sha(removedLater) }.Order().ToArray());
        await Assert.That((await db.Accounts.SingleAsync(a => a.Id == caller.Account)).StorageBytes).IsEqualTo(kept.Length + removedLater.Length);
        await Assert.That(StoredObjects(rig).Length).IsEqualTo(2);
    }

    [Test]
    public async Task An_upload_left_unfinished_is_discarded_after_a_day()
    {
        await using var rig = await StartAsync();
        var caller = await SignInAsync(rig.Server, "a@example.com");
        var bytes = Random(2 * 1024 * 1024);
        var sha = Sha(bytes);
        (await StartUploadAsync(rig.Server, caller, sha, bytes.Length)).EnsureSuccessStatusCode();
        (await PutPartAsync(rig.Server, caller, sha, 0, bytes)).EnsureSuccessStatusCode();

        rig.Server.Clock.Advance(TimeSpan.FromHours(25));
        await using var scope = rig.Server.Factory.Services.CreateAsyncScope();
        var swept = await scope.ServiceProvider.GetRequiredService<BlobService>().SweepAsync(CancellationToken.None);
        await Assert.That(swept.Uploads).IsEqualTo(1);
        // (A day later the test's token has expired, so look at the tables rather than ask the API.)
        await using var db = ServerDatabase.Open(rig.Server.ConnectionString);
        await Assert.That(await db.BlobUploads.CountAsync()).IsEqualTo(0);
        await Assert.That(await db.BlobParts.CountAsync()).IsEqualTo(0);
        await Assert.That(Directory.Exists(Path.Combine(rig.BlobDirectory, "objects", "parts", caller.Account.ToString("N"), sha))).IsFalse();
    }

    [Test]
    public async Task Deleting_the_account_removes_its_blobs_and_objects()
    {
        await using var rig = await StartAsync();
        var caller = await SignInAsync(rig.Server, "a@example.com");
        var other = await SignInAsync(rig.Server, "b@example.com");
        (await UploadAsync(rig.Server, caller, Random(3000))).EnsureSuccessStatusCode();
        var partial = Random(2 * 1024 * 1024);
        (await StartUploadAsync(rig.Server, caller, Sha(partial), partial.Length)).EnsureSuccessStatusCode();
        (await PutPartAsync(rig.Server, caller, Sha(partial), 0, partial)).EnsureSuccessStatusCode();
        (await UploadAsync(rig.Server, other, Random(3000))).EnsureSuccessStatusCode();

        using var delete = await SendAsync(rig.Server, caller, HttpMethod.Delete, "/v1/account", new StringContent("{\"confirmation\":\"DELETE\"}", Encoding.UTF8, "application/json"));
        await Assert.That(delete.StatusCode).IsEqualTo(HttpStatusCode.NoContent);

        await using var db = ServerDatabase.Open(rig.Server.ConnectionString);
        await Assert.That(await db.Blobs.CountAsync(b => b.AccountId == caller.Account)).IsEqualTo(0);
        await Assert.That(await db.BlobUploads.CountAsync(b => b.AccountId == caller.Account)).IsEqualTo(0);
        await Assert.That(Directory.Exists(Path.Combine(rig.BlobDirectory, "objects", "blobs", caller.Account.ToString("N")))).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(rig.BlobDirectory, "objects", "parts", caller.Account.ToString("N")))).IsFalse();
        await Assert.That(await db.Blobs.CountAsync(b => b.AccountId == other.Account)).IsEqualTo(1);
        await Assert.That(StoredObjects(rig).Length).IsEqualTo(1);
    }

    [Test]
    public async Task The_service_is_ready_only_while_the_scanner_and_store_answer()
    {
        await using var rig = await StartAsync();
        using var ready = await rig.Server.Factory.CreateClient().GetAsync("/health/ready");
        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}

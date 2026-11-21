using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Tasks;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Service;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Sync;
using Microsoft.EntityFrameworkCore;

namespace Chaos.Server.Tests.Sync;

/// <summary>A clock the test moves; one per data root, so two devices can disagree about the time.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// The first three seeded ids (profile, Personal space, Inbox) are shared, as two devices restored from the same
/// archive would have; everything after is random, so offline duplicates really are duplicates.
/// </summary>
internal sealed class SharedSeedIds : IIdGenerator
{
    public static readonly Guid SpaceId = new(1, 0, 0, new byte[8]);
    public static readonly Guid InboxId = new(2, 0, 0, new byte[8]);
    public static readonly Guid ProfileId = new(0, 0, 0, new byte[8]);

    private int _seeded;

    public Guid NewId() => _seeded < 3 ? new Guid(_seeded++, 0, 0, new byte[8]) : Guid.NewGuid();
}

/// <summary>Sits between a device and the in-process server so a test can take the network away or lose an answer.</summary>
internal sealed class FlakyNetwork(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private int _dropAnswers;

    public bool Offline { get; set; }

    /// <summary>The next <paramref name="count"/> pushes reach the server, but their answers are lost (the server "restarted" before replying).</summary>
    public void DropNextPushAnswers(int count) => Interlocked.Exchange(ref _dropAnswers, count);

    public int Pushes;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Offline)
        {
            throw new HttpRequestException("The network is down.");
        }

        var response = await base.SendAsync(request, cancellationToken);
        if (request.RequestUri!.AbsolutePath.EndsWith("/sync/push", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref Pushes);
            if (Interlocked.Decrement(ref _dropAnswers) >= 0)
            {
                response.Dispose();
                throw new HttpRequestException("The connection was lost before the answer arrived.");
            }

            Interlocked.Exchange(ref _dropAnswers, 0);
        }

        return response;
    }
}

/// <summary>One desktop: a real data root with real SQLite, a real service client and a real <see cref="SyncEngine"/>.</summary>
internal sealed class SyncDevice : IAsyncDisposable
{
    public required string Root { get; init; }
    public required ChaosStorageHandle Storage { get; init; }
    public required ManualClock Clock { get; init; }
    public required IIdGenerator Ids { get; init; }
    public required ChaosServiceClient Client { get; init; }
    public required SyncEngine Engine { get; init; }
    public required FlakyNetwork Network { get; init; }
    public required TaskCommands Tasks { get; init; }
    public required MemorySecretStore Secrets { get; init; }

    public SqliteApplicationStore Store => Storage.Store;

    /// <summary>
    /// <paramref name="joined"/> false makes a computer that is signed in but has not joined: sync is off and its profile, Personal space and
    /// Inbox IDs are its own random ones, as a computer with its own data would have (the account's join flow then takes over).
    /// </summary>
    public static async Task<SyncDevice> StartAsync(TestServer server, string email, DateTimeOffset start, SyncEngineOptions? options = null, bool joined = true, IIdGenerator? seedIds = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "chaos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var clock = new ManualClock(start);
        IIdGenerator ids = seedIds ?? (joined ? new SharedSeedIds() : new GuidIdGenerator());
        var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.2.1", ids, clock, CancellationToken.None);
        var network = new FlakyNetwork(server.Factory.Server.CreateHandler());
        var secrets = new MemorySecretStore();
        var identity = new SqliteDeviceIdentity(storage.Store, "0.2.1");
        var client = new ChaosServiceClient(
            ChaosServiceOptions.TryCreate("http://localhost/", allowLoopbackHttp: true), FakeBrowser.Person(server, email), secrets, identity, server.Clock, new HttpClient(network))
        {
            SignInTimeout = TimeSpan.FromSeconds(30),
        };
        await client.SignInAsync(CancellationToken.None);
        var engine = new SyncEngine(storage.Store, new SyncApplier(storage.Store, clock, ids), client, clock, options ?? new SyncEngineOptions { Jitter = () => 0 });
        var device = new SyncDevice
        {
            Root = root, Storage = storage, Clock = clock, Ids = ids, Client = client, Engine = engine, Network = network, Secrets = secrets,
            Tasks = new TaskCommands(storage.Store, ids, clock),
        };
        if (joined)
        {
            // The join flow records the account; protocol 2 discovery checks the streams against it.
            await using var db = ServerDatabase.Open(server.ConnectionString);
            var account = await db.Accounts.Where(a => a.PrimaryEmail == email).Select(a => a.Id).SingleAsync();
            await device.ExecuteAsync($"UPDATE SyncState SET Enabled = 1, AccountId = '{account:D}', ProfileBinding = (SELECT Id FROM Profiles WHERE DeletedAtUtc IS NULL ORDER BY CreatedAtUtc LIMIT 1) WHERE Id = 1;");
        }

        return device;
    }

    public Task<SyncCycleResult> SyncAsync() => Engine.SyncNowAsync(CancellationToken.None);

    public Task ExecuteAsync(string sql) =>
        Store.ExecuteAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(token);
            return 0;
        }, CancellationToken.None);

    public async Task<long> CountAsync(string sql) => Convert.ToInt64((await ScalarAsync(sql)) ?? 0L, CultureInfo.InvariantCulture);

    public async Task<string?> TextAsync(string sql) => await ScalarAsync(sql) as string;

    public Task<object?> ScalarAsync(string sql) =>
        Store.QueryAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = sql;
            return await command.ExecuteScalarAsync(token);
        });

    public async Task<List<string>> RowsAsync(string sql)
    {
        var rows = new List<string>();
        await Store.QueryAsync(async (session, token) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var parts = new string[reader.FieldCount];
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    parts[index] = reader.IsDBNull(index) ? "<null>" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty;
                }

                rows.Add(string.Join("|", parts));
            }

            return 0;
        }, CancellationToken.None);
        return rows;
    }

    /// <summary>The synced columns of every table: what must be identical on every device.</summary>
    public async Task<string> SnapshotAsync()
    {
        var lines = new List<string>();
        foreach (var table in SyncCatalog.Ordered().Where(table => table.Mode != SyncMode.LocalOnly))
        {
            var columns = new List<string>(table.Key);
            foreach (var column in table.Groups.SelectMany(group => group.Columns))
            {
                if (!columns.Contains(column))
                {
                    columns.Add(column);
                }
            }

            // sync.merged entries are each device's own record of what a remote change replaced.
            var filter = table.Kind == "task_history" ? " WHERE EventKind <> 'sync.merged'" : string.Empty;
            var sql = $"SELECT {string.Join(", ", columns.Select(column => "\"" + column + "\""))} FROM \"{table.Table}\"{filter} ORDER BY {string.Join(", ", table.Key)};";
            lines.AddRange((await RowsAsync(sql)).Select(row => $"{table.Kind}:{row}"));
        }

        lines.AddRange((await RowsAsync("SELECT Kind, EntityKey, GroupName, Hlc FROM SyncClocks ORDER BY Kind, EntityKey, GroupName;")).Select(row => $"clock:{row}"));
        return string.Join("\n", lines);
    }

    public async ValueTask DisposeAsync()
    {
        Engine.Dispose();
        Client.Dispose();
        Storage.Dispose();
        await Task.CompletedTask;
    }
}

/// <summary>A hosted service on real PostgreSQL with one account and as many desktops as a test adds.</summary>
internal sealed class SyncWorld : IAsyncDisposable
{
    public const string Email = "person@example.com";

    private readonly List<SyncDevice> _devices = [];

    public required TestServer Server { get; init; }

    public static async Task<SyncWorld> StartAsync(PostgresServer postgres, Dictionary<string, string?>? settings = null, Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configure = null) =>
        new() { Server = await TestServer.StartAsync(postgres, settings, configure) };

    public async Task<SyncDevice> AddDeviceAsync(DateTimeOffset? start = null, SyncEngineOptions? options = null, bool joined = true)
    {
        var device = await SyncDevice.StartAsync(Server, Email, start ?? new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero), options, joined);
        _devices.Add(device);
        if (joined && _devices.Count == 1)
        {
            // P14 binds the account's canonical ids on the first device; the tests do it directly.
            await using var db = ServerDatabase.Open(Server.ConnectionString);
            await db.Accounts.Where(a => a.PrimaryEmail == Email).ExecuteUpdateAsync(s => s
                .SetProperty(a => a.ProfileId, SharedSeedIds.ProfileId)
                .SetProperty(a => a.PersonalSpaceId, SharedSeedIds.SpaceId)
                .SetProperty(a => a.InboxId, SharedSeedIds.InboxId));
        }

        return device;
    }

    /// <summary>Syncs every device in turn until a whole round pushes and pulls nothing.</summary>
    public async Task SettleAsync()
    {
        for (var round = 0; round < 8; round++)
        {
            var busy = false;
            foreach (var device in _devices)
            {
                var result = await device.SyncAsync();
                if (result.Problem is not null)
                {
                    throw new InvalidOperationException("A sync cycle failed: " + result.Problem);
                }

                busy |= result.Pushed > 0 || result.Pulled > 0;
            }

            if (!busy)
            {
                return;
            }
        }

        throw new InvalidOperationException("The devices did not settle within eight rounds.");
    }

    public async Task AssertConvergedAsync()
    {
        var first = await _devices[0].SnapshotAsync();
        foreach (var device in _devices.Skip(1))
        {
            var other = await device.SnapshotAsync();
            if (!string.Equals(first, other, StringComparison.Ordinal))
            {
                var directory = Path.Combine(Path.GetTempPath(), "chaos-convergence");
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, "first.txt"), first);
                await File.WriteAllTextAsync(Path.Combine(directory, "other.txt"), other);
                throw new InvalidOperationException("The devices did not converge; both snapshots are in " + directory);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var device in _devices)
        {
            await device.DisposeAsync();
        }

        await Server.DisposeAsync();
    }
}

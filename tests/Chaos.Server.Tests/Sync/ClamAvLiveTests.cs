using System.Text;
using Chaos.Server.Blobs;

namespace Chaos.Server.Tests.Sync;

/// <summary>
/// The clamd client against a real daemon. Opt-in: set <c>CHAOS_TEST_CLAM=host:port</c> (for example a
/// <c>clamav/clamav:1.5.5-debian</c> container on 3310, which needs a minute or two to load its signatures).
/// Without the variable the test returns at once, so a plain run needs no ClamAV.
/// </summary>
public sealed class ClamAvLiveTests
{
    private const string Eicar = @"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*";

    [Test]
    public async Task A_real_clamd_passes_clean_bytes_and_blocks_the_EICAR_string()
    {
        if (Environment.GetEnvironmentVariable("CHAOS_TEST_CLAM") is not { Length: > 0 } address)
        {
            return;
        }

        var parts = address.Split(':');
        var scanner = new ClamAvScanner(parts[0], parts.Length > 1 ? int.Parse(parts[1]) : 3310);
        await scanner.CheckAsync(CancellationToken.None);

        var clean = await scanner.ScanAsync(new MemoryStream(Encoding.ASCII.GetBytes("hello")), CancellationToken.None);
        var infected = await scanner.ScanAsync(new MemoryStream(Encoding.ASCII.GetBytes(Eicar)), CancellationToken.None);

        await Assert.That(clean.Clean).IsTrue();
        await Assert.That(infected.Clean).IsFalse();
        await Assert.That(infected.Signature).Contains("Eicar");
    }
}

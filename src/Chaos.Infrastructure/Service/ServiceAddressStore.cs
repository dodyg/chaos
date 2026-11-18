using System.Text.Json;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Service;

/// <summary>
/// The service address chosen in Settings, kept in a small file in the data root (it is not a secret). It wins over
/// <c>CHAOS_SERVICE_URL</c>, which wins over <c>chaos-service.json</c> beside the exe, so a person never needs either.
/// </summary>
public sealed class ServiceAddressStore(string settingPath, string fallbackJsonPath, bool allowLoopbackHttp) : IServiceAddressSettings
{
    private readonly Lock _gate = new();

    /// <summary>The address to use now; read on every call so a change applies at once.</summary>
    public ChaosServiceOptions? Resolve() => Describe().Options;

    public ServiceAddressInfo Get() => Describe().Info;

    public ServiceAddressResult Set(string? address)
    {
        var typed = address?.Trim() ?? string.Empty;
        lock (_gate)
        {
            if (typed.Length == 0)
            {
                try
                {
                    File.Delete(settingPath);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return new(false, "Chaos could not clear the saved address.", Get());
                }

                return new(true, null, Get());
            }

            var options = ChaosServiceOptions.TryCreate(Normalize(typed), allowLoopbackHttp);
            if (options is null)
            {
                var plainHttp = Normalize(typed).StartsWith("http://", StringComparison.OrdinalIgnoreCase);
                return new(false, allowLoopbackHttp
                    ? "Enter the service's web address, such as https://sync.example.com. Plain http only works for localhost."
                    : plainHttp
                        ? "This version of Chaos only connects over https://. Plain http://localhost is available in development builds."
                        : "Enter the service's web address, starting with https://, such as https://sync.example.com.", Get());
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(settingPath))!);
                var temp = settingPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new { baseUrl = options.BaseAddress.ToString() }));
                File.Move(temp, settingPath, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return new(false, "Chaos could not save the address.", Get());
            }

            return new(true, null, Get());
        }
    }

    /// <summary>Adds the scheme people leave out: http for this computer, https for everything else.</summary>
    internal static string Normalize(string typed)
    {
        if (typed.Contains("://", StringComparison.Ordinal))
        {
            return typed;
        }

        var host = typed.Split('/', '?', '#')[0].Split(':')[0];
        return (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" ? "http://" : "https://") + typed;
    }

    private (ChaosServiceOptions? Options, ServiceAddressInfo Info) Describe()
    {
        var saved = ReadJson(settingPath);
        if (saved is not null)
        {
            return (saved, new(saved.BaseAddress.ToString(), ServiceAddressSource.Setting, saved.BaseAddress.ToString()));
        }

        if (ChaosServiceOptions.TryCreate(Environment.GetEnvironmentVariable(ChaosServiceOptions.EnvironmentVariable), allowLoopbackHttp) is { } fromEnvironment)
        {
            return (fromEnvironment, new(fromEnvironment.BaseAddress.ToString(), ServiceAddressSource.Environment, null));
        }

        var fromFile = ReadJson(fallbackJsonPath);
        return fromFile is null
            ? (null, new(null, ServiceAddressSource.None, null))
            : (fromFile, new(fromFile.BaseAddress.ToString(), ServiceAddressSource.File, null));
    }

    private ChaosServiceOptions? ReadJson(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("baseUrl", out var value) ? ChaosServiceOptions.TryCreate(value.GetString(), allowLoopbackHttp) : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

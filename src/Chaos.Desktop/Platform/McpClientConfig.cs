using System.Text.Json;

namespace Chaos.Desktop.Platform;

/// <summary>
/// The MCP client configuration shown once beside a new token (PLAN3 Q18): the command that starts the <c>--mcp</c> shim and the
/// environment that carries the token, for Claude Code (a <c>claude mcp add</c> line) and Claude Desktop (a
/// <c>claude_desktop_config.json</c> entry). The data root is added only when it is not the default one.
/// </summary>
public sealed class McpClientConfig(string command, IReadOnlyList<string> leadingArguments, string? dataRoot)
{
    public const string ServerName = "chaos";
    public const string TokenVariable = "CHAOS_AGENT_TOKEN";

    /// <summary>The configuration for this running app. A <c>dotnet</c> host (a development run) is started with the app's assembly.</summary>
    public static McpClientConfig ForThisApp(string dataRoot)
    {
        var executable = Environment.ProcessPath ?? "Chaos.Desktop.exe";
        var isDefault = string.Equals(Path.GetFullPath(dataRoot), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chaos"),
            StringComparison.OrdinalIgnoreCase);
        List<string> leading = [];
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            leading.Add(typeof(McpClientConfig).Assembly.Location);
        }

        return new McpClientConfig(executable, leading, isDefault ? null : Path.GetFullPath(dataRoot));
    }

    private IReadOnlyList<string> Arguments()
    {
        List<string> args = [.. leadingArguments, "--mcp"];
        if (dataRoot is not null)
        {
            args.Add("--data-dir");
            args.Add(dataRoot);
        }

        return args;
    }

    /// <summary>A <c>claude mcp add</c> command for PowerShell.</summary>
    public string ClaudeCode(string token) =>
        $"claude mcp add {ServerName} --env {TokenVariable}={token} -- {Quote(command)} {string.Join(' ', Arguments().Select(Quote))}";

    /// <summary>The <c>mcpServers</c> entry for <c>claude_desktop_config.json</c>.</summary>
    public string ClaudeDesktop(string token) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                [ServerName] = new { command, args = Arguments(), env = new Dictionary<string, string> { [TokenVariable] = token } },
            },
        }, new JsonSerializerOptions { WriteIndented = true });

    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}

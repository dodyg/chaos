using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chaos.Server.Web;

namespace Chaos.Server.Tests;

/// <summary>
/// The shape of the JSON the PWA reads and writes (<c>Web*</c> records), kept in <c>src/Chaos.Web/contract/web-dto.json</c>.
/// The PWA's tests check their fixtures against that file, so renaming or retyping a server field fails here until the
/// snapshot is regenerated, and then fails the PWA's tests until its fixtures and code follow. Needs no database.
/// Regenerate with <c>UPDATE_WEB_CONTRACT=1 dotnet test --project tests/Chaos.Server.Tests --treenode-filter "/*/*/WebContractTests/*"</c>.
/// </summary>
public sealed class WebContractTests
{
    private static string SnapshotPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Chaos.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("Chaos.slnx not found above the test output."), "src", "Chaos.Web", "contract", "web-dto.json");
    }

    private static string Describe(Type type, out bool nullable)
    {
        nullable = false;
        if (Nullable.GetUnderlyingType(type) is { } inner)
        {
            nullable = true;
            type = inner;
        }

        if (type == typeof(string))
        {
            return "string";
        }

        if (type == typeof(Guid))
        {
            return "guid";
        }

        if (type == typeof(DateOnly))
        {
            return "date";
        }

        if (type == typeof(TimeOnly))
        {
            return "time";
        }

        if (type == typeof(DateTimeOffset))
        {
            return "datetime";
        }

        if (type == typeof(bool))
        {
            return "boolean";
        }

        if (type == typeof(long) || type == typeof(int) || type == typeof(short))
        {
            return "number";
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            return "list<" + Describe(type.GetGenericArguments()[0], out _) + ">";
        }

        return type.Name;
    }

    private static JsonObject Build()
    {
        var contract = new JsonObject();
        var types = typeof(WebSession).Assembly.GetTypes()
            .Where(type => type.Namespace == "Chaos.Server.Web" && type.Name.StartsWith("Web", StringComparison.Ordinal) && type.GetProperty("EqualityContract", BindingFlags.NonPublic | BindingFlags.Instance) is not null)
            .OrderBy(type => type.Name, StringComparer.Ordinal);
        foreach (var type in types)
        {
            var fields = new JsonObject();
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.GetMethod is not null).OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                var kind = Describe(property.PropertyType, out var nullable);
                var nullableReference = property.PropertyType.IsValueType ? nullable : new NullabilityInfoContext().Create(property).ReadState == NullabilityState.Nullable;
                fields[JsonNamingPolicy.CamelCase.ConvertName(property.Name)] = nullableReference ? kind + "?" : kind;
            }

            contract[type.Name] = fields;
        }

        return contract;
    }

    [Test]
    public async Task The_snapshot_matches_the_server_records()
    {
        var actual = Build().ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n";
        var path = SnapshotPath();
        if (Environment.GetEnvironmentVariable("UPDATE_WEB_CONTRACT") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual);
        }

        await Assert.That(File.Exists(path)).IsTrue();
        var expected = (await File.ReadAllTextAsync(path)).ReplaceLineEndings("\n");
        await Assert.That(actual.ReplaceLineEndings("\n")).IsEqualTo(expected);
    }
}

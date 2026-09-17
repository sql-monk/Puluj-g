using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puluj.Transport.Spike.Tests.Spike;

/// <summary>Записує факти кожного тесту в docs/evidence/message-platform/P02-crash-evidence.json (handoff §16.4).</summary>
public static class Evidence
{
    private static readonly Mutex FileMutex = new(false, @"Global\PulujG.P02Evidence");

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Puluj.sln")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName ?? throw new InvalidOperationException("Puluj.sln not found above " + AppContext.BaseDirectory);
    }

    public static string Path_(string file)
    {
        // Release-gate runs keep generated evidence in their ignored artifact folder.
        // Historical evidence remains the default for dedicated P02 maintenance runs.
        var directory = Environment.GetEnvironmentVariable("PULUJ_TEST_EVIDENCE_DIR");
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(RepoRoot(), "docs", "evidence", "message-platform")
            : directory;
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, file);
    }

    public static void Record(string test, object facts)
    {
        var path = Path_("P02-crash-evidence.json");
        FileMutex.WaitOne();
        try
        {
            var root = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
            root["$comment"] = "Факти з реального прогону spike suite (Testcontainers RabbitMQ); перезаписується кожним прогоном тесту.";
            var node = JsonSerializer.SerializeToNode(facts)!.AsObject();
            node["recorded_at"] = DateTimeOffset.UtcNow;
            root[test] = node;
            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        }
        finally
        {
            FileMutex.ReleaseMutex();
        }
    }
}

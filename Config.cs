using System.Text.Json;

namespace SSHRtm;

public sealed class AppConfig
{
    public int PollIntervalMs { get; init; } = 1000;
    public int RenderIntervalMs { get; init; } = 500;
    public List<ServerConfig> Servers { get; init; } = new();
}

public sealed class ServerConfig
{
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 22;
    public string User { get; init; } = "";
}

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppConfig Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException("Config parsed to null.");
    }

    public const string SampleJson = """
    {
      "pollIntervalMs": 1000,
      "renderIntervalMs": 500,
      "servers": [
        { "name": "web01", "host": "10.0.0.10", "port": 22, "user": "monitor" },
        { "name": "db01",  "host": "10.0.0.11", "port": 22, "user": "monitor" }
      ]
    }
    """;
}

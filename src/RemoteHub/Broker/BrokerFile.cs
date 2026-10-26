using System.Text.Json;
using System.Text.Json.Nodes;
using static RemoteHub.Agent.BrokerProtocol;

namespace RemoteHub.Broker;

/// <summary>What a <c>.rhbroker</c> file gives a console: the broker, the organisation and its console secret.</summary>
public sealed record BrokerFile(string Url, string Org, string Name, string ConsoleSecret)
{
    public const string Extension = ".rhbroker";

    /// <summary>Reads and checks a file the administrator handed out; throws <see cref="BrokerException"/> saying what is wrong.</summary>
    public static BrokerFile Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new BrokerException($"{info.Name} doesn't exist.");
        if (info.Length > 16 * 1024) throw new BrokerException($"{info.Name} isn't a RemoteHub Broker file (too large).");
        return Parse(File.ReadAllText(path), info.Name);
    }

    public static BrokerFile Parse(string text, string name = "The file")
    {
        JsonObject o;
        try { o = JsonNode.Parse(text) as JsonObject ?? throw new BrokerException($"{name} isn't a RemoteHub Broker file."); }
        catch (JsonException) { throw new BrokerException($"{name} isn't a RemoteHub Broker file."); }
        if (o["v"] is not JsonValue v || !v.TryGetValue<int>(out var version) || version != 1)
            throw new BrokerException($"{name} was made for a newer version of RemoteHub.");
        var url = Text(o, "broker");
        if (ParseBrokerUrl(url) is null) throw new BrokerException($"{name} has no valid broker address.");
        var secret = Text(o, "consoleSecret");
        if (ParseSecret(secret) is not { } s || s.Kind != SecretKind.Console) throw new BrokerException($"{name} holds no console secret.");
        var org = s.Org;
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(s.Key);
        if (Text(o, "org") is { Length: > 0 } stated && stated != org) throw new BrokerException($"{name} is damaged: its secret belongs to another organisation.");
        var display = Text(o, "name");
        return new BrokerFile(url, org, display.Length is > 0 and <= 200 ? display : org, secret);
    }

    private static string Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
}

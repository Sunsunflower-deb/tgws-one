using System.Text.Json;
using System.Text.Json.Serialization;

namespace TgwsOne.Core.Zapret;

/// <summary>The closed set of commands the service understands.</summary>
public static class ControlCommands
{
    public const string Status = "status";
    public const string On = "on";
    public const string Off = "off";
    public const string Use = "use";
    public const string Strategies = "strategies";
    public const string Exclusions = "exclusions";
    public const string SetExclusions = "set-exclusions";
}

public sealed record ControlRequest(string Cmd, string? Strategy = null, IReadOnlyList<string>? Hosts = null, IReadOnlyList<string>? Ips = null);

/// <summary>
/// <c>State</c> is one of disabled, starting, running, backoff, error. <c>Auto</c> (per host, sorted) is only
/// there while the strategy is <c>auto</c>.
/// </summary>
public sealed record ZapretStatus(bool Enabled, string Strategy, string State, int? Pid, DateTimeOffset? Since,
                                  int Restarts, string? LastError, string Bundle, IReadOnlyList<AutoHost>? Auto = null);

/// <summary><c>Kind</c> is "builtin" or "custom".</summary>
public sealed record StrategyItem(string Id, string Kind);

/// <summary>One shape for every answer: <c>Ok</c> and, depending on the command, the fields that apply. Errors carry Russian text.</summary>
public sealed record ControlResponse(bool Ok, string? Error = null, ZapretStatus? Status = null,
    IReadOnlyList<StrategyItem>? Items = null, string? Current = null,
    IReadOnlyList<string>? Hosts = null, IReadOnlyList<string>? Ips = null, IReadOnlyList<string>? Invalid = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
internal sealed partial class ControlJson : JsonSerializerContext;

public interface IControlHandler
{
    Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken ct);
}

/// <summary>The wire format: one JSON object per line, UTF-8.</summary>
public static class ControlProtocol
{
    /// <summary>Longest request line (without the newline) the server reads.</summary>
    public const int MaxLineBytes = 64 * 1024;

    public const string InvalidRequest = "неверный запрос";
    public const string UnknownCommand = "неизвестная команда";
    public const string RequestTooLong = "слишком длинный запрос";

    private static readonly HashSet<string> Known =
    [
        ControlCommands.Status, ControlCommands.On, ControlCommands.Off, ControlCommands.Use,
        ControlCommands.Strategies, ControlCommands.Exclusions, ControlCommands.SetExclusions,
    ];

    public static string Serialize(ControlRequest r) => JsonSerializer.Serialize(r, ControlJson.Default.ControlRequest);

    public static string Serialize(ControlResponse r) => JsonSerializer.Serialize(r, ControlJson.Default.ControlResponse);

    /// <summary>
    /// Null (with <paramref name="error"/>) for anything that is not a well-formed request of a known command:
    /// the handler only ever sees commands it knows, with the arguments they need.
    /// </summary>
    public static ControlRequest? ParseRequest(string line, out string? error)
    {
        error = InvalidRequest;
        ControlRequest? r;
        try
        {
            r = JsonSerializer.Deserialize(line, ControlJson.Default.ControlRequest);
        }
        catch (JsonException)
        {
            return null;
        }
        if (r == null || string.IsNullOrEmpty(r.Cmd)) return null;

        if (!Known.Contains(r.Cmd))
        {
            error = UnknownCommand;
            return null;
        }
        bool wellFormed = r.Cmd switch
        {
            ControlCommands.Use => !string.IsNullOrEmpty(r.Strategy),
            ControlCommands.SetExclusions => r.Hosts is { } h && r.Ips is { } i && !h.Contains(null!) && !i.Contains(null!),
            _ => true,
        };
        if (!wellFormed) return null;

        error = null;
        return r;
    }

    /// <summary>A garbled answer becomes an error response, so callers deal with one shape only.</summary>
    public static ControlResponse ParseResponse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize(line, ControlJson.Default.ControlResponse)
                   ?? new ControlResponse(false, "неверный ответ службы");
        }
        catch (JsonException)
        {
            return new ControlResponse(false, "неверный ответ службы");
        }
    }
}

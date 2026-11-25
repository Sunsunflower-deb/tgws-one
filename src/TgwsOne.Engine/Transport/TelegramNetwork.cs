namespace TgwsOne.Engine.Transport;

/// <summary>Where Telegram's data centres are and what their WebSocket endpoints are called.</summary>
public static class TelegramNetwork
{
    public const string WsPath = "/apiws";
    public const string WsPathTest = WsPath + "_test";

    public static readonly IReadOnlyDictionary<int, string> DcDefaultIps = new Dictionary<int, string>
    {
        [1] = "149.154.175.50",
        [2] = "149.154.167.51",
        [3] = "149.154.175.100",
        [4] = "149.154.167.91",
        [5] = "149.154.171.5",
        [203] = "91.105.192.100",
    };

    public static readonly IReadOnlyDictionary<int, string> DcTestIps = new Dictionary<int, string>
    {
        [1] = "149.154.175.10",
        [2] = "149.154.167.40",
        [3] = "149.154.175.117",
    };

    /// <summary>DC 203 (the Amsterdam "other" DC) uses DC 2's WebSocket hosts.</summary>
    public static IReadOnlyList<string> WsDomains(int dc, bool isMedia)
    {
        if (dc == 203) dc = 2;
        return isMedia
            ? [$"kws{dc}-1.web.telegram.org", $"kws{dc}.web.telegram.org"]
            : [$"kws{dc}.web.telegram.org"];
    }
}

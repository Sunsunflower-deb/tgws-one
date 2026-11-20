using TgwsOne.Core.Zapret;

namespace TgwsOne.Tests;

/// <summary>
/// The bypass service as far as the UI can tell: it keeps a state, answers the seven commands, and can be told to refuse. An entry
/// containing <c>!</c> is "invalid" (the real service has its own rules; this is only enough to exercise the refusal path).
/// </summary>
internal sealed class FakeBypassService : IControlClient
{
    private readonly List<ControlRequest> _sent = [];

    public bool Unavailable { get; set; }
    public bool Enabled { get; set; } = true;
    public string Strategy { get; set; } = "auto";
    public string State { get; set; } = "running";
    public string? LastError { get; set; }
    public List<AutoHost> Auto { get; } = [];
    public List<string> StrategyIds { get; } = ["auto", "fake-tls", "split", "split-stealth", "aggressive"];
    public List<string> Hosts { get; } = [];
    public List<string> Ips { get; } = [];

    /// <summary>When set, <c>use</c> is refused with this message.</summary>
    public string? UseRefusal { get; set; }

    /// <summary>When set, <c>on</c> and <c>off</c> are refused with this message.</summary>
    public string? SwitchRefusal { get; set; }

    public IReadOnlyList<ControlRequest> Sent { get { lock (_sent) return [.. _sent]; } }

    public int Count(string cmd) => Sent.Count(r => r.Cmd == cmd);

    public ZapretStatus Current => new(Enabled, Strategy, Enabled ? State : "disabled", Pid: Enabled ? 777 : null, Since: null, Restarts: 0,
                                       LastError, Bundle: "v1.0.7", Auto: Strategy == "auto" ? [.. Auto] : null);

    public Task<ControlResponse> SendAsync(ControlRequest request, CancellationToken ct = default)
    {
        lock (_sent) _sent.Add(request);
        if (Unavailable) throw new ServiceUnavailableException("служба обхода DPI недоступна");
        return Task.FromResult(Handle(request));
    }

    private ControlResponse Handle(ControlRequest r)
    {
        switch (r.Cmd)
        {
            case ControlCommands.Status:
                return new ControlResponse(true, Status: Current);
            case ControlCommands.On or ControlCommands.Off:
                if (SwitchRefusal != null) return new ControlResponse(false, SwitchRefusal, Status: Current);
                Enabled = r.Cmd == ControlCommands.On;
                return new ControlResponse(true, Status: Current);
            case ControlCommands.Use:
                if (UseRefusal != null) return new ControlResponse(false, UseRefusal, Status: Current);
                Strategy = r.Strategy!;
                return new ControlResponse(true, Status: Current);
            case ControlCommands.Strategies:
                return new ControlResponse(true, Items: [.. StrategyIds.Select(id => new StrategyItem(id, id.StartsWith("custom:") ? "custom" : "builtin"))], Current: Strategy);
            case ControlCommands.Exclusions:
                return new ControlResponse(true, Hosts: [.. Hosts], Ips: [.. Ips]);
            case ControlCommands.SetExclusions:
                string[] bad = [.. r.Hosts!.Concat(r.Ips!).Where(e => e.Contains('!'))];
                if (bad.Length > 0) return new ControlResponse(false, "есть неверные записи", Invalid: bad);
                Hosts.Clear();
                Hosts.AddRange(r.Hosts!.Select(h => h.ToLowerInvariant()).Distinct());
                Ips.Clear();
                Ips.AddRange(r.Ips!.Distinct());
                return new ControlResponse(true, Hosts: [.. Hosts], Ips: [.. Ips]);
            default:
                return new ControlResponse(false, "неизвестная команда");
        }
    }
}

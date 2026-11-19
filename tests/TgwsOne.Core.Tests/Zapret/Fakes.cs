using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

internal sealed record RunnerCall(string File, string[] Args, string? Stdin);

/// <summary>Records every command and answers from a script instead of running anything.</summary>
internal sealed class FakeRunner : ICommandRunner
{
    public List<RunnerCall> Calls { get; } = [];
    public Func<RunnerCall, (int Code, string Output)> Respond { get; set; } = _ => (0, "");

    public Task<(int Code, string Output)> RunAsync(string file, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default)
    {
        var call = new RunnerCall(file, [.. args], stdin);
        lock (Calls) Calls.Add(call);
        return Task.FromResult(Respond(call));
    }
}

internal sealed class FakeSysctl : ISysctl
{
    public Dictionary<string, string> Values { get; } = [];
    public List<string> Writes { get; } = [];
    public bool FailWrites { get; set; }

    public string? Read(string key) => Values.TryGetValue(key, out string? v) ? v : null;

    public void Write(string key, string value)
    {
        if (FailWrites) throw new UnauthorizedAccessException("read-only /proc/sys");
        Writes.Add($"{key}={value}");
        Values[key] = value;
    }
}

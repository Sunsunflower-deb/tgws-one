using TgwsOne.Core;
using TgwsOne.Core.Zapret;
using TgwsOne.Services;
using TgwsOne.ViewModels;

namespace TgwsOne.Tests;

/// <summary>Runs nothing: records what it was asked to run and answers as told.</summary>
internal sealed class FakeCommandRunner : ICommandRunner
{
    private readonly List<(string File, string[] Args)> _calls = [];

    public int Code { get; set; }
    public string Output { get; set; } = "";
    public IReadOnlyList<(string File, string[] Args)> Calls { get { lock (_calls) return [.. _calls]; } }

    public Task<(int Code, string Output)> RunAsync(string file, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default)
    {
        lock (_calls) _calls.Add((file, [.. args]));
        return Task.FromResult((Code, Output));
    }
}

/// <summary>An administrator who does as the test says; <see cref="OnSaved"/> and <see cref="OnDeleted"/> are where the service's list of strategies changes.</summary>
internal sealed class FakeElevatedHelper : IElevatedHelper
{
    public List<(string Name, string Text)> Saved { get; } = [];
    public List<string> Deleted { get; } = [];
    public AdminOutcome SaveResult { get; set; } = new(true, "Стратегия сохранена.");
    public AdminOutcome DeleteResult { get; set; } = new(true, "Стратегия удалена.");
    public Action<string, string>? OnSaved { get; set; }
    public Action<string>? OnDeleted { get; set; }

    public Task<AdminOutcome> SaveStrategyAsync(string name, string text, CancellationToken ct = default)
    {
        Saved.Add((name, text));
        if (SaveResult.Ok) OnSaved?.Invoke(name, text);
        return Task.FromResult(SaveResult);
    }

    public Task<AdminOutcome> DeleteStrategyAsync(string name, CancellationToken ct = default)
    {
        Deleted.Add(name);
        if (DeleteResult.Ok) OnDeleted?.Invoke(name);
        return Task.FromResult(DeleteResult);
    }
}

/// <summary>What the strategy editor is made from in the tests: a bundle whose executable is a placeholder path, and fakes for the rest.</summary>
internal sealed class EditorKit : IDisposable
{
    private readonly TempDir _tmp = new();

    public FakeCommandRunner Runner { get; } = new();
    public FakeElevatedHelper Helper { get; } = new();
    public FakeClipboard Clipboard { get; } = new();
    public FakeLauncher Launcher { get; } = new();
    public ZapretContext Context { get; }
    public string ExePath { get; }

    /// <param name="exeExists">False to model a bundle that is not there.</param>
    public EditorKit(bool exeExists = true)
    {
        ExePath = _tmp.Combine("bundle", "nfqws2");
        if (exeExists)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ExePath)!);
            File.WriteAllText(ExePath, "");
        }
        ZapretBundle bundle = ZapretBundle.Locate(OsKind.Linux, _tmp.Path, n => n switch
        {
            "TGWS_ZAPRET_DIR" => _tmp.Combine("bundle"),
            "TGWS_ZAPRET_EXE" => ExePath,
            _ => null,
        });
        Context = new ZapretContext(OsKind.Linux, bundle, new ZapretLists("/srv/cf-ips.txt", "/srv/telegram.txt", "/srv/exclude-hosts.txt", "/srv/exclude-ips.txt"),
                                    "/var/lib/tgwsone", "/etc/tgwsone/strategies");
    }

    public CustomStrategyViewModel Editor(string? name = null, string text = "--lua-desync=multisplit:pos=1\n")
        => new(name, text, Context, Runner, Helper, Clipboard, Launcher);

    public void Dispose() => _tmp.Dispose();
}

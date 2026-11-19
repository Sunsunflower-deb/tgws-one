using System.Diagnostics.CodeAnalysis;

namespace TgwsOne.Core.Zapret;

/// <summary>
/// The service's brain: it owns the persisted state (on/off, strategy), turns it into a command line for the
/// <see cref="ZapretRunner"/>, and answers the control commands. It is only ever called by one request at a time.
/// </summary>
public sealed class ZapretService : IControlHandler, IAsyncDisposable
{
    private const string CustomPrefix = "custom:";

    private readonly OsKind _os;
    private readonly ServicePaths _paths;
    private readonly ZapretBundle _bundle;
    private readonly string _appListsDir;
    private readonly ZapretRunner _runner;
    private readonly AppLog _log;
    private readonly Func<string, UnixFileMeta?>? _stat;
    private readonly string? _debugFile;

    private ServiceState _state = new(true, BuiltInStrategies.Auto);
    // Why the bypass is wanted but the runner never got to start it (a missing bundle, a strategy that can't be built).
    private string? _applyError;

    /// <param name="stat">Replaces the file-ownership lookup for custom strategies (tests).</param>
    /// <param name="debugFile">When set, nfqws2/winws2 gets <c>--debug=@file</c>.</param>
    public ZapretService(OsKind os, ServicePaths paths, ZapretBundle bundle, string appListsDir, ZapretRunner runner, AppLog log,
                         Func<string, UnixFileMeta?>? stat = null, string? debugFile = null)
    {
        _os = os;
        _paths = paths;
        _bundle = bundle;
        _appListsDir = appListsDir;
        _runner = runner;
        _log = log;
        _stat = stat;
        _debugFile = debugFile;
    }

    /// <summary>Seeds the lists, loads the saved state and, if the bypass is on, starts it. A failed start is a state, not an exception.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _paths.SeedTargetLists(_appListsDir);
        _paths.EnsureExclusionFiles();
        Directory.CreateDirectory(_paths.StateDir);   // nfqws2 creates its --writable folder, but not the folder above it

        _state = ServiceState.Load(_paths.StateIni);
        _log.Write($"служба: состояние — {(_state.Enabled ? "включено" : "выключено")}, стратегия {_state.Strategy}");
        if (!_state.Enabled) return;

        if (BuildArgv(_state.Strategy).Argv == null)
        {
            _log.Write($"служба: сохранённая стратегия {_state.Strategy} недоступна, используется {BuiltInStrategies.Auto}");
            _state = _state with { Strategy = BuiltInStrategies.Auto };
            TrySave(out _);
        }
        string? error = await ApplyStateAsync(ct);
        if (error != null) _log.Write($"служба: запуск не удался: {error}");
    }

    public async Task<ControlResponse> HandleAsync(ControlRequest request, CancellationToken ct)
    {
        switch (request.Cmd)
        {
            case ControlCommands.Status:
                return Ok();
            case ControlCommands.On:
                return await OnAsync(ct);
            case ControlCommands.Off:
                return await OffAsync();
            case ControlCommands.Use:
                return await UseAsync(request.Strategy!, ct);
            case ControlCommands.Strategies:
                return ListStrategies();
            case ControlCommands.Exclusions:
                return new ControlResponse(true,
                    Hosts: Exclusions.Read(_paths.Lists.ExcludeHosts), Ips: Exclusions.Read(_paths.Lists.ExcludeIps));
            case ControlCommands.SetExclusions:
                return await SetExclusionsAsync(request.Hosts!, request.Ips!);
            default:
                return new ControlResponse(false, ControlProtocol.UnknownCommand);
        }
    }

    public ValueTask DisposeAsync() => _runner.DisposeAsync();

    private async Task<ControlResponse> OnAsync(CancellationToken ct)
    {
        // Already up (or on its way, or backing off): nothing to do. Only a failed or stopped run is started again.
        bool active = _runner.State is RunnerState.Starting or RunnerState.Running or RunnerState.Backoff;
        _state = _state with { Enabled = true };
        if (!TrySave(out string? saveError)) return Fail(saveError);
        if (active) return Ok();

        string? error = await ApplyStateAsync(ct);
        return error == null ? Ok() : Fail(error);
    }

    private async Task<ControlResponse> OffAsync()
    {
        _state = _state with { Enabled = false };
        if (!TrySave(out string? saveError)) return Fail(saveError);
        await _runner.StopAsync();
        _applyError = null;
        _log.Write("служба: выключено");
        return Ok();
    }

    private async Task<ControlResponse> UseAsync(string id, CancellationToken ct)
    {
        (IReadOnlyList<string>? argv, string? buildError) = BuildArgv(id);
        if (argv == null) return Fail(buildError!);

        if (_state.Enabled)
        {
            if (!_bundle.Exists) return Fail(MissingBundle());
            string? error = await _runner.ApplyAsync(argv, ct);
            if (error != null) return Fail($"стратегия не прошла проверку: {error}");
            _applyError = null;
            ClearAutoState();
        }

        _state = _state with { Strategy = id };
        if (!TrySave(out string? saveError)) return Fail(saveError);
        _log.Write($"служба: стратегия {id}");
        return Ok();
    }

    private ControlResponse ListStrategies()
    {
        var items = new List<StrategyItem>(BuiltInStrategies.Ids.Select(id => new StrategyItem(id, "builtin")));
        items.AddRange(CustomStrategies.List(_paths.StrategiesDir, _os, _stat).Select(n => new StrategyItem(CustomPrefix + n, "custom")));
        return new ControlResponse(true, Items: items, Current: _state.Strategy);
    }

    private async Task<ControlResponse> SetExclusionsAsync(IReadOnlyList<string> hosts, IReadOnlyList<string> ips)
    {
        (IReadOnlyList<string> validHosts, IReadOnlyList<string> badHosts) = Exclusions.NormalizeHosts(hosts);
        (IReadOnlyList<string> validIps, IReadOnlyList<string> badIps) = Exclusions.NormalizeIps(ips);
        if (badHosts.Count + badIps.Count > 0)
            return new ControlResponse(false, "есть неверные записи", Invalid: [.. badHosts, .. badIps]);

        try
        {
            Exclusions.Write(_paths.Lists.ExcludeHosts, validHosts, ServicePaths.ExclusionsHeader);
            Exclusions.Write(_paths.Lists.ExcludeIps, validIps, ServicePaths.ExclusionsHeader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail($"не удалось записать исключения: {ex.Message}");
        }
        _log.Write($"служба: исключения обновлены — узлов {validHosts.Count}, адресов {validIps.Count}");
        await _runner.ReloadListsAsync();
        return new ControlResponse(true, Hosts: validHosts, Ips: validIps);
    }

    // Builds the command line for the saved strategy and hands it to the runner. Null when it is running.
    private async Task<string?> ApplyStateAsync(CancellationToken ct)
    {
        _applyError = null;
        if (!_bundle.Exists)
        {
            _applyError = MissingBundle();
            return _applyError;
        }
        (IReadOnlyList<string>? argv, string? error) = BuildArgv(_state.Strategy);
        if (argv == null)
        {
            _applyError = error;
            return error;
        }
        string? failure = await _runner.ApplyAsync(argv, ct);
        if (failure == null) ClearAutoState();
        return failure;
    }

    // A new run starts with a clean slate: what the previous one picked per host must not be shown as current.
    // (The script only rewrites the file when its content changes, so a stale one would otherwise linger.)
    private void ClearAutoState()
    {
        try { File.Delete(_paths.AutoStateFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* nothing to clear, or not ours to */ }
    }

    private string MissingBundle() => $"пакет zapret2 не найден: {_bundle.Exe}";

    // A strategy ID becomes a command line. A custom one that is gone is "unknown"; one that exists but is
    // refused (not root's, forbidden option) says why, so the administrator can fix the file.
    private (IReadOnlyList<string>? Argv, string? Error) BuildArgv(string id)
    {
        DesyncPart? custom = null;
        if (id.StartsWith(CustomPrefix, StringComparison.Ordinal))
        {
            string name = id[CustomPrefix.Length..];
            if (!CustomStrategies.IsValidName(name) || !File.Exists(Path.Combine(_paths.StrategiesDir, name + ".args")))
                return (null, "неизвестная стратегия");
            (custom, string? loadError) = CustomStrategies.Load(_paths.StrategiesDir, name, _os, _stat);
            if (custom == null) return (null, $"стратегия не прошла проверку: {loadError}");
        }
        else if (!BuiltInStrategies.IsBuiltIn(id))
        {
            return (null, "неизвестная стратегия");
        }
        return (ZapretArgs.Build(_os, _bundle, _paths.Lists, id, _paths.StateDir, custom, _debugFile), null);
    }

    private ControlResponse Ok() => new(true, Status: BuildStatus());

    private ControlResponse Fail(string error) => new(false, error, Status: BuildStatus());

    private ZapretStatus BuildStatus()
    {
        RunnerState runner = _runner.State;
        string? lastError = _runner.LastError;
        string state;
        if (!_state.Enabled) state = "disabled";
        else if (runner == RunnerState.Disabled && _applyError != null) state = "error";
        else state = runner.ToString().ToLowerInvariant();
        if (state == "error") lastError ??= _applyError;

        IReadOnlyList<AutoHost>? auto = _state.Strategy == BuiltInStrategies.Auto ? AutoState.Read(_paths.AutoStateFile) : null;
        return new ZapretStatus(_state.Enabled, _state.Strategy, state, _runner.Pid, _runner.Since,
            _runner.Restarts, lastError, _bundle.Version, auto);
    }

    private bool TrySave([NotNullWhen(false)] out string? error)
    {
        try
        {
            _state.Save(_paths.StateIni);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write($"служба: не удалось сохранить состояние: {ex.Message}");
            error = $"не удалось сохранить состояние: {ex.Message}";
            return false;
        }
    }
}

using TgwsOne.Core;
using TgwsOne.Engine.Desync;

namespace TgwsOne.Mobile;

/// <summary>
/// The finder's handle on the engine's desync controller. Selecting a strategy here goes through
/// <see cref="MobileEngineHost.Desync"/>, so the engine drops its idle pools exactly as it does for a user's choice.
/// The store is only read: the finder leaves a temporary strategy behind only until <see cref="RestoreAsync"/>.
/// </summary>
public sealed class MobileDesyncControl(MobileEngineHost host, MobileStorage storage) : IDesyncControl
{
    public IReadOnlyList<DesyncStrategy> Rotation
    {
        get
        {
            if (host.Desync is DesyncController live) return live.Rotation;
            return DesyncRotations.ForOperator(AndroidSettingsStore.Load(IniFile.Load(storage.IniPath)).Provider);
        }
    }

    public IReadOnlyList<AutoHost> AutoHosts
        => host.Desync is DesyncController { IsAuto: true } live ? live.AutoHosts : [];

    public Task<FinderBaseline> CaptureAsync(CancellationToken ct = default)
    {
        if (host.Desync is DesyncController live)
        {
            string id = live.Selected?.Id ?? DesyncFinder.AutoId;
            // «Без обхода» is how the finder and the bypass switch turn desync off: it is a selection, but not an active bypass.
            return Task.FromResult(new FinderBaseline(id, id != DesyncFinder.NoBypassId));
        }

        AndroidSettings settings = AndroidSettingsStore.Load(IniFile.Load(storage.IniPath));
        return Task.FromResult(new FinderBaseline(settings.Strategy, false));
    }

    public Task<string?> ApplyAsync(string candidateId, CancellationToken ct = default)
    {
        try
        {
            if (host.Config is null) return Task.FromResult<string?>("движок прокси не запущен");

            DesyncController controller = EnsureController();
            if (candidateId == DesyncFinder.NoBypassId)
            {
                controller.Select(new DesyncStrategy(DesyncFinder.NoBypassId, []));
                return Task.FromResult<string?>(null);
            }
            if (candidateId == DesyncFinder.AutoId)
            {
                controller.UseAuto();
                return Task.FromResult<string?>(null);
            }

            if (!DesyncFinder.TryParseCandidate(candidateId, out string id, out int? ttl))
                return Task.FromResult<string?>($"непонятный кандидат: {candidateId}");

            DesyncStrategy? strategy = Rotation.FirstOrDefault(s => s.Id == id) ?? BuiltInDesync.Find(id);
            if (strategy is null) return Task.FromResult<string?>($"нет такой стратегии: {id}");
            if (ttl is { } value) strategy = DesyncFinder.WithFakeTtl(strategy, value);

            controller.Select(strategy);
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException)
        {
            return Task.FromResult<string?>(ex.Message);
        }
    }

    public Task<string?> RestoreAsync(FinderBaseline baseline, CancellationToken ct = default)
    {
        try
        {
            if (!baseline.Enabled)
            {
                host.Desync = null;
                return Task.FromResult<string?>(null);
            }

            DesyncController controller = EnsureController();
            if (baseline.Strategy == DesyncFinder.AutoId || BuiltInDesync.Find(baseline.Strategy) is not { } strategy)
                controller.UseAuto();
            else
                controller.Select(strategy);

            return Task.FromResult<string?>(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException)
        {
            return Task.FromResult<string?>(ex.Message);
        }
    }

    public DesyncStrategy? Resolve(string sni, string route)
        => host.Desync?.Resolve(sni, route);

    private DesyncController EnsureController()
    {
        if (host.Desync is DesyncController existing) return existing;

        AndroidSettings settings = AndroidSettingsStore.Load(IniFile.Load(storage.IniPath));
        var controller = new DesyncController();
        controller.SetOperator(settings.Provider);
        if (settings.Strategy == DesyncFinder.AutoId || BuiltInDesync.Find(settings.Strategy) is not { } strategy)
            controller.UseAuto();
        else
            controller.Select(strategy);

        host.Desync = controller;
        return controller;
    }
}

using System.Runtime.InteropServices;
using TgwsOne.Engine;
using TgwsOne.Engine.Relay;

namespace TgwsOne;

/// <summary>
/// <c>tgwsone --engine …</c> (or the historical engine argv, where the first flag is <c>--host</c>): the proxy
/// without a window, used by <c>--start</c> when no application is running. The same code the window hosts, so
/// there is exactly one engine implementation — native, no Python runtime, no PyInstaller bootloader.
/// </summary>
internal static class EngineCli
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        string? error = EngineConfigParser.TryParse(args, out EngineConfig config);
        if (error == "help")
        {
            Console.WriteLine(EngineConfigParser.Usage);
            return 0;
        }
        if (error is not null)
        {
            Console.Error.WriteLine("движок: " + error);
            Console.Error.WriteLine(EngineConfigParser.Usage);
            return 2;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void RequestStop() => stopped.TrySetResult();
        using PosixSignalRegistration? term = Register(PosixSignal.SIGTERM, RequestStop);
        using PosixSignalRegistration? interrupt = Register(PosixSignal.SIGINT, RequestStop);
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            RequestStop();
        };
        Console.CancelKeyPress += onCancel;

        try
        {
            await using var engine = new ProxyEngine(config);
            try
            {
                await engine.StartAsync(stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("движок не запустился: " + ex.Message);
                return 1;
            }

            await stopped.Task.WaitAsync(stop.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static PosixSignalRegistration? Register(PosixSignal signal, Action action)
    {
        try
        {
            return PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                action();
            });
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }
}

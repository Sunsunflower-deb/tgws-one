using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace TgwsOne.Core.Zapret;

/// <summary>Runs a program and reports its exit code and output. Injectable so the callers can be tested without root.</summary>
public interface ICommandRunner
{
    /// <summary>Exit code (-1 when the program couldn't run or timed out) and stdout + stderr together.</summary>
    Task<(int Code, string Output)> RunAsync(string file, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default);
}

/// <summary>Real processes. Arguments go through <c>ArgumentList</c>, so no shell sees them.</summary>
public sealed class ProcessCommandRunner(TimeSpan? timeout = null) : ICommandRunner
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(30);

    public async Task<(int Code, string Output)> RunAsync(string file, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            RedirectStandardInput = stdin != null,
        };
        if (stdin != null) psi.StandardInputEncoding = new UTF8Encoding(false);
        foreach (string a in args) psi.ArgumentList.Add(a);

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("процесс не запустился");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, ex.Message);
        }

        using (process)
        {
            var output = new StringBuilder();
            Task pumps = Task.WhenAll(Pump(process.StandardOutput, output), Pump(process.StandardError, output));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);
            try
            {
                if (stdin != null)
                {
                    try
                    {
                        await process.StandardInput.WriteAsync(stdin.AsMemory(), cts.Token);
                        process.StandardInput.Close();
                    }
                    catch (IOException) { /* it exited without reading everything: its exit code says why */ }
                }
                await process.WaitForExitAsync(cts.Token);
                await pumps;
                return (process.ExitCode, output.ToString());
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                if (ct.IsCancellationRequested) throw;
                try { await pumps.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* a surviving child keeps the pipe: take what we have */ }
                lock (output) return (-1, $"{output}\n(тайм-аут: {_timeout.TotalSeconds:0.#} с)");
            }
        }
    }

    private static async Task Pump(StreamReader reader, StringBuilder into)
    {
        var buffer = new char[4096];
        int n;
        while ((n = await reader.ReadAsync(buffer)) > 0)
            lock (into) into.Append(buffer, 0, n);
    }
}

/// <summary>Kernel parameters by their sysctl name.</summary>
public interface ISysctl
{
    /// <summary>The value, or null when the parameter doesn't exist (the kernel module isn't loaded).</summary>
    string? Read(string key);
    void Write(string key, string value);
}

/// <summary><c>net.netfilter.x</c> is the file <c>/proc/sys/net/netfilter/x</c>. The root is overridable for tests.</summary>
public sealed partial class ProcSysctl(string root = "/proc/sys") : ISysctl
{
    [GeneratedRegex(@"^[A-Za-z0-9_-]+(\.[A-Za-z0-9_-]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();

    public string? Read(string key)
    {
        if (!KeyRegex().IsMatch(key)) return null;
        string path = PathOf(key);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    public void Write(string key, string value)
    {
        if (!KeyRegex().IsMatch(key)) throw new ArgumentException($"invalid sysctl key '{key}'", nameof(key));
        File.WriteAllText(PathOf(key), value);
    }

    private string PathOf(string key) => Path.Combine(root, key.Replace('.', Path.DirectorySeparatorChar));
}

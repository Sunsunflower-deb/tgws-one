using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

public class CommandRunnerTests
{
    [LinuxFact]
    public async Task ReturnsTheExitCode_AndBothStreamsCombined()
    {
        var runner = new ProcessCommandRunner();

        (int code, string output) = await runner.RunAsync("/bin/sh", ["-c", "echo to-out; echo to-err >&2; exit 3"]);

        Assert.Equal(3, code);
        Assert.Contains("to-out", output);
        Assert.Contains("to-err", output);
    }

    [LinuxFact]
    public async Task Arguments_AreNotInterpretedByAShell()
    {
        var runner = new ProcessCommandRunner();

        (int code, string output) = await runner.RunAsync("/bin/sh", ["-c", "printf '%s|%s' \"$0\" \"$1\"", "a b; echo injected", "c"]);

        Assert.Equal(0, code);
        Assert.Equal("a b; echo injected|c", output);
    }

    [LinuxFact]
    public async Task Stdin_IsPassedOn()
    {
        var runner = new ProcessCommandRunner();

        (int code, string output) = await runner.RunAsync("/bin/cat", [], stdin: "table inet tgwsone\nмногострочный текст\n");

        Assert.Equal(0, code);
        Assert.Equal("table inet tgwsone\nмногострочный текст\n", output);
    }

    [LinuxFact]
    public async Task Timeout_KillsTheProcess_AndReturnsMinusOne()
    {
        var runner = new ProcessCommandRunner(TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        (int code, string output) = await runner.RunAsync("/bin/sh", ["-c", "echo started; sleep 30"]);

        Assert.Equal(-1, code);
        Assert.True(sw.ElapsedMilliseconds < 5000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.Contains("started", output);
        Assert.Contains("тайм-аут", output);
    }

    [LinuxFact]
    public async Task MissingProgram_IsMinusOneWithAMessage()
    {
        var runner = new ProcessCommandRunner();

        (int code, string output) = await runner.RunAsync("/no/such/program-" + Guid.NewGuid().ToString("N"), []);

        Assert.Equal(-1, code);
        Assert.NotEmpty(output);
    }

    [LinuxFact]
    public async Task Cancellation_KillsTheProcess_AndThrows()
    {
        var runner = new ProcessCommandRunner();
        using var cts = new CancellationTokenSource(200);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync("/bin/sleep", ["30"], ct: cts.Token));
    }

    [Fact]
    public void ProcSysctl_MapsKeysToPaths_AndReadsAndWrites()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Combine("net", "netfilter"));
        File.WriteAllText(tmp.Combine("net", "netfilter", "nf_conntrack_tcp_be_liberal"), "0\n");
        var sysctl = new ProcSysctl(tmp.Path);

        Assert.Equal("0", sysctl.Read("net.netfilter.nf_conntrack_tcp_be_liberal"));
        sysctl.Write("net.netfilter.nf_conntrack_tcp_be_liberal", "1");
        Assert.Equal("1", sysctl.Read("net.netfilter.nf_conntrack_tcp_be_liberal"));
        Assert.Null(sysctl.Read("net.netfilter.no_such_key"));
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("net/netfilter/x")]
    [InlineData("")]
    [InlineData("a..b")]
    public void ProcSysctl_RejectsKeysThatEscapeTheTree(string key)
    {
        using var tmp = new TempDir();
        var sysctl = new ProcSysctl(tmp.Path);

        Assert.Null(sysctl.Read(key));
        Assert.Throws<ArgumentException>(() => sysctl.Write(key, "1"));
    }

    [LinuxFact]
    public void ProcSysctl_ReadsTheRealProc()
        => Assert.Equal("Linux", new ProcSysctl().Read("kernel.ostype"));
}

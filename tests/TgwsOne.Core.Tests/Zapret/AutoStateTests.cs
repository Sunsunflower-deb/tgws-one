using System.Diagnostics;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

public class AutoStateTests
{
    private static IReadOnlyList<AutoHost> ReadText(string text)
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("autostate.tsv");
        File.WriteAllText(file, text);
        return AutoState.Read(file);
    }

    [Fact]
    public void Read_MapsTheNumbersToBuiltInIds_AndSortsByHost()
    {
        IReadOnlyList<AutoHost> hosts = ReadText("c.example.com\t1\t0\nb.example.com\t2\t1\na.example.com\t4\t0\nd.example.com\t3\t2\n");

        Assert.Equal(
        [
            new AutoHost("a.example.com", "aggressive", 0),
            new AutoHost("b.example.com", "split", 1),
            new AutoHost("c.example.com", "fake-tls", 0),
            new AutoHost("d.example.com", "split-stealth", 2),
        ], hosts);
    }

    [Fact]
    public void Read_ToleratesCrLf_AndIpHosts()
        => Assert.Equal([new AutoHost("1.2.3.4", "split", 0), new AutoHost("a.com", "fake-tls", 3)], ReadText("a.com\t1\t3\r\n1.2.3.4\t2\t0\r\n"));

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("only-host\n")]
    [InlineData("h\t1\n")]
    [InlineData("h\t1\t0\textra\n")]
    [InlineData("h\tx\t0\n")]
    [InlineData("h\t0\t0\n")]
    [InlineData("h\t5\t0\n")]
    [InlineData("h\t-1\t0\n")]
    [InlineData("h\t1\t-3\n")]
    [InlineData("h\t1\tabc\n")]
    [InlineData("h\t 1\t0\n")]
    [InlineData("h\t1.0\t0\n")]
    [InlineData("\t1\t0\n")]
    [InlineData("h\t99999999999999999999\t0\n")]
    public void Read_SkipsLinesThatDontFit(string text) => Assert.Empty(ReadText(text));

    [Fact]
    public void Read_KeepsTheGoodLinesAmongBadOnes()
    {
        IReadOnlyList<AutoHost> hosts = ReadText("junk\na.com\t2\t0\nb.com\t9\t0\n\nc.com\t1\tz\nd.com\t3\t1\n");

        Assert.Equal([new AutoHost("a.com", "split", 0), new AutoHost("d.com", "split-stealth", 1)], hosts);
    }

    [Fact]
    public void Read_ADuplicateHost_KeepsTheFirstLine()
        => Assert.Equal([new AutoHost("a.com", "split", 1)], ReadText("a.com\t2\t1\na.com\t4\t0\n"));

    [Fact]
    public void Read_MissingOrUnreadable_IsEmpty()
    {
        using var tmp = new TempDir();

        Assert.Empty(AutoState.Read(tmp.Combine("nope.tsv")));
        Assert.Empty(AutoState.Read(tmp.Path));   // a directory
    }
}

/// <summary>Lets the Lua script tests run where a standalone Lua interpreter is installed (nfqws2 itself needs root).</summary>
internal static class LuaInterpreter
{
    public static string? Path { get; } = Find();

    private static string? Find()
    {
        foreach (string name in new[] { "lua5.4", "lua", "luajit", "lua5.3" })
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = System.IO.Path.Combine(dir, name);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
    }
}

public sealed class LuaFactAttribute : FactAttribute
{
    public LuaFactAttribute()
    {
        if (LuaInterpreter.Path is null || TestRepo.Root is null) Skip = "needs a Lua interpreter (lua5.4/lua/luajit) in PATH";
    }
}

/// <summary>
/// zapret2/tgws-state.lua run outside nfqws2, against a pretend timer API and a pretend autostate table. This checks the
/// script's own logic; that nfqws2 really feeds it the real state is what the namespace check in tests/root verifies.
/// </summary>
public class TgwsStateLuaTests
{
    private sealed record Run(string[] Output, string? Tsv, string[] Files);

    private static Run Execute(string scenario, bool withWritable = true)
    {
        using var tmp = new TempDir();
        string writable = tmp.Combine("writable");
        Directory.CreateDirectory(writable);
        string scenarioFile = tmp.Combine("scenario.lua");
        File.WriteAllText(scenarioFile, scenario);

        var psi = new ProcessStartInfo(LuaInterpreter.Path!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tgws-state-harness.lua"));
        psi.ArgumentList.Add(Path.Combine(TestRepo.Root!, "zapret2", "tgws-state.lua"));
        psi.ArgumentList.Add(scenarioFile);
        if (withWritable) psi.Environment["WRITABLE"] = writable; else psi.Environment.Remove("WRITABLE");

        using Process p = Process.Start(psi)!;
        Task<string> err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"lua failed ({p.ExitCode}): {output}\n{err.Result}");

        string tsv = Path.Combine(writable, AutoState.FileName);
        return new Run(output.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            File.Exists(tsv) ? File.ReadAllText(tsv) : null,
            [.. Directory.GetFiles(writable).Select(Path.GetFileName)!]);
    }

    [LuaFact]
    public void ArmsAFivesecondPeriodicTimer()
    {
        Run r = Execute("return { autostate = {} }");

        Assert.Equal("timer=5000 oneshot=false", r.Output[0]);
    }

    [LuaFact]
    public void WritesSortedRows_OfTheTgwsKeyOnly_WithDefaultsForUnsetFields()
    {
        Run r = Execute("""
            return { autostate = {
              tgws = {
                ["b.example.com"] = { nstrategy = 2, failure_counter = 1 },
                ["a.example.com"] = { nstrategy = 4 },
                ["fresh.example.com"] = {},
                ["1.2.3.4"] = { nstrategy = 3, failure_counter = 2 },
              },
              other = { ["x.example.com"] = { nstrategy = 9 } },
            } }
            """);

        Assert.Equal("1.2.3.4\t3\t2\na.example.com\t4\t0\nb.example.com\t2\t1\nfresh.example.com\t1\t0\n", r.Tsv);
        Assert.Equal(["autostate.tsv"], r.Files);   // the temporary file was renamed away
        Assert.Equal(
        [
            new AutoHost("1.2.3.4", "split-stealth", 2), new AutoHost("a.example.com", "aggressive", 0),
            new AutoHost("b.example.com", "split", 1), new AutoHost("fresh.example.com", "fake-tls", 0),
        ], ParseWithAutoState(r.Tsv!));
    }

    [LuaFact]
    public void SkipsHostKeys_ThatCouldForgeRows()
    {
        Run r = Execute("""
            return { autostate = { tgws = {
              ["good.example.com"] = { nstrategy = 2 },
              ["evil\thost\nforged.example.com\t1\t0"] = { nstrategy = 1 },
              ["tab\there.example.com"] = { nstrategy = 1 },
              ["line\nbreak.example.com"] = { nstrategy = 1 },
              ["cr\rhere.example.com"] = { nstrategy = 1 },
              [""] = { nstrategy = 1 },
              [string.rep("a", 254)] = { nstrategy = 1 },
              [42] = { nstrategy = 1 },
            } } }
            """);

        Assert.Equal("good.example.com\t2\t0\n", r.Tsv);
    }

    [LuaFact]
    public void WritesOnlyWhenTheContentChanged()
    {
        Run r = Execute("""
            return {
              autostate = { tgws = { ["a.example.com"] = { nstrategy = 1 } } },
              steps = {
                function() end,
                function() autostate.tgws["a.example.com"].nstrategy = 2 end,
                function() end,
                function() autostate.tgws["b.example.com"] = {} end,
              },
            }
            """);

        Assert.Equal(["tick 1 writes=1", "tick 2 writes=1", "tick 3 writes=2", "tick 4 writes=2", "tick 5 writes=3"], r.Output.Skip(1));
        Assert.Equal("a.example.com\t2\t0\nb.example.com\t1\t0\n", r.Tsv);
    }

    [LuaFact]
    public void NoAutostateYet_WritesAnEmptyFile_ThatClearsAStaleOne()
    {
        Run r = Execute("return { autostate = nil }");

        Assert.Equal("", r.Tsv);
    }

    [LuaFact]
    public void WithoutAWritableFolder_DoesNothing_AndDoesNotFail()
    {
        Run r = Execute("return { autostate = { tgws = { ['a.example.com'] = {} } } }", withWritable: false);

        Assert.Null(r.Tsv);
        Assert.Equal(["tick 1 writes=0"], r.Output.Skip(1));
    }

    [LuaFact]
    public void ABrokenStateTable_NeverRaisesOutOfTheTimerFunction()
    {
        // nfqws2 deletes a timer whose function errors; a garbage record must not be able to cause that.
        Run r = Execute("""
            return { autostate = { tgws = {
              ["a.example.com"] = "not a table",
              ["b.example.com"] = { nstrategy = "two", failure_counter = {} },
              ["c.example.com"] = { nstrategy = 2 },
            } } }
            """);

        Assert.Equal("b.example.com\t1\t0\nc.example.com\t2\t0\n", r.Tsv);   // the garbage is ignored, the rest still written
    }

    [LuaFact]
    public void NumbersThatCantBeFormatted_AreCaughtByTheTimerFunction()
    {
        // string.format("%d", 1e308) raises; the harness exits non-zero if that escaped the timer function.
        Run r = Execute("return { autostate = { tgws = { ['a.example.com'] = { nstrategy = 1e308 } } } }");

        Assert.Null(r.Tsv);
        Assert.Equal("tick 1 writes=0", r.Output[1]);
    }

    private static IReadOnlyList<AutoHost> ParseWithAutoState(string tsv)
    {
        string file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, tsv);
            return AutoState.Read(file);
        }
        finally
        {
            File.Delete(file);
        }
    }
}

using System.Text.RegularExpressions;

namespace TgwsOne.Core.Tests;

public class AppLogTests
{
    [Fact]
    public void Write_AppendsTimestampedLine_AndRaisesEvent()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("logs", "app.log");
        var log = new AppLog(file);
        var events = new List<string>();
        log.Line += events.Add;

        log.Write("привет");

        string[] lines = File.ReadAllLines(file);
        Assert.Single(lines);
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}  привет$"), lines[0]);
        Assert.Single(events);
        Assert.Matches(new Regex(@"^\d{2}:\d{2}:\d{2}  привет$"), events[0]);
    }

    [Fact]
    public void Write_RotatesWhenOverMax()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("app.log");
        var log = new AppLog(file, maxBytes: 200);

        for (int i = 0; i < 100 && !File.Exists(file + ".1"); i++)
            log.Write("строка номер " + i);

        Assert.True(File.Exists(file + ".1"));
        Assert.True(File.Exists(file));
        Assert.True(new FileInfo(file).Length < new FileInfo(file + ".1").Length);
    }

    [Fact]
    public void Write_ConcurrentCallsDoNotThrow()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("app.log");
        var log = new AppLog(file);

        var ex = Record.Exception(() => Parallel.For(0, 200, i => log.Write("line " + i)));

        Assert.Null(ex);
        Assert.Equal(200, File.ReadAllLines(file).Length);
    }

    [Fact]
    public void Write_FailingEventHandler_DoesNotThrow()
    {
        using var tmp = new TempDir();
        var log = new AppLog(tmp.Combine("app.log"));
        log.Line += _ => throw new InvalidOperationException("handler bug");

        Assert.Null(Record.Exception(() => log.Write("x")));
    }
}

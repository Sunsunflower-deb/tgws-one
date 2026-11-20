using System.Text;
using TgwsOne.Core;
using TgwsOne.Services;

namespace TgwsOne.Tests;

public class LogTailTests
{
    private static void Write(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));

    private static void Append(string path, string text) => File.AppendAllText(path, text, new UTF8Encoding(false));

    private static string Numbered(int from, int to) => string.Concat(Enumerable.Range(from, to - from + 1).Select(i => $"строка {i}\n"));

    // ----- opening -----

    [Fact]
    public void ASmallFile_IsShownWhole()
    {
        using var tmp = new TempDir();
        string log = tmp.Combine("a.log");
        Write(log, Numbered(1, 3));
        var tail = new LogTail(log);

        Assert.True(tail.Refresh());

        Assert.Equal(["строка 1", "строка 2", "строка 3"], tail.Lines);
        Assert.Null(tail.Problem);
    }

    [Fact]
    public void ABigFile_ShowsOnlyItsLast500Lines()
    {
        using var tmp = new TempDir();
        string log = tmp.Combine("a.log");
        // 6000 lines of about 100 bytes: over 600 KB, more than the window that is looked at.
        Write(log, string.Concat(Enumerable.Range(1, 6000).Select(i => $"2026-11-20 10:00:00  line {i:D5} {new string('x', 80)}\n")));
        var tail = new LogTail(log);

        tail.Refresh();

        Assert.Equal(500, tail.Lines.Count);
        Assert.StartsWith("2026-11-20 10:00:00  line 05501 ", tail.Lines[0]);
        Assert.StartsWith("2026-11-20 10:00:00  line 06000 ", tail.Lines[^1]);
        Assert.All(tail.Lines, l => Assert.StartsWith("2026-11-20 10:00:00  line ", l));   // no line cut in the middle
    }

    [Fact]
    public void ABigFile_WithFewerLinesThanTheWindow_ShowsThemAll_WithoutCuttingTheFirst()
    {
        using var tmp = new TempDir();
        string log = tmp.Combine("a.log");
        // Long lines: 300 of 2 KB each is 600 KB, so the window starts in the middle of a line, and the half line must not be shown.
        Write(log, string.Concat(Enumerable.Range(1, 300).Select(i => $"#{i:D3} {new string('y', 2000)}\n")));
        var tail = new LogTail(log);

        tail.Refresh();

        Assert.All(tail.Lines, l => Assert.Matches(@"^#\d{3} y{2000}$", l));
        Assert.EndsWith("#300 " + new string('y', 2000), tail.Lines[^1]);
        Assert.InRange(tail.Lines.Count, 200, 300);
    }

    [Fact]
    public void AnEmptyFile_IsNoLines_AndNoProblem()
    {
        using var tmp = new TempDir();
        string log = tmp.Combine("a.log");
        Write(log, "");
        var tail = new LogTail(log);

        tail.Refresh();

        Assert.Empty(tail.Lines);
        Assert.Null(tail.Problem);
    }

    [Fact]
    public void AMissingFile_IsAMessage_NotAnException_AndIsPickedUpWhenItAppears()
    {
        using var tmp = new TempDir();
        string log = tmp.Combine("later.log");
        var tail = new LogTail(log);

        Assert.True(tail.Refresh());
        Assert.NotNull(tail.Problem);
        Assert.Empty(tail.Lines);
        Assert.False(tail.Refresh());   // nothing new: still missing

        Write(log, "первая\n");
        Assert.True(tail.Refresh());

        Assert.Null(tail.Problem);
        Assert.Equal(["первая"], tail.Lines);
    }

    [Fact]
    public void AMissingFolder_IsTheSameMessage()
    {
        using var tmp = new TempDir();
        var tail = new LogTail(tmp.Combine("no", "such", "dir", "a.log"));

        tail.Refresh();

        Assert.NotNull(tail.Problem);
    }

    [Fact]
    public void AFileThatCannotBeRead_IsAMessage_AndReadableAgainOnceItCan()
    {
        if (!OperatingSystem.IsLinux() || Environment.IsPrivilegedProcess) return;   // permissions mean nothing to root
        using var tmp = new TempDir();
        string log = tmp.Combine("a.log");
        Write(log, "секрет\n");
        File.SetUnixFileMode(log, UnixFileMode.None);
        var tail = new LogTail(log);

        tail.Refresh();
        Assert.Equal("Нет доступа к этому журналу.", tail.Problem);
        Assert.Empty(tail.Lines);

        File.SetUnixFileMode(log, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        tail.Refresh();
        Assert.Null(tail.Problem);
        Assert.Equal(["секрет"], tail.Lines);
    }

    // ----- following -----

    [Fact]
    public void AppendedLines_Arrive_AndRefreshSaysSo()
    {
        using var tmp = new TempDir();
        string log = tmp.Combine("a.log");
        Write(log, Numbered(1, 2));
        var tail = new LogTail(log);
        tail.Refresh();
        Assert.False(tail.Refresh());   // nothing new

        Append(log, Numbered(3, 4));

        Assert.True(tail.Refresh());
        Assert.Equal(["строка 1", "строка 2", "строка 3", "строка 4"], tail.Lines);
        Assert.False(tail.Refresh());
    }

    [Fact]
    public void ALineIsWithheld_UntilItsNewlineArrives()
    {
        using var tmp = new TempDir();
        string log = tmp.Combine("a.log");
        Write(log, "готовая\nполовин");
        var tail = new LogTail(log);
        tail.Refresh();
        Assert.Equal(["готовая"], tail.Lines);   // the half line is not a line yet

        Append(log, "а строки\n");
        tail.Refresh();

        Assert.Equal(["готовая", "половина строки"], tail.Lines);   // not "половин" and then "а строки"
    }

    [Fact]
    public void ACharacterSplitBetweenTwoReads_IsNotBroken()
    {
        using var tmp = new TempDir();
        string log = tmp.Combine("a.log");
        byte[] all = new UTF8Encoding(false).GetBytes("привет\n");
        File.WriteAllBytes(log, all[..3]);   // one and a half Cyrillic letters
        var tail = new LogTail(log);
        tail.Refresh();

        using (var f = new FileStream(log, FileMode.Append)) f.Write(all[3..]);
        tail.Refresh();

        Assert.Equal(["привет"], tail.Lines);
    }

    [Fact]
    public void WindowsLineEndings_AreHandled()
    {
        using var tmp = new TempDir();
        string log = tmp.Combine("a.log");
        Write(log, "раз\r\nдва\r\n");
        var tail = new LogTail(log);

        tail.Refresh();
        Append(log, "три\r\n");
        tail.Refresh();

        Assert.Equal(["раз", "два", "три"], tail.Lines);
    }

    [Fact]
    public void TheWindow_DropsTheOldestLines_WhenNewOnesPushThemOut()
    {
        using var tmp = new TempDir();
        string log = tmp.Combine("a.log");
        Write(log, Numbered(1, 5));
        var tail = new LogTail(log, maxLines: 5);
        tail.Refresh();

        Append(log, Numbered(6, 8));
        tail.Refresh();

        Assert.Equal(["строка 4", "строка 5", "строка 6", "строка 7", "строка 8"], tail.Lines);
    }

    [Fact]
    public void WhenOnlyTheWindowMoves_RefreshStillReportsAChange()
    {
        // The count is the same before and after, only the contents differ.
        using var tmp = new TempDir();
        string log = tmp.Combine("a.log");
        Write(log, Numbered(1, 3));
        var tail = new LogTail(log, maxLines: 3);
        tail.Refresh();

        Append(log, Numbered(4, 4));

        Assert.True(tail.Refresh());
        Assert.Equal(["строка 2", "строка 3", "строка 4"], tail.Lines);
    }

    // ----- a file that is replaced -----

    [Fact]
    public void ARotatedFile_IsReadAgainFromItsStart()
    {
        using var tmp = new TempDir();
        string log = tmp.Combine("a.log");
        Write(log, Numbered(1, 50));
        var tail = new LogTail(log);
        tail.Refresh();

        File.Move(log, log + ".1");
        Write(log, "новый журнал\n");
        tail.Refresh();

        Assert.Equal(["новый журнал"], tail.Lines);
    }

    [Fact]
    public void ARotatedFile_ThatIsAlreadyLongerThanTheOldOne_IsStillNoticed()
    {
        // Between two looks the old file went away and the new one grew past the old read position: the length alone says "appended".
        using var tmp = new TempDir();
        string log = tmp.Combine("a.log");
        Write(log, Numbered(1, 3));
        var tail = new LogTail(log);
        tail.Refresh();

        File.Move(log, log + ".1");
        Write(log, "совсем другое начало\n" + Numbered(100, 160));
        tail.Refresh();

        Assert.Equal("совсем другое начало", tail.Lines[0]);
        Assert.Equal("строка 160", tail.Lines[^1]);
        Assert.DoesNotContain("строка 1", tail.Lines);   // nothing of the old file is left over
    }

    [Fact]
    public void ATruncatedFile_StartsOver()
    {
        using var tmp = new TempDir();
        string log = tmp.Combine("a.log");
        Write(log, Numbered(1, 20));
        var tail = new LogTail(log);
        tail.Refresh();

        Write(log, "после очистки\n");
        tail.Refresh();

        Assert.Equal(["после очистки"], tail.Lines);
    }

    [Fact]
    public void AFileThatIsDeleted_BecomesAMessage_AndTheOldLinesGo()
    {
        using var tmp = new TempDir();
        string log = tmp.Combine("a.log");
        Write(log, Numbered(1, 3));
        var tail = new LogTail(log);
        tail.Refresh();

        File.Delete(log);
        Assert.True(tail.Refresh());

        Assert.NotNull(tail.Problem);
        Assert.Empty(tail.Lines);
    }

    [Fact]
    public void TheRealAppLog_RotatingUnderneath_IsFollowedExactly()
    {
        // AppLog renames the file to .1 and starts a new one when it grows past its limit: the tail must always show what is on disk now.
        using var tmp = new TempDir();
        string path = tmp.Combine("logs", "app.log");
        var log = new AppLog(path, maxBytes: 1500);
        var tail = new LogTail(path);

        for (int i = 1; i <= 120; i++)
        {
            log.Write($"сообщение номер {i}");
            if (i % 7 == 0 || i > 100) tail.Refresh();   // looked at now and then, not after every write
        }
        tail.Refresh();

        Assert.Equal(File.ReadAllLines(path), tail.Lines);
        Assert.True(File.Exists(path + ".1"), "the test did not rotate anything");
        Assert.EndsWith("сообщение номер 120", tail.Lines[^1]);
    }
}

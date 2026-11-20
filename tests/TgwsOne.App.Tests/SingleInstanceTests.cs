using System.Net.Sockets;
using System.Text;
using TgwsOne.Services;

namespace TgwsOne.Tests;

/// <summary>The lock decides who is the application; the socket carries «show» to it. Real files and sockets in a temp directory.</summary>
public class SingleInstanceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Sock(TempDir tmp) => Path.Combine(tmp.Path, "run", "app.sock");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime limit = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > limit) throw new TimeoutException("the condition never became true");
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public void TheFirstLaunch_IsTheApplication_AndMakesItsDirectory()
    {
        using var tmp = new TempDir();

        using SingleInstance first = SingleInstance.Acquire(Sock(tmp), SingleInstance.ShowMessage);

        Assert.Equal(InstanceRole.First, first.Role);
        Assert.True(File.Exists(Sock(tmp)));
    }

    [Fact]
    public async Task ASecondLaunch_TellsTheFirstToShow_AndHasNothingToDo()
    {
        using var tmp = new TempDir();
        using SingleInstance first = SingleInstance.Acquire(Sock(tmp), SingleInstance.ShowMessage);
        int shows = 0;
        first.ShowRequested += () => Interlocked.Increment(ref shows);

        using SingleInstance second = SingleInstance.Acquire(Sock(tmp), SingleInstance.ShowMessage);

        Assert.Equal(InstanceRole.Second, second.Role);
        await WaitUntilAsync(() => Volatile.Read(ref shows) == 1);
    }

    [Fact]
    public async Task EveryLaterLaunch_AsksAgain()
    {
        using var tmp = new TempDir();
        using SingleInstance first = SingleInstance.Acquire(Sock(tmp), SingleInstance.ShowMessage);
        int shows = 0;
        first.ShowRequested += () => Interlocked.Increment(ref shows);

        for (int i = 0; i < 3; i++)
        {
            using SingleInstance later = SingleInstance.Acquire(Sock(tmp), SingleInstance.ShowMessage);
            Assert.Equal(InstanceRole.Second, later.Role);
        }

        await WaitUntilAsync(() => Volatile.Read(ref shows) == 3);
    }

    [Fact]
    public async Task AQuietLaunch_IsStillTurnedAway_ButTheWindowStaysAsItWas()
    {
        // The autostart's --tray start while the application already runs: nothing to start, and no window to pop up.
        using var tmp = new TempDir();
        using SingleInstance first = SingleInstance.Acquire(Sock(tmp), SingleInstance.ShowMessage);
        int shows = 0;
        first.ShowRequested += () => Interlocked.Increment(ref shows);

        using SingleInstance quiet = SingleInstance.Acquire(Sock(tmp), signal: null);
        await Task.Delay(300, Ct);

        Assert.Equal(InstanceRole.Second, quiet.Role);
        Assert.Equal(0, Volatile.Read(ref shows));
    }

    [Fact]
    public void WhenTheFirstIsGone_TheNextLaunchIsTheApplication()
    {
        using var tmp = new TempDir();
        SingleInstance first = SingleInstance.Acquire(Sock(tmp), SingleInstance.ShowMessage);
        first.Dispose();

        using SingleInstance next = SingleInstance.Acquire(Sock(tmp), SingleInstance.ShowMessage);

        Assert.Equal(InstanceRole.First, next.Role);
    }

    [Fact]
    public void ASocketLeftByACrashedInstance_DoesNotStopTheNextOne()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.GetDirectoryName(Sock(tmp))!);
        File.WriteAllText(Sock(tmp), "");   // what a killed process leaves: a path that nobody listens on, and no lock

        using SingleInstance next = SingleInstance.Acquire(Sock(tmp), SingleInstance.ShowMessage);

        Assert.Equal(InstanceRole.First, next.Role);
    }

    [Fact]
    public async Task OnlyTheShowWordIsObeyed_AndASilentClientDoesNotBlockTheNext()
    {
        using var tmp = new TempDir();
        using SingleInstance first = SingleInstance.Acquire(Sock(tmp), SingleInstance.ShowMessage);
        int shows = 0;
        first.ShowRequested += () => Interlocked.Increment(ref shows);

        using var silent = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        silent.Connect(new UnixDomainSocketEndPoint(Sock(tmp)));   // connects and never says a word
        using var rude = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        rude.Connect(new UnixDomainSocketEndPoint(Sock(tmp)));
        rude.Send(Encoding.UTF8.GetBytes("quit\n"));
        using var flood = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        flood.Connect(new UnixDomainSocketEndPoint(Sock(tmp)));
        flood.Send(new byte[4096]);   // a line that never ends

        using SingleInstance second = SingleInstance.Acquire(Sock(tmp), SingleInstance.ShowMessage);

        await WaitUntilAsync(() => Volatile.Read(ref shows) == 1);
        await Task.Delay(200, Ct);
        Assert.Equal(1, Volatile.Read(ref shows));   // «quit» and the flood were not taken for «show»
    }

    [Fact]
    public void ASocketPathTooLongForTheSystem_LeavesTheAppRunning_Unguarded()
    {
        using var tmp = new TempDir();
        string longPath = Path.Combine(tmp.Path, new string('d', 120), "app.sock");
        var logged = new List<string>();

        using SingleInstance instance = SingleInstance.Acquire(longPath, SingleInstance.ShowMessage, logged.Add);

        Assert.Equal(InstanceRole.Unavailable, instance.Role);
        Assert.Single(logged);
        Assert.Contains("не настроена", logged[0]);
    }

    [Fact]
    public void ADirectoryThatCannotBeWritten_LeavesTheAppRunning_Unguarded()
    {
        using var tmp = new TempDir();
        string blocker = Path.Combine(tmp.Path, "file");
        File.WriteAllText(blocker, "not a directory");

        using SingleInstance instance = SingleInstance.Acquire(Path.Combine(blocker, "run", "app.sock"), SingleInstance.ShowMessage);

        Assert.Equal(InstanceRole.Unavailable, instance.Role);
    }

    [Fact]
    public async Task TwoLaunchesAtOnce_ExactlyOneBecomesTheApplication()
    {
        using var tmp = new TempDir();
        var results = new List<SingleInstance>();
        var gate = new ManualResetEventSlim();

        Task[] launches = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            SingleInstance instance = SingleInstance.Acquire(Sock(tmp), signal: null);
            lock (results) results.Add(instance);
        }, Ct)).ToArray();
        gate.Set();
        await Task.WhenAll(launches);

        try
        {
            Assert.Equal(1, results.Count(r => r.Role == InstanceRole.First));
            Assert.Equal(5, results.Count(r => r.Role == InstanceRole.Second));
        }
        finally
        {
            foreach (SingleInstance r in results) r.Dispose();
        }
    }

    [Fact]
    public void TheFirstInstanceOwnsTheSocketFile_AndRemovesItWhenItEnds()
    {
        using var tmp = new TempDir();
        SingleInstance first = SingleInstance.Acquire(Sock(tmp), SingleInstance.ShowMessage);
        Assert.True(File.Exists(Sock(tmp)));

        first.Dispose();

        Assert.False(File.Exists(Sock(tmp)));
    }
}

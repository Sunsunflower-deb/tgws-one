// A stand-in for winws2/nfqws2, used by the tests. Behaviour is driven by environment variables (the runner passes
// its own environment on to the child it starts) and by the arguments it is given:
//   FAKEZ_CTL    control file of key=value lines, re-read on every start:
//                  dryrun_fail=1       --dry-run prints "bad option" and exits 1
//                  crash_after_ms=N    a real run exits with code 2 after N ms
//                  single_instance=1   like winws2 on Windows: a second copy with the same filter is refused, even as --dry-run
//                                      ("A copy of winws2 is already running with the same filter", exit 1); a real run holds
//                                      the lock <FAKEZ_CTL>.instance until it ends
//   FAKEZ_ARGS   a real run writes its argv here, one argument per line
//   FAKEZ_HUP    every SIGHUP appends a line "HUP"
//   --die-at-start in the argv: a real run exits with code 3 right after writing its argv
using System.Runtime.InteropServices;

string? Ctl(string key)
{
    string? file = Environment.GetEnvironmentVariable("FAKEZ_CTL");
    if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
    foreach (string line in File.ReadAllLines(file))
    {
        int eq = line.IndexOf('=');
        if (eq > 0 && line[..eq].Trim() == key) return line[(eq + 1)..].Trim();
    }
    return null;
}

string instanceLock = (Environment.GetEnvironmentVariable("FAKEZ_CTL") ?? "fakez") + ".instance";

FileStream? TakeInstanceLock()
{
    try { return new FileStream(instanceLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
    catch (IOException) { return null; }
}

if (args.Contains("--dry-run"))
{
    if (Ctl("single_instance") == "1")
    {
        using FileStream? free = TakeInstanceLock();
        if (free == null)
        {
            Console.Error.WriteLine("A copy of winws2 is already running with the same filter");
            return 1;
        }
    }
    if (Ctl("dryrun_fail") == "1")
    {
        Console.Error.WriteLine("bad option");
        return 1;
    }
    Console.WriteLine("command line parameters verified");
    return 0;
}

string? argsFile = Environment.GetEnvironmentVariable("FAKEZ_ARGS");
if (!string.IsNullOrEmpty(argsFile)) File.WriteAllLines(argsFile, args);

if (args.Contains("--die-at-start"))
{
    Console.Error.WriteLine("fake zapret: dying at start as asked");
    return 3;
}

using FileStream? instance = Ctl("single_instance") == "1" ? TakeInstanceLock() : null;
if (Ctl("single_instance") == "1" && instance == null)
{
    Console.Error.WriteLine("A copy of winws2 is already running with the same filter");
    return 1;
}

string? hupFile = Environment.GetEnvironmentVariable("FAKEZ_HUP");
using PosixSignalRegistration hup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
{
    context.Cancel = true;
    if (!string.IsNullOrEmpty(hupFile)) File.AppendAllText(hupFile, "HUP\n");
});

Console.WriteLine("fake zapret running");
Console.Out.Flush();

if (int.TryParse(Ctl("crash_after_ms"), out int crashAfter) && crashAfter > 0)
{
    Thread.Sleep(crashAfter);
    Console.Error.WriteLine("fake zapret: crashing as asked");
    return 2;
}

Thread.Sleep(Timeout.Infinite);
return 0;

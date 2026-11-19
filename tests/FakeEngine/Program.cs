// A stand-in for the real tg-ws-proxy engine, used by the tests. Behaviour is driven by environment variables
// (the controller passes its own environment on to the engine it starts):
//   FAKE_ARGS_FILE        write argv, one argument per line, to this file
//   FAKE_MODE             listen (default) | exit:N | nolisten | child
//   FAKE_OUTPUT_LINES     exit mode: print this many "line i" lines to stderr before the fatal message
//   FAKE_CHILD_PID_FILE   child mode: the child's PID is written here
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

string host = "127.0.0.1";
int port = 0;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--host") host = args[i + 1];
    else if (args[i] == "--port") port = int.Parse(args[i + 1]);
}

string? argsFile = Environment.GetEnvironmentVariable("FAKE_ARGS_FILE");
if (!string.IsNullOrEmpty(argsFile)) File.WriteAllLines(argsFile, args);

string mode = Environment.GetEnvironmentVariable("FAKE_MODE") ?? "listen";

if (mode.StartsWith("exit:", StringComparison.Ordinal))
{
    int noise = int.Parse(Environment.GetEnvironmentVariable("FAKE_OUTPUT_LINES") ?? "0");
    for (int i = 1; i <= noise; i++) Console.Error.WriteLine($"line {i}");
    Console.Error.WriteLine("fake fatal: boom");
    return int.Parse(mode["exit:".Length..]);
}

if (mode == "nolisten")
{
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

if (mode == "child")
{
    // The PyInstaller one-file bootloader runs the real interpreter as a child; imitate that.
    var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    psi.Environment["FAKE_MODE"] = "nolisten";
    psi.Environment.Remove("FAKE_ARGS_FILE");   // the child must not overwrite our argv dump
    using Process child = Process.Start(psi)!;
    string? pidFile = Environment.GetEnvironmentVariable("FAKE_CHILD_PID_FILE");
    if (!string.IsNullOrEmpty(pidFile)) File.WriteAllText(pidFile, child.Id.ToString());
}

TcpListener listener;
try
{
    listener = new TcpListener(IPAddress.Parse(host), port);
    listener.Start();
}
catch (SocketException ex)
{
    Console.Error.WriteLine($"fake fatal: cannot listen on {host}:{port}: {ex.SocketErrorCode}");
    return 1;
}

while (true)
{
    using TcpClient client = listener.AcceptTcpClient();   // accept and drop: enough for a connect probe
}

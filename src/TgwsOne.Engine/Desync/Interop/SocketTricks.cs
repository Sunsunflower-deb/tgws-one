namespace TgwsOne.Engine.Desync;

/// <summary>Chooses the socket primitives of the current OS. The choice is made once per egress socket.</summary>
public static class SocketTricks
{
    public static ISocketTricks ForCurrentPlatform()
    {
        // Android runs a Linux kernel with bionic: the same splice/vmsplice syscalls are in SYSCALLS.TXT and are not
        // in the app seccomp blocklist, so the Linux implementation is the Android one too.
        if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid()) return new LinuxSocketTricks();
        if (OperatingSystem.IsWindows()) return new WindowsSocketTricks();
        return new UnsupportedSocketTricks();
    }
}

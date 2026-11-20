using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

/// <summary>The biggest <c>set-exclusions</c> the lists accept: <see cref="Exclusions.MaxEntries"/> distinct hosts at the 253-character limit and as many IPv6 networks, the long way round.</summary>
internal static class MaximalExclusions
{
    public static ControlRequest Request()
    {
        var hosts = new List<string>();
        var ips = new List<string>();
        for (int i = 0; i < Exclusions.MaxEntries; i++)
        {
            // 63 + 1 + 63 + 1 + 63 + 1 + 61 = 253 characters, every label valid, the last one not all digits.
            hosts.Add($"{i:D4}{new string('a', 59)}.{new string('b', 63)}.{new string('c', 63)}.{new string('d', 61)}");
            // The counter sits inside the /64, so no two networks collapse into one.
            ips.Add($"2001:0db8:0000:{i:x4}:0000:0000:0000:0000/64");
        }
        return new ControlRequest(ControlCommands.SetExclusions, Hosts: hosts, Ips: ips);
    }
}

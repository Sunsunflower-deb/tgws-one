using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

public class ExclusionsTests
{
    [Theory]
    [InlineData("sberbank.ru", "sberbank.ru")]
    [InlineData("Sub.Example.COM", "sub.example.com")]
    [InlineData("  spaced.example.com \t", "spaced.example.com")]
    [InlineData("пример.рф", "xn--e1afmkfd.xn--p1ai")]
    [InlineData("ПРИМЕР.РФ", "xn--e1afmkfd.xn--p1ai")]
    [InlineData("xn--e1afmkfd.xn--p1ai", "xn--e1afmkfd.xn--p1ai")]
    [InlineData("a.b", "a.b")]
    [InlineData("my-site.example.com", "my-site.example.com")]
    [InlineData("123.example.com", "123.example.com")]
    public void Hosts_Valid(string raw, string expected)
    {
        (IReadOnlyList<string> valid, IReadOnlyList<string> invalid) = Exclusions.NormalizeHosts([raw]);

        Assert.Equal([expected], valid);
        Assert.Empty(invalid);
    }

    [Theory]
    [InlineData("localhost")]                        // no dot
    [InlineData("-a.com")]
    [InlineData("a-.com")]
    [InlineData("a..b")]
    [InlineData(".example.com")]
    [InlineData("example.com.")]
    [InlineData("exa mple.com")]
    [InlineData("http://x.com")]
    [InlineData("x.com/path")]
    [InlineData("user@x.com")]
    [InlineData("*.example.com")]
    [InlineData("under_score.example.com")]
    [InlineData("203.0.113.5")]                      // an IP belongs in the IP list
    [InlineData("example.123")]
    public void Hosts_Invalid(string raw)
    {
        (IReadOnlyList<string> valid, IReadOnlyList<string> invalid) = Exclusions.NormalizeHosts([raw]);

        Assert.Empty(valid);
        Assert.Equal([raw.Trim()], invalid);
    }

    [Fact]
    public void Hosts_LabelLength_Limit()
    {
        string ok = new string('a', 63) + ".com";
        string tooLong = new string('a', 64) + ".com";

        (IReadOnlyList<string> valid, IReadOnlyList<string> invalid) = Exclusions.NormalizeHosts([ok, tooLong]);

        Assert.Equal([ok], valid);
        Assert.Equal([tooLong], invalid);
    }

    [Fact]
    public void Hosts_TotalLength_Limit()
    {
        // 63 + 1 + 63 + 1 + 63 + 1 + 61 = 253 is fine, one more character is not.
        string Label(int n) => new('a', n);
        string ok = $"{Label(63)}.{Label(63)}.{Label(63)}.{Label(61)}";
        string tooLong = $"{Label(63)}.{Label(63)}.{Label(63)}.{Label(62)}";
        Assert.Equal(253, ok.Length);

        (IReadOnlyList<string> valid, IReadOnlyList<string> invalid) = Exclusions.NormalizeHosts([ok, tooLong]);

        Assert.Equal([ok], valid);
        Assert.Equal([tooLong], invalid);
    }

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.5")]
    [InlineData("203.0.113.0/24", "203.0.113.0/24")]
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData("2001:DB8::1", "2001:db8::1")]
    [InlineData("2001:0db8:0:0:0:0:0:0/32", "2001:db8::/32")]
    [InlineData("203.0.113.77/24", "203.0.113.0/24")]   // host bits are cleared
    [InlineData("10.0.0.0/8", "10.0.0.0/8")]
    [InlineData("10.0.0.1/32", "10.0.0.1/32")]
    [InlineData("  192.168.0.1  ", "192.168.0.1")]
    public void Ips_Valid(string raw, string expected)
    {
        (IReadOnlyList<string> valid, IReadOnlyList<string> invalid) = Exclusions.NormalizeIps([raw]);

        Assert.Equal([expected], valid);
        Assert.Empty(invalid);
    }

    [Theory]
    [InlineData("300.1.1.1")]
    [InlineData("10.0.0.0/33")]
    [InlineData("2001:db8::/129")]
    [InlineData("abc")]
    [InlineData("1")]                // IPAddress.TryParse would read this as 0.0.0.1
    [InlineData("10.1")]
    [InlineData("1.2.3.4/")]
    [InlineData("1.2.3.4/-1")]
    [InlineData("1.2.3.4/x")]
    [InlineData("1.2.3.4/24/8")]
    [InlineData("1.2.3.4 /24")]
    [InlineData("fe80::1%eth0")]
    [InlineData("[2001:db8::1]")]
    [InlineData("example.com")]
    public void Ips_Invalid(string raw)
    {
        (IReadOnlyList<string> valid, IReadOnlyList<string> invalid) = Exclusions.NormalizeIps([raw]);

        Assert.Empty(valid);
        Assert.Equal([raw.Trim()], invalid);
    }

    [Fact]
    public void Dedup_KeepsFirst_AcrossCaseAndPunycode()
    {
        (IReadOnlyList<string> hosts, _) = Exclusions.NormalizeHosts(["a.com", "B.com", "A.COM", " a.com ", "пример.рф", "xn--e1afmkfd.xn--p1ai", "b.com"]);
        Assert.Equal(["a.com", "b.com", "xn--e1afmkfd.xn--p1ai"], hosts);

        (IReadOnlyList<string> ips, _) = Exclusions.NormalizeIps(["10.0.0.1", "10.0.0.0/24", "10.0.0.7/24", "10.0.0.1"]);
        Assert.Equal(["10.0.0.1", "10.0.0.0/24"], ips);
    }

    [Fact]
    public void BlankEntries_AreIgnored()
    {
        (IReadOnlyList<string> valid, IReadOnlyList<string> invalid) = Exclusions.NormalizeHosts(["", "   ", "a.com", "\t"]);

        Assert.Equal(["a.com"], valid);
        Assert.Empty(invalid);
    }

    [Fact]
    public void LimitEnforced_BeyondThousandGoesToInvalid()
    {
        // Duplicates and rejected entries don't use up slots; the 1001st distinct valid entry is the one refused.
        var raw = new List<string> { "bad host", "dup.example.com" };
        for (int i = 0; i < 1001; i++) raw.Add($"h{i}.example.com");
        raw.Add("dup.example.com");

        (IReadOnlyList<string> valid, IReadOnlyList<string> invalid) = Exclusions.NormalizeHosts(raw);

        Assert.Equal(Exclusions.MaxEntries, valid.Count);
        Assert.Equal("dup.example.com", valid[0]);
        Assert.Equal("h998.example.com", valid[^1]);
        Assert.Equal(["bad host", "h999.example.com", "h1000.example.com"], invalid);
    }

    [Fact]
    public void LimitEnforced_Ips()
    {
        var raw = Enumerable.Range(0, 1002).Select(i => $"10.{i / 256}.{i % 256}.1");

        (IReadOnlyList<string> valid, IReadOnlyList<string> invalid) = Exclusions.NormalizeIps(raw);

        Assert.Equal(1000, valid.Count);
        Assert.Equal(2, invalid.Count);
    }

    [Fact]
    public void WriteRead_RoundTrip_WithHeader()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("sub", "exclude-hosts.txt");

        Exclusions.Write(file, ["a.example.com", "xn--e1afmkfd.xn--p1ai"], "# Исключения обхода DPI — правятся из приложения TgwsOne");

        string text = File.ReadAllText(file);
        Assert.StartsWith("# Исключения обхода DPI — правятся из приложения TgwsOne\n", text);
        Assert.Equal("a.example.com\nxn--e1afmkfd.xn--p1ai\n", text[(text.IndexOf('\n') + 1)..]);
        Assert.Equal(["a.example.com", "xn--e1afmkfd.xn--p1ai"], Exclusions.Read(file));
        Assert.Equal([Path.GetFileName(file)], Directory.GetFiles(Path.GetDirectoryName(file)!).Select(Path.GetFileName));
    }

    [Fact]
    public void Write_HeaderWithoutHash_GetsOne_AndEmptyListKeepsOnlyTheHeader()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("exclude-ips.txt");

        Exclusions.Write(file, [], "первая строка\nвторая строка");

        Assert.Equal("# первая строка\n# вторая строка\n", File.ReadAllText(file));
        Assert.Empty(Exclusions.Read(file));
    }

    [Fact]
    public void Write_WithoutBom_AndOverwritesAtomically()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("exclude-hosts.txt");
        File.WriteAllText(file, "old.example.com\n");

        Exclusions.Write(file, ["new.example.com"], "# h");

        byte[] bytes = File.ReadAllBytes(file);
        Assert.Equal((byte)'#', bytes[0]);   // no UTF-8 BOM in front of the first line
        Assert.Equal(["new.example.com"], Exclusions.Read(file));
        Assert.Single(Directory.GetFiles(tmp.Path));   // the temp file is gone
    }

    [Fact]
    public void Read_SkipsCommentsAndBlankLines_TrimsAndToleratesCrLf()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("x.txt");
        File.WriteAllText(file, "# header\r\n\r\n  a.com  \r\n#b.com\r\nc.com\r\n   \r\n");

        Assert.Equal(["a.com", "c.com"], Exclusions.Read(file));
    }

    [Fact]
    public void Read_MissingFile_IsEmpty()
    {
        using var tmp = new TempDir();

        Assert.Empty(Exclusions.Read(tmp.Combine("nope.txt")));
    }
}

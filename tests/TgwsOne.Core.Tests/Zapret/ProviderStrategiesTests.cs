using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

public class ProviderStrategiesTests
{
    [Fact]
    public void Rotation_Universal_IsTheBuiltInSet()
        => Assert.Equal(BuiltInStrategies.Rotation, ProviderStrategies.Rotation("/nowhere", ProviderStrategies.Universal));

    [Fact]
    public void Rotation_WithoutAProvider_IsTheBuiltInSet()
        => Assert.Equal(BuiltInStrategies.Rotation, ProviderStrategies.Rotation("/nowhere", null));

    [Fact]
    public void Rotation_UnknownProvider_FallsBackToTheBuiltInSet()
        => Assert.Equal(BuiltInStrategies.Rotation, ProviderStrategies.Rotation("/nowhere", "some-operator"));

    [Fact]
    public void Rotation_ReadsTheProvidersOwnFile()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Combine("mts" + ProviderStrategies.Extension), "# оператор\naggressive\nsplit\n");

        Assert.Equal(["aggressive", "split"], ProviderStrategies.Rotation(tmp.Path, "mts"));
    }

    [Fact]
    public void Rotation_InAnUnreadableFolder_FallsBackToTheBuiltInSet()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("rostelecom" + ProviderStrategies.Extension);
        File.WriteAllText(file, "split\n");
        // A directory where the file is expected: reading it fails, and the bypass must not lose its strategies.
        Directory.CreateDirectory(file + ".dir");

        Assert.Equal(["split"], ProviderStrategies.Rotation(tmp.Path, "rostelecom"));
    }

    [Fact]
    public void Expand_KeepsBuiltInsInOrder_DroppingAutoUnknownAndRepeats()
    {
        IReadOnlyList<string> rotation = ProviderStrategies.Expand(
            ["# comment", "split", "auto", "nonsense", "  ", "split", "aggressive", "fake-tls"]);

        Assert.Equal(["split", "aggressive", "fake-tls"], rotation);
    }

    [Fact]
    public void Expand_WithNothingUsable_FallsBackToTheBuiltInSet()
        => Assert.Equal(BuiltInStrategies.Rotation, ProviderStrategies.Expand(["auto", "nonsense", "# split"]));

    [Theory]
    [InlineData("mts", true)]
    [InlineData("rostelecom-relay", true)]
    [InlineData("a.b_c", true)]
    [InlineData("", false)]
    [InlineData("-lead", false)]
    [InlineData("UPPER", false)]
    [InlineData("with space", false)]
    [InlineData("../escape", false)]
    public void IsValidProvider_AcceptsOnlyFileSafeNames(string name, bool valid)
        => Assert.Equal(valid, ProviderStrategies.IsValidProvider(name));

    [Fact]
    public void Providers_ListsTheUniversalSetFirst_ThenTheFiles()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Combine("mts" + ProviderStrategies.Extension), "split\n");
        File.WriteAllText(tmp.Combine("beeline" + ProviderStrategies.Extension), "aggressive\n");
        File.WriteAllText(tmp.Combine("README.txt"), "not a rotation\n");

        IReadOnlyList<string> providers = ProviderStrategies.Providers(tmp.Path);

        Assert.Equal(ProviderStrategies.Universal, providers[0]);
        Assert.Equal(["beeline", "mts"], providers.Skip(1));
    }

    [Fact]
    public void ServiceState_KeepsTheProvider()
    {
        using var tmp = new TempDir();
        string ini = tmp.Combine("service.ini");
        var state = new ServiceState(true, BuiltInStrategies.Auto, ZapretScope.Telegram, "mts");

        state.Save(ini);

        Assert.Equal(state, ServiceState.Load(ini));
    }

    [Fact]
    public void ServiceState_WithoutTheKey_UsesTheUniversalProvider()
    {
        using var tmp = new TempDir();
        string ini = tmp.Combine("service.ini");
        File.WriteAllText(ini, "[zapret]\nenabled=1\nstrategy=auto\n");

        Assert.Equal(ProviderStrategies.Universal, ServiceState.Load(ini).Provider);
    }

    [Fact]
    public void Auto_UsesTheProvidersRotation_AndTagsTheStepsWithItsOrder()
    {
        var bundle = ZapretBundle.Locate(OsKind.Linux, "/opt/tgwsone", _ => null);
        var lists = new ZapretLists("/l/cloudflare-ips.txt", "/l/telegram.txt", "/l/exclude-hosts.txt", "/l/exclude-ips.txt");

        string[] argv = [.. ZapretArgs.Build(OsKind.Linux, bundle, lists, BuiltInStrategies.Auto, "/var/lib/tgwsone",
            rotation: [BuiltInStrategies.Aggressive, BuiltInStrategies.Split])];

        // The aggressive recipe is the first step here, the split one the second: the tags follow that order.
        Assert.Contains("--lua-desync=fake:blob=tls_google:tcp_md5:repeats=11:strategy=1", argv);
        Assert.Contains("--lua-desync=multisplit:pos=1:seqovl=681:seqovl_pattern=tls_google:strategy=2", argv);
        // The fake-tls recipe of the universal set is not part of this provider's rotation.
        Assert.DoesNotContain(argv, a => a.Contains("sni=www.google.com:strategy", StringComparison.Ordinal));
    }

    [Fact]
    public void Auto_WithoutARotation_StillUsesTheUniversalSet()
    {
        var bundle = ZapretBundle.Locate(OsKind.Linux, "/opt/tgwsone", _ => null);
        var lists = new ZapretLists("/l/cloudflare-ips.txt", "/l/telegram.txt", "/l/exclude-hosts.txt", "/l/exclude-ips.txt");

        string[] argv = [.. ZapretArgs.Build(OsKind.Linux, bundle, lists, BuiltInStrategies.Auto, "/var/lib/tgwsone")];

        Assert.Contains("--lua-desync=fake:blob=0x00000000:tcp_seq=-10000:repeats=11:strategy=1", argv);
        Assert.Contains(argv, a => a.Contains("strategy=4", StringComparison.Ordinal));
    }
}

using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

public class StrategiesTests
{
    [Fact]
    public void Ids_AutoFirst_ThenTheFourPlainOnes()
        => Assert.Equal(["auto", "fake-tls", "split", "split-stealth", "aggressive"], BuiltInStrategies.Ids);

    [Fact]
    public void Rotation_IsTheFourPlainOnesInTableOrder()
        => Assert.Equal(["fake-tls", "split", "split-stealth", "aggressive"], BuiltInStrategies.Rotation);

    [Theory]
    [InlineData("auto", true)]
    [InlineData("split", true)]
    [InlineData("aggressive", true)]
    [InlineData("custom:mine", false)]
    [InlineData("Split", false)]
    [InlineData("", false)]
    public void IsBuiltIn(string id, bool expected) => Assert.Equal(expected, BuiltInStrategies.IsBuiltIn(id));

    [Fact]
    public void Part_FakeTls_Exact()
    {
        DesyncPart p = BuiltInStrategies.Part("fake-tls");
        Assert.Empty(p.Blobs);
        Assert.Equal(
        [
            "--lua-desync=fake:blob=0x00000000:tcp_seq=-10000:repeats=11",
            "--lua-desync=fake:blob=fake_default_tls:tcp_seq=-10000:repeats=11:tls_mod=rnd,dupsid,sni=www.google.com",
            "--lua-desync=multidisorder:pos=1,midsld",
        ], p.Args);
    }

    [Fact]
    public void Part_Split_Exact()
    {
        DesyncPart p = BuiltInStrategies.Part("split");
        Assert.Equal([new Blob("tls_google", "tls_clienthello_www_google_com.bin")], p.Blobs);
        Assert.Equal(["--lua-desync=multisplit:pos=1:seqovl=681:seqovl_pattern=tls_google"], p.Args);
    }

    [Fact]
    public void Part_SplitStealth_UsesVkBlob()
    {
        DesyncPart p = BuiltInStrategies.Part("split-stealth");
        Assert.Equal([new Blob("tls_vk", "tls_clienthello_vk_com.bin")], p.Blobs);
        Assert.Equal(["--lua-desync=multisplit:pos=1:seqovl=568:seqovl_pattern=tls_vk"], p.Args);
    }

    [Fact]
    public void Part_Aggressive_Exact()
    {
        DesyncPart p = BuiltInStrategies.Part("aggressive");
        Assert.Equal([new Blob("tls_google", "tls_clienthello_www_google_com.bin")], p.Blobs);
        Assert.Equal(
        [
            "--lua-desync=fake:blob=tls_google:tcp_md5:repeats=11",
            "--lua-desync=multisplit:pos=1",
        ], p.Args);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("nonsense")]
    [InlineData("custom:mine")]
    public void Part_AutoAndUnknown_Throw(string id) => Assert.Throws<ArgumentException>(() => BuiltInStrategies.Part(id));
}

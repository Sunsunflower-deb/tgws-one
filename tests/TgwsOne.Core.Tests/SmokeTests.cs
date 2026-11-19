namespace TgwsOne.Core.Tests;

public class SmokeTests
{
    // The cross-process CLI tests run TgwsOne.dll from the test output directory.
    [Fact]
    public void ExeAssembly_IsCopiedNextToTests()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "TgwsOne.dll")));
    }
}

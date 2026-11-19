namespace TgwsOne.Core.Tests;

public class SmokeTests
{
    // Cross-process CLI tests (Task 9) run TgwsOne.dll from the test output directory.
    [Fact]
    public void ExeAssembly_IsCopiedNextToTests()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "TgwsOne.dll")));
    }
}

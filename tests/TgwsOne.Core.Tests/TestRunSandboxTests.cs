namespace TgwsOne.Core.Tests;

/// <summary>Under <c>make test</c> the test host itself runs on throwaway XDG directories (see the Makefile); this checks that it did.</summary>
public class TestRunSandboxTests
{
    [Fact]
    public void UnderMakeTest_TheHostHasThrowawayXdgDirectories()
    {
        if (Environment.GetEnvironmentVariable("TGWS_TEST_SANDBOX") != "1") return;   // a plain `dotnet test`: nothing is promised

        string home = Environment.GetEnvironmentVariable("HOME") ?? "/nonexistent-home";
        foreach (string variable in new[] { "XDG_CONFIG_HOME", "XDG_STATE_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_RUNTIME_DIR" })
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            Assert.False(string.IsNullOrEmpty(value), variable + " is not set");
            Assert.StartsWith(Path.GetTempPath().TrimEnd('/'), value);
            Assert.DoesNotContain(home + "/.", value);
        }
    }
}

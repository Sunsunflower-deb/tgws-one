using TgwsOne.Services;

namespace TgwsOne.Tests;

public class RussianTests
{
    [Theory]
    [InlineData(0, "сбоев")]
    [InlineData(1, "сбой")]
    [InlineData(2, "сбоя")]
    [InlineData(3, "сбоя")]
    [InlineData(4, "сбоя")]
    [InlineData(5, "сбоев")]
    [InlineData(10, "сбоев")]
    [InlineData(11, "сбоев")]   // 11 to 14 are the "many" form whatever the last digit
    [InlineData(12, "сбоев")]
    [InlineData(14, "сбоев")]
    [InlineData(21, "сбой")]
    [InlineData(22, "сбоя")]
    [InlineData(25, "сбоев")]
    [InlineData(101, "сбой")]
    [InlineData(111, "сбоев")]
    [InlineData(112, "сбоев")]
    public void Plural_PicksTheFormByTheNumber(int n, string expected)
    {
        Assert.Equal(expected, Russian.Plural(n, "сбой", "сбоя", "сбоев"));
    }
}

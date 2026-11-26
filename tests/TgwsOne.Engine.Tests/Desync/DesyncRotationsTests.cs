using TgwsOne.Engine.Desync;

namespace TgwsOne.Engine.Tests.Desync;

public sealed class DesyncRotationsTests
{
    private static string[] Ids(IEnumerable<DesyncStrategy> strategies)
        => [.. strategies.Select(strategy => strategy.Id)];

    [Fact]
    public void Parse_KeepsBuiltInsInOrder_DroppingUnknownAutoAndRepeats()
    {
        IReadOnlyList<DesyncStrategy> rotation = DesyncRotations.Parse(
        [
            "# operator rotation", " split-sni ", "auto", "nonsense", "", "split-sni", "tlsrec-midsni",
        ]);

        Assert.Equal(["split-sni", "tlsrec-midsni"], Ids(rotation));
    }

    [Fact]
    public void Parse_AnEmptyOrUnusableFile_IsTheUniversalSet()
    {
        Assert.Equal(Ids(BuiltInDesync.BuiltIn), Ids(DesyncRotations.Parse([])));
        Assert.Equal(Ids(BuiltInDesync.BuiltIn), Ids(DesyncRotations.Parse(["auto", "# split-sni", "junk"])));
    }

    [Fact]
    public void ForOperator_Universal_IsTheBuiltInOrder()
    {
        Assert.Equal(Ids(BuiltInDesync.BuiltIn), Ids(DesyncRotations.ForOperator(DesyncRotations.Universal)));
        Assert.Equal(Ids(BuiltInDesync.BuiltIn), Ids(DesyncRotations.ForOperator(null)));
        Assert.Equal(Ids(BuiltInDesync.BuiltIn), Ids(DesyncRotations.ForOperator("")));
    }

    [Fact]
    public void ForOperator_UnknownName_FallsBackToTheUniversalSet()
    {
        Assert.Equal(Ids(BuiltInDesync.BuiltIn), Ids(DesyncRotations.ForOperator("not-an-operator")));
        Assert.Equal(Ids(BuiltInDesync.BuiltIn), Ids(DesyncRotations.ForOperator("../escape")));
    }

    [Fact]
    public void ForOperator_ReadsTheShippedRotation()
    {
        Assert.Equal(["split-sni", "fake-split", "disorder-sld", "split-midsni"],
            Ids(DesyncRotations.ForOperator("mts")));
        Assert.Equal(["split-sni", "disorder-1", "fake-split", "tlsrec-midsni"],
            Ids(DesyncRotations.ForOperator("megafon")));
        Assert.Equal(["split-midsni", "fake", "disorder-sld", "oob-sni"],
            Ids(DesyncRotations.ForOperator("beeline")));
        Assert.Equal(["split-sni", "tlsrec-midsni", "fake-split", "disorder-1"],
            Ids(DesyncRotations.ForOperator("tele2")));
    }

    [Fact]
    public void Operators_ListsTheUniversalSetFirst_ThenTheShippedOnes()
    {
        IReadOnlyList<string> operators = DesyncRotations.Operators;

        Assert.Equal(DesyncRotations.Universal, operators[0]);
        Assert.Equal(["beeline", "megafon", "mts", "tele2", "universal"], operators.Order(StringComparer.Ordinal));
        Assert.Contains("mts", operators);
        Assert.Contains("megafon", operators);
        Assert.Contains("beeline", operators);
        Assert.Contains("tele2", operators);
    }

    [Theory]
    [InlineData("25001", "mts")]
    [InlineData("25002", "megafon")]
    [InlineData("25020", "tele2")]
    [InlineData("25099", "beeline")]
    [InlineData(" 25001 ", "mts")]
    [InlineData("99999", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OperatorForMccMnc_MapsTheRussianCodes(string? mccMnc, string? expected)
        => Assert.Equal(expected, DesyncRotations.OperatorForMccMnc(mccMnc));

    [Fact]
    public void ForMccMnc_UsesTheOperatorsRotation_AndUniversalForUnknownCodes()
    {
        Assert.Equal(["split-sni", "fake-split", "disorder-sld", "split-midsni"],
            Ids(DesyncRotations.ForMccMnc("25001")));
        Assert.Equal(Ids(BuiltInDesync.BuiltIn), Ids(DesyncRotations.ForMccMnc("99999")));
    }

    [Fact]
    public void Controller_SetOperator_SwitchesTheRotation_AndResetsAutoState()
    {
        var controller = new DesyncController();
        int changes = 0;
        controller.Changed += () => changes++;
        Assert.Same(BuiltInDesync.BuiltIn[0], controller.Resolve("a.example", "ws"));

        controller.SetOperator("mts");

        Assert.Equal("mts", controller.Operator);
        Assert.Same(BuiltInDesync.Find("split-sni"), controller.Resolve("a.example", "ws"));
        Assert.Equal(1, changes);

        controller.SetOperator("unknown");
        Assert.Equal(DesyncRotations.Universal, controller.Operator);
        Assert.Equal(Ids(BuiltInDesync.BuiltIn), Ids(controller.Rotation));
        Assert.Equal(2, changes);

        // Already universal: not a change, and nothing is reset or announced.
        controller.SetOperator(null);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Controller_AnUnknownOperator_ReplacesACustomRotationWithTheUniversalSet()
    {
        var custom = new DesyncStrategy("custom", [new Split(new SniMid())]);
        var controller = new DesyncController([custom]);

        controller.SetOperator("unknown");

        Assert.Equal(DesyncRotations.Universal, controller.Operator);
        Assert.Equal(Ids(BuiltInDesync.BuiltIn), Ids(controller.Rotation));
    }

    [Fact]
    public void Controller_SetOperatorFromMccMnc_SelectsTheOperator()
    {
        var controller = new DesyncController();

        controller.SetOperatorFromMccMnc("25002");

        Assert.Equal("megafon", controller.Operator);
        Assert.Equal(["split-sni", "disorder-1", "fake-split", "tlsrec-midsni"],
            Ids(controller.Rotation));
    }
}

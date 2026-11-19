using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

public class ControlProtocolTests
{
    [Fact]
    public void Serialize_Request_OmitsNulls_AndIsCamelCase()
    {
        Assert.Equal("""{"cmd":"status"}""", ControlProtocol.Serialize(new ControlRequest("status")));
        Assert.Equal("""{"cmd":"use","strategy":"split"}""", ControlProtocol.Serialize(new ControlRequest("use", Strategy: "split")));
        Assert.Equal("""{"cmd":"set-exclusions","hosts":["a.com"],"ips":[]}""",
            ControlProtocol.Serialize(new ControlRequest("set-exclusions", Hosts: ["a.com"], Ips: [])));
    }

    [Fact]
    public void Serialize_Response_OmitsNulls()
    {
        Assert.Equal("""{"ok":true}""", ControlProtocol.Serialize(new ControlResponse(true)));
        Assert.Equal("""{"ok":false,"error":"boom"}""", ControlProtocol.Serialize(new ControlResponse(false, "boom")));
        Assert.Equal("неизвестная команда", ControlProtocol.ParseResponse(ControlProtocol.Serialize(new ControlResponse(false, "неизвестная команда"))).Error);
    }

    [Fact]
    public void Serialize_IsOneLine_EvenWithNewlinesInText()
    {
        string line = ControlProtocol.Serialize(new ControlResponse(false, "первая\nвторая\r\nтретья"));

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Equal("первая\nвторая\r\nтретья", ControlProtocol.ParseResponse(line).Error);
    }

    [Fact]
    public void Response_RoundTrip_Status()
    {
        var status = new ZapretStatus(true, "auto", "running", 4242, new DateTimeOffset(2026, 11, 20, 10, 30, 0, TimeSpan.FromHours(3)),
            Restarts: 2, LastError: "boom: «ошибка»", Bundle: "v1.0.7");
        string line = ControlProtocol.Serialize(new ControlResponse(true, Status: status));

        Assert.Contains("\"status\":{\"enabled\":true,\"strategy\":\"auto\",\"state\":\"running\",\"pid\":4242,", line);
        Assert.Contains("\"restarts\":2,\"lastError\":", line);
        ControlResponse back = ControlProtocol.ParseResponse(line);
        Assert.True(back.Ok);
        Assert.Equal(status, back.Status);
    }

    [Fact]
    public void Response_RoundTrip_StatusWithAutoHosts()
    {
        var auto = new[] { new AutoHost("a.example.com", "split", 1), new AutoHost("b.example.com", "fake-tls", 0) };
        var status = new ZapretStatus(true, "auto", "running", 1, null, 0, null, "v1.0.7", auto);
        string line = ControlProtocol.Serialize(new ControlResponse(true, Status: status));

        Assert.Contains("\"auto\":[{\"host\":\"a.example.com\",\"strategy\":\"split\",\"failures\":1},{\"host\":\"b.example.com\",\"strategy\":\"fake-tls\",\"failures\":0}]", line);
        Assert.Equal(auto, ControlProtocol.ParseResponse(line).Status!.Auto);
    }

    [Fact]
    public void Response_RoundTrip_StatusWithoutOptionalFields()
    {
        var status = new ZapretStatus(false, "split", "disabled", null, null, 0, null, "v1.0.7");
        string line = ControlProtocol.Serialize(new ControlResponse(true, Status: status));

        Assert.DoesNotContain("pid", line);
        Assert.DoesNotContain("since", line);
        Assert.DoesNotContain("lastError", line);
        Assert.Equal(status, ControlProtocol.ParseResponse(line).Status);
    }

    [Fact]
    public void Response_RoundTrip_StrategiesAndExclusions()
    {
        var items = new[] { new StrategyItem("auto", "builtin"), new StrategyItem("custom:mine", "custom") };

        ControlResponse s = ControlProtocol.ParseResponse(ControlProtocol.Serialize(new ControlResponse(true, Items: items, Current: "auto")));
        Assert.Equal(items, s.Items);
        Assert.Equal("auto", s.Current);

        ControlResponse e = ControlProtocol.ParseResponse(ControlProtocol.Serialize(
            new ControlResponse(true, Hosts: ["a.com"], Ips: ["10.0.0.0/8"], Invalid: ["bad host"])));
        Assert.Equal(["a.com"], e.Hosts);
        Assert.Equal(["10.0.0.0/8"], e.Ips);
        Assert.Equal(["bad host"], e.Invalid);
    }

    [Fact]
    public void ParseResponse_Garbage_IsAnErrorResponse()
    {
        foreach (string garbage in new[] { "", "not json", "[]", "null", "{\"ok\":" })
        {
            ControlResponse r = ControlProtocol.ParseResponse(garbage);
            Assert.False(r.Ok);
            Assert.False(string.IsNullOrEmpty(r.Error));
        }
    }

    [Theory]
    [InlineData("""{"cmd":"status"}""", "status")]
    [InlineData("""{"cmd":"on"}""", "on")]
    [InlineData("""{"cmd":"off"}""", "off")]
    [InlineData("""{"cmd":"strategies"}""", "strategies")]
    [InlineData("""{"cmd":"exclusions"}""", "exclusions")]
    [InlineData("""{"CMD":"status"}""", "status")]
    [InlineData("""{"cmd":"status","extra":{"a":1},"more":[1,2]}""", "status")]
    public void ParseRequest_Valid(string line, string cmd)
    {
        ControlRequest? r = ControlProtocol.ParseRequest(line, out string? error);

        Assert.Null(error);
        Assert.Equal(cmd, r!.Cmd);
    }

    [Fact]
    public void ParseRequest_UseAndSetExclusions_CarryTheirArguments()
    {
        ControlRequest? use = ControlProtocol.ParseRequest("""{"cmd":"use","strategy":"custom:mine"}""", out _);
        Assert.Equal("custom:mine", use!.Strategy);

        ControlRequest? set = ControlProtocol.ParseRequest("""{"cmd":"set-exclusions","hosts":["a.com","b.com"],"ips":[]}""", out _);
        Assert.Equal(["a.com", "b.com"], set!.Hosts);
        Assert.Empty(set.Ips!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{\"cmd\":")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"status\"")]
    [InlineData("{}")]
    [InlineData("""{"cmd":""}""")]
    [InlineData("""{"cmd":5}""")]
    [InlineData("""{"cmd":null}""")]
    [InlineData("""{"cmd":"use"}""")]
    [InlineData("""{"cmd":"use","strategy":""}""")]
    [InlineData("""{"cmd":"use","strategy":3}""")]
    [InlineData("""{"cmd":"set-exclusions"}""")]
    [InlineData("""{"cmd":"set-exclusions","hosts":["a.com"]}""")]
    [InlineData("""{"cmd":"set-exclusions","ips":[]}""")]
    [InlineData("""{"cmd":"set-exclusions","hosts":"a.com","ips":[]}""")]
    [InlineData("""{"cmd":"set-exclusions","hosts":[1],"ips":[]}""")]
    [InlineData("""{"cmd":"set-exclusions","hosts":["a.com",null],"ips":[]}""")]
    public void ParseRequest_Malformed_IsInvalidRequest(string line)
    {
        ControlRequest? r = ControlProtocol.ParseRequest(line, out string? error);

        Assert.Null(r);
        Assert.Equal("неверный запрос", error);
    }

    [Theory]
    [InlineData("""{"cmd":"reboot"}""")]
    [InlineData("""{"cmd":"STATUS"}""")]
    [InlineData("""{"cmd":"status "}""")]
    public void ParseRequest_UnknownCommand(string line)
    {
        ControlRequest? r = ControlProtocol.ParseRequest(line, out string? error);

        Assert.Null(r);
        Assert.Equal("неизвестная команда", error);
    }

    [Fact]
    public void MaxLineBytes_Is64K() => Assert.Equal(65536, ControlProtocol.MaxLineBytes);
}

namespace TgwsOne.Mobile.Tests;

public class MobileAppTests
{
    [Fact]
    public void TheMobileAssemblyLoadsAndExposesItsStub()
    {
        Type type = typeof(MobileApp);

        Assert.Equal("TgwsOne.Mobile", type.Assembly.GetName().Name);
        Assert.True(type.IsAbstract && type.IsSealed, "the stub is a static class");
    }
}

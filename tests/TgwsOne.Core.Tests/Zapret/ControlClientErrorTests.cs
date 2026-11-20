using TgwsOne.Core;
using TgwsOne.Core.Zapret;

namespace TgwsOne.Core.Tests.Zapret;

/// <summary>What the client says when the endpoint can't be used, whatever the operating system throws for it.</summary>
public class ControlClientErrorTests
{
    private static ServicePaths Paths() => ServicePaths.For(OsKind.Linux, _ => null);

    private static ControlClient ClientThatFailsWith(Exception error)
        => new(Paths(), TimeSpan.FromSeconds(2), _ => throw error);

    // Windows: the control pipe is open to interactive logons only, so a network logon (an SSH session) and any other session
    // that is not the user's own get UnauthorizedAccessException from NamedPipeClientStream, not a SocketException or IOException.
    [Fact]
    public async Task AccessDenied_is_a_ServiceUnavailableException_with_a_message_for_the_user()
    {
        ControlClient client = ClientThatFailsWith(new UnauthorizedAccessException("Access to the path is denied."));

        ServiceUnavailableException ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.SendAsync(new ControlRequest(ControlCommands.Status)));

        Assert.Contains("нет доступа", ex.Message);
        Assert.DoesNotContain("Access to the path", ex.Message);   // the .NET text is for the log, not for the person at the keyboard
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(TimeoutException))]
    public async Task Other_connect_failures_still_mean_the_service_is_unavailable(Type kind)
    {
        ControlClient client = ClientThatFailsWith((Exception)Activator.CreateInstance(kind)!);

        ServiceUnavailableException ex = await Assert.ThrowsAsync<ServiceUnavailableException>(() => client.SendAsync(new ControlRequest(ControlCommands.Status)));

        Assert.Contains("недоступна", ex.Message);
    }
}

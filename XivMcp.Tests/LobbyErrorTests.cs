using XivMcp.Ui;

namespace XivMcp.Tests;

public class LobbyErrorTests
{
    [Theory]
    [InlineData("90002 Connection with the server was lost.")]
    [InlineData("Connection with the server was lost. 90002")]
    [InlineData("The lobby server connection has encountered an error. 2002")]
    public void A_lost_connection_only_needs_a_new_login(string text) => Assert.True(LobbyError.IsDisconnect(text));

    [Theory]
    [InlineData("The server is currently undergoing maintenance. 2007")]
    [InlineData("Unable to log in: the world is full. 4004")]
    [InlineData("Something went wrong.")]
    public void Other_errors_stop_the_login(string text) => Assert.False(LobbyError.IsDisconnect(text));
}

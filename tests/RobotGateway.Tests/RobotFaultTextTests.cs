using RobotGateway.Core;

namespace RobotGateway.Tests;

public class RobotFaultTextTests
{
    [Theory]
    [InlineData("[316013] An error occurred while running the parallel program: [errore aria]. - Clear the fault.", "errore aria")]
    [InlineData("An error occurred while running the parallel program: [errore aria].", "errore aria")]
    [InlineData("[42] Collision detected - Clear fault", "Collision detected - Clear fault")]
    [InlineData("Arresto di emergenza premuto.", "Arresto di emergenza premuto.")]
    [InlineData("[7] Program error: [ ]", "Program error: [ ]")]
    public void Extract_ReturnsThePlanMessage_OrTheDescription(string message, string expected) =>
        Assert.Equal(expected, RobotFaultText.Extract(message));
}

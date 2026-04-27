using TestLib;
using Xunit;

namespace XUnitFixture;

public class SampleTests
{
    [Fact]
    public void DirectGreetTest()
    {
        var greeter = new Greeter();
        var result = greeter.Greet("world");
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("alice")]
    [InlineData("bob")]
    public void ParameterisedGreetTest(string name)
    {
        var greeter = new Greeter();
        var result = greeter.Greet(name);
        Assert.NotNull(result);
    }
}

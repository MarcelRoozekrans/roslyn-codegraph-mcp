using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestLib;

namespace MSTestFixture;

[TestClass]
public class SampleTests
{
    [TestMethod]
    public void DirectGreetTest()
    {
        var greeter = new Greeter();
        var result = greeter.Greet("world");
        Assert.IsNotNull(result);
    }

    [DataTestMethod]
    [DataRow("alice")]
    public void ParameterisedGreetTest(string name)
    {
        var greeter = new Greeter();
        var result = greeter.Greet(name);
        Assert.IsNotNull(result);
    }
}

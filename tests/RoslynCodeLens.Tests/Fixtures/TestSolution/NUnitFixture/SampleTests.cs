using NUnit.Framework;
using TestLib;

namespace NUnitFixture;

[TestFixture]
public class SampleTests
{
    [Test]
    public void DirectGreetTest()
    {
        var greeter = new Greeter();
        var result = greeter.Greet("world");
        Assert.That(result, Is.Not.Null);
    }

    [TestCase("alice")]
    [TestCase("bob")]
    public void ParameterisedGreetTest(string name)
    {
        var greeter = new Greeter();
        var result = greeter.Greet(name);
        Assert.That(result, Is.Not.Null);
    }

    private static void HelperThatGreets(string name)
    {
        var greeter = new Greeter();
        greeter.Greet(name);
    }

    [Test]
    public void TransitiveGreetTest()
    {
        HelperThatGreets("via helper");
    }
}

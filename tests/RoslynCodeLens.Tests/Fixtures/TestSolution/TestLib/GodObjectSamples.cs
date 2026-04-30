using TestLib.GodObjectSamples.CallerA;
using TestLib.GodObjectSamples.CallerB;
using TestLib.GodObjectSamples.CallerC;
using TestLib.GodObjectSamples.CallerD;
using TestLib.GodObjectSamples.CallerE;

namespace TestLib.GodObjectSamples.Bad;

public class BadGodObject
{
    public int Field1, Field2, Field3, Field4, Field5, Field6;
    public int Field7, Field8, Field9, Field10, Field11, Field12;
    public void Method1() { }
    public void Method2() { }
    public void Method3() { }
    public void Method4() { }
    public void Method5() { }
    public void Method6() { }
    public void Method7() { }
    public void Method8() { }
    public void Method9() { }
    public void Method10() { }
    public void Method11() { }
    public void Method12() { }
    public void Method13() { }
    public void Method14() { }
    public void Method15() { }
    public void Method16() { }
}

namespace TestLib.GodObjectSamples.Isolated;

public class LargeButIsolated
{
    public void Method1() { }
    public void Method2() { }
    public void Method3() { }
    public void Method4() { }
    public void Method5() { }
    public void Method6() { }
    public void Method7() { }
    public void Method8() { }
    public void Method9() { }
    public void Method10() { }
    public void Method11() { }
    public void Method12() { }
    public void Method13() { }
    public void Method14() { }
    public void Method15() { }
    public void Method16() { }
}

public class IsolatedConsumer
{
    public void Use()
    {
        var x = new LargeButIsolated();
        x.Method1();
    }
}

namespace TestLib.GodObjectSamples.Small;

public class SmallButHighlyCoupled
{
    public void OneMethod() { }
}

namespace TestLib.GodObjectSamples.CallerA;
public class A
{
    public void Use(BadGodObject b, SmallButHighlyCoupled s) { b.Method1(); s.OneMethod(); }
}

namespace TestLib.GodObjectSamples.CallerB;
public class B
{
    public void Use(BadGodObject b, SmallButHighlyCoupled s) { b.Method2(); s.OneMethod(); }
}

namespace TestLib.GodObjectSamples.CallerC;
public class C
{
    public void Use(BadGodObject b, SmallButHighlyCoupled s) { b.Method3(); s.OneMethod(); }
}

namespace TestLib.GodObjectSamples.CallerD;
public class D
{
    public void Use(BadGodObject b, SmallButHighlyCoupled s) { b.Method4(); s.OneMethod(); }
}

namespace TestLib.GodObjectSamples.CallerE;
public class E
{
    public void Use(BadGodObject b, SmallButHighlyCoupled s) { b.Method5(); s.OneMethod(); }
}

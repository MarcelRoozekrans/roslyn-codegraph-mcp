// Fixture sizing is tuned to the test-class thresholds passed to FindGodObjectsLogic.
// Tests pass explicit minLines/minMembers/minFields/minIncomingNamespaces values — do not
// assume default thresholds. Adjust both the fixture and the test thresholds together if
// you change the shape here.

namespace TestLib.GodObjectSamples.Bad
{
    using TestLib.GodObjectSamples.CallerA;
    using TestLib.GodObjectSamples.CallerB;
    using TestLib.GodObjectSamples.CallerC;
    using TestLib.GodObjectSamples.CallerD;
    using TestLib.GodObjectSamples.CallerE;

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
}

namespace TestLib.GodObjectSamples.Isolated
{
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
}

namespace TestLib.GodObjectSamples.Small
{
    public class SmallButHighlyCoupled
    {
        public void OneMethod() { }
    }
}

namespace TestLib.GodObjectSamples.CallerA
{
    using TestLib.GodObjectSamples.Bad;
    using TestLib.GodObjectSamples.Small;

    public class A
    {
        public void Use(BadGodObject b, SmallButHighlyCoupled s) { b.Method1(); s.OneMethod(); }
    }
}

namespace TestLib.GodObjectSamples.CallerB
{
    using TestLib.GodObjectSamples.Bad;
    using TestLib.GodObjectSamples.Small;

    public class B
    {
        public void Use(BadGodObject b, SmallButHighlyCoupled s) { b.Method2(); s.OneMethod(); }
    }
}

namespace TestLib.GodObjectSamples.CallerC
{
    using TestLib.GodObjectSamples.Bad;
    using TestLib.GodObjectSamples.Small;

    public class C
    {
        public void Use(BadGodObject b, SmallButHighlyCoupled s) { b.Method3(); s.OneMethod(); }
    }
}

namespace TestLib.GodObjectSamples.CallerD
{
    using TestLib.GodObjectSamples.Bad;
    using TestLib.GodObjectSamples.Small;

    public class D
    {
        public void Use(BadGodObject b, SmallButHighlyCoupled s) { b.Method4(); s.OneMethod(); }
    }
}

namespace TestLib.GodObjectSamples.CallerE
{
    using TestLib.GodObjectSamples.Bad;
    using TestLib.GodObjectSamples.Small;

    public class E
    {
        public void Use(BadGodObject b, SmallButHighlyCoupled s) { b.Method5(); s.OneMethod(); }
    }
}

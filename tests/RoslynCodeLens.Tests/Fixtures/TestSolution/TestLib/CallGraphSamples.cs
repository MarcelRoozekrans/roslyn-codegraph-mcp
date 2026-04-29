using System;

namespace TestLib;

public class CallGraphSamples
{
    public string Root() => Level1A() + Level1B();

    public string Level1A() => Level2();
    public string Level1B() => Level2();
    public string Level2() => Level3();
    public string Level3() => "leaf";

    public string CallsExternal() => string.Format("{0}", "x");

    public void CycleA() => CycleB();
    public void CycleB() => CycleA();

    public string PropertyAndCtor()
    {
        var holder = new SampleHolder();
        var read = holder.Value;
        holder.Value = read + "x";
        return read;
    }

    public Money UseOperator(Money a, Money b) => a + b;
}

public class SampleHolder
{
    public string Value { get; set; } = "";
}

public readonly record struct Money(int Cents)
{
    public static Money operator +(Money a, Money b) => new(a.Cents + b.Cents);
}

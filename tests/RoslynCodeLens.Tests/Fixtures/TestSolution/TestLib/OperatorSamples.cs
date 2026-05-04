namespace TestLib;

public readonly record struct Money(decimal Amount, string Currency)
{
    /// <summary>Add two amounts in the same currency.</summary>
    public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount, a.Currency);

    public static Money operator -(Money a, Money b) => new(a.Amount - b.Amount, a.Currency);

    public static Money operator *(Money a, decimal factor) => new(a.Amount * factor, a.Currency);

    public static bool operator <(Money a, Money b) => a.Amount < b.Amount;
    public static bool operator >(Money a, Money b) => a.Amount > b.Amount;
    public static bool operator <=(Money a, Money b) => a.Amount <= b.Amount;
    public static bool operator >=(Money a, Money b) => a.Amount >= b.Amount;

    public static implicit operator decimal(Money m) => m.Amount;
    public static explicit operator Money(decimal d) => new(d, "USD");

    // .NET 7+ checked variant
    public static Money operator checked +(Money a, Money b) => checked(new(a.Amount + b.Amount, a.Currency));
}

public class NoOperators { public int X { get; set; } }

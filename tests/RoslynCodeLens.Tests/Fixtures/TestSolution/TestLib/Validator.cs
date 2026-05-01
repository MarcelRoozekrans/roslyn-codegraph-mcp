namespace TestLib;

public class Validator
{
    public void Validate(string input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        if (input.Length == 0) throw new ArgumentException("Empty");
    }
}

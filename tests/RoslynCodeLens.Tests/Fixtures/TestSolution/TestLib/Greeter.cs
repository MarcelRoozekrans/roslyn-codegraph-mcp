using System;

namespace TestLib;

[Serializable]
public class Greeter : IGreeter, IDisposable
{
    public virtual string Greet(string name) => $"Hello, {name}!";

    [Obsolete("Use Greet instead")]
    public string OldGreet(string name) => Greet(name);

    // Helper method intentionally triggers CA1822 (can be made static)
    public int ComputeLength(string value) => value.Length;

    public void Dispose() { }

    public string GreetFormal(string name) => $"Greetings, {name}";

    // Public computed property — uncovered. Complexity > 1 because of the if branch.
    public int FormalNameLength
    {
        get
        {
            if (_lastFormalName is null)
                return 0;
            return _lastFormalName.Length;
        }
    }

    private string? _lastFormalName;

    // Public method — uncovered, high complexity (>= 5) for the riskHotspotCount test.
    public string ClassifyName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "empty";
        if (name.Length < 3)
            return "short";
        if (name.Length > 20)
            return "long";
        if (name.Contains(' '))
            return "multi-word";
        return "normal";
    }
}

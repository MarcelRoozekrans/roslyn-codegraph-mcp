using RoslynCodeLens.Models;

namespace RoslynCodeLens;

public sealed class MultiSolutionManager : IDisposable
{
    private readonly Dictionary<string, SolutionManager> _managers;
    private string? _activeKey;
    private readonly Lock _lock = new();

    private MultiSolutionManager(Dictionary<string, SolutionManager> managers, string? activeKey)
    {
        _managers = managers;
        _activeKey = activeKey;
    }

    public static async Task<MultiSolutionManager> CreateAsync(IReadOnlyList<string> solutionPaths)
    {
        if (solutionPaths.Count == 0)
            return CreateEmpty();

        var managers = new Dictionary<string, SolutionManager>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in solutionPaths)
        {
            var normalised = Path.GetFullPath(path);
            if (!managers.ContainsKey(normalised))
                managers[normalised] = await SolutionManager.CreateAsync(normalised).ConfigureAwait(false);
        }

        var firstKey = Path.GetFullPath(solutionPaths[0]);
        return new MultiSolutionManager(managers, firstKey);
    }

    public static MultiSolutionManager CreateEmpty() =>
        new([], null);

    private SolutionManager Active
    {
        get
        {
            string? key;
            lock (_lock) { key = _activeKey; }
            if (key == null || !_managers.TryGetValue(key, out var m))
                throw new InvalidOperationException(
                    key == null
                        ? "No solution loaded. Pass a .sln/.slnx path as argument."
                        : $"Active solution key '{key}' not found in loaded solutions.");
            return m;
        }
    }

    public void EnsureLoaded() => Active.EnsureLoaded();
    public LoadedSolution GetLoadedSolution() => Active.GetLoadedSolution();
    public SymbolResolver GetResolver() => Active.GetResolver();
    public Task WaitForWarmupAsync() => Active.WaitForWarmupAsync();
    public Task<(int ProjectCount, TimeSpan Elapsed)> ForceReloadAsync() => Active.ForceReloadAsync();

    public IReadOnlyList<SolutionInfo> ListSolutions()
    {
        string? activeKey;
        lock (_lock) { activeKey = _activeKey; }

        return _managers
            .Select(kvp =>
            {
                var m = kvp.Value;
                int projectCount = 0;
                string status;
                try
                {
                    var loaded = m.GetLoadedSolution();
                    projectCount = loaded.Compilations.Count;
                    status = loaded.IsEmpty ? "empty" : "ready";
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("warmup failed", StringComparison.OrdinalIgnoreCase))
                {
                    status = "error";
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[roslyn-codelens] Error reading solution status ({kvp.Key}): {ex}");
                    status = "unknown";
                }
                return new SolutionInfo(kvp.Key, string.Equals(kvp.Key, activeKey, StringComparison.Ordinal), projectCount, status);
            })
            .ToList();
    }

    public string SetActiveSolution(string name)
    {
        var matches = _managers.Keys
            .Where(k => k.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            throw new InvalidOperationException(
                $"No solution matching '{name}'. Available: {string.Join(", ", _managers.Keys.Select(Path.GetFileName))}");

        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"Ambiguous match for '{name}'. Matches: {string.Join(", ", matches)}");

        lock (_lock) { _activeKey = matches[0]; }
        return matches[0];
    }

    /// <summary>
    /// Load a new solution at runtime. If it's already loaded, just activates it.
    /// Returns the normalised path of the loaded solution.
    /// </summary>
    public async Task<string> LoadSolutionAsync(string solutionPath)
    {
        var normalised = Path.GetFullPath(solutionPath);

        if (_managers.ContainsKey(normalised))
        {
            lock (_lock) { _activeKey = normalised; }
            return normalised;
        }

        var manager = await SolutionManager.CreateAsync(normalised).ConfigureAwait(false);

        lock (_lock)
        {
            _managers[normalised] = manager;
            _activeKey = normalised;
        }

        return normalised;
    }

    /// <summary>
    /// Unload a solution by partial name match, freeing its memory.
    /// Cannot unload the currently active solution unless it's the only one.
    /// </summary>
    public string UnloadSolution(string name)
    {
        var matches = _managers.Keys
            .Where(k => k.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            throw new InvalidOperationException(
                $"No solution matching '{name}'. Available: {string.Join(", ", _managers.Keys.Select(Path.GetFileName))}");

        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"Ambiguous match for '{name}'. Matches: {string.Join(", ", matches)}");

        var key = matches[0];

        lock (_lock)
        {
            if (string.Equals(_activeKey, key, StringComparison.OrdinalIgnoreCase))
            {
                // Switch active to another solution if available
                var remaining = _managers.Keys.Where(k => !string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList();
                _activeKey = remaining.Count > 0 ? remaining[0] : null;
            }

            if (_managers.Remove(key, out var manager))
                manager.Dispose();
        }

        return key;
    }

    public void Dispose()
    {
        foreach (var m in _managers.Values)
            m.Dispose();
    }
}

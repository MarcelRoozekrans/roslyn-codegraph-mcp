---
title: Configuration
sidebar_position: 3
---

# Configuration

## Solution path

The server needs to know which `.sln` or `.slnx` file to load.

**Option 1: CLI argument**
```json
"args": ["--solution", "/path/to/Solution.sln"]
```

**Option 2: Environment variable**
```json
"env": {"ROSLYN_CODELENS_SOLUTION": "/path/to/Solution.sln"}
```

**Option 3: At runtime** — call `set_active_solution` after the server starts. Useful for multi-solution workflows.

## Multiple solutions

`roslyn-codelens-mcp` has a built-in solution manager. Load and switch between solutions at runtime:

```
Use list_solutions to see what's loaded
Use set_active_solution with path /path/to/OtherSolution.sln
```

Only one solution is "active" at a time. All tool calls operate on the active solution.

## Automatic hot reload

The server watches for file changes via `FileChangeTracker`. When you edit and save a `.cs` file, the server updates its index automatically — no manual sync needed.

DLL changes (e.g. after `dotnet build`) also invalidate the IL cache automatically.

## Force reload

If the solution gets into a bad state:

```
Use rebuild_solution to force a full reload
```

---
title: Installation
sidebar_position: 1
---

# Installation

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- A .NET solution (`.sln` or `.slnx` file)

## Install the tool

```bash
dotnet tool install -g RoslynCodeLens.Mcp
```

Verify the install:

```bash
roslyn-codelens-mcp --version
```

## Configure `.mcp.json`

Add the server to your project's `.mcp.json` (or `~/.claude/.mcp.json` for global config):

```json
{
  "mcpServers": {
    "roslyn-codelens": {
      "command": "roslyn-codelens-mcp",
      "args": ["--solution", "/absolute/path/to/YourSolution.sln"]
    }
  }
}
```

The `--solution` argument is required on first start. Alternatively, use the `ROSLYN_CODELENS_SOLUTION` environment variable:

```json
{
  "mcpServers": {
    "roslyn-codelens": {
      "command": "roslyn-codelens-mcp",
      "env": {
        "ROSLYN_CODELENS_SOLUTION": "/absolute/path/to/YourSolution.sln"
      }
    }
  }
}
```

## Verify the server starts

Restart your MCP client (Claude Code, etc.). The server loads the solution on startup — this takes 5–30 seconds for large solutions.

Once loaded, try:

```
Use get_type_overview to describe the type MyClass
```

If the server responds with type info, setup is complete.

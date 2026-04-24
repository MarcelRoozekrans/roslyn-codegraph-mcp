---
title: Marketplace Install
sidebar_position: 2
---

# Install via Claude Marketplace

If you use [Superpowers Extensions](https://github.com/superpowers-marketplace/superpowers-extensions) for Claude Code, you can install `roslyn-codelens-mcp` as a managed plugin — no manual `.mcp.json` editing required.

## Steps

1. Open Claude Code and run `/mcp-add`
2. Search for `roslyn-codelens`
3. Follow the install prompts

The plugin configures the server command and loads the `SKILL.md` that teaches Claude when and how to use each tool.

## After install

Set your solution path. Either set `ROSLYN_CODELENS_SOLUTION` in your environment, or call `set_active_solution` once the server is running:

```
Use set_active_solution with path /path/to/YourSolution.sln
```

---
title: Refactor with Code Actions
sidebar_position: 4
---

# Refactor with Code Actions

All built-in Roslyn refactorings are available through two generic tools: `get_code_actions` and `apply_code_action`.

## 1. See available actions

```
Use get_code_actions for MyFile.cs at line 42
```

Returns all refactoring and fix suggestions at that location — rename, extract method, implement interface, etc.

## 2. Preview before applying

```
Use apply_code_action for MyFile.cs at line 42 with title="Extract Method" and preview=true
```

Returns a diff without writing to disk. Review it before committing.

## 3. Apply the change

```
Use apply_code_action for MyFile.cs at line 42 with title="Extract Method" and preview=false
```

Writes to disk. The `FileChangeTracker` picks up the change automatically.

## Common actions

| Goal | Title to use |
|------|-------------|
| Rename symbol | `Rename <name>` |
| Extract method | `Extract Method` |
| Implement interface | `Implement interface` |
| Generate constructor | `Generate constructor...` |
| Add null checks | `Add null checks for all parameters` |
| Encapsulate field | `Encapsulate field...` |
| Generate Equals/GetHashCode | `Generate Equals and GetHashCode...` |

:::tip
Use `get_code_fixes` for diagnostic-driven fixes (e.g. "fix CS0246") and `get_code_actions` for general refactorings.
:::

---
title: Find External Usages
sidebar_position: 2
---

# Find External Usages

Trace how a NuGet package is actually used across your codebase — useful before upgrading or replacing a dependency.

## 1. List NuGet packages

```
Use get_nuget_dependencies to show all packages
```

Pick the assembly you want to trace, e.g. `Newtonsoft.Json`.

## 2. Find all reference sites

```
Use find_references for Newtonsoft.Json.JsonConvert
```

Returns every file and line that uses `JsonConvert` directly.

## 3. Find callers of a specific method

```
Use find_callers for JsonConvert.DeserializeObject
```

Returns every method in your codebase that calls `DeserializeObject`.

## 4. Assess full change impact

```
Use analyze_change_impact for JsonConvert.DeserializeObject
```

Returns the blast radius: direct callers, transitive callers, affected projects. Useful for estimating migration effort.

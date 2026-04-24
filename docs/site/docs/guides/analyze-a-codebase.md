---
title: Analyze a Codebase
sidebar_position: 1
---

# Analyze a Codebase

Use this workflow to onboard into an unfamiliar .NET codebase.

## 1. Map project structure

```
Use get_project_dependencies for all projects
```

Shows which projects reference which, revealing the architectural layering.

## 2. Find structural problems

```
Use find_circular_dependencies to check for cycles
Use get_nuget_dependencies to list all NuGet packages
```

## 3. Survey a key type

```
Use get_type_overview for OrderService
```

Returns all members, base types, interfaces, and active diagnostics — one call.

## 4. Check overall health

```
Use get_diagnostics to show all errors and warnings
Use find_unused_symbols to find dead code
Use find_naming_violations to check .NET naming conventions
Use find_large_classes to spot oversized types
```

## 5. Understand DI wiring

See the [Understand DI Wiring](understand-di-wiring) guide for a deeper walkthrough.

---
title: First Use
sidebar_position: 4
---

# First Use

Once the server is running, try these calls to get oriented.

## 1. Get a type overview

```
Use get_type_overview to describe the type Program
```

Returns members, hierarchy, diagnostics — all in one call.

## 2. Find where something is used

```
Use find_references to find all usages of IMyService
```

## 3. Check diagnostics

```
Use get_diagnostics to show all errors and warnings in the solution
```

## 4. Explore an unfamiliar type

```
Use get_type_hierarchy for MyController
Use analyze_method for MyController.HandleRequest
```

`get_type_overview` is the fastest onboarding call — it returns context + members + hierarchy + diagnostics in a single round-trip.

## Next steps

- [Guides](../guides/analyze-a-codebase) — end-to-end workflows
- [Tool Reference](/tools) — full parameter docs for all tools

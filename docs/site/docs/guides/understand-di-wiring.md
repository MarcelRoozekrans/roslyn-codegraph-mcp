---
title: Understand DI Wiring
sidebar_position: 5
---

# Understand DI Wiring

Trace service registrations and constructor dependencies in a DI container.

## 1. Find all registrations

```
Use get_di_registrations for IMyService
```

Returns all `AddSingleton`/`AddScoped`/`AddTransient` calls for the interface — with lifetime and implementation type.

## 2. Find implementations

```
Use find_implementations for IMyService
```

Returns all concrete types that implement the interface.

## 3. Inspect a concrete type's dependencies

```
Use get_type_overview for MyServiceImpl
```

Members include the constructor — so you can see what it depends on.

## 4. Assess impact before refactoring

```
Use analyze_change_impact for IMyService
```

Shows all callers, affected types, and projects touched if the interface changes.

## End-to-end example

```
1. get_di_registrations for IOrderRepository
   → registered as scoped, implementation: SqlOrderRepository

2. get_type_overview for SqlOrderRepository
   → constructor takes IDbConnectionFactory, ILogger<SqlOrderRepository>

3. find_implementations for IDbConnectionFactory
   → SqlConnectionFactory, InMemoryConnectionFactory (test double)

4. analyze_change_impact for IOrderRepository
   → 12 callers, 3 projects affected
```

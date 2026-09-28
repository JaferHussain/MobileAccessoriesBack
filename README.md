# Moiz Mobile & Corporation — POS, Inventory & Ledger System

Point-of-sale, inventory and customer credit (udhaar) system for **Moiz Mobile & Corporation,
Danwran Lodhran** — a mobile accessories retail shop.

## Layout

This is the **backend** repository. The React counter app is its own repository,
[Mobile-Accessories-Frontend](https://github.com/JaferHussain/Mobile-Accessories-Frontend), and the
commands below assume the two are cloned side by side as `backend/` and `frontend/`.

```
backend/          ASP.NET Core 8 Web API (Controller → Service → Repository, Dapper, MySQL 8)
  src/
    MoizPos/                 one project, one assembly
      Domain/                entities, enums, domain errors — depends on nothing
      Application/           services, DTOs, validators, pure calculators
      Infrastructure/        Dapper repositories, PDF, backup, clock
      Api/                   controllers, middleware, auth policies
      Migrator/              DbUp runner + numbered SQL scripts
      Program.cs             the host, and `migrate` as a command
  tests/
    MoizPos.UnitTests/         calculators, services, validators
    MoizPos.IntegrationTests/  endpoints against a disposable MySQL schema
    MoizPos.ArchitectureTests/ asserts Staff DTOs carry no cost/profit property

  specs/          Spec Kit feature specifications, plans and tasks (both halves)
  docs/           deployment and operational notes

frontend/         React 18 + TypeScript (Vite) — a separate repository
```

Layering is enforced by project references, not convention: `Domain` references nothing,
`Application` references `Domain`, `Infrastructure` implements `Application`'s interfaces, and
`Api` composes them.

## Prerequisites

| Tool | Version |
|---|---|
| .NET SDK | 8.0+ |
| Node.js | 20 LTS+ |
| MySQL | 8.0+ (`mysql` and `mysqldump` must be on `PATH` for backup/restore) |

## Getting started

See [specs/001-pos-inventory-ledger/quickstart.md](specs/001-pos-inventory-ledger/quickstart.md)
for full setup, run and validation instructions.

```bash
# database
mysql -u root -p -e "CREATE DATABASE moizpos CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;"
mysql -u root -p -e "CREATE DATABASE moizpos_test CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;"

# secrets (never committed)
cd src/MoizPos
dotnet user-secrets set "ConnectionStrings:Default" "Server=localhost;Database=moizpos;Uid=root;Pwd=<password>;"
dotnet user-secrets set "Jwt:Key" "<at least 32 random characters>"

# schema
dotnet run --project src/MoizPos -- migrate

# run
dotnet run --project src/MoizPos        # http://localhost:5080
cd ../frontend && npm install && npm run dev        # http://localhost:5173
```

> **Change the seeded `admin` password before the shop uses the system.**

## Tests

```bash
dotnet test                      
cd ../frontend && npm run test
```

Integration tests create and drop their own schema in `moizpos_test`. They fail fast if MySQL is
unreachable — intentional, since the transactional guarantees cannot be verified against a fake.

## Key business rules

Two rules decided by the shop owner drive much of the design:

1. **Latest purchase cost.** When stock is purchased, that purchase's unit cost replaces the
   product's cost for *all* units on hand — not a weighted average. Buying 10 @ 800, selling 5,
   then buying 10 @ 850 leaves all 15 units costed at **850**.
2. **Current sale price.** Old stock sells at the current price, not the price in force when it
   was bought.

Each sale line snapshots the cost in force at the moment of sale, so a later purchase never
rewrites the profit of a sale already recorded.

Full specification: [specs/001-pos-inventory-ledger/spec.md](specs/001-pos-inventory-ledger/spec.md).
Engineering principles: [.specify/memory/constitution.md](.specify/memory/constitution.md).

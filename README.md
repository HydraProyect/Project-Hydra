# Project-Hydra

Plataforma SaaS multi-tenant de gestión CAE (Coordinación de Actividades
Empresariales), construida en .NET 10 / Blazor Server con arquitectura en capas
(Domain / Application / Infrastructure / Web) y PostgreSQL.

## Ejecutar en local

```bash
docker compose up -d          # PostgreSQL
dotnet restore CaeManager.slnx
dotnet run --project src/CaeManager.Web
```

## Tests

```bash
dotnet test CaeManager.slnx
```

## Estado

En desarrollo activo. El pipeline de CI (`.github/workflows/ci.yml`) es la
fuente de verdad de qué debe pasar antes de mergear a `main`.

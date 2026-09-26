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
docker compose up -d          # los tests de integración y E2E necesitan PostgreSQL
dotnet build CaeManager.slnx
pwsh tests/CaeManager.E2ETests/bin/Debug/net10.0/playwright.ps1 install chromium   # solo para los E2E
dotnet test CaeManager.slnx
```

## Documentación

Este repositorio contiene solo lo necesario para compilar, probar y desplegar.
La documentación (arquitectura, dominio, ADR, runbooks, auditorías y
decisiones) vive en el repositorio privado `Project-Hydra-Negocio`, que no
tiene remoto público. En el código se cita por su ruta dentro de ese
repositorio, con el prefijo `Project-Hydra-Negocio/` (por ejemplo,
`Project-Hydra-Negocio/tecnico/RUNBOOK-RLS.md`).

## Estado

En desarrollo activo. El pipeline de CI (`.github/workflows/ci.yml`) es la
fuente de verdad de qué debe pasar antes de mergear a `main`.

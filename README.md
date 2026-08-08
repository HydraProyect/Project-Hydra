# Hydra (CAE Manager)

Plataforma SaaS multi-tenant de gestión de Coordinación de Actividades Empresariales (CAE): documentación obligatoria de trabajadores, empresas y vehículos frente a los Clientes para los que trabajan, con vigencias, alertas y verificación asistida por IA. Ver `PROJECT.md` para el qué y el porqué del producto.

## Arranque en local

Requisitos: [.NET SDK 10](https://dotnet.microsoft.com/download), Docker (o un PostgreSQL propio), y [LibreOffice](https://www.libreoffice.org/) si vas a probar la conversión de Word a PDF (opcional para lo demás).

```bash
# 1. Base de datos — PostgreSQL en Docker, con los mismos usuario/contraseña/puerto
#    que ya esperan appsettings.json y los tests (cero configuración adicional).
docker compose up -d

# 2. Restaurar y compilar
dotnet restore CaeManager.slnx
dotnet build CaeManager.slnx

# 3. Arrancar la app — migra el esquema y siembra el Administrador inicial
#    automáticamente la primera vez (ver IdentitySeeder).
dotnet run --project src/CaeManager.Web
```

Abre `https://localhost:5001` (o el puerto que indique la consola) e inicia sesión con `admin@caemanager.local` / `CaeManager#2026` — credenciales de desarrollo local únicamente, nunca válidas así en un despliegue real (`AdministradorInicial__Email`/`AdministradorInicial__Contrasena` en `DEPLOY.md`).

Para poblar la base con datos de ejemplo (varios Clientes, Empresas, Trabajadores y documentación con vencimientos repartidos), añade a `src/CaeManager.Web/appsettings.Development.json`:

```json
{ "DatosPrueba": { "Activo": true } }
```

### Tests

```bash
dotnet test tests/CaeManager.Domain.Tests
dotnet test tests/CaeManager.Application.Tests
dotnet test tests/CaeManager.IntegrationTests   # contra el PostgreSQL de docker compose
dotnet test tests/CaeManager.Web.Tests          # componentes Blazor (bUnit)
```

Los tests E2E (`tests/CaeManager.E2ETests`, Playwright) arrancan su propia instancia de la app — ver ese proyecto antes de correrlos la primera vez (instalación de navegadores de Playwright).

## Estructura del repositorio

- `src/CaeManager.Domain` — entidades, invariantes de negocio. Sin dependencias externas.
- `src/CaeManager.Application` — Commands/Queries (CQRS ligero con MediatR), interfaces hacia Infrastructure.
- `src/CaeManager.Infrastructure` — EF Core/PostgreSQL, Identity, integraciones (IA, email, S3, KMS...).
- `src/CaeManager.Migrations.PostgreSQL` — migraciones de EF Core, en su propio ensamblado.
- `src/CaeManager.Web` — Blazor Server (interfaz), minimal API endpoints, `Program.cs`.
- `tests/` — un proyecto de test por capa, más E2E (Playwright) y componentes (bUnit).

## Documentación

Este repositorio documenta sus propias decisiones extensamente — pensado tanto para quien desarrolle como para sesiones de IA que trabajen sobre el código. Puntos de entrada según lo que busques:

| Si quieres... | Empieza por |
|---|---|
| Entender qué es el producto y a quién sirve | `PROJECT.md` |
| El modelo de dominio (agregados, invariantes) | `DOMAIN.md` |
| Cómo está organizado el código (capas, patrones) | `ARCHITECTURE.md` |
| Persistencia y la regla de negocio del estado de un Documento | `DATABASE.md` |
| Multi-tenancy: aislamiento, catálogos, resolución de tenant | `docs/MULTITENANCY.md` |
| Qué es "plataforma" vs. "módulo de negocio" en este código | `docs/PLATFORM.md` |
| Convenciones de código | `CODING_STANDARDS.md` |
| Diseño, UX e identidad visual | `01_PRODUCT_EXPERIENCE.md` … `08_COMPONENT_CATALOG.md` + `DESIGN_DECISION_LOG.md` (ver `docs/README.md`) |
| Desplegar (Railway) | `DEPLOY.md` |
| Historial de fases y qué queda pendiente | `ROADMAP.md` |
| El porqué de una decisión de arquitectura ya tomada | los `ADR-*.md` de la raíz |
| Informes de auditoría/análisis ya cerrados (no vigentes) | `docs/archive/` |
| Modelo de negocio, precios, mercado | `docs/business/` (empieza por `docs/business/README.md`) |

`CLAUDE.md` indexa todo esto con más detalle — es el punto de partida recomendado para cualquier sesión (humana o de IA) que vaya a tocar el código.

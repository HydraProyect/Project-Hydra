# Instrucciones para cualquier sesión de Claude en este repositorio

Lee esto antes de planificar cambios de arquitectura o de leer `ADR-001-multitenant.md`.

## Estado actual del producto (fuente de verdad: `ADR-002-single-tenant.md`)

CAE Manager (Project Hydra) es hoy software de **uso interno, single-tenant** — una sola organización, sin aislamiento entre clientes SaaS distintos. La vía "SaaS multi-cliente" (`ADR-001-multitenant.md`) está **en pausa**, no descartada. Antes de:

- Implementar `TenantId`, un Global Query Filter de tenant, o cualquier aislamiento multi-organización.
- Retomar el Issue #8 de GitHub (multi-tenant).
- Asumir en un plan que el destino del producto es SaaS multi-cliente.

**Para** y lee `ADR-002-single-tenant.md` completo. Si la tarea que te han pedido no menciona explícitamente "retomar multi-tenant" o "vender a un segundo cliente", asume que sigue en pausa y no lo construyas por iniciativa propia, aunque `ADR-001-multitenant.md` lo describa como "Decidido" — ese ADR se conserva como referencia técnica, no como estado vigente (tiene su propio aviso de pausa al principio).

Esto no significa que las obligaciones de RGPD/LOPDGDD se relajen — siguen aplicando igual al tratamiento de datos personales y de salud de trabajadores, uso interno o no. Ver `ADR-002-single-tenant.md` § 4 para la tabla de qué sigue siendo bloqueante y qué no.

## Documentos que hay que leer según la tarea

- `PROJECT.md` — qué es el producto, a quién sirve, principios de decisión (YAGNI, consistencia de patrones).
- `ARCHITECTURE.md` — capas, patrones, stack técnico.
- `DATABASE.md` — modelo de datos y regla de negocio central (cálculo de estado de Documento).
- `ROADMAP.md` — historial de fases, backlog, estado de la Iniciativa de hardening. Es largo — usa `grep`/búsqueda por sección en vez de leerlo entero salvo que la tarea lo requiera.
- `RGPD-TRATAMIENTO-DATOS.md` — qué datos personales se tratan, base legal, subencargados. No sustituye revisión legal.
- `ADR-001-multitenant.md` / `ADR-002-single-tenant.md` — ver arriba.
- `CODING_STANDARDS.md`, `DESIGN_SYSTEM.md`, `UX_PATTERNS.md` — convenciones de código y de producto, antes de escribir código o UI nueva.

## Reglas de trabajo ya establecidas en este proyecto (no las reinventes)

- YAGNI por encima de flexibilidad especulativa — no construyas para un caso hipotético futuro (`PROJECT.md` § Principios de decisión).
- Ningún Command/Query nuevo debe usar SQL crudo (`FromSqlRaw`/`ExecuteSqlRaw`) ni `IgnoreQueryFilters()` sin revisión explícita — es la propiedad que hace seguro el mecanismo de filtrado global (soft delete hoy, `TenantId` el día que se retome).
- Antes de cerrar cualquier fase/tarea de producto: verificación end-to-end en navegador (no solo tests), siguiendo el patrón ya usado en todas las fases de `ROADMAP.md`.
- No implementes nada de cumplimiento normativo (retención, derecho al olvido, DPIA, DPA) sin confirmar primero con el usuario — son decisiones con componente legal, no solo técnico.

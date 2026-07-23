# Instrucciones para cualquier sesión de Claude en este repositorio

Lee esto antes de planificar cambios de arquitectura.

## Estado actual del producto (fuente de verdad: `ADR-003-saas-multitenant.md`)

Hydra (CAE Manager) es una **plataforma SaaS multi-tenant** en construcción: producto comercial para consultoras de PRL y empresas contratistas (decisión 2026-07-23, que supersede la pausa de `ADR-002-single-tenant.md`). La organización que hoy usa el sistema en producción será el tenant #1.

**Estado de la implementación multi-tenant**: en fase de consolidación documental → plan de migración → implementación (ver secuencia en `ADR-003`). **No asumas que el aislamiento por tenant ya existe en el código** hasta que `ROADMAP.md` registre esa fase como completada. Antes de tocar nada de `TenantId`, filtros globales o aprovisionamiento de tenants, lee `docs/MULTITENANCY.md` (documento normativo: reglas de aislamiento, catálogos global/por-tenant, Tenant Resolution Strategy) e `INFORME-MULTITENANT.md` (análisis y estrategia de migración por etapas).

Las obligaciones RGPD/LOPDGDD siguen aplicando íntegras al tratamiento de datos personales y de salud de trabajadores — y la vía SaaS **reactiva** además las obligaciones de encargado del tratamiento frente a cada tenant (DPA, términos de uso). Ver `ADR-003` § condiciones de salida.

## Documentos que hay que leer según la tarea

- `PROJECT.md` — qué es el producto, a quién sirve, principios de decisión (YAGNI, consistencia de patrones).
- `DOMAIN.md` — modelo de dominio: agregados, relaciones e invariantes (fuente de verdad conceptual).
- `ARCHITECTURE.md` — capas, patrones, stack técnico.
- `DATABASE.md` — persistencia y regla de negocio central (cálculo de estado de Documento).
- `docs/MULTITENANCY.md` — normativa multi-tenant: aislamiento, catálogos, resolución de tenant.
- `ADR-001` (guía técnica multi-tenant, reactivada) · `ADR-002` (superseded, histórico) · `ADR-003` (decisión vigente).
- `INFORME-MULTITENANT.md` / `PLAN-MIGRACION-MULTITENANT.md` — análisis y plan de ejecución del multi-tenant, por etapas.
- `ARQUITECTURA-INTEGRACIONES.md` — diseño de la futura Plataforma de Integraciones (Dokify, 6Coordina, CTAIMA...); backlog, no implementado — léelo antes de tomar cualquier decisión de multi-tenant/credenciales/jobs de fondo que pudiera cerrarle puertas.
- `ROADMAP.md` — historial de fases y backlog. Es largo — usa `grep` por sección en vez de leerlo entero.
- `RGPD-TRATAMIENTO-DATOS.md` — datos personales tratados, base legal, subencargados. No sustituye revisión legal.
- `CODING_STANDARDS.md`, `DESIGN_SYSTEM.md`, `UX_PATTERNS.md` — convenciones de código y producto, antes de escribir código o UI nueva.
- `PLAN-MASTER-DETAIL-WORKSPACE.md` / `PLAN-CONTEXT-WORKSPACE.md` — rediseño de navegación contextual (diseño en debate, implementación pendiente).

## Reglas de trabajo ya establecidas en este proyecto (no las reinventes)

- YAGNI por encima de flexibilidad especulativa — no construyas para un caso hipotético futuro (`PROJECT.md` § Principios de decisión).
- Ningún Command/Query nuevo usa SQL crudo (`FromSqlRaw`/`ExecuteSqlRaw`) ni `IgnoreQueryFilters()` sin revisión explícita — es la propiedad que hace seguro el filtrado global (soft delete hoy; **frontera de seguridad entre tenants** cuando se active el filtro de `TenantId`).
- Ninguna feature nueva introduce una tabla sin `TenantId`, salvo catálogo global justificado y documentado en `docs/MULTITENANCY.md` § 7.
- Todo Command que reciba Ids de otras entidades las carga antes de usarlas (con el filtro de tenant activo, un Id ajeno debe resultar "no encontrado").
- Antes de cerrar cualquier fase/tarea de producto: verificación end-to-end en navegador (no solo tests), siguiendo el patrón de todas las fases de `ROADMAP.md`.
- No implementes nada de cumplimiento normativo (retención, derecho al olvido, DPIA, DPA, términos de uso) sin confirmar primero con el usuario — son decisiones con componente legal, no solo técnico.
- No mezcles refactors independientes en un mismo cambio (ej.: unificación de las 3 clases de credenciales, Context Workspace y multi-tenant son trabajos separados).

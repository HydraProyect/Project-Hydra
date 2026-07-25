# Instrucciones para cualquier sesión de Claude en este repositorio

Lee esto antes de planificar cambios de arquitectura.

## Estado actual del producto (fuente de verdad: `ADR-003-saas-multitenant.md`)

Hydra (CAE Manager) es una **plataforma SaaS multi-tenant**: producto comercial para consultoras de PRL y empresas contratistas (decisión 2026-07-23, que supersede la pausa de `ADR-002-single-tenant.md`). La organización que hoy usa el sistema en producción es el tenant #1.

**Estado de la implementación multi-tenant**: ✅ **implementada y validada** (`ROADMAP.md`, "Decisión multi-tenant", 2026-07-24) — `TenantId` en las 25 tablas de dominio, filtro global + interceptor de sellado, índices únicos compuestos, almacenamiento particionado por tenant, 25 tests de aislamiento por agregado. Antes de tocar nada de `TenantId`, filtros globales o aprovisionamiento de tenants, lee igualmente `docs/MULTITENANCY.md` (documento normativo: reglas de aislamiento, catálogos global/por-tenant, Tenant Resolution Strategy) — sigue siendo la frontera de seguridad más sensible del sistema, ya construida no significa "sin cuidado". **Pendiente, no implementado**: `ADR-004-delegacion-consultoras-cae.md` — modelo de delegación reversible para que una consultora (Geseme) opere sobre tenants ajenos sin poseerlos (diseño cerrado 2026-07-25, condición para el primer cliente real). Y de las condiciones de salida a producción de `ADR-003`: migración a PostgreSQL y DPA/Términos de Uso por tenant siguen sin hacer.

Las obligaciones RGPD/LOPDGDD siguen aplicando íntegras al tratamiento de datos personales y de salud de trabajadores — y la vía SaaS **reactiva** además las obligaciones de encargado del tratamiento frente a cada tenant (DPA, términos de uso). Ver `ADR-003` § condiciones de salida.

## Documentos que hay que leer según la tarea

- `PROJECT.md` — qué es el producto, a quién sirve, principios de decisión (YAGNI, consistencia de patrones).
- `DOMAIN.md` — modelo de dominio: agregados, relaciones e invariantes (fuente de verdad conceptual).
- `ARCHITECTURE.md` — capas, patrones, stack técnico.
- `DATABASE.md` — persistencia y regla de negocio central (cálculo de estado de Documento).
- `docs/PLATFORM.md` — qué es Hydra como plataforma: kernel transversal (MultiTenant/Identity/Authorization/Integrations/AI/Notifications/Storage/Observability/Background Jobs/Feature Flags/Licensing) vs. módulos de negocio (CAE). Léelo antes de decidir si algo nuevo es kernel o dominio. **Con este documento se cierra la fase de consolidación documental** — lo siguiente es implementación (`PLAN-MIGRACION-MULTITENANT.md`), no más documentos de arquitectura salvo necesidad real.
- `docs/MULTITENANCY.md` — normativa multi-tenant: aislamiento, catálogos, resolución de tenant.
- `docs/business/` — capa de documentación de negocio (modelo de ingresos, ICP, pricing, lenguaje ubicuo de negocio). Empieza por `docs/business/README.md`. **`docs/business/UBIQUITOUS_LANGUAGE.md` es normativo para los términos `Approved`** (Cliente/Cliente Directo/Cliente Delegante, Delegated Workspace...) — no redefinas ahí un término que ya tenga entrada, ni reintroduzcas "Workspace" a secas para nada de negocio (colisión ya resuelta con el Context Workspace técnico, ver ese documento § "Colisiones de nombre").
- `ADR-001` (guía técnica multi-tenant, reactivada) · `ADR-002` (superseded, histórico) · `ADR-003` (decisión vigente) · `ADR-004` (delegación de gestión CAE a consultoras externas — Delegated Workspace —, diseño pendiente de implementar).
- `INFORME-MULTITENANT.md` / `PLAN-MIGRACION-MULTITENANT.md` — análisis y plan de ejecución del multi-tenant, por etapas.
- `ARQUITECTURA-INTEGRACIONES.md` — diseño de la futura Plataforma de Integraciones (Dokify, 6Coordina, CTAIMA...), basado en capacidades (`CapacidadesIntegracion`) y versionado de API, no en nombres de proveedor; backlog, no implementado — léelo antes de tomar cualquier decisión de multi-tenant/credenciales/jobs de fondo que pudiera cerrarle puertas.
- `docs/INTEGRATION_GUIDELINES.md` — guía paso a paso para construir un conector nuevo, cuando llegue el primero (no antes).
- `ROADMAP.md` — historial de fases y backlog. Es largo — usa `grep` por sección en vez de leerlo entero.
- `RGPD-TRATAMIENTO-DATOS.md` — datos personales tratados, base legal, subencargados. No sustituye revisión legal.
- `CODING_STANDARDS.md`, `DESIGN_SYSTEM.md`, `UX_PATTERNS.md` — convenciones de código y producto, antes de escribir código o UI nueva.
- `PLAN-MASTER-DETAIL-WORKSPACE.md` / `PLAN-CONTEXT-WORKSPACE.md` — rediseño de navegación contextual (diseño en debate, implementación pendiente).

## Disciplina de decisión para cambios de arquitectura

Cuando la tarea toque más que una feature aislada (multi-tenant, integraciones, IA, observabilidad — cualquier decisión "de plataforma"), resuelve en este orden y no lo saltees: **1. Dominio** (qué representa el negocio) → **2. Arquitectura** (cómo se organiza el sistema) → **3. Plataforma** (multi-tenancy/integraciones/IA/observabilidad como capacidades transversales) → **4. Implementación** (código). Documentar en ese orden es lo que permite incorporar una capacidad nueva sin reabrir las anteriores — ver `ARQUITECTURA-INTEGRACIONES.md` § 0 como ejemplo aplicado.

## Reglas de trabajo ya establecidas en este proyecto (no las reinventes)

- YAGNI por encima de flexibilidad especulativa — no construyas para un caso hipotético futuro (`PROJECT.md` § Principios de decisión).
- Ningún Command/Query nuevo usa SQL crudo (`FromSqlRaw`/`ExecuteSqlRaw`) ni `IgnoreQueryFilters()` sin revisión explícita — es la propiedad que hace seguro el filtrado global (soft delete hoy; **frontera de seguridad entre tenants** cuando se active el filtro de `TenantId`).
- Ninguna feature nueva introduce una tabla sin `TenantId`, salvo catálogo global justificado y documentado en `docs/MULTITENANCY.md` § 7.
- Todo Command que reciba Ids de otras entidades las carga antes de usarlas (con el filtro de tenant activo, un Id ajeno debe resultar "no encontrado").
- Antes de cerrar cualquier fase/tarea de producto: verificación end-to-end en navegador (no solo tests), siguiendo el patrón de todas las fases de `ROADMAP.md`.
- No implementes nada de cumplimiento normativo (retención, derecho al olvido, DPIA, DPA, términos de uso) sin confirmar primero con el usuario — son decisiones con componente legal, no solo técnico.
- No mezcles refactors independientes en un mismo cambio (ej.: unificación de las 3 clases de credenciales, Context Workspace y multi-tenant son trabajos separados).

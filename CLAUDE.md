# Instrucciones para cualquier sesión de Claude en este repositorio

Lee esto antes de planificar cambios de arquitectura.

## Estado actual del producto (fuente de verdad: `ADR-003-saas-multitenant.md`)

Hydra (CAE Manager) es una **plataforma SaaS multi-tenant**: producto comercial para consultoras de PRL y empresas contratistas (decisión 2026-07-23, que supersede la pausa de `ADR-002-single-tenant.md`). La organización que hoy usa el sistema en producción es el tenant #1.

**Estado de la implementación multi-tenant**: ✅ **implementada y validada** (`ROADMAP.md`, "Decisión multi-tenant", 2026-07-24) — `TenantId` en las 25 tablas de dominio, filtro global + interceptor de sellado, índices únicos compuestos, almacenamiento particionado por tenant, 25 tests de aislamiento por agregado. Antes de tocar nada de `TenantId`, filtros globales o aprovisionamiento de tenants, lee igualmente `docs/MULTITENANCY.md` (documento normativo: reglas de aislamiento, catálogos global/por-tenant, Tenant Resolution Strategy) — sigue siendo la frontera de seguridad más sensible del sistema, ya construida no significa "sin cuidado". `ADR-004-delegacion-consultoras-cae.md` (delegación reversible para que una consultora opere sobre tenants ajenos sin poseerlos) está **implementado salvo el alta**: `DelegacionTenant` + `AsignacionOperadorDelegado` en dominio, con sus repositorios, configuraciones, migración, `ObtenerClientesAutorizadosQuery`, el endpoint `/cuenta/cliente-activo`, el selector de Delegated Workspace en la interfaz y la pantalla `/delegaciones` para revocar/reactivar una delegación y retirar operadores (cualquiera de las dos partes puede revocar; se decide con el tenant de **origen**, nunca con el Delegated Workspace activo). **Lo que sigue sin flujo de producto es crear una delegación nueva**: `CrearDelegacionTenantCommand` y `CrearAsignacionOperadorDelegadoCommand` existen pero no se despachan desde ninguna UI, porque `ADR-004` § 12.2 deja abierto a propósito quién puede iniciarla (¿el cliente, la consultora, ambos?) y lo marca como decisión con implicaciones comerciales. Hoy solo las siembra `DelegacionDemoSeeder`.

**Acceso de soporte** (Fase 60): existe como `DelegacionTenant` de propósito `Soporte`, no como rol que cruce tenants. Nace inactiva, exige motivo y caducidad al activarse, se provisiona sola para cada tenant nuevo, y traza navegación y clicks (`RegistroActividadSoporte`) contra el tenant visitado. Si vas a tocarlo, no lo conviertas en un rol global — esa puerta se cerró a propósito.

**Retención de datos** (Fase 60): ciclo completo en `/retencion` (detectar → avisar → autorizar con fecha → ejecutar), apagado por defecto (`RetencionDatos:Activa`). La invariante que no se negocia: **no hay camino a "ejecutada" sin autorización expresa con fecha**. Criterios legales en `RGPD-TRATAMIENTO-DATOS.md` § 5, ya decididos por el usuario — no los redefinas.

**Pendiente de verdad**: de las condiciones de salida a producción de `ADR-003`, la migración a PostgreSQL y el DPA/Términos de Uso por tenant siguen sin hacer. El DPA además tiene que declarar el acceso de soporte antes de usarlo con un cliente real.

Las obligaciones RGPD/LOPDGDD siguen aplicando íntegras al tratamiento de datos personales y de salud de trabajadores — y la vía SaaS **reactiva** además las obligaciones de encargado del tratamiento frente a cada tenant (DPA, términos de uso). Ver `ADR-003` § condiciones de salida.

## Documentos que hay que leer según la tarea

- `PROJECT.md` — qué es el producto, a quién sirve, principios de decisión (YAGNI, consistencia de patrones).
- `DOMAIN.md` — modelo de dominio: agregados, relaciones e invariantes (fuente de verdad conceptual).
- `ARCHITECTURE.md` — capas, patrones, stack técnico.
- `DATABASE.md` — persistencia y regla de negocio central (cálculo de estado de Documento).
- `docs/PLATFORM.md` — qué es Hydra como plataforma: kernel transversal (MultiTenant/Identity/Authorization/Integrations/AI/Notifications/Storage/Observability/Background Jobs/Feature Flags/Licensing) vs. módulos de negocio (CAE). Léelo antes de decidir si algo nuevo es kernel o dominio. **Con este documento se cierra la fase de consolidación documental** — lo siguiente es implementación (`PLAN-MIGRACION-MULTITENANT.md`), no más documentos de arquitectura salvo necesidad real.
- `docs/MULTITENANCY.md` — normativa multi-tenant: aislamiento, catálogos, resolución de tenant.
- `docs/business/` — capa de documentación de negocio (modelo de ingresos, ICP, pricing, lenguaje ubicuo de negocio). Empieza por `docs/business/README.md`. **`docs/business/UBIQUITOUS_LANGUAGE.md` es normativo para los términos `Approved`** (Cliente/Cliente Directo/Cliente Delegante, Delegated Workspace...) — no redefinas ahí un término que ya tenga entrada, ni reintroduzcas "Workspace" a secas para nada de negocio (colisión ya resuelta con el Context Workspace técnico, ver ese documento § "Colisiones de nombre").
- `ADR-001` (guía técnica multi-tenant, reactivada) · `ADR-002` (superseded, histórico) · `ADR-003` (decisión vigente) · `ADR-004` (delegación de gestión CAE a consultoras externas — Delegated Workspace —, implementado salvo el alta de delegaciones, ver arriba).
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
- Todo Command que **edite** un agregado lleva la `Version` que vio el usuario y la compara con `ConcurrenciaOptimista`. Confiar en el token de EF a secas **no funciona aquí**: los handlers recargan la entidad antes de guardar, así que EF compara la versión consigo misma y el segundo guardado pisa al primero sin avisar (verificado con dos pestañas, Fase 60).
- Todo Command que reciba Ids de otras entidades las carga antes de usarlas (con el filtro de tenant activo, un Id ajeno debe resultar "no encontrado").
- Antes de cerrar cualquier fase/tarea de producto: verificación end-to-end en navegador (no solo tests), siguiendo el patrón de todas las fases de `ROADMAP.md`.
- No implementes nada de cumplimiento normativo (retención, derecho al olvido, DPIA, DPA, términos de uso) sin confirmar primero con el usuario — son decisiones con componente legal, no solo técnico.
- No mezcles refactors independientes en un mismo cambio (ej.: unificación de las 3 clases de credenciales, Context Workspace y multi-tenant son trabajos separados).

## Disciplina de tokens (aplica a toda sesión, no solo a cambios de arquitectura)

Este repo tiene muchos documentos normativos largos (`ROADMAP.md`, `docs/business/`, ADRs). Leer de más es el mayor costo de tokens aquí — la sección anterior ya dice qué leer *según la tarea*; esto es *cómo* leerlo y cómo trabajar el código.

- **Lee solo lo que la tarea exige.** No abras los ~15 documentos de la lista "por si acaso" — usa la tabla de arriba para identificar los 2-3 relevantes. Dentro de un doc largo, usa grep/búsqueda por sección en vez de verlo entero (`ROADMAP.md` y `docs/business/` ya lo piden explícitamente).
- **No releas un documento ya leído en esta sesión**, salvo que haya podido cambiar (ej. tras editar `docs/MULTITENANCY.md` en el mismo hilo).
- **Edición quirúrgica, no reescritura.** Reemplazo parcial (Edit) sobre archivos existentes; `Write` completo solo si el cambio es >80% del archivo. No "limpies" código alrededor del cambio pedido.
- **No narres el plan antes de ejecutar.** El usuario ve los tool calls; no hace falta un preview en texto de "voy a leer X, luego editar Y".
- **Respuestas cortas.** Sin preámbulo, sin resumen final, sin repetir lo que pidió el usuario. Si ya editaste un archivo o creaste uno, no lo copies entero en la respuesta — el diff ya lo muestra.
- **Paraleliza lecturas independientes** (ej. `DOMAIN.md` + `ARCHITECTURE.md` + `DATABASE.md` para una feature nueva) en vez de una por una.
- **Cero relleno conversacional** ("Excelente pregunta", "Perfecto", etc.) — directo al trabajo.
- **Sin abstracciones no pedidas.** Esto es consistente con YAGNI (`PROJECT.md`): no agregues helpers, capas o validación especulativa que no pidió la tarea, aunque "se vea más limpio".
- **Valida antes de decir "hecho"**: build/tests como mínimo; la verificación end-to-end en navegador solo aplica al cerrar fase/tarea de producto (ya está arriba), no a cada micro-cambio.
- **Si el usuario da una instrucción directa, ejecútala.** Si hay un riesgo real (seguridad, pérdida de datos, o choca con una regla ya establecida arriba —p.ej. tocar `TenantId` sin filtro, o SQL crudo—), dilo en una frase y procede según lo que decida el usuario.

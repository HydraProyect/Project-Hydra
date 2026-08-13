# Instrucciones para cualquier sesión de Claude en este repositorio

Lee esto antes de planificar cambios de arquitectura.

## Gobernanza común

Antes de interpretar una tarea, lee `docs/AGENT_GOVERNANCE.md`. Define la
jerarquía de autoridad, qué puede implementar o solo proponer un agente y cómo
dejar decisiones pendientes sin bloquear el trabajo. Esta sección complementa,
pero no sustituye, las reglas específicas que siguen.

## Qué no entra en este repositorio (es público)

Este repositorio es público a propósito — decisión del usuario, ver `docs/AGENT_GOVERNANCE.md`
§ 1 para la jerarquía de autoridad. Eso no es un descuido a corregir; es una restricción de
diseño que **cualquier documento o dato nuevo** tiene que respetar, no solo el código.
Historial: tres purgas completas de historial ya hechas (2026-08-11, identificadores del
tenant M365; 2026-08-13, toda `docs/business/`; 2026-08-13, informes de auditoría de
seguridad) — la meta es que no haga falta una cuarta. Lección de la tercera: cuando un
documento se movió de sitio en algún momento de su historia (p. ej. de la raíz a
`docs/archive/`), purgar solo su ruta actual no basta — hay que purgar **todas** las rutas
que ocupó, o el contenido sigue accesible bajo el nombre antiguo en los commits de antes del
movimiento. Verifica con `git log --all --follow --name-only -- <ruta actual>` antes de dar
una purga por completa.

**Nunca en un commit de este repositorio**, sin excepción por conveniencia ni por prisa:

- Identificadores reales de infraestructura (Tenant ID, Client ID/Secret, cuentas de
  administración, dominios internos, nombres de bucket, ARNs) — ni siquiera los que no son
  técnicamente "secretos" (un Tenant ID viaja en claro en cualquier request), porque publicados
  son material de reconocimiento y un UPN de admin es un objetivo de phishing con nombre y
  apellidos.
- Estrategia de negocio (pricing, competidores, go-to-market, unit economics, ICP) y
  documentación legal real (DPA, contratos, políticas internas no destinadas a publicarse
  tal cual).
- Nombres reales de clientes, empresas o personas — salvo los ya aprobados como ficticios
  (ArcoSPA/Ibertec/EcoPlant/Obras Reyval en `ADR-004`/`docs/MULTITENANCY.md`, confirmado con
  el usuario 2026-08-13; no asumas que cualquier nombre nuevo lo es sin confirmar).
- Material de marca/identidad real (nombre comercial, lockups, dominio del sitio) — el
  repositorio solo conoce el nombre histórico genérico (`CaeManager.Application.Common.Marca`),
  el real entra por configuración. Ver el comentario de `branding/` en `.gitignore`.
- Informes de auditoría de seguridad, pentesting o hallazgos de vulnerabilidades con detalle
  de explotación (ubicación exacta, payload, comando reproducible) — **incluso si el hallazgo
  ya está cerrado**. Un informe de vulnerabilidad ya parcheada sigue siendo un mapa de qué
  clase de fallo buscar a continuación; y "cerrado" en el propio informe no siempre significa
  cerrado del todo — verifica contra el código antes de asumirlo (pasó con `INFORME-AUDITORIA-2.md`:
  parte de lo que decía "latente" dejó de serlo en cuanto un módulo pasó a producción).

**Dónde va en su lugar**, según qué sea:

- Documentación de negocio o legal → el repositorio local `C:\Users\chris\Project-Hydra-Negocio`
  (sin remoto, ver más abajo) — la tarea que la necesite se inicia ahí, no aquí.
- Informes de auditoría de seguridad → el mismo repositorio local, carpeta `seguridad/`.
- Un valor real puntual dentro de un documento técnico que por lo demás sí debe vivir aquí
  (identificadores, credenciales concretas de un runbook) → el documento versionado se queda
  con placeholders, y el valor real va en un fichero `NOMBRE.local.md` junto a él, cubierto por
  el patrón `*.local.md` de `.gitignore`. Patrón ya aplicado en `RUNBOOK-GRAPH-M365.md` /
  `RUNBOOK-GRAPH-M365.local.md` — cópialo para el siguiente caso en vez de inventar uno nuevo.
- Material de marca/identidad real → `branding/` (ya en `.gitignore`, sin nombre propio
  siquiera en el comentario, a propósito).

**La duda se resuelve a favor de la privacidad**: si no está claro si algo de lo anterior
aplica a un documento nuevo, trátalo como si aplicara y pregunta al usuario antes de
commitear — no lo subas primero "para revisar después". Esta regla se cumple en el mismo
cambio que introduce el documento, no en una limpieza posterior.

## Estado actual del producto (fuente de verdad: `ADR-003-saas-multitenant.md`)

Hydra (CAE Manager) es una **plataforma SaaS multi-tenant**: producto comercial para consultoras de PRL y empresas contratistas (decisión 2026-07-23, que supersede la pausa de `ADR-002-single-tenant.md`). La organización que hoy usa el sistema en producción es el tenant #1.

**Estado de la implementación multi-tenant**: ✅ **implementada y validada** (`ROADMAP.md`, "Decisión multi-tenant", 2026-07-24) — `TenantId` en 46 entidades de dominio, filtro global + interceptor de sellado, índices únicos compuestos, almacenamiento particionado por tenant, 40 tests de aislamiento por agregado. Antes de tocar nada de `TenantId`, filtros globales o aprovisionamiento de tenants, lee igualmente `docs/MULTITENANCY.md` (documento normativo: reglas de aislamiento, catálogos global/por-tenant, Tenant Resolution Strategy) — sigue siendo la frontera de seguridad más sensible del sistema, ya construida no significa "sin cuidado". `ADR-004-delegacion-consultoras-cae.md` (delegación reversible para que una consultora opere sobre tenants ajenos sin poseerlos) está **implementado, incluida el alta** (P0-7, `CrearClienteDeleganteCommand` desde el botón "Nueva delegación" de `/delegaciones`, restringido a Administrador de plataforma): `DelegacionTenant` + `AsignacionOperadorDelegado` en dominio, con sus repositorios, configuraciones, migración, `ObtenerClientesAutorizadosQuery`, el endpoint `/cuenta/cliente-activo`, el selector de Delegated Workspace en la interfaz y la pantalla `/delegaciones` para dar de alta, revocar/reactivar una delegación y retirar operadores (cualquiera de las dos partes puede revocar; se decide con el tenant de **origen**, nunca con el Delegated Workspace activo). `ADR-004` § 12.2 deja abierto a propósito el autoservicio (¿puede el propio Cliente Directo o la Consultora iniciar la delegación sin pasar por el Administrador de plataforma?) — v1 es solo-plataforma, no autoservicio.

**Acceso de soporte** (Fase 60): existe como `DelegacionTenant` de propósito `Soporte`, no como rol que cruce tenants. Nace inactiva, exige motivo y caducidad al activarse, se provisiona sola para cada tenant nuevo, y traza navegación y clicks (`RegistroActividadSoporte`) contra el tenant visitado. Si vas a tocarlo, no lo conviertas en un rol global — esa puerta se cerró a propósito.

**Retención de datos** (Fase 60): ciclo completo en `/retencion` (detectar → avisar → autorizar con fecha → ejecutar), apagado por defecto (`RetencionDatos:Activa`). La invariante que no se negocia: **no hay camino a "ejecutada" sin autorización expresa con fecha**. Criterios legales en `RGPD-TRATAMIENTO-DATOS.md` § 5, ya decididos por el usuario — no los redefinas.

**Pendiente de verdad**: de las condiciones de salida a producción de `ADR-003`, el paquete legal ya tiene 16 borradores completos en `legal/` del repositorio local de negocio (ver más abajo) — DPA, Términos y Condiciones, Política de Privacidad, Anexos de Seguridad/IA/Terceros M365, etc. — desde `LEGAL_FRAMEWORK.md`; lo que falta es la **revisión legal y firma real por tenant**, no la redacción (la migración a PostgreSQL se ejecutó en producción el 2026-08-01 — ver `RUNBOOK-MIGRACION-POSTGRESQL.md`; la rama SQLite se retiró del código). El DPA además tiene que declarar el acceso de soporte y el conector de Microsoft 365 antes de usarlo con un cliente real.

Las obligaciones RGPD/LOPDGDD siguen aplicando íntegras al tratamiento de datos personales y de salud de trabajadores — y la vía SaaS **reactiva** además las obligaciones de encargado del tratamiento frente a cada tenant (DPA, términos de uso). Ver `ADR-003` § condiciones de salida.

## Documentos que hay que leer según la tarea

- `PROJECT.md` — qué es el producto, a quién sirve, principios de decisión (YAGNI, consistencia de patrones).
- `DOMAIN.md` — modelo de dominio: agregados, relaciones e invariantes (fuente de verdad conceptual).
- `ARCHITECTURE.md` — capas, patrones, stack técnico.
- `DATABASE.md` — persistencia y regla de negocio central (cálculo de estado de Documento).
- `docs/PLATFORM.md` — qué es Hydra como plataforma: kernel transversal (MultiTenant/Identity/Authorization/Integrations/AI/Notifications/Storage/Observability/Background Jobs/Feature Flags/Licensing) vs. módulos de negocio (CAE). Léelo antes de decidir si algo nuevo es kernel o dominio. **Con este documento se cierra la fase de consolidación documental** — lo siguiente es implementación (`PLAN-MIGRACION-MULTITENANT.md`), no más documentos de arquitectura salvo necesidad real.
- `docs/MULTITENANCY.md` — normativa multi-tenant: aislamiento, catálogos, resolución de tenant.
- **Documentación de negocio (modelo de ingresos, ICP, pricing, legal, lenguaje ubicuo de negocio) ya NO vive en este repositorio** (2026-08-13) — este repositorio es público, y esos documentos no aportan nada a quien solo necesita compilar o desplegar. Viven en un repositorio git local aparte, `C:\Users\chris\Project-Hydra-Negocio` (sin remoto — la copia de seguridad es el respaldo diario a Drive, ver `scripts/respaldo-local.ps1`), con la misma estructura relativa que tenía `docs/business/` (`README.md`, `UBIQUITOUS_LANGUAGE.md`, `legal/`, `inbound/`...). **Cualquier tarea que necesite consultar o editar esos documentos debe iniciarse desde ese repositorio local, no desde una sesión que solo tenga clonado `Project-Hydra`.** `UBIQUITOUS_LANGUAGE.md` sigue siendo normativo para los términos `Approved` (Cliente/Cliente Directo/Cliente Delegante, Delegated Workspace...) — no redefinas ahí un término que ya tenga entrada, ni reintroduzcas "Workspace" a secas para nada de negocio (colisión ya resuelta con el Context Workspace técnico, ver ese documento § "Colisiones de nombre"). Las citas a `docs/business/...` que quedan en comentarios de código y en `ROADMAP.md` (identificadores de ticket P0/P1/P2/P3) son punteros históricos válidos como ID, aunque el archivo ya no esté aquí — no los "arregles" reescribiéndolos.
- `ADR-001` (guía técnica multi-tenant, reactivada) · `ADR-002` (superseded, histórico) · `ADR-003` (decisión vigente) · `ADR-004` (delegación de gestión CAE a consultoras externas — Delegated Workspace —, implementado salvo el alta de delegaciones, ver arriba).
- `INFORME-MULTITENANT.md` / `PLAN-MIGRACION-MULTITENANT.md` — análisis y plan de ejecución del multi-tenant, por etapas.
- `ARQUITECTURA-INTEGRACIONES.md` — diseño de la futura Plataforma de Integraciones (Dokify, 6Coordina, CTAIMA...), basado en capacidades (`CapacidadesIntegracion`) y versionado de API, no en nombres de proveedor; backlog, no implementado — léelo antes de tomar cualquier decisión de multi-tenant/credenciales/jobs de fondo que pudiera cerrarle puertas.
- `docs/INTEGRATION_GUIDELINES.md` — guía paso a paso para construir un conector nuevo, cuando llegue el primero (no antes).
- `ROADMAP.md` — historial de fases y backlog. Es largo — usa `grep` por sección en vez de leerlo entero.
- `RGPD-TRATAMIENTO-DATOS.md` — datos personales tratados, base legal, subencargados. No sustituye revisión legal.
- `CODING_STANDARDS.md` — convenciones de código, antes de escribir código.
- **Diseño y UX: los ocho documentos normativos del reset (2026-08-08).** Sustituyen a
  `DESIGN_SYSTEM.md`, `UX_PATTERNS.md`, `PLAN-CONTEXT-WORKSPACE.md` y
  `PLAN-MASTER-DETAIL-WORKSPACE.md`, que quedan como **histórico y no se consultan para trabajo
  nuevo**. Lee solo el que corresponda a la tarea:

  | Pregunta | Documento |
  |---|---|
  | Qué debe conseguir la experiencia | `01_PRODUCT_EXPERIENCE.md` |
  | Colores, superficies, tipografía, iconografía | `02_BRAND_AND_VISUAL_IDENTITY.md` |
  | Dónde vive una pantalla, navegación, rutas | `03_INFORMATION_ARCHITECTURE.md` |
  | Cómo se comporta una interacción, microcopy | `04_UX_PATTERNS.md` |
  | Cómo se estructura una superficie de trabajo | `05_WORKSPACE_PATTERNS.md` |
  | Tokens y reglas de sistema | `06_DESIGN_SYSTEM.md` |
  | Qué movimiento existe y qué comunica | `07_MOTION_SYSTEM.md` |
  | Qué componentes hay y cuándo se usan | `08_COMPONENT_CATALOG.md` |
  | Cómo se aplica a una superficie concreta | `docs/blueprints/` |
  | **Por qué existe una regla y qué reemplaza** | `DESIGN_DECISION_LOG.md` |

  **Reglas de esa normativa que aplican a cualquier sesión**: no se resuelve en silencio un
  conflicto entre documentos — se registra en el Decision Log (DDL-024); no se añade un patrón,
  un token o un efecto de movimiento sin la decisión previa (DDL-040, `07` § 8); y ningún
  documento afirma como construido algo que no lo está (DDL-023). El Context Workspace sigue
  teniendo huecos reales declarados en `05` § 3.6 (deep-link, cierre al navegar, teclado): no
  los des por hechos.

## Disciplina de decisión para cambios de arquitectura

Cuando la tarea toque más que una feature aislada (multi-tenant, integraciones, IA, observabilidad — cualquier decisión "de plataforma"), resuelve en este orden y no lo saltees: **1. Dominio** (qué representa el negocio) → **2. Arquitectura** (cómo se organiza el sistema) → **3. Plataforma** (multi-tenancy/integraciones/IA/observabilidad como capacidades transversales) → **4. Implementación** (código). Documentar en ese orden es lo que permite incorporar una capacidad nueva sin reabrir las anteriores — ver `ARQUITECTURA-INTEGRACIONES.md` § 0 como ejemplo aplicado.

## Reglas de trabajo ya establecidas en este proyecto (no las reinventes)

- **Requerimiento global nº 1 — datos de prueba en toda funcionalidad nueva.** Ninguna funcionalidad se da por terminada sin datos de prueba que permitan ejercitarla de inicio a fin con **todas sus variantes** (cada estado, cada rama, cada rol implicado). Antes de cerrar la tarea: (1) verifica que la siembra existente (`src/CaeManager.Infrastructure/Persistence/Seed/` — `DatosPruebaSeeder` y los seeders que lo acompañan, activados con `DatosPrueba:Activo`) ya cubre los flujos y casos de la funcionalidad, incluidos los **usuarios** necesarios para cada flujo (hoy se siembran 3 usuarios por cada rol de `Roles.Todos`, contraseña en `DatosPruebaSeeder.ContrasenaUsuariosPrueba`); (2) si la funcionalidad introduce entidades, estados, roles o ramas que la siembra actual no cubre, **extender el seeder correspondiente forma parte del mismo cambio**, no de un trabajo posterior. Reglas de la siembra que no se rompen: por tenant y con el filtro global activo, idempotente (guard de "ya sembrado" por tenant), apagada por defecto, y con nombres ficticios de caricaturas — nunca datos que puedan confundirse con personas o empresas reales. Esto alimenta (no sustituye) la verificación end-to-end en navegador exigida más abajo.
- YAGNI por encima de flexibilidad especulativa — no construyas para un caso hipotético futuro (`PROJECT.md` § Principios de decisión).
- Ningún Command/Query nuevo usa SQL crudo (`FromSqlRaw`/`ExecuteSqlRaw`) ni `IgnoreQueryFilters()` sin revisión explícita — es la propiedad que hace seguro el filtrado global (soft delete hoy; **frontera de seguridad entre tenants** cuando se active el filtro de `TenantId`).
- Ninguna feature nueva introduce una tabla sin `TenantId`, salvo catálogo global justificado y documentado en `docs/MULTITENANCY.md` § 7.
- Todo Command que **edite** un agregado lleva la `Version` que vio el usuario y la compara con `ConcurrenciaOptimista`. Confiar en el token de EF a secas **no funciona aquí**: los handlers recargan la entidad antes de guardar, así que EF compara la versión consigo misma y el segundo guardado pisa al primero sin avisar (verificado con dos pestañas, Fase 60).
- Todo Command que reciba Ids de otras entidades las carga antes de usarlas (con el filtro de tenant activo, un Id ajeno debe resultar "no encontrado").
- Antes de cerrar cualquier fase/tarea de producto: verificación end-to-end en navegador (no solo tests), siguiendo el patrón de todas las fases de `ROADMAP.md`.
- No implementes nada de cumplimiento normativo (retención, derecho al olvido, DPIA, DPA, términos de uso) sin confirmar primero con el usuario — son decisiones con componente legal, no solo técnico.
- No mezcles refactors independientes en un mismo cambio (ej.: unificación de las 3 clases de credenciales, Context Workspace y multi-tenant son trabajos separados).

## Disciplina de tokens (aplica a toda sesión, no solo a cambios de arquitectura)

Este repo tiene muchos documentos normativos largos (`ROADMAP.md`, ADRs, y el repositorio local de negocio). Leer de más es el mayor costo de tokens aquí — la sección anterior ya dice qué leer *según la tarea*; esto es *cómo* leerlo y cómo trabajar el código.

- **Lee solo lo que la tarea exige.** No abras los ~15 documentos de la lista "por si acaso" — usa la tabla de arriba para identificar los 2-3 relevantes. Dentro de un doc largo, usa grep/búsqueda por sección en vez de verlo entero (`ROADMAP.md` y los documentos de negocio ya lo piden explícitamente).
- **No releas un documento ya leído en esta sesión**, salvo que haya podido cambiar (ej. tras editar `docs/MULTITENANCY.md` en el mismo hilo).
- **Edición quirúrgica, no reescritura.** Reemplazo parcial (Edit) sobre archivos existentes; `Write` completo solo si el cambio es >80% del archivo. No "limpies" código alrededor del cambio pedido.
- **No narres el plan antes de ejecutar.** El usuario ve los tool calls; no hace falta un preview en texto de "voy a leer X, luego editar Y".
- **Respuestas cortas.** Sin preámbulo, sin resumen final, sin repetir lo que pidió el usuario. Si ya editaste un archivo o creaste uno, no lo copies entero en la respuesta — el diff ya lo muestra.
- **Paraleliza lecturas independientes** (ej. `DOMAIN.md` + `ARCHITECTURE.md` + `DATABASE.md` para una feature nueva) en vez de una por una.
- **Cero relleno conversacional** ("Excelente pregunta", "Perfecto", etc.) — directo al trabajo.
- **Sin abstracciones no pedidas.** Esto es consistente con YAGNI (`PROJECT.md`): no agregues helpers, capas o validación especulativa que no pidió la tarea, aunque "se vea más limpio".
- **Valida antes de decir "hecho"**: build/tests como mínimo; la verificación end-to-end en navegador solo aplica al cerrar fase/tarea de producto (ya está arriba), no a cada micro-cambio.
- **Si el usuario da una instrucción directa, ejecútala.** Si hay un riesgo real (seguridad, pérdida de datos, o choca con una regla ya establecida arriba —p.ej. tocar `TenantId` sin filtro, o SQL crudo—), dilo en una frase y procede según lo que decida el usuario.

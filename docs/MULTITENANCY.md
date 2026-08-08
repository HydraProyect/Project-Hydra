# MULTITENANCY — Documento normativo de multi-tenancy de Hydra

**Estado**: Implementado y validado (`TenantId` en las 25 tablas de dominio originales + las añadidas desde entonces, filtro global + interceptor de sellado, índices únicos compuestos, almacenamiento particionado por tenant, tests de aislamiento por agregado — ver `CLAUDE.md`, "Decisión multi-tenant" de `ROADMAP.md`, 2026-07-24, y la segunda línea de RLS de PostgreSQL de § 4.2, 2026-08-01). Este documento sigue siendo la referencia normativa de las reglas de aislamiento, catálogos y Tenant Resolution Strategy — no una propuesta pendiente de aprobar.

---

## 1. Filosofía del Tenant

El **Tenant** es la organización que compra y utiliza Hydra. No es un concepto del dominio CAE: es la frontera comercial y de aislamiento del sistema.

- El Tenant **no** representa un `Cliente` (Retail Iberia S.A., Bebidas del Norte S.A., ...). `Cliente` es una entidad *dentro* del tenant: la empresa propietaria de los centros donde se trabaja.
- El Tenant es la **frontera absoluta**: todo dato pertenece a exactamente un tenant, ninguna consulta ni operación puede cruzar esa frontera, y ningún usuario puede ver ni inferir la existencia de datos de otro tenant.
- La partición por tenant es **infraestructura, no negocio**: ninguna regla de dominio compara tenants; los agregados no razonan sobre `TenantId`. El aislamiento vive en la capa de persistencia (filtro global + interceptor de sellado), invisible para Application y Domain.

## 2. Escenarios de negocio

Estos dos escenarios describen el modelo de aislamiento técnico por tenant; su desarrollo comercial (cualificación, tarifas, canal) vive en `docs/business/ICP.md` y `docs/business/BUSINESS_MODEL.md` — **TODO**: cuando ese contenido se desarrolle, esta sección debe referenciarlo en vez de duplicarlo.

**Escenario 1 — Consultora PRL (ej. ArcoSPA Prevención S.L.).** ArcoSPA compra Hydra y gestiona la CAE de varias empresas contratistas (Ibertec S.A., EcoPlant Reciclaje S.L., Techmed Equipos S.A.) frente a los clientes finales de estas (Retail Iberia S.A., Bebidas del Norte S.A., Refrescos Levante S.A., Distribuciones Iberia S.L.). Todo — Empresas, Clientes, Centros, Plataformas, Trabajadores, Vehículos, Documentación, Asignaciones — vive dentro del tenant ArcoSPA, y sus usuarios ven toda la operación (modulada por el sistema de roles/cartera interno, ver § 6).

**Escenario 2 — Contratista directa (ej. Ibertec S.A.).** Ibertec S.A. compra Hydra. Tenant = Ibertec S.A., con una sola `Empresa` (Ibertec S.A.) y sus `Cliente`s (Retail Iberia S.A., Bebidas del Norte S.A., Distribuciones Iberia S.L.). Para Ibertec S.A. no existe EcoPlant, ni Techmed Equipos, ni ArcoSPA — ni como dato ni como posibilidad observable.

Ambos escenarios usan **el mismo esquema y el mismo código**: el escenario 2 es simplemente un tenant con una única Empresa. Que no haga falta ninguna rama especial es la señal de que la frontera está bien elegida. Nota: un mismo sujeto real (el trabajador con DNI X, la empresa con CIF Y) puede existir legítimamente en dos tenants a la vez (Ibertec S.A. como tenant directo y ArcoSPA gestionando a Ibertec S.A.) — son filas distintas, sin relación entre sí; por eso las unicidades de negocio son por tenant (§ 5).

## 3. Modelo de dominio y relaciones (resumen — detalle en `DOMAIN.md`)

Dentro de cada tenant, el modelo CAE es el ya existente, sin cambios:

- `Empresa ←→ EmpresaCliente ←→ Cliente` — **N:N**. Una Empresa trabaja para muchos Clientes; un Cliente contrata a muchas Empresas. Empresa **no** pertenece a un Cliente.
- `Trabajador` y `Vehiculo` pertenecen a una única `Empresa` **o** a una única `Subcontrata` (personal/flota subcontratados — mutuamente excluyente). Su relación con los Clientes es derivada: vía `Asignacion` + `Centro` (Trabajador) o transitiva (Vehículo). **No llevan `ClienteId`** — el dominio no garantiza un único Cliente para ellos.
- `Centro` pertenece a un único `Cliente` (`ClienteId`, FK real) y es operado por una `Empresa` (`EmpresaId`).
- `Documento` tiene propietario polimórfico excluyente: `Trabajador`, `Cliente`, `Empresa` o `Vehiculo`.
- La "vista de Cliente" (Context Panel, ver `05_WORKSPACE_PATTERNS.md` § 3) construye la sensación de que "todo pertenece al Cliente" mediante **consultas agregadas de lectura**, nunca desnormalizando FKs.

`TenantId` es **ortogonal** a este grafo: se añade a las tablas sin sustituir ni reordenar ninguna relación.

## 4. Reglas de aislamiento

1. **Toda tabla de datos lleva `TenantId` (Guid, NOT NULL)** — incluidas las de unión (`EmpresaCliente`, `VisitaTrabajador`...) y las transversales (`RegistroAuditoria`, `Alerta`, `NotificacionUsuario`, `DeteccionTrabajador`). Ninguna tabla depende de un JOIN para saber de quién es (defensa en profundidad). Excepciones: `Tenant` misma, `AspNetRoles` (catálogo global, § 7) y las tablas de Identity que derivan del usuario ya particionado.
2. **Lectura**: Global Query Filter de EF Core por entidad, **combinado** con el de soft-delete existente (`!EstaEliminado && TenantId == tenantActual`) — EF Core solo admite un `HasQueryFilter` por entidad; un filtro nuevo separado *reemplazaría* silenciosamente al de soft-delete. Prohibido `IgnoreQueryFilters()` y SQL crudo sin revisión explícita (regla ya vigente en `CLAUDE.md`, que pasa a ser frontera de seguridad entre tenants).

   **Segunda línea (implementada 2026-08-01, P2 #21 de `docs/business/MATURITY_REVIEW.md`)**: Row-Level Security de PostgreSQL con `FORCE`, política `aislamiento_tenant` en todas las tablas de este filtro (46 a día de hoy — `HabilitarRlsPostgres` + las migraciones que la ampliaron después según se creaban tablas nuevas, ver `RUNBOOK-RLS.md`), comparando `TenantId` contra la variable de sesión `app.tenant_id` que `TenantRlsConnectionInterceptor` fija en cada conexión desde el mismo `ITenantActual`. Cubre exactamente el caso que el filtro de EF no puede cubrir por construcción: una consulta que se lo salte (`IgnoreQueryFilters` mal revisado, SQL crudo, un bug del propio EF). `PoliticasRlsCubrenModeloTests` (`tests/CaeManager.IntegrationTests/Tenants/`) compara `pg_policies` contra el modelo en CI para que una tabla nueva sin su migración `HabilitarRlsX` falle el build en vez de quedar sin cubrir en silencio (ya pasó dos veces: `ClavesApi` de P3-29 y las 4 tablas de Integraciones de P3-33). Hoy sigue siendo defensa en profundidad *inerte* en producción — RLS nunca restringe al propietario de la tabla ni a un superusuario, y la aplicación todavía conecta con ese rol —, hasta que se rote la conexión de runtime al rol restringido `cae_app_runtime` siguiendo `RUNBOOK-RLS.md` (paso operativo con credenciales reales, deliberadamente no automatizado).
3. **Escritura**: interceptor de `SaveChanges` que sella `TenantId` en toda entidad nueva desde `ITenantActual` y rechaza modificaciones cuyo `TenantId` no coincida. Los Commands **nunca** reciben ni pasan `TenantId`.
4. **Referencias cruzadas**: todo Command que reciba un Id de otra entidad la carga antes de usarla — el filtro global convierte un Id de otro tenant en "no encontrado". Regla de revisión de código, cubierta por tests.
5. **Fallo cerrado**: sin tenant resoluble → sin datos (lista vacía / 403). Nunca "sin filtro".
6. **Archivos**: `IFileStorageService` particiona por ruta (`{tenantId}/...`). Los endpoints de descarga resuelven el archivo a través de queries ya filtradas.
7. **Identity**: `ApplicationUser.TenantId`; unicidad `(TenantId, NormalizedUserName)` (ver § 8 para la excepción v1 del email de login).
8. **Aprovisionamiento**: un tenant nuevo nace vacío + seed de sus catálogos configurables (§ 7). Jamás ve datos de otro tenant, tampoco del tenant #1 (la organización actual).

## 5. Unicidades por tenant

Los 7 índices únicos hoy globales pasan a compuestos con `TenantId` como primera columna: `(TenantId, Cif)` en Cliente y Empresa; `(TenantId, RazonSocial)` en Empresa y Subcontrata; `(TenantId, Nombre)` en TipoDocumento; `(TenantId, Dni)` en Trabajador; `(TenantId, NumeroPlaca)` en Vehiculo. Los índices únicos de las tablas de unión también se prefijan con `TenantId`. Justificación de negocio: § 2, nota final.

## 6. Autorización en capas (de fuera hacia dentro)

| Capa | Mecanismo | Decide | Estado |
|---|---|---|---|
| 1. Tenant | Filtro global + interceptor (Infrastructure) | De qué organización es cada fila | Implementado |
| 2. Rol | Policies ASP.NET Core (`Administrador`...`Cliente`) | Qué puede hacer un usuario | Existe |
| 3. Cartera | `IAlcanceDatosService` | Qué subconjunto del tenant ve un rol restringido | Existe (no se toca) |
| 4. Escritura | `AutorizacionEscrituraBehavior` + regla de referencias (§ 4.4) | Qué puede mutar | Existe + refuerzo |

Las capas 2–4 operan siempre *dentro* del tenant ya resuelto por la capa 1. Ninguna sustituye a otra.

## 7. Catálogos: globales del producto vs. configurables por tenant

Criterio de clasificación: un catálogo es **global** si es parte del producto (el código depende de sus valores, o cambiarlo por tenant no tiene caso de uso real — YAGNI); es **por tenant** si es configuración de negocio que cada organización querrá adaptar.

| Catálogo | Clasificación | Justificación |
|---|---|---|
| `TipoDocumento` (apto médico, EPIS, reciclajes...) | **Por tenant** | Es el corazón configurable del negocio: cada consultora/contratista tiene su propio catálogo documental, vigencias y criterios (una consultora puede exigir tipos que otra no usa). Ya era editable por Administrador incluso en single-tenant. El seed actual (15+ tipos del Excel real) pasa a ser **plantilla de aprovisionamiento**: se copia al crear el tenant y desde entonces cada tenant la modifica sin afectar a nadie. Las actualizaciones futuras de la plantilla del producto **no** sobrescriben catálogos ya personalizados. |
| `ParametroSistema` (umbrales ámbar/rojo 30/15) | **Por tenant** | Los umbrales de alerta son política de cada organización, no del producto (deja de ser singleton global → una fila por tenant, sembrada con 30/15). |
| `TipoDocumentoCentro`, `ConfiguracionIaDocumentoCliente` | **Por tenant** (son datos, no catálogos) | Relacionan entidades del tenant; llevan `TenantId` como cualquier tabla de datos. |
| Roles (`AspNetRoles`: Administrador, DireccionCae, CoordinadorCae, GestorCae, Consulta, Cliente) | **Global** | Son parte del código del producto: las policies, `IAlcanceDatosService` y la UI dependen de estos seis códigos. Roles personalizados por tenant es una feature especulativa sin caso de uso — no se construye (YAGNI). El *quién tiene qué rol* sí es por tenant (vía `ApplicationUser.TenantId`). |
| Enums de dominio (`EstadoDocumento`, `NivelAlerta`, `AmbitoAplicacion`, `TipoDeteccion`, `TipoIdentificacion`) | **Global** | Son tipos del código, no datos. La lógica de negocio (cálculo de estado, semáforos) depende de ellos. |
| Plantillas de importación Excel (`/clientes/plantilla.xlsx`, etc.) | **Global** | Formato de intercambio del producto, igual para todos los tenants. |
| Umbrales/textos de UI, microcopy, Design System | **Global** | Identidad del producto. Branding por tenant (logo, colores) es una posible feature comercial futura — backlog, no ahora. |
| Configuración de integraciones (`AzureAd:*`, `Graph:*`, `Anthropic:*`, Sentry, Backups) | **Global hoy, por-tenant en el futuro señalado** | Hoy son configuración de la instalación (appsettings). SSO por tenant y cuotas de IA por tenant están identificados como deuda SaaS en `docs/archive/INFORME-MULTITENANT.md` § 16 — se abordan cuando haya un segundo tenant real que los necesite, con diseño propio (secretos por tenant cifrados, no appsettings). |
| `ProveedorIntegracion` / `VersionApiProveedor` (catálogo de la futura Plataforma de Integraciones — Dokify, 6Coordina, CTAIMA, eCoordina, Microsoft 365, Anthropic, OpenAI...) | **Global** | Es parte del producto: qué proveedores soporta Hydra y qué capacidades tiene cada versión de su API, del mismo modo que los roles son parte del código. La instancia que cada tenant activa y configura (`ConexionIntegracion`, `CredencialIntegracion`, `SaludConexionIntegracion`, `TrabajoIntegracion`, `SincronizacionIntegracion`, `SuscripcionWebhook`, `EventoWebhook`) es **por tenant**, con `TenantId` obligatorio como cualquier otra tabla de datos. Ver `ARQUITECTURA-INTEGRACIONES.md` (diseño de backlog, no implementado). |
| `ProveedorPlataformaCae` / `DominioProveedorPlataformaCae` (catálogo de plataformas externas de acreditación documental — Dokify, Nalanda, CTAIMA/Twind...; PLAN-EJECUCION-UX.md § Parte 2 (a), implementado) | **Global** | Mismos ~23 proveedores para cualquier tenant, nadie los renombra — mismo criterio que Roles. Nombre distinto a propósito, sin relación con las dos filas de arriba: no es el enum `ProveedorIntegracion` del conector de mensajería (Email/WhatsApp) ni el catálogo completo `ProveedorIntegracion`/`VersionApiProveedor` de la Plataforma de Integraciones (esa sigue sin construir) — este es el catálogo, mucho más estrecho, de "a qué portal CAE pertenece esta URL", sin `VersionApiProveedor` ni aparato de conector. Decisión 2026-08-07: se evaluó "Global + extensión por tenant" (como pide la redacción original del plan) pero el aprovisionamiento automático de tenant que ese patrón necesita no existe todavía (no hay `CrearTenantCommand`) — construirlo era un lote propio fuera de alcance; catálogo global puro hasta que haga falta de verdad personalizarlo por tenant. |
| `AceptacionTerminos` (registro de qué usuario aceptó qué versión de Términos y Condiciones + Política de Privacidad — implementado 2026-08-08) | **Global** | Mismo tratamiento que `DelegacionTenant` (`ADR-004` § 5.3): es un hecho sobre el usuario frente a la plataforma, no un dato de un tenant. Un usuario puede operar más de un tenant (Operador Delegado) — la aceptación no debe repetirse una vez por cada tenant que visite. Extiende `Entity`, sin `HasQueryFilter`, sin `TenantId`, sin RLS (`PoliticasRlsCubrenModeloTests` solo exige RLS a lo que extiende `EntidadConTenant`). |

## 8. Tenant Resolution Strategy

**Decisión propuesta: resolución por claim de sesión, con subdominios como evolución futura.**

### Cómo funciona (v1)

1. Al autenticarse (login local o SSO), se resuelve `ApplicationUser.TenantId` y se estampa como claim `tenant_id` en la cookie de autenticación — junto a los claims de rol que ya existen.
2. `ITenantActual` (Application) lee ese claim vía el mismo mecanismo que `ICurrentUserService`. El `CaeManagerDbContext` lo recibe por DI y lo usa en el filtro global.
3. **Fallo cerrado**: usuario autenticado sin claim de tenant válido (o con tenant suspendido) → se cierra la sesión / 403. Nunca se ejecuta una query sin tenant resuelto.
4. **Contextos sin usuario** (jobs programados: generación de alertas, notificaciones, detección IA): no hay claim — el job itera los tenants activos explícitamente y abre un **ámbito de tenant** por cada uno (`ITenantActual` con implementación de ámbito explícito para procesos de fondo). Prohibido un "modo sin filtro" global para jobs: el job que necesite cruzar tenants no existe como caso de uso.

### Por qué claim y no subdominio/path/header (trade-offs)

| Estrategia | Evaluación |
|---|---|
| **Claim en cookie (elegida)** | Encaja con el stack real: Blazor Server con cookie de Identity, sin API pública en v1. Cero cambios de despliegue/DNS/TLS. El tenant queda criptográficamente atado a la sesión (no manipulable por el usuario, a diferencia de un header). Mismo patrón arquitectónico que `ICurrentUserService` — coste mínimo, consistencia máxima. |
| Subdominio (`geseme.hydra.app`) | La opción canónica SaaS a largo plazo: branding, aislamiento de cookies por origen, y necesaria para SSO por tenant (elegir IdP antes del login). Coste hoy: DNS wildcard + TLS wildcard + lógica de host en despliegue — sin valor mientras no haya segundo tenant. **Evolución prevista, compatible con el claim**: el subdominio pasará a *seleccionar* el tenant en el login; el claim seguirá siendo la fuente de verdad de la sesión. Se decide su adopción cuando se aborde SSO por tenant. |
| Path (`/t/{tenant}/...`) | Contamina todas las rutas (rompe la regla de "nunca renombrar rutas" de los planes de navegación) y no aporta nada sobre el claim en una app con sesión. Descartada. |
| Header (`X-Tenant-Id`) | Pensada para APIs machine-to-machine, que no existen en v1. Manipulable si no se valida contra la sesión — y si hay que validarla contra la sesión, es redundante con el claim. Descartada hasta que exista una API pública (donde el tenant vendrá del token OAuth, no de un header suelto). |

### Tercer modo, específico de integraciones: identificador de recurso + verificación de firma

Los dos modos anteriores (claim de sesión, ámbito explícito de jobs) cubren contextos interactivos y de fondo, pero no un tercer contexto real: **webhooks entrantes** de proveedores externos (Dokify, 6Coordina, CTAIMA..., ver `ARQUITECTURA-INTEGRACIONES.md`), que llegan sin sesión de usuario ni claim posible. Se resuelven por el identificador de la `ConexionIntegracion` embebido en la propia URL del webhook, **verificado por firma HMAC** contra el secreto de esa conexión antes de confiar en el tenant que implica — nunca se resuelve el tenant de un payload sin verificar la firma primero. Es el mismo principio de fallo cerrado que el resto de esta estrategia, adaptado a un origen que no es un usuario ni un proceso interno.

### Cuarto modo: delegación de acceso entre tenants (Capa 0 de ADR-004, implementada 2026-07-30)

Ninguno de los tres modos anteriores cubre a una **Consultora** externa (p. ej. ArcoSPA Prevención S.L.) cuyos Operadores Delegados necesitan operar sobre tenants de Clientes Delegantes que no poseen — el caso real que destraba el primer cliente comercial de Hydra (vocabulario de negocio: `docs/business/UBIQUITOUS_LANGUAGE.md` — Consultora, Cliente Directo/Delegante, Delegated Workspace, Operador Delegado). Diseño completo en **`ADR-004-delegacion-consultoras-cae.md`**: la Consultora es un `Tenant` sin datos operativos propios, solo identidad de sus usuarios y su jerarquía interna; `DelegacionTenant` + `AsignacionOperadorDelegado` (catálogo global, sin `TenantId`, mismo tratamiento que `Tenant`) autorizan a usuarios concretos a resolver como **Delegated Workspace** activo un tenant ajeno (uno a la vez, nunca varios simultáneos); el filtro global y el interceptor de sellado (§ 4) **no cambian** — la delegación decide solo de dónde sale el `TenantId` activo de la sesión, nunca cómo se aplica.

**Cómo se implementó** (`IClienteActivoSeleccionado`/`ClienteActivoSeleccionado`, `TenantActual`, `ClienteActivoEndpoints`): la selección se guarda en una cookie httpOnly (`cae_cliente_activo`), no en estado en memoria del circuito de Blazor — un intento inicial con estado en memoria + navegación soft (`forceLoad:false`) no sobrevivía a que Blazor Server no re-ejecuta `OnInitializedAsync` en una navegación a la misma ruta; la cookie sí sobrevive al `forceLoad:true` que el cambio de cliente activo necesita para remontar el circuito entero. `TenantActual.TenantId` comprueba la cookie **antes** que la claim de sesión (`AmbitoTenantExplicito` de jobs de fondo sigue teniendo prioridad máxima). El cambio de cliente pasa siempre por `GET /cuenta/cliente-activo/{tenantId}` (minimal API, no Blazor), que revalida server-side que el usuario tiene una `AsignacionOperadorDelegado` activa sobre ese tenant (o que es su tenant de origen) antes de escribir la cookie — nunca se confía en el valor que llega del cliente sin esa revalidación. Selector de UI: `SelectorClienteActivo.razor` en `MainLayout`, etiqueta "Cliente activo" (nunca "tenant"/"Workspace" de cara al usuario, § 12.1 de ADR-004).

**Pendiente, no implementado**: los agregados entre tenants que necesitan Coordinador/Director (cartera de su equipo) requieren la **Capa de Reporting** de ADR-004 § 7 — decisión explícita de arrancar solo con esta Capa 0 primero. No construir esa capa por extensión de este mecanismo sin ese diseño.

### Implicaciones documentadas

- **Autenticación / login (limitación v1 aceptada)**: con resolución por claim, el formulario de login no conoce el tenant antes de autenticar. Para que "email → usuario" sea determinista, **el email de login se mantiene único globalmente en v1** (la misma persona no puede tener cuenta en dos tenants con el mismo email). Es una limitación consciente y reversible: desaparece cuando se adopten subdominios (el login sabrá el tenant por el host y la unicidad pasará a `(TenantId, Email)`). Se acepta porque el caso "misma persona en dos tenants" es marginal hasta que haya decenas de tenants.
- **Autorización**: el claim alimenta la capa 1 de § 6; las capas 2–4 no cambian.
- **API pública** (implementada, P3-29 — `/api/v1`, no publicada todavía): el tenant se resuelve del token, exactamente como se predijo aquí — no de un header ni de un parámetro suelto. No es OAuth: es una API key por tenant (`ClaveApi`, hash SHA-256), y `ApiKeyAuthenticationHandler` (Infrastructure) rellena el mismo claim `tenant_id` que ya lee `ITenantActual` — el aislamiento existente se hereda sin tocar el mecanismo de resolución.
- **Despliegue**: v1 sin cambios de infraestructura. La adopción de subdominios (junto con SSO por tenant y PostgreSQL) forma parte de las condiciones/deudas de salida SaaS listadas en `ADR-003` — decisión separada, no implícita.

## 9. Buenas prácticas (reglas de trabajo para cualquier sesión futura)

1. Ninguna Query/Command nueva usa `FromSqlRaw`/`ExecuteSqlRaw`/`IgnoreQueryFilters()` sin revisión explícita — es la propiedad que hace que el filtro de tenant proteja todo a la vez.
2. Todo Command que reciba Ids de otras entidades las carga antes de usarlas (§ 4.4).
3. Ninguna feature nueva introduce una tabla sin `TenantId` (salvo catálogo global justificado en § 7 — y entonces se documenta aquí).
4. Los tests de aislamiento ("tenant A no ve a tenant B") se escriben **por agregado expuesto en `IApplicationDbContext`**, y toda entidad nueva añade el suyo.
5. Nada de cumplimiento normativo por tenant (DPA, términos, retención) se implementa sin confirmación del propietario del producto (regla heredada de `CLAUDE.md`).
6. Los jobs de fondo usan el ámbito explícito por tenant (§ 8.4) — nunca un bypass del filtro.

## 10. Decisiones arquitectónicas de referencia

- `ADR-003-saas-multitenant.md` — la decisión vigente (SaaS in-place, supersede ADR-002).
- `ADR-001-multitenant.md` — el modelo técnico (TenantId por fila, filtro global, interceptor, índices compuestos) — reactivado como guía por ADR-003.
- `ADR-002-single-tenant.md` — superseded; se conserva como registro histórico y por su § 4 (obligaciones RGPD que siguen vigentes).
- `docs/archive/INFORME-MULTITENANT.md` — análisis técnico completo: riesgos, estrategia de migración por etapas, impactos CQRS/DDD/rendimiento.
- `PLAN-MIGRACION-MULTITENANT.md` — plan de ejecución por etapas.
- `ARQUITECTURA-INTEGRACIONES.md` — diseño de la futura Plataforma de Integraciones (proveedores CAE/ERP/CRM/IA), backlog, no implementado — asegura que las decisiones de esta página no le cierren puertas.
- `RUNBOOK-RLS.md` — cómo activar de verdad la segunda línea de RLS de § 4.2 en producción (rotación del rol de conexión, paso operativo no automatizado).

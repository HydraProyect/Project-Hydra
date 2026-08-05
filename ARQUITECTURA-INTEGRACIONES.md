# Arquitectura — Plataforma de Integraciones (Integration Platform)

**Estado**: Diseño arquitectónico para el backlog. **No implementado como framework genérico** (`IIntegrationProvider`/`IIntegrationProviderFactory`/`IIntegrationOrchestrator` de § 4-6 siguen sin código), con una excepción ya completa: § 12 (Comunicaciones — bandeja de correo compartida) tiene el conector Microsoft 365 real y en producción (P3-33, commit `3b29348` — OAuth por buzón + webhooks entrantes reales de Graph), ver esa sección para el alcance exacto. Para el resto — ningún adaptador concreto de Dokify/6Coordina/CTAIMA/eCoordina/Anthropic/OpenAI, ninguna migración EF Core adicional, ninguna UI adicional. El objetivo de este documento es que las decisiones que **sí** se están tomando ahora mismo (multi-tenancy, `ADR-003`) no cierren puertas a este ecosistema futuro.

## Aviso sobre el documento de referencia aportado

El material de referencia (`HYDRA_INTEGRACIONES_PRESUPUESTO_NEGOCIO`) describe una arquitectura en **Python/FastAPI/PostgreSQL/Celery/Redis** — un stack distinto al de este repositorio (ASP.NET Core/Blazor Server/EF Core, ver `ARCHITECTURE.md`). Este documento **traduce el patrón** (proveedor desacoplado, orquestador, factory, resiliencia, webhooks, auditoría) a las convenciones ya establecidas aquí (CQRS con MediatR, Clean Architecture, Result pattern, nomenclatura Spanish-domain/English-technical de `CODING_STANDARDS.md`) — no se adopta el stack de referencia ni las cifras de presupuesto/pricing/modelos de negocio del documento, que son una decisión de negocio separada y fuera del alcance de una arquitectura de repositorio. **TODO**: si esas cifras llegan a confirmarse como decisión de negocio, viven en `docs/business/PRICING.md` y `docs/business/BUSINESS_MODEL.md` — no en este documento de arquitectura.

## 0. Disciplina de decisión (orden que este documento sigue, y que futuras sesiones deberían mantener)

1. **Dominio** — qué representa el negocio (§ 3).
2. **Arquitectura** — cómo se organiza el sistema (§ 4-6).
3. **Plataforma** — multi-tenancy, integraciones, IA, observabilidad como capacidades transversales (§ 2, § 7, § 8).
4. **Implementación** — código (explícitamente fuera de alcance de este documento, § 13, salvo la excepción de § 12).

Mientras las decisiones se tomen en ese orden, incorporar una capacidad nueva (un proveedor, un tipo de sincronización, un canal de observabilidad) no debería obligar a reabrir las anteriores.

## 1. Principio rector

**Hydra no conoce Dokify; Hydra conoce Proveedores de Integración, y de cada proveedor conoce sus capacidades — no su nombre.** Ningún tipo de `Domain`/`Application` referencia el nombre de un proveedor concreto, y el orquestador nunca asume "si es Dokify, entonces X" — decide en función de qué **capacidades** declara el proveedor (§ 3.1), no de quién es. Incorporar un proveedor nuevo significa: un adaptador nuevo en `Infrastructure` + una fila de catálogo — cero cambios en `Domain`, `Application` o `Presentation`. Es la misma disciplina que ya aplica este proyecto a `IEmailService`/`IFileStorageService`/`IConversorWordPdfService`: la interfaz vive en `Application`, la implementación concreta vive en `Infrastructure`, intercambiable sin tocar el dominio.

## 2. Por qué se diseña *alrededor* de esto y no *encima*, en relación al multi-tenant

- Toda integración es una **capacidad que cada Tenant activa y configura de forma independiente** (credenciales, mapeos de campos, frecuencia de sincronización) — nunca una integración global compartida entre tenants. Es el mismo principio que ya vertebra `docs/MULTITENANCY.md`.
- El catálogo de "qué proveedores existen y qué saben hacer" es **global** (`ProveedorIntegracion` + `VersionApiProveedor` — parte del producto); la instancia que cada tenant configura es **por-tenant** (`ConexionIntegracion` y sus satélites) — mismo criterio de clasificación que `TipoDocumento`/`ParametroSistema` en `docs/MULTITENANCY.md` § 7 (actualizada, ver § 10 de este documento).
- Añade un caso a la **Tenant Resolution Strategy** que el diseño original (claim de sesión) no cubría: los **webhooks entrantes** llegan sin sesión de usuario. Se resuelve por identificador de conexión + verificación de firma (§ 6.4) — nuevo modo de resolución, incorporado a `docs/MULTITENANCY.md` § 8.
- Los jobs de sincronización programada reutilizan el **ámbito de tenant explícito para procesos de fondo** ya diseñado en `PLAN-MIGRACION-MULTITENANT.md` § 4.7 — no se inventa un mecanismo nuevo de aislamiento.
- Ninguna entidad nueva de este documento rompe la regla ya vigente en `CLAUDE.md`: "ninguna feature nueva introduce una tabla sin `TenantId`, salvo catálogo global justificado y documentado".

## 3. Modelo de dominio (`src/CaeManager.Domain/Integraciones/`, cuando se implemente)

| Entidad | Clasificación | Descripción |
|---|---|---|
| `ProveedorIntegracion` | **Global** (catálogo del producto) | Qué proveedores soporta Hydra: `Codigo` (`dokify`, `6coordina`, `ctaima`, `ecoordina`, `microsoft365`, `anthropic`, `openai`...), `Nombre`, `Categoria` (PlataformaCae / ERP / CRM / IA / Almacenamiento), `Estado` (Disponible/Deprecado). |
| `VersionApiProveedor` (satélite de `ProveedorIntegracion`) | **Global** | Un proveedor puede tener varias versiones de API vivas a la vez (Dokify v2 y v3 conviviendo). Campos: `ProveedorIntegracionId`, `Version` (string, `"v2"`/`"v3"`), `Estado` (Soportada/Deprecada/Retirada), `FechaDeprecacionPrevista?`, y **`Capacidades`** (§ 3.1) — las capacidades son propiedad de una versión concreta de API, no del proveedor en abstracto, precisamente porque v3 puede añadir capacidades que v2 no tenía. |
| `ConexionIntegracion` (agregado raíz) | Por tenant | La instancia que un Tenant ha activado: `TenantId`, `ProveedorIntegracionId`, `VersionApiProveedorId` (FK — determina qué capacidades tiene disponibles esta conexión, ver § 3.1), `Nombre` (alias libre, ej. "Dokify — cartera Ibertec"; **no** se fuerza unicidad `(TenantId, ProveedorIntegracionId)** porque un mismo tenant puede legítimamente tener más de una conexión al mismo proveedor con configuraciones distintas — el `Nombre` es lo que las distingue, la unicidad real es `(TenantId, Nombre)`), `Estado` (Habilitada/Deshabilitada/ConError), `ConfiguracionJson`, `PerfilSincronizacion` (§ 7), `FechaUltimaSincronizacionUtc`. |
| `CredencialIntegracion` (satélite 1:1) | Por tenant | Igual patrón que `CredencialAccesoEmpresa`/`PlataformaAcceso` ya existentes: cifrado en reposo vía Data Protection API, nunca en logs ni auditoría en claro. **Nota de deuda** (ver `DOMAIN.md`): esta sería la *cuarta* clase que modela "credenciales de un sistema externo" — no se unifica con las 3 existentes en este épico (regla de `CLAUDE.md`: no mezclar refactors independientes). |
| `SaludConexionIntegracion` (satélite 1:1, alta frecuencia de escritura) | Por tenant | **Separada deliberadamente** de `ConexionIntegracion` (no como columnas del agregado): se actualiza en cada intento de sincronización/health-check, y mezclarla con el agregado de configuración generaría contención de concurrencia optimista sobre una fila que en realidad cambia por dos motivos completamente distintos (el usuario reconfigura vs. el sistema registra salud). Campos: `EstadoSalud` (🟢Ok/🟡Degradado/🔴Caído), `UltimaLatenciaMs`, `UltimoErrorMensaje?`, `RateLimitRestante?`, `FechaUltimaComprobacionUtc`. Ver § 8. |
| `TrabajoIntegracion` (Job) | Por tenant | `TenantId`, `ConexionIntegracionId`, `Tipo` (Manual/Programado/Webhook), `Estado` (Pendiente/EnProgreso/Completado/Fallido), `Intentos`, `ProximoIntentoUtc`, `ClaveIdempotencia` (única por tenant). |
| `SincronizacionIntegracion` | Por tenant | Resultado de una ejecución: `ConexionIntegracionId`, `TrabajoIntegracionId?`, `Resultado` (Exito/ParcialConErrores/Fallo), `ElementosSincronizados`, `Errores`, `FechaUtc`, `DuracionMs`. Convive con `RegistroAuditoria` (quién activó/reconfiguró) sin sustituirlo. |
| `SuscripcionWebhook` | Por tenant | `ConexionIntegracionId`, secreto de verificación de firma (cifrado), `UrlExternaOpcional`. |
| `EventoWebhook` | Por tenant | `SuscripcionWebhookId`, `PayloadCrudo`, `Procesado`, `FechaRecepcionUtc`, `FechaProcesadoUtc` — persistido **antes** de procesar. |

Todas las entidades por-tenant llevan `TenantId` (NOT NULL), soft delete (`EntidadBase`) y quedan cubiertas automáticamente por `AuditoriaInterceptor`.

### 3.1 Capacidades: el orquestador trabaja contra capacidades, no contra nombres de proveedor

```csharp
// CaeManager.Domain.Integraciones.CapacidadesIntegracion
[Flags]
public enum CapacidadesIntegracion
{
    Ninguna              = 0,
    Trabajadores         = 1 << 0,  // SupportsWorkers
    Vehiculos            = 1 << 1,  // SupportsVehicles
    Documentos           = 1 << 2,  // SupportsDocuments
    Centros              = 1 << 3,  // SupportsCenters
    Visitas              = 1 << 4,  // SupportsVisits
    Webhooks             = 1 << 5,  // SupportsWebhooks
    SincronizacionTiempoReal = 1 << 6,  // SupportsRealtimeSync
    EscrituraRemota      = 1 << 7,  // API de escritura (no todos los proveedores la ofrecen — ver ejemplo abajo)
    DescargaDocumental   = 1 << 8,
}
```

Ejemplo (igual al planteado en la conversación): `VersionApiProveedor` de Dokify v3 declara `Trabajadores | Vehiculos | Documentos | Centros | Webhooks | DescargaDocumental`; la de 6Coordina v1 declara `Trabajadores | Documentos | Centros | Webhooks` (sin `Vehiculos` ni `EscrituraRemota`). El **orquestador** (§ 6) consulta `IIntegrationProvider.Capacidades` **antes** de intentar una operación — si una `ConexionIntegracion` de 6Coordina recibe una orden de sincronizar Vehículos, el orquestador la rechaza con un `Result` de negocio ("Esta conexión no soporta Vehículos"), nunca intentando la llamada HTTP y fallando en tiempo de ejecución. Esto es lo que permite que Hydra decida automáticamente qué operaciones son posibles sin ningún `if (proveedor == "dokify")` disperso por el código.

## 4. Contratos de Application

```csharp
// CaeManager.Application/Integraciones/Common/IIntegrationProvider.cs
public interface IIntegrationProvider
{
    string CodigoProveedor { get; }         // debe coincidir con ProveedorIntegracion.Codigo
    string VersionApi { get; }              // debe coincidir con VersionApiProveedor.Version
    CapacidadesIntegracion Capacidades { get; }

    Task<Result> ValidarCredencialesAsync(CredencialIntegracionDto credenciales, CancellationToken ct);
    Task<Result<SincronizacionResultadoDto>> SincronizarAsync(EntidadIntegracionDto entidad, ConfiguracionConexionDto config, CancellationToken ct);
    Task<Result> ManejarWebhookAsync(string payloadCrudo, ConfiguracionConexionDto config, CancellationToken ct);
    Task<EstadoSaludProveedorDto> ComprobarSaludAsync(ConfiguracionConexionDto config, CancellationToken ct);
    MapeoCamposProveedor ObtenerMapeoCampos();
}
```

- `EntidadIntegracionDto` es la forma **universal** que expone Hydra hacia cualquier proveedor (derivada de los DTOs de lectura ya existentes de Cliente/Empresa/Trabajador/Documento, no un modelo paralelo).
- `IIntegrationProviderFactory`: resuelve la implementación de `IIntegrationProvider` a partir de `(ProveedorIntegracion.Codigo, VersionApiProveedor.Version)`, vía DI (un diccionario de registro poblado en `InfrastructureServiceCollectionExtensions`, **nunca** un `switch`/`if` disperso comparando nombres de proveedor).
- `IIntegrationOrchestrator` (Application): carga la `ConexionIntegracion` de un tenant, resuelve el proveedor vía la Factory, **verifica capacidades** (§ 3.1) antes de ejecutar, aplica políticas de resiliencia (§ 6), ejecuta, persiste `TrabajoIntegracion`/`SincronizacionIntegracion`/`SaludConexionIntegracion`, y **publica un evento de integración** (§ 6.5) en vez de invocar directamente a otros módulos.
- CQRS de gestión (mismo patrón que el resto del proyecto): `HabilitarIntegracionCommand`, `ConfigurarCredencialIntegracionCommand`, `ConfigurarPerfilSincronizacionCommand`, `DeshabilitarIntegracionCommand`, `DispararSincronizacionManualCommand`, `ObtenerConexionesIntegracionQuery`, `ObtenerHistorialSincronizacionQuery`, `ObtenerSaludConexionesQuery`.

## 5. Infrastructure — adaptadores concretos

```
src/CaeManager.Infrastructure/Integraciones/
├── Providers/
│   ├── DokifyV3IntegrationProvider.cs      (una clase por versión de API soportada, no una por proveedor "genérica" que ramifique por dentro)
│   ├── SeisCoordinaV1IntegrationProvider.cs
│   ├── CtaimaIntegrationProvider.cs
│   ├── ECoordinaIntegrationProvider.cs
│   ├── MicrosoftGraphIntegrationProvider.cs   (reutiliza patrón ya existente de GraphEmailService)
│   └── ...
├── IntegrationProviderFactory.cs
├── Resiliencia/ (Polly — ver § 6.1)
└── Webhooks/
```

Mismo patrón "**inerte por defecto**" que ya usan `AzureAd`/`Graph`/`Anthropic`/Sentry/Backups (`ARCHITECTURE.md`): una `ConexionIntegracion` deshabilitada o sin credenciales simplemente no se ejecuta.

### 5.1 Nota de mercado sobre el primer conector objetivo (CTAIMA/Twind) — solo referencias, sin cifras de negocio

Verificado en la sesión de benchmark de mercado del 2026-08-02 (detalle completo, incluidas cifras, en `docs/business/BENCHMARK_PRECIOS_CAE.md`; esta nota solo referencia, per `DOCUMENT_STANDARDS.md` § 6):

- **Objetivo del conector**: Twind, la plataforma nueva de CTAIMA Group que unifica CTAIMACAE y e-coordina — nunca CTAIMACAE legacy. La migración está en curso durante 2026 sin garantía pública de compatibilidad retroactiva para integraciones existentes (los titulares con API/SSO son contactados por CTAIMA para reconfigurar antes de la actualización).
- **Estado del catálogo API**: `developers.ctaima.com` (Azure API Management) publica hoy 8 APIs REST **1.0 del legacy** (Gestión Documental, Contratos, Clientes, Recursos, Control de Accesos, Entradas y Salidas, Autorizaciones de Pagos, Gestión General). El catálogo de Twind todavía no es público. Las 1.0 sirven hoy como inventario funcional de lo integrable, no como contrato de implementación.
- **Restricción de niveles de acceso**: STANDARD (1.000 peticiones/semana) / EXTRA (10.000/semana) / ADVANTAGE (ilimitado), contratación por contacto comercial. El impacto de coste de cada nivel sobre el margen del add-on vive en `docs/business/UNIT_ECONOMICS.md`, no aquí.
- **Konvergia**: sin API pública ni modelo de consumo por uso — descartada como opción técnica de integración a corto plazo. La postura estratégica frente a esa red (adherirse/integrar/orquestar sin membresía) vive en `docs/business/PRODUCT_STRATEGY.md`, referencia cruzada, no desarrollo aquí.

**Nota de ADR futuro, no redactado todavía (regla YAGNI de `CLAUDE.md`)**: una **capa adaptadora (anti-corruption layer) por conector externo** se justifica cuando exista decisión real de construir el primer conector — la propia migración legacy→Twind ya demuestra que el proveedor objetivo rompe compatibilidad con el tiempo, y aislar el dominio de Hydra de cada API externa protege el roadmap de integraciones de decisiones de terceros. Este documento lo anticipa como nota; se formaliza como ADR cuando la construcción del conector deje de ser hipotética.

## 6. Orquestación

### 6.1 Retry / Circuit Breaker / Rate Limiting

**Polly** (librería estándar de .NET — mismo criterio de "usar el paquete NuGet estándar, no reinventarlo" que ClosedXML/PDFsharp/MediatR/FluentValidation) sobre `IHttpClientFactory` con clientes tipados por proveedor. El `RateLimitRestante` que Polly observa se persiste en `SaludConexionIntegracion` (§ 8), no se descarta tras la llamada.

### 6.2 Idempotencia

`TrabajoIntegracion.ClaveIdempotencia`, único por tenant vía índice de base de datos (mismo mecanismo que `Asignacion`).

### 6.3 Cola de trabajos y Sync Profiles

A la escala actual (`PROJECT.md`) no se justifica una cola externa — se reutiliza el job de fondo con ámbito de tenant explícito ya diseñado en `PLAN-MIGRACION-MULTITENANT.md` § 4.7. La frecuencia **no está hardcodeada**: `ConexionIntegracion.PerfilSincronizacion` (`Modo`: Manual/Programado; `Frecuencia`: intervalo configurable por el propio tenant, ej. Ibertec cada 5 minutos, otro tenant cada 6 horas; `ProximaEjecucionUtc`, calculada tras cada ejecución). El job de fondo recorre las conexiones cuya `ProximaEjecucionUtc` ya venció, no un cron único compartido por todos los tenants.

### 6.4 Webhooks entrantes

Endpoint mínimo por proveedor (`/api/integraciones/webhooks/{proveedorCodigo}/{conexionId}`), verifica la firma HMAC contra el secreto de `SuscripcionWebhook` **antes** de resolver el tenant. Persiste `EventoWebhook` y encola un `TrabajoIntegracion`; nunca procesa de forma síncrona dentro del request HTTP entrante.

### 6.5 Eventos de integración — el orquestador publica, no invoca

Regla central de este documento: **las integraciones no hablan directamente con los módulos de negocio**. El flujo no es `Dokify → Webhook → Hydra (llama directamente a Documentos/IA/Alertas)`; es `Dokify → Webhook → Integration Layer → Evento → (quien esté suscrito)`.

Mecanismo elegido para v1: **`MediatR.INotification`**, ya dependencia del proyecto (`ARCHITECTURE.md`, "CQRS ligero con MediatR") — MediatR no es solo Commands/Queries, su mecanismo de `INotification`/`INotificationHandler<T>` es, de fábrica, un bus de eventos en proceso. No hace falta introducir ningún paquete nuevo para tener esta separación desde el primer día:

```csharp
// CaeManager.Application/Integraciones/Events/DocumentoIntegracionRecibidoEvent.cs
public sealed record DocumentoIntegracionRecibidoEvent(
    Guid TenantId,
    Guid ConexionIntegracionId,
    EntidadIntegracionDto Documento) : INotification;
```

El orquestador publica (`IPublisher.Publish`) tras persistir el resultado de la sincronización; módulos independientes (una futura clasificación por IA, la generación de una `Alerta`, una notificación) implementan su propio `INotificationHandler<DocumentoIntegracionRecibidoEvent>` sin que el orquestador ni el adaptador del proveedor sepan que existen. Así, `Dokify → Webhook → Integration Layer → Evento → Orquestador → IA → Clasificación → Validación → Alerta` se construye añadiendo handlers, nunca modificando el orquestador ni el adaptador.

**Importante — esto no es lo mismo que "Domain Events"** (que `DOMAIN.md` señala como inexistentes y sin caso de uso, YAGNI): un *Integration Event* vive en la frontera de Application (desacopla el orquestador de integraciones de los módulos que reaccionan a un evento externo), no dentro de un agregado de dominio protegiendo invariantes. Este épico sí tiene el caso de uso real que justifica introducir el mecanismo — el resto del dominio sigue sin necesitarlo.

Si en el futuro el volumen exige mensajería asíncrona real (fuera de proceso), el punto de sustitución es exactamente ese `IPublisher` — se cambia la implementación de "quién entrega la notificación", el contrato (`INotification`, los handlers) no cambia. Diseñar con esta separación desde ahora es lo que hace esa futura migración localizada, no una reescritura.

## 7. Sincronización

- **Manual**: Command disparado por el usuario.
- **Programada**: job de fondo + `PerfilSincronizacion` por conexión (§ 6.3).
- **Tiempo real (webhook)**: § 6.4.
- **Cola de eventos**: los tres modos anteriores convergen en el mismo `TrabajoIntegracion`, y su resultado se propaga vía Eventos de Integración (§ 6.5) — no hay tres implementaciones distintas de "qué pasa cuando llega un dato nuevo".

## 8. Monitorización — Health Monitoring, no solo logs

Objetivo: un panel `Tenant → Proveedor → Estado (🟢/🟡/🔴) → Última sincronización → Latencia → Errores → Rate Limit restante`, alimentado por `SaludConexionIntegracion` (§ 3), actualizada tras cada intento real (no un ping separado y desincronizado de la actividad real — el propio `SincronizarAsync`/`ComprobarSaludAsync` de `IIntegrationProvider` alimenta la misma fila). Presentación (`Features/Integraciones/`) sigue en v1 el patrón lista + Drawer (`UX_PATTERNS.md`) — reutiliza `Badge`/`EstadoVacio`/`ToastService` del Design System, sin componentes nuevos; es una pantalla de administración de bajo volumen, no candidata al Context Workspace (mismo criterio de exclusión que `TiposDocumento`/`Configuracion`/`Usuarios` en `PLAN-CONTEXT-WORKSPACE.md` § 4).

## 9. Guía práctica para incorporar un proveedor nuevo

Ver `docs/INTEGRATION_GUIDELINES.md` (documento nuevo, no de arquitectura — guía paso a paso para cualquier desarrollador o sesión de IA que construya un conector).

## 10. Actualización de `docs/MULTITENANCY.md` (a aplicar cuando se apruebe este documento)

- § 7 (catálogos): `ProveedorIntegracion` y `VersionApiProveedor` → **Global**.
- § 4 (reglas de aislamiento): añadir `ConexionIntegracion`, `CredencialIntegracion`, `SaludConexionIntegracion`, `TrabajoIntegracion`, `SincronizacionIntegracion`, `SuscripcionWebhook`, `EventoWebhook` a la lista de tablas con `TenantId` obligatorio.
- § 8 (Tenant Resolution Strategy): tercer modo — identificador de recurso + verificación de firma, para webhooks entrantes.

## 11. Riesgos

| Riesgo | Mitigación |
|---|---|
| Un proveedor externo cambia su API/esquema | El mapeo de campos vive en el adaptador de esa versión (`Infrastructure`), no en `Domain` — convivencia de versiones explícita (§ 3) en vez de romper la conexión existente |
| Fuga entre tenants vía credenciales o eventos de webhook mal resueltos | `TenantId` + filtro global (`ADR-003`) + resolución de tenant verificada por firma antes de tocar cualquier dato (§ 6.4) |
| Cuarta clase de credenciales cifradas duplicando concepto | Señalado como deuda (§ 3), no se resuelve aquí |
| Un proveedor con límites de tasa agresivos degrada la experiencia de otros tenants | Rate limiting por conexión (Polly), no global |
| Orquestador u orígenes de eventos acoplándose "por comodidad" a un módulo concreto con el tiempo | La regla de § 6.5 (publicar, nunca invocar directamente) se añade al checklist de revisión de código junto al resto de reglas de `CLAUDE.md` |
| Construir conectores/capacidades especulativas antes de tener un proveedor real priorizado | No se construye ningún adaptador concreto todavía, salvo la excepción de § 12 (Comunicaciones) — YAGNI |

## 12. Adenda — Comunicaciones: bandeja de correo compartida (sustituye Outlook para el trato con clientes)

**Estado**: primera pieza real de este documento con implementación en curso — el resto del documento (§1-11, §13) sigue siendo diseño de backlog sin construir. Contexto de negocio: cada Tenant (Cliente Directo o Consultora) ya opera hoy un buzón corporativo propio tipo `CAE.IBEROTEC@ArcoSPA.com` para hablar con sus clientes/trabajadores/subcontratas; el objetivo es traer esa conversación dentro de Hydra en una bandeja tipo Zendesk/Front, sin salir de la app.

### 12.1 Por qué es un Domain Module, no una extensión del kernel

Una conversación de correo tiene estado de negocio (Abierta/Pendiente/Resuelta/Cerrada), se asigna a un Ejecutivo CAE y cuelga de `Cliente` — no es infraestructura pura. Vive como módulo de dominio nuevo (`src/CaeManager.Domain/Comunicaciones/`), construido **sobre** dos capacidades de plataforma ya existentes/diseñadas, sin inventar un tercer mecanismo:

- **Notifications** (`docs/PLATFORM.md` § 4) generalizada de "envío unidireccional" (`IEmailService.EnviarAsync`, hoy fire-and-forget) a canal bidireccional real.
- **Integrations** (§ 3 de este documento) — reutiliza `ConexionIntegracion`/`CredencialIntegracion` en vez de inventar una cuarta clase de credenciales (deuda ya señalada en § 3).

### 12.2 Extensión necesaria al modelo de § 3: `ConexionIntegracion` deja de ser solo "por tenant"

El buzón real no es 1:1 con el Tenant — es 1:1 con el **Cliente** (una Consultora gestiona varios Clientes Delegantes, cada uno con su propio buzón de marca, ej. Iberotec tiene el suyo dentro del dominio de ArcoSPA). Esto obliga a un cambio real sobre § 3, no solo a una tabla nueva:

- `ConexionIntegracion.ClienteId` (**nuevo, nullable**): `null` cuando el buzón es del propio Tenant (caso Cliente Directo, donde Tenant y Cliente prácticamente coinciden); poblado cuando es el buzón específico de un Cliente Delegante dentro de una Consultora. La unicidad `(TenantId, Nombre)` ya definida en § 3 sigue siendo la que distingue conexiones — se añade `ClienteId` como filtro adicional al resolver "qué buzón usar para responder a esta conversación", no como una unicidad nueva.
- Nueva capacidad en `CapacidadesIntegracion` (§ 3.1): `CorreoBidireccional = 1 << 9` — envía, recibe y mantiene threading real. La declara `VersionApiProveedor` de `microsoft365`.
- Proveedor `microsoft365` (ya listado como ejemplo en § 3) implementa `IIntegrationProvider` reutilizando el patrón ya anticipado en § 5 (`MicrosoftGraphIntegrationProvider`, "reutiliza patrón ya existente de `GraphEmailService`") — pero el envío usa los endpoints `/messages/{id}/reply` / `/createReply` de Graph (preservan `conversationId` automáticamente), nunca `sendMail` para una respuesta — reconstruir threading a mano con `In-Reply-To`/`References` es innecesario y frágil cuando Graph ya lo resuelve.
- El dominio propio de Hydra (`Graph__BuzonRemitente` de `DEPLOY.md`) sigue existiendo sin cambios, exclusivo de notificaciones transaccionales de plataforma (alta/baja de usuario, cambio de contraseña) — nunca se mezcla con los buzones de cliente de este módulo.

### 12.3 Modelo de dominio (`src/CaeManager.Domain/Comunicaciones/`)

| Entidad | Clasificación | Descripción |
|---|---|---|
| `ConversacionCorreo` (agregado raíz) | Por tenant | `TenantId`, `ClienteId?` (null = sin resolver, ver § 13.4), `ConexionIntegracionId?` (qué buzón la atiende), `Asunto`, `Estado` (Abierta/Pendiente/Resuelta/Cerrada), `EjecutivoAsignadoId?`, `Etiquetas`, `FechaUltimoMensajeUtc`, `HiloExternoId` (conversationId de Graph — clave para threading) |
| `ParticipanteConversacion` (entidad hija) | Por tenant | `ConversacionCorreoId`, `Email`, `Rol` (De/Para/Cc), `TipoOrigen` (UsuarioCliente/Trabajador/Subcontrata/Empresa/Centro/Desconocido), `EntidadRelacionadaId?` (apunta a `Trabajador`/`Subcontrata`/`Empresa`/`Centro` según `TipoOrigen` — nullable y polimórfico por tipo, no una FK única). Es lo que permite que un hilo con el trabajador, la subcontrata y el centro de la visita en copia siga siendo una sola `ConversacionCorreo`. |
| `MensajeCorreo` (entidad hija) | Por tenant | `ConversacionCorreoId`, `Direccion` (Entrante/Saliente), `RemitenteEmail`, `CuerpoHtml`, `FechaUtc`, `MensajeExternoId` (Message-ID de Graph, idempotencia ante reintentos de webhook) |
| `AdjuntoMensajeCorreo` | Por tenant | Reutiliza `IFileStorageService` ya existente — no un storage paralelo |
| `MacroRespuesta` | Por tenant | `TenantId`, `ClienteId?` (null = macro genérica del tenant; poblado = específica de ese cliente), `Titulo`, `CuerpoHtml`, variables de sustitución simples (contacto/cliente/centro) |

Todas llevan `TenantId` NOT NULL, soft delete (`EntidadBase`) y quedan cubiertas por `AuditoriaInterceptor` — misma regla que el resto del módulo de Integraciones (§ 3).

### 12.4 Resolución de remitente desconocido — pipeline, nunca una asignación automática ciega

1. Dominio del email del remitente contra dominios ya registrados de un `Cliente`/`TenantId` conocido → auto-asocia `ConversacionCorreo.ClienteId` sin intervención humana.
2. Si no matchea ningún dominio conocido: se reutiliza el mismo patrón ya existente en el proyecto para IA de apoyo (`IExtraccionTrabajadoresIaService`, generalizable al `IAIProvider` esbozado en `docs/PLATFORM.md` § 4) para leer el cuerpo y **sugerir** un `Cliente` candidato — la IA propone, nunca decide ni asigna directamente (mismo criterio que la detección de altas/bajas de trabajadores).
3. Cae siempre en una cola de triage (`ClienteId = null`, visible para el rol `Supervisor` existente — no se crea un rol de autorización nuevo) hasta que una persona confirma el cliente y, con eso, la conversación puede asignarse a un Ejecutivo CAE concreto.

### 12.5 Visibilidad

Reutiliza `IAlcanceDatosService` por cartera, ya existente (`DOMAIN.md`/`ARCHITECTURE.md`) — sin mecanismo de autorización nuevo. Una Consultora ve todas las conversaciones de todos sus Clientes Delegantes (acceso completo dentro de su alcance); un Cliente Delegante ve las suyas.

### 12.6 Alcance de la primera implementación (vertical slice)

**Actualizado (P3-33, commit `3b29348`)**: la siguiente iteración ya se construyó — `IMicrosoft365GraphClient`/`Microsoft365GraphClient` (Infrastructure) implementa OAuth delegado por buzón real, `IngestaWebhookHostedService`/`RenovacionSuscripcionWebhookHostedService` (envueltos en la elección de líder de multi-réplica, ver `docs/PLATFORM.md`) procesan webhooks entrantes reales de Microsoft Graph, y `/integraciones` permite conectar/desconectar un buzón real desde la UI (`ConectarMicrosoft365Endpoints.cs`, `WebhookMicrosoft365Endpoints.cs`). Ya no depende de datos de prueba sembrados para este flujo. Sigue sin construirse: SSO federado por tenant (fuera de alcance explícito de P3-33) y el resto de `IIntegrationProvider`/`IIntegrationProviderFactory`/`IIntegrationOrchestrator` genéricos de § 4-6 — este conector se implementó directamente contra Microsoft Graph, sin pasar por el framework genérico de proveedores descrito en este documento (deuda de diseño conocida, no una implementación del framework completo).

### 12.7 Adenda — WhatsApp Cloud API: segundo canal de Comunicaciones (2026-08-04)

Segundo conector real de mensajería, construido como **clon estructural del slice de Microsoft 365** (no a través del framework genérico de § 4-6, deliberadamente): el framework `IIntegrationProvider` está pensado para sincronización documental CAE (Dokify/CTAIMA — `SincronizarAsync`, mapeo de entidades), y forzar un canal de chat dentro de ese contrato sería cumplir la letra violando el espíritu (§ 11, YAGNI). Lo que sí se adoptó de este documento: § 6.4 íntegro (firma antes de tenant, cola durable `EventoWebhook`, nunca procesar en el request) y § 6.5 con el **primer `MediatR.INotification` del repositorio** (`MensajeWhatsAppRecibidoEvent`, publicado tras el commit de la ingesta — hoy lo consume el notificador de tiempo real de la UI; mañana IA o alertas sin tocar la ingesta).

Diferencias deliberadas respecto al conector M365:

| Aspecto | Microsoft 365 | WhatsApp Cloud API |
|---|---|---|
| URL de webhook | Una por conexión (`/{conexionId}`) | **Una por app de Meta** (`/api/integraciones/webhooks/whatsapp`) — la línea se resuelve por `phone_number_id` del payload contra el índice único GLOBAL de `LineaWhatsApp.PhoneNumberId` (mismo criterio que `ClaveApi.HashClave`) |
| Autenticación del webhook | Comparación de `clientState` (Graph no firma) | **HMAC-SHA256 real** (`X-Hub-Signature-256` con el App Secret, comparación en tiempo constante) — cumple docs/MULTITENANCY.md § 8 más estrictamente que el precedente |
| Credencial | Refresh token OAuth rotativo (`CredencialIntegracion`) | System User token de larga duración en `LineaWhatsApp.TokenAcceso` (cifrado con el mismo protector `CaeManager.CredencialIntegracion.Credenciales.v1` — satélite del mismo agregado) |
| Latencia del consumidor | Tick de 10 s | Híbrido **señal-en-memoria + tick** (`ISenalIngestaWhatsApp`): el webhook despierta al consumidor tras persistir; la señal no transporta datos (no reintroduce el problema de `Channel<T>`) y perderla cuesta como máximo un tick. Lock de líder propio (`ingesta-webhook-whatsapp`) para que un backlog de correo no retrase el chat |
| Modelo de conversación | `ConversacionCorreo` por `HiloExternoId` | El **mismo agregado** con `Canal = WhatsApp` (bandeja multicanal, decisión 2026-08-04): hilo = `(ConexionIntegracionId, TelefonoContacto)` + estado no cerrado; `HiloExternoId` queda null. Dedup por wamid sobre el índice existente `{TenantId, MensajeExternoId}` |

Piezas propias del canal: enrutamiento híbrido de conversaciones nuevas (catálogo autoalimentado `ContactoWhatsApp` teléfono→Cliente→gestor de cartera; si no, modo de línea `GestorFijo`/`PoolInbound` con reparto equitativo por carga; si el cliente sigue sin identificarse, auto-mensaje de triage — gratis, dentro de la ventana de 24 h recién abierta), ventana de servicio de 24 h de Meta (persistida como `FechaUltimoMensajeEntranteUtc`; fuera de ella el envío libre se bloquea en servidor y UI — las plantillas aprobadas quedan para una fase posterior), estados de entrega (`statuses[]` → `EstadoEntregaMensaje` con progresión monótona) y la página `/comunicaciones/chat` (UX de chat en vivo, refresco instantáneo vía `INotificadorMensajesTiempoReal` — costura multi-réplica documentada: hoy singleton en proceso, sustituible por el backplane Redis ya modelado).

## 13. Qué NO se construye ahora

Excepción ya construida: el conector Microsoft 365 de § 12 (P3-33) — entidades de Domain, migración EF Core y pantalla `/integraciones` reales. Para el resto (Dokify, 6Coordina, CTAIMA, eCoordina, Anthropic, OpenAI): ningún adaptador concreto, ninguna entidad de Domain adicional, ninguna migración EF Core, ninguna pantalla, ningún handler de eventos real, y el framework genérico `IIntegrationProvider`/`IIntegrationProviderFactory`/`IIntegrationOrchestrator` de § 4-6 tampoco existe todavía — el conector de Microsoft 365 se construyó directo contra Graph, no a través de ese framework. Este documento existe para que el multi-tenant (`ADR-003`, ya implementado) no tome ninguna decisión que cierre esta puerta — el framework genérico se construye cuando exista un segundo proveedor real priorizado con caso de uso confirmado por el negocio.

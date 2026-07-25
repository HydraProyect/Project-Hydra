# Arquitectura — Plataforma de Integraciones (Integration Platform)

**Estado**: Diseño arquitectónico para el backlog. **No implementado.** Ningún adaptador concreto (Dokify, 6Coordina, CTAIMA, eCoordina, Microsoft 365, Anthropic, OpenAI...), ninguna migración EF Core, ninguna UI. El objetivo de este documento es que las decisiones que **sí** se están tomando ahora mismo (multi-tenancy, `ADR-003`) no cierren puertas a este ecosistema futuro.

## Aviso sobre el documento de referencia aportado

El material de referencia (`HYDRA_INTEGRACIONES_PRESUPUESTO_NEGOCIO`) describe una arquitectura en **Python/FastAPI/PostgreSQL/Celery/Redis** — un stack distinto al de este repositorio (ASP.NET Core/Blazor Server/EF Core, ver `ARCHITECTURE.md`). Este documento **traduce el patrón** (proveedor desacoplado, orquestador, factory, resiliencia, webhooks, auditoría) a las convenciones ya establecidas aquí (CQRS con MediatR, Clean Architecture, Result pattern, nomenclatura Spanish-domain/English-technical de `CODING_STANDARDS.md`) — no se adopta el stack de referencia ni las cifras de presupuesto/pricing/modelos de negocio del documento, que son una decisión de negocio separada y fuera del alcance de una arquitectura de repositorio.

## 0. Disciplina de decisión (orden que este documento sigue, y que futuras sesiones deberían mantener)

1. **Dominio** — qué representa el negocio (§ 3).
2. **Arquitectura** — cómo se organiza el sistema (§ 4-6).
3. **Plataforma** — multi-tenancy, integraciones, IA, observabilidad como capacidades transversales (§ 2, § 7, § 8).
4. **Implementación** — código (explícitamente fuera de alcance de este documento, § 12).

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
| `ConexionIntegracion` (agregado raíz) | Por tenant | La instancia que un Tenant ha activado: `TenantId`, `ProveedorIntegracionId`, `VersionApiProveedorId` (FK — determina qué capacidades tiene disponibles esta conexión, ver § 3.1), `Nombre` (alias libre, ej. "Dokify — cartera KHS"; **no** se fuerza unicidad `(TenantId, ProveedorIntegracionId)** porque un mismo tenant puede legítimamente tener más de una conexión al mismo proveedor con configuraciones distintas — el `Nombre` es lo que las distingue, la unicidad real es `(TenantId, Nombre)`), `Estado` (Habilitada/Deshabilitada/ConError), `ConfiguracionJson`, `PerfilSincronizacion` (§ 7), `FechaUltimaSincronizacionUtc`. |
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

## 6. Orquestación

### 6.1 Retry / Circuit Breaker / Rate Limiting

**Polly** (librería estándar de .NET — mismo criterio de "usar el paquete NuGet estándar, no reinventarlo" que ClosedXML/PDFsharp/MediatR/FluentValidation) sobre `IHttpClientFactory` con clientes tipados por proveedor. El `RateLimitRestante` que Polly observa se persiste en `SaludConexionIntegracion` (§ 8), no se descarta tras la llamada.

### 6.2 Idempotencia

`TrabajoIntegracion.ClaveIdempotencia`, único por tenant vía índice de base de datos (mismo mecanismo que `Asignacion`).

### 6.3 Cola de trabajos y Sync Profiles

A la escala actual (`PROJECT.md`) no se justifica una cola externa — se reutiliza el job de fondo con ámbito de tenant explícito ya diseñado en `PLAN-MIGRACION-MULTITENANT.md` § 4.7. La frecuencia **no está hardcodeada**: `ConexionIntegracion.PerfilSincronizacion` (`Modo`: Manual/Programado; `Frecuencia`: intervalo configurable por el propio tenant, ej. KHS cada 5 minutos, otro tenant cada 6 horas; `ProximaEjecucionUtc`, calculada tras cada ejecución). El job de fondo recorre las conexiones cuya `ProximaEjecucionUtc` ya venció, no un cron único compartido por todos los tenants.

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
| Construir conectores/capacidades especulativas antes de tener un proveedor real priorizado | No se construye ningún adaptador concreto todavía (§ 12) — YAGNI |

## 12. Qué NO se construye ahora

Ningún adaptador concreto, ninguna entidad de Domain, ninguna migración EF Core, ninguna pantalla, ningún handler de eventos real. Este documento existe para que el multi-tenant (`ADR-003`, en fase de aprobación) no tome ninguna decisión que cierre esta puerta — se construye cuando el aislamiento multi-tenant esté implementado y exista al menos un proveedor real priorizado con caso de uso confirmado por el negocio.

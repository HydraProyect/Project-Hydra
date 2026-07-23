# Arquitectura — Plataforma de Integraciones (Integration Platform)

**Estado**: Diseño arquitectónico para el backlog. **No implementado.** Ningún adaptador concreto (Dokify, 6Coordina, CTAIMA, eCoordina, Microsoft 365, Anthropic, OpenAI...), ninguna migración EF Core, ninguna UI. El objetivo de este documento es que las decisiones que **sí** se están tomando ahora mismo (multi-tenancy, `ADR-003`) no cierren puertas a este ecosistema futuro.

## Aviso sobre el documento de referencia aportado

El material de referencia (`HYDRA_INTEGRACIONES_PRESUPUESTO_NEGOCIO`) describe una arquitectura en **Python/FastAPI/PostgreSQL/Celery/Redis** — un stack distinto al de este repositorio (ASP.NET Core/Blazor Server/EF Core, ver `ARCHITECTURE.md`). Este documento **traduce el patrón** (proveedor desacoplado, orquestador, factory, resiliencia, webhooks, auditoría) a las convenciones ya establecidas aquí (CQRS con MediatR, Clean Architecture, Result pattern, nomenclatura Spanish-domain/English-technical de `CODING_STANDARDS.md`) — no se adopta el stack de referencia ni las cifras de presupuesto/pricing/modelos de negocio del documento, que son una decisión de negocio separada y fuera del alcance de una arquitectura de repositorio.

## 1. Principio rector

**Hydra no conoce Dokify; Hydra conoce Proveedores de Integración.** Ningún tipo de `Domain`/`Application` referencia el nombre de un proveedor concreto. Incorporar un proveedor nuevo significa: un adaptador nuevo en `Infrastructure` + una fila de catálogo — cero cambios en `Domain`, `Application` o `Presentation`. Es la misma disciplina que ya aplica este proyecto a `IEmailService`/`IFileStorageService`/`IConversorWordPdfService`: la interfaz vive en `Application`, la implementación concreta (y el nombre del proveedor real) vive en `Infrastructure`, intercambiable sin tocar el dominio.

## 2. Por qué se diseña *alrededor* de esto y no *encima*, en relación al multi-tenant

- Toda integración es una **capacidad que cada Tenant activa y configura de forma independiente** (credenciales, mapeos de campos, frecuencia de sincronización) — nunca una integración global compartida entre tenants. Es el mismo principio que ya vertebra `docs/MULTITENANCY.md`.
- El catálogo de "qué proveedores existen" es **global** (`ProveedorIntegracion` — parte del producto); la instancia que cada tenant configura es **por-tenant** (`ConexionIntegracion` y sus satélites) — mismo criterio de clasificación que `TipoDocumento`/`ParametroSistema` en `docs/MULTITENANCY.md` § 7 (actualizada, ver § 9 de este documento).
- Añade un caso a la **Tenant Resolution Strategy** que el diseño original (claim de sesión) no cubría: los **webhooks entrantes** llegan sin sesión de usuario. Se resuelve por identificador de conexión + verificación de firma (§ 6.4) — nuevo modo de resolución, incorporado a `docs/MULTITENANCY.md` § 8.
- Los jobs de sincronización programada reutilizan el **ámbito de tenant explícito para procesos de fondo** ya diseñado en `PLAN-MIGRACION-MULTITENANT.md` § 4.7 — no se inventa un mecanismo nuevo de aislamiento.
- Ninguna entidad nueva de este documento rompe la regla ya vigente en `CLAUDE.md`: "ninguna feature nueva introduce una tabla sin `TenantId`, salvo catálogo global justificado y documentado".

## 3. Modelo de dominio (`src/CaeManager.Domain/Integraciones/`, cuando se implemente)

| Entidad | Clasificación | Descripción |
|---|---|---|
| `ProveedorIntegracion` | **Global** (catálogo del producto) | Qué proveedores soporta Hydra como producto: `Codigo` (`dokify`, `6coordina`, `ctaima`, `ecoordina`, `microsoft365`, `anthropic`, `openai`...), `Nombre`, `Categoria` (PlataformaCae / ERP / CRM / IA / Almacenamiento), `Estado` (Disponible/Deprecado), `EsquemaConfiguracion` (qué campos de configuración espera este proveedor — descriptor, no acoplamiento). Añadir un proveedor nuevo al catálogo no requiere migración de las tablas por-tenant. |
| `ConexionIntegracion` (agregado raíz) | Por tenant | La instancia de un `ProveedorIntegracion` que un Tenant ha activado: `TenantId`, `ProveedorIntegracionId`, `Estado` (Habilitada/Deshabilitada/ConError), `ConfiguracionJson` (parámetros propios de ese tenant: URL base si aplica, mapeos, frecuencia), `FechaUltimaSincronizacionUtc`. |
| `CredencialIntegracion` (satélite 1:1) | Por tenant | Igual patrón que `CredencialAccesoEmpresa`/`PlataformaAcceso` ya existentes: cifrado en reposo vía Data Protection API, nunca en logs ni auditoría en claro. **Nota de deuda** (ver `DOMAIN.md`): esta sería la *cuarta* clase que modela "credenciales de un sistema externo" — no se unifica con las 3 existentes en este épico (regla de `CLAUDE.md`: no mezclar refactors independientes), pero refuerza que esa unificación ya identificada como deuda es cada vez más rentable de abordar. |
| `TrabajoIntegracion` (Job) | Por tenant | Unidad de trabajo encolada: `TenantId`, `ConexionIntegracionId`, `Tipo` (Manual/Programado/Webhook), `Estado` (Pendiente/EnProgreso/Completado/Fallido), `Intentos`, `ProximoIntentoUtc`, `ClaveIdempotencia` (única por tenant — evita procesar dos veces el mismo evento). |
| `SincronizacionIntegracion` | Por tenant | Resultado de una ejecución: `ConexionIntegracionId`, `TrabajoIntegracionId?`, `Resultado` (Exito/ParcialConErrores/Fallo), `ElementosSincronizados`, `Errores`, `FechaUtc`, `DuracionMs`. Es al ecosistema de integraciones lo que `RegistroAuditoria` es al resto del dominio — **no lo sustituye**, conviven (auditoría de "quién activó/reconfiguró la integración" sigue siendo `RegistroAuditoria`; "qué pasó en cada sincronización" es `SincronizacionIntegracion`). |
| `SuscripcionWebhook` | Por tenant | `ConexionIntegracionId`, secreto de verificación de firma (cifrado), `UrlExternaOpcional` (si Hydra necesita registrarse contra la API del proveedor). |
| `EventoWebhook` | Por tenant | `SuscripcionWebhookId`, `PayloadCrudo`, `Procesado`, `FechaRecepcionUtc`, `FechaProcesadoUtc` — persistido **antes** de procesar (para no perder el evento si el procesamiento falla), consumido de forma asíncrona por un `TrabajoIntegracion`. |

Todas las entidades por-tenant llevan `TenantId` (NOT NULL), soft delete (`EntidadBase`) y quedan cubiertas automáticamente por `AuditoriaInterceptor` — sin trabajo adicional, es el mismo mecanismo que ya protege el resto del dominio.

## 4. Contratos de Application — el "Provider" como abstracción técnica

```csharp
// CaeManager.Application/Integraciones/Common/IIntegrationProvider.cs
public interface IIntegrationProvider
{
    string CodigoProveedor { get; } // debe coincidir con ProveedorIntegracion.Codigo

    Task<Result> ValidarCredencialesAsync(CredencialIntegracionDto credenciales, CancellationToken ct);
    Task<Result<SincronizacionResultadoDto>> SincronizarAsync(EntidadIntegracionDto entidad, ConfiguracionConexionDto config, CancellationToken ct);
    Task<Result> ManejarWebhookAsync(string payloadCrudo, ConfiguracionConexionDto config, CancellationToken ct);
    MapeoCamposProveedor ObtenerMapeoCampos();
}
```

- `EntidadIntegracionDto` es la forma **universal** que expone Hydra hacia cualquier proveedor (equivalente al `Contractor`/`Document` del documento de referencia, pero derivado de los DTOs de lectura ya existentes de Cliente/Empresa/Trabajador/Documento — no un modelo paralelo). Cada adaptador concreto traduce de esta forma universal a la suya, nunca al revés — el dominio no aprende el vocabulario de Dokify.
- `IIntegrationProviderFactory`: resuelve la implementación de `IIntegrationProvider` a partir de `ProveedorIntegracion.Codigo`, vía DI (un diccionario de registro poblado en `InfrastructureServiceCollectionExtensions`, **nunca** un `switch`/`if` disperso comparando nombres de proveedor por el código de Application o Presentation — es exactamente el anti-patrón `DokifyService`/`SixCoordinaService` que se quiere evitar).
- `IIntegrationOrchestrator` (Application): carga la `ConexionIntegracion` de un tenant, resuelve el proveedor vía la Factory, aplica políticas de resiliencia (§ 6), ejecuta, persiste `TrabajoIntegracion`/`SincronizacionIntegracion`. Es el único punto que conoce tanto "tenant" como "proveedor resuelto" a la vez.
- CQRS de gestión (Commands/Queries normales, mismo patrón que el resto del proyecto): `HabilitarIntegracionCommand`, `ConfigurarCredencialIntegracionCommand`, `DeshabilitarIntegracionCommand`, `DispararSincronizacionManualCommand`, `ObtenerConexionesIntegracionQuery`, `ObtenerHistorialSincronizacionQuery`.

## 5. Infrastructure — adaptadores concretos

```
src/CaeManager.Infrastructure/Integraciones/
├── Providers/
│   ├── DokifyIntegrationProvider.cs
│   ├── SeisCoordinaIntegrationProvider.cs
│   ├── CtaimaIntegrationProvider.cs
│   ├── ECoordinaIntegrationProvider.cs
│   ├── MicrosoftGraphIntegrationProvider.cs   (reutiliza patrón ya existente de GraphEmailService)
│   └── ...
├── IntegrationProviderFactory.cs
├── Resiliencia/ (Polly — ver § 6)
└── Webhooks/
```

Mismo patrón "**inerte por defecto**" que ya usan `AzureAd`/`Graph`/`Anthropic`/Sentry/Backups (`ARCHITECTURE.md`): un proveedor sin `ConexionIntegracion` habilitada para un tenant, o sin credenciales configuradas, simplemente no se ejecuta — nunca un fallo, nunca un cambio de comportamiento en el resto de la app.

## 6. Orquestación y políticas (equivalente a la "Fase 2" del épico original)

1. **Retry / Circuit Breaker / Rate Limiting**: **Polly** (librería estándar de .NET, ya en el ecosistema del framework — mismo criterio de "usar el paquete NuGet estándar, no reinventarlo" que ya se aplicó a ClosedXML/PDFsharp/MediatR/FluentValidation en `ARCHITECTURE.md`) sobre `IHttpClientFactory` con clientes tipados por proveedor. Se descarta construir un motor de reintentos a mano (lo que sí hacía el documento de referencia).
2. **Idempotencia**: `TrabajoIntegracion.ClaveIdempotencia`, único por tenant vía índice de base de datos (mismo mecanismo que ya usa `Asignacion` para evitar altas duplicadas) — no una comprobación solo en memoria.
3. **Cola de trabajos**: a la escala actual del producto (~10 usuarios, `PROJECT.md`) no se justifica introducir RabbitMQ/Redis Streams — se usa el mismo mecanismo de **jobs de fondo con ámbito de tenant explícito** ya diseñado en `PLAN-MIGRACION-MULTITENANT.md` § 4.7 (`IHostedService`/`Channel<T>` en proceso). Migrar a una cola externa queda documentado como evolución cuando el volumen lo justifique (YAGNI, mismo criterio que PostgreSQL en `ADR-003`), no como decisión de partida.
4. **Webhooks entrantes**: endpoint mínimo por proveedor (`/api/integraciones/webhooks/{proveedorCodigo}/{conexionId}`), verifica la firma HMAC contra el secreto de `SuscripcionWebhook` **antes** de resolver el tenant — el `conexionId` de la ruta identifica la `ConexionIntegracion` (y por tanto el tenant) sin depender de una sesión de usuario. Persiste `EventoWebhook` y encola un `TrabajoIntegracion`; nunca procesa de forma síncrona dentro del request HTTP entrante.

## 7. Sincronización (equivalente a la "Fase 4" del épico original)

- **Manual**: Command disparado por el usuario desde la UI de gestión de integraciones.
- **Programada**: el job de fondo (§ 6.3) recorre `ConexionIntegracion` activas por tenant según su frecuencia configurada.
- **Tiempo real (webhook)**: § 6.4.
- **Cola de eventos**: los tres modos anteriores convergen en el mismo `TrabajoIntegracion` — no hay tres implementaciones distintas de "cómo se ejecuta una sincronización", solo tres formas distintas de *encolar* una.

## 8. Monitorización (equivalente a la "Fase 5" del épico original)

Dashboard de estado de integraciones (activas, con error, última sincronización, reintentos pendientes) y alertas de fallo repetido — reutiliza `Badge`/`EstadoVacio`/`ToastService` del Design System (`DESIGN_SYSTEM.md`), sin componentes nuevos. Presentación (`Features/Integraciones/`) sigue en v1 el patrón lista + Drawer ya establecido (`UX_PATTERNS.md`) — es una pantalla de administración de bajo volumen (una fila por proveedor activado), no una candidata al Context Workspace (mismo criterio de exclusión que `TiposDocumento`/`Configuracion`/`Usuarios` en `PLAN-CONTEXT-WORKSPACE.md` § 4).

## 9. Actualización de `docs/MULTITENANCY.md` (a aplicar cuando se apruebe este documento)

- § 7 (catálogos): añadir fila `ProveedorIntegracion` → **Global** (parte del producto, igual criterio que los roles: el código de `IIntegrationProviderFactory` depende de qué proveedores existen).
- § 4 (reglas de aislamiento): añadir `ConexionIntegracion`, `CredencialIntegracion`, `TrabajoIntegracion`, `SincronizacionIntegracion`, `SuscripcionWebhook`, `EventoWebhook` a la lista de tablas con `TenantId` obligatorio.
- § 8 (Tenant Resolution Strategy): añadir un tercer modo de resolución, **identificador de recurso + verificación de firma**, para contextos machine-to-machine sin sesión (webhooks entrantes) — complementa, no sustituye, el claim de sesión (interactivo) y el ámbito explícito de jobs (fondo).

## 10. Riesgos

| Riesgo | Mitigación |
|---|---|
| Un proveedor externo cambia su API/esquema | El mapeo de campos vive en el adaptador (`Infrastructure`), no en `Domain` — el radio de cambio queda contenido a una clase |
| Fuga entre tenants vía credenciales o eventos de webhook mal resueltos | Misma disciplina que el resto del dominio: `TenantId` + filtro global (`ADR-003`) + resolución de tenant verificada por firma antes de tocar cualquier dato (§ 6.4) |
| Cuarta clase de credenciales cifradas duplicando concepto | Señalado como deuda (§ 3), no se resuelve aquí — decisión aparte, no mezclar refactors |
| Un proveedor con límites de tasa agresivos degrada la experiencia de otros tenants | Rate limiting por conexión (Polly), no global — un tenant ruidoso no afecta a otro, mismo principio que "vecino ruidoso" ya identificado en `INFORME-MULTITENANT.md` § 3 |
| Construir conectores especulativos antes de tener un proveedor real priorizado | No se construye ningún adaptador concreto todavía (§ 11) — YAGNI |

## 11. Qué NO se construye ahora

Ningún adaptador concreto (Dokify/6Coordina/CTAIMA/eCoordina/Microsoft 365/Anthropic/OpenAI), ninguna entidad de Domain, ninguna migración EF Core, ninguna pantalla. Este documento existe para que el multi-tenant (`ADR-003`, en fase de aprobación) no tome ninguna decisión que cierre esta puerta — se construye cuando el aislamiento multi-tenant esté implementado y exista al menos un proveedor real priorizado con caso de uso confirmado por el negocio.

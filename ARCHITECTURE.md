# Arquitectura — CAE Manager

## Stack

- **Backend**: ASP.NET Core 10 (LTS). *Nota: el brief original especificaba .NET 9; se usa .NET 10 porque es la versión LTS vigente y la única disponible a través de los canales de paquetes permitidos en el entorno de desarrollo — .NET 9 es STS y ya estaría cerca de fin de soporte. No afecta ninguna decisión de arquitectura de este documento.*
- **Frontend**: Blazor Server (interactividad server-side; sin necesidad de API pública en v1)
- **ORM**: Entity Framework Core 10
- **Base de datos**: PostgreSQL (migrado desde SQLite en el corte de `ADR-003`, ver `ROADMAP.md` § Fase 61/62); el acceso a datos se hace exclusivamente a través de EF Core, sin SQL crudo fuera de los repositorios.
- **Autenticación**: ASP.NET Core Identity + cookies, con login corporativo opcional vía Microsoft Entra ID (OpenID Connect) — ver más abajo.

## Por qué Blazor Server (y no WASM ni una API + SPA)

10 usuarios concurrentes iniciales, con crecimiento moderado esperado. Blazor Server da:
- Estado en el servidor → lógica de negocio, validaciones y acceso a datos sin duplicar contratos entre cliente y servidor.
- Interactividad rica (tablas virtualizadas, filtros en vivo) sin construir una API REST/GraphQL separada.
- Tiempo de carga inicial mínimo (no se descarga runtime .NET al navegador).

El coste (una conexión SignalR persistente por usuario) es irrelevante a esta escala. Si el producto crece a un volumen de usuarios donde esto se vuelva un problema, es una migración localizada a la capa de Presentation — las capas Domain/Application/Infrastructure no cambian.

## Las cuatro capas

Cada capa es un proyecto (`.csproj`) separado. Las dependencias solo apuntan hacia adentro:

```
Presentation (Blazor Server)
      ↓
Application (casos de uso, CQRS, validación)
      ↓
Domain (entidades, reglas de negocio, sin dependencias externas)
      ↑
Infrastructure (EF Core, Identity, almacenamiento de archivos, cifrado)
```

`Infrastructure` implementa interfaces definidas en `Domain`/`Application`; nunca al revés. `Domain` no referencia ningún paquete NuGet de infraestructura (ni siquiera EF Core).

```
CaeManager.sln
├── src/
│   ├── CaeManager.Domain/
│   │   ├── Clientes/            (Cliente.cs, ICLienteRepository.cs, ...)
│   │   ├── Centros/
│   │   ├── Empresas/
│   │   ├── Trabajadores/
│   │   ├── Documentos/          (TipoDocumento.cs, Documento.cs, EstadoDocumento.cs)
│   │   ├── Asignaciones/
│   │   ├── Alertas/
│   │   ├── Auditoria/
│   │   └── Common/              (Entity base, EntidadBase con soft delete, Result<T> — sin Domain Events: no existen y no se construyen sin caso de uso real, YAGNI)
│   │
│   ├── CaeManager.Application/
│   │   ├── Clientes/
│   │   │   ├── Commands/        (CrearCliente, EditarCliente, EliminarCliente)
│   │   │   ├── Queries/         (ObtenerClientes, ObtenerClientePorId)
│   │   │   ├── Dtos/
│   │   │   └── Validators/
│   │   ├── Centros/
│   │   ├── ...                  (una carpeta por feature de dominio)
│   │   └── Common/               (behaviors de MediatR, mapeos base)
│   │
│   ├── CaeManager.Infrastructure/
│   │   ├── Persistence/
│   │   │   ├── CaeManagerDbContext.cs
│   │   │   ├── Configurations/  (EntityTypeConfiguration por entidad, Fluent API)
│   │   │   ├── Migrations/
│   │   │   └── Repositories/
│   │   ├── Identity/
│   │   ├── FileStorage/
│   │   ├── Encryption/          (cifrado de credenciales de plataformas externas)
│   │   └── Auditing/            (SaveChangesInterceptor)
│   │
│   └── CaeManager.Web/           (Presentation — Blazor Server)
│       ├── Features/
│       │   ├── Dashboard/
│       │   ├── Clientes/
│       │   │   ├── Pages/       (ListaClientes.razor, DetalleCliente.razor)
│       │   │   ├── Components/  (ClienteFormulario.razor, ClienteTabla.razor)
│       │   │   └── State/
│       │   ├── Centros/
│       │   ├── Trabajadores/
│       │   ├── Documentos/
│       │   ├── Asignaciones/
│       │   ├── Alertas/
│       │   ├── Calendario/
│       │   ├── Reportes/
│       │   ├── Usuarios/
│       │   ├── Roles/
│       │   ├── Configuracion/
│       │   └── Auditoria/
│       ├── DesignSystem/         (componentes compartidos: Button, Table, Modal, ...)
│       ├── Layout/
│       └── Program.cs
│
└── tests/
    ├── CaeManager.Domain.Tests/
    ├── CaeManager.Application.Tests/
    └── CaeManager.IntegrationTests/
```

### Resolviendo "Feature First" vs "Clean Architecture"

El brief pide ambas cosas y no son contradictorias si se aplican en el nivel correcto:

- **Entre capas**: Clean Architecture (Domain / Application / Infrastructure / Presentation como proyectos separados).
- **Dentro de Application y Presentation**: Feature-First — una carpeta por dominio de negocio (Clientes, Centros, Trabajadores...), nunca una carpeta `Controllers/`, `Services/` o `Models/` genérica que mezcle features distintas.
- **Dentro de Domain**: organizado por agregado (mismo criterio, es el dominio quien manda).

Regla práctica: si para entender "todo lo relacionado con Trabajadores" hay que abrir carpetas dispersas por tipo técnico, la organización está mal. Debe bastar con abrir `Trabajadores/` en cada capa.

## CQRS ligero con MediatR

Cada caso de uso es un Command (escritura) o Query (lectura) explícito, manejado por MediatR:

- **Commands**: pasan por el Domain (cargan el agregado vía repositorio, invocan métodos de negocio que protegen invariantes, persisten). Ejemplo: `CrearClienteCommand → CrearClienteCommandHandler`.
- **Queries**: proyectan directamente a DTOs de lectura con `.Select(...)`, sin pasar por el repositorio de agregados — las queries no tienen invariantes que proteger, solo necesitan ser rápidas. No se usa `AsNoTracking()`: proyectar a un DTO ya no engancha nada al change tracker, así que sería ruido. Sí haría falta en las pocas lecturas que materializan entidades completas. Application define `IApplicationDbContext` con una propiedad `IQueryable<T>` de solo lectura por agregado; `CaeManagerDbContext` la implementa en Infrastructure. Application referencia el paquete `Microsoft.EntityFrameworkCore` (la capa de abstracciones, para poder usar `CountAsync`/`ToListAsync`/etc. sobre `IQueryable<T>`) pero **nunca** un proveedor concreto (`Npgsql.EntityFrameworkCore.PostgreSQL`, `...SqlServer`, ...) ni el tipo `CaeManagerDbContext` — eso es exclusivo de Infrastructure.
- **Pipeline behaviors** de MediatR, en este orden (de fuera adentro): `LoggingBehavior` (request, duración, tenant, usuario, resultado — nunca el contenido del request), `SerializacionAccesoDatosBehavior`, `ConcurrenciaBehavior`, `AutorizacionEscrituraBehavior` y `ValidationBehavior` (FluentValidation). **No hay behavior de captura de excepciones de dominio → `Result<T>`**: una `ArgumentException` de entidad sigue llegando cruda a la UI (hallazgo de coherencia de `docs/business/MATURITY_REVIEW.md` § 1, pendiente).

No se usa un `IRepository<T>` genérico. Cada agregado raíz (Cliente, Centro, Empresa, Trabajador, Documento, Asignacion) tiene su propia interfaz de repositorio definida en `Domain`, con los métodos que ese agregado necesita — no un CRUD genérico que invite a saltarse invariantes.

## Manejo de errores

- Errores esperables de negocio (validación, reglas violadas, "no encontrado") → `Result<T>` / `Result`, nunca excepciones. La UI los traduce a microcopy en español (ver `UX_PATTERNS.md`).
- Excepciones reservadas para errores verdaderamente inesperados (fallo de infraestructura). `LoggingBehavior` las registra con nivel Error (correlacionadas con tenant y usuario), Sentry las reporta si hay DSN configurado, y `app.UseExceptionHandler("/Error")` las traduce a la página de error genérica. No se traducen a `Result<T>`.

## Autenticación y autorización

- ASP.NET Core Identity como almacén de usuarios y roles, con cookie de autenticación (`AuthenticationStateProvider` nativo de Blazor Server).
- Roles semilla (fuente de verdad: `Infrastructure/Identity/Roles.cs`): `Administrador`, `DireccionCae`, `CoordinadorCae`, `GestorCae`, `Consulta`, `Cliente`. `CoordinadorCae` y `GestorCae` se llamaron `Supervisor` y `EjecutivoCae` hasta la migración `AgregarRolesJerarquia`.
- Autorización basada en policies (`[Authorize(Policy = "...")]`), no en checks de rol hardcodeados dispersos por el código.
- **SSO con Microsoft Entra ID** (opcional, `AzureAd:*` en configuración — ver `DEPLOY.md`): Identity sigue siendo el almacén de usuarios/roles, Entra ID solo se añade como external login provider (OpenID Connect), restringido al tenant de la empresa. Cualquier cuenta del tenant puede iniciar sesión — si es la primera vez, se auto-provisiona un `ApplicationUser` **sin ningún rol**, que queda en una pantalla de espera (`/cuenta/pendiente-de-rol`) hasta que un Administrador le asigna uno desde la pestaña "Pendientes de asignar" en `/roles` (ver `Roles.razor`). Mientras Entra ID esté configurado, `RestriccionLoginLocalClaimsTransformation` (`IClaimsTransformation` global) limita el rol efectivo de cualquier sesión que no se haya autenticado por Microsoft a `Consulta`, como capa extra de control para los roles editores — **excepto Administrador**, que conserva su rol real incluso por login local (vía de escape deliberada para nunca perder acceso de administración). Sin `AzureAd:*` configurado, todo esto queda completamente inerte (mismo principio que Sentry/Backups/Anthropic) — el login local se comporta exactamente igual que antes de que existiera SSO.
- **Envío de correo** (`IEmailService` en Application, `GraphEmailService` en Infrastructure, opcional, `Graph:*` en configuración — ver `DEPLOY.md`): Microsoft Graph con permisos de aplicación (client credentials, no depende de que haya sesión de usuario), usado para notificar a los Administradores cuando hay un usuario pendiente de rol y para confirmar al usuario cuando se le asigna uno. Siempre "best effort": un fallo de envío se registra en el log pero nunca revierte ni bloquea la acción de negocio que lo dispara. Sin `Graph:*` configurado, queda inerte igual que el resto de integraciones opcionales.

## Multi-tenancy (implementado — ver `docs/MULTITENANCY.md`)

Hydra es multi-tenant por diseño (`ADR-003-saas-multitenant.md`): cada organización compradora es un tenant, frontera absoluta de aislamiento. Mecanismo (`ADR-001`, reactivado): `TenantId` por fila + **Global Query Filter combinado con el de soft delete** (EF Core solo admite un `HasQueryFilter` por entidad: `!EstaEliminado && TenantId == tenantActual`), interceptor de `SaveChanges` que sella `TenantId` en escritura (los Commands nunca lo pasan), índices únicos de negocio compuestos `(TenantId, campo)`, y `ITenantActual` resuelto por claim de sesión (mismo patrón que `ICurrentUserService`; estrategia completa en `docs/MULTITENANCY.md` § 8). El aislamiento es un concern de Infrastructure: Domain y Application no razonan sobre tenants.

Estado: **implementado y cubierto por tests**. Todas las entidades que heredan de `EntidadConTenant`/`EntidadBase` (38 a fecha de la Fase 59) tienen su filtro global en `CaeManagerDbContext.OnModelCreating`, el `TenantSelladoInterceptor` sella en escritura y rechaza modificaciones cruzadas, y hay un test de aislamiento por agregado (`AislamientoPorAgregadoTests`, uno por cada una). La invariante es "todas, sin excepciones" — el número concreto envejece, la regla no. Lo que sigue pendiente de `ADR-003` son sus condiciones de salida a producción (PostgreSQL, DPA/Términos por tenant), no el mecanismo.

## Plataforma de Integraciones (diseño de backlog, no implementado — ver `ARQUITECTURA-INTEGRACIONES.md`)

Hydra se integrará con plataformas CAE/ERP/CRM/IA externas (Dokify, 6Coordina, CTAIMA, eCoordina, Microsoft 365...) mediante una capa de **proveedores de integración** (`IIntegrationProvider`) desacoplada del dominio — mismo principio que ya aplican `IEmailService`/`IFileStorageService`: la interfaz vive en Application, el adaptador concreto de cada proveedor vive en Infrastructure, y ningún tipo de Domain/Application/Presentation conoce el nombre de un proveedor real. Cada tenant activa y configura sus integraciones de forma independiente (capacidad por-tenant), sobre el mismo aislamiento de `TenantId` del resto del dominio. Detalle completo, incluida la clasificación de catálogos y el modo de resolución de tenant para webhooks entrantes, en `ARQUITECTURA-INTEGRACIONES.md` y `docs/MULTITENANCY.md` § 7-8.

## Auditoría y soft delete

- Interceptor de EF Core (`SaveChangesInterceptor`) que registra en una tabla `Auditoria` cada creación/modificación/eliminación de entidades marcadas como auditables: quién, cuándo, qué cambió (antes/después serializado).
- Soft delete por convención: propiedades `EstaEliminado`, `EliminadoEnUtc`, `EliminadoPor` en la clase base de entidad, con un **global query filter** de EF Core que las excluye automáticamente de cualquier consulta. Eliminar nunca borra la fila físicamente.

## Datos sensibles

Las credenciales de plataformas externas (usuario/contraseña de portales como CTAIMA) se cifran en reposo usando la **ASP.NET Core Data Protection API**, mediante un `ValueConverter` de EF Core aplicado solo a esos campos. Nunca se registran en logs ni en el historial de auditoría en texto plano. El acceso a verlas está restringido por policy y queda registrado en auditoría como "acceso a dato sensible".

## Archivos (PDFs de documentos)

Abstracción `IFileStorageService` en Application, con dos implementaciones intercambiables en Infrastructure sin tocar Application ni Presentation: disco local (por defecto, ruta configurable) o S3 (`AlmacenamientoS3:Activo`, P2 #22 de `docs/business/MATURITY_REVIEW.md` — necesario para más de una réplica, ver `DEPLOY.md`). **Cifrado en reposo** con `IDataProtector` (mismo mecanismo que las credenciales de plataformas externas, protector `"CaeManager.Archivos.v1"`) implementado en `DiskFileStorageService`: el contenido nunca queda en claro en el volumen local, sin migración automática de lo escrito antes de este cambio (un archivo legado se sigue sirviendo por fallback si `Unprotect` falla, y queda cifrado la próxima vez que algo lo reescriba). **Gap conocido**: `S3FileStorageService` todavía NO aplica ese mismo cifrado — quedó pendiente al converger ambos cambios en paralelo (P1-12 y P2-22); si `AlmacenamientoS3:Activo` se activa antes de cerrarlo, los PDFs (incluidos reconocimientos médicos, art. 9 RGPD) llegan a S3 sin cifrar. Ver el comentario de `S3FileStorageService`.

Todo archivo adjunto de un Documento se guarda siempre como un único PDF, aunque el usuario suba imágenes, Word o varios archivos a la vez:

- **JPG/PNG → PDF**: PDFsharp local (`ConversorArchivosPdf` en `CaeManager.Web/Documentos/`), instantáneo.
- **Word (.docx) → PDF**: abstracción `IConversorWordPdfService` en Application, implementada en Infrastructure (`LibreOfficeConversorWordPdfService`) invocando **LibreOffice headless** (`soffice --headless --convert-to pdf`) como proceso externo. Se descartó Aspose.Words/GroupDocs por licencia comercial (mismo criterio que QuestPDF más abajo) y una API cloud de conversión por sacar documentos de trabajadores fuera del servidor — dato sensible en este dominio (PRL). El Dockerfile instala el paquete `libreoffice-writer` en la imagen final.
- **Varios archivos en una misma subida** (cualquier mezcla de PDF/imagen/Word) se combinan en un único PDF multipágina antes de guardarse.

## Generación de reportes (Excel/PDF)

Excel se genera con **ClosedXML** (MIT). Para PDF se evaluó y se descartó **QuestPDF**: su licencia Community factura según los ingresos *de la empresa que usa el software*, no de CAE Manager como producto — inaceptable para un SaaS comercial de terceros. Se usa **PDFsharp 6.x** (MIT, github.com/empira/PDFsharp) en su lugar, dibujando la tabla directamente con `XGraphics` — el volumen esperado de filas no justifica una librería de layout de tablas.

PDFsharp 6 no depende de GDI+ ni de las fuentes del sistema operativo (es multiplataforma de verdad), pero por eso mismo exige un `IFontResolver` explícito. Se embebe **DejaVu Sans** (licencia estilo Bitstream Vera, permisiva, ver `src/CaeManager.Web/Resources/Fonts/LICENSE-DejaVuFonts.txt`) como recurso del ensamblado (`EmbeddedFontResolver`), para no depender de que el servidor de despliegue tenga fuentes instaladas — necesario, además, para que los caracteres acentuados del español se rendericen bien.

## Testing desde el inicio

- `CaeManager.Domain.Tests`: pruebas unitarias de reglas de negocio puras (p. ej. cálculo de estado de un Documento según vigencia y umbrales) — sin base de datos.
- `CaeManager.Application.Tests`: handlers de Commands/Queries con repositorios en memoria o mocks.
- `CaeManager.IntegrationTests`: EF Core contra PostgreSQL real (una base de datos propia por fixture, ver `BaseDatosPostgresDePruebas`), validando migraciones y queries reales.

## Convención de nombres de proyecto

Prefijo `CaeManager` en todos los proyectos, en español para el dominio (`Cliente`, `Trabajador`, `Documento`) y en inglés para términos técnicos genéricos (`Command`, `Query`, `Repository`, `Result`) — ver detalle completo en `CODING_STANDARDS.md`.

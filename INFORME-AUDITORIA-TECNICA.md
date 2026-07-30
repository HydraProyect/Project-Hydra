# Informe de auditoría técnica — Hydra / CAE Manager

Fecha: 2026-07-30 · Rama: `claude/repository-technical-audit-00jdko` · Commit base: `fbd9baf`

**Alcance**: auditoría estática completa (4 proyectos `src/`, 5 de `tests/`, ~477 archivos `.cs`, 86 `.razor`, 24 migraciones, 15 documentos normativos).

**Limitación declarada**: no hay SDK de .NET en el entorno de auditoría (`dotnet` no disponible). **No se ha compilado ni ejecutado la suite de tests.** Todas las afirmaciones se apoyan en lectura directa del código, con `archivo:línea`. Lo que no he podido demostrar se marca explícitamente como hipótesis.

---

# FASE 0 — Comprensión del sistema

## Qué es

Plataforma SaaS multi-tenant de gestión CAE (Coordinación de Actividades Empresariales, PRL español). Sustituye hojas de cálculo por un sistema normalizado y auditable. Dos perfiles de comprador: consultoras de PRL y empresas contratistas. Cada organización compradora es un tenant.

## Arquitectura real (verificada, no la documentada)

Clean Architecture en 4 proyectos con dependencias correctas hacia adentro:

```
CaeManager.Web (Blazor Server, .NET 10)
      ↓
CaeManager.Application (CQRS con MediatR, FluentValidation)
      ↓
CaeManager.Domain (sin dependencias de infraestructura)
      ↑
CaeManager.Infrastructure (EF Core 10 / SQLite, Identity, Storage, IA)
```

**Verificado**: `Domain` no referencia EF Core ni Web. `Application` referencia `Microsoft.EntityFrameworkCore` (solo abstracciones, para `ToListAsync` sobre `IQueryable<T>` de `IApplicationDbContext`) pero nunca un proveedor concreto ni `CaeManagerDbContext`. La inversión de dependencias es correcta: Infrastructure implementa interfaces de Application.

Dentro de `Application` y `Web`, organización *feature-first* (una carpeta por dominio). Dentro de `Domain`, por agregado. Esto resuelve correctamente la tensión "Feature First vs Clean Architecture" declarada en `ARCHITECTURE.md`.

## Bounded contexts / módulos

Un único bounded context de negocio (CAE) con ~20 agregados, más un kernel transversal:

- **Núcleo CAE**: Clientes, Centros, Empresas, Subcontratas, Trabajadores, Documentos, TiposDocumento, Asignaciones, RequisitosDocumentales, Visitas, Vehículos, Proyectos, Evaluaciones, Incidencias, Alertas.
- **Kernel**: MultiTenancy, Identity/Authorization, Storage, Auditoría, Notificaciones, IA documental, Backups, Facturación.

La regla de negocio central (cálculo de estado de un Documento por vigencia y umbrales) está correctamente centralizada en `Domain/Documentos/CalculadoraEstadoDocumento.cs` como lógica pura, invocada desde el agregado (`Documento.cs:166`). **No está filtrada a handlers ni a UI.**

## Flujos principales

**Autenticación**: ASP.NET Core Identity + cookie. SSO opcional con Entra ID (OIDC), inerte si no está configurado. `TenantClaimsPrincipalFactory` inyecta el claim firmado `tenant_id` en el `ClaimsPrincipal`. `FallbackPolicy` exige sesión en todo salvo `[AllowAnonymous]`.

**Autorización**: dos capas independientes y bien separadas conceptualmente:
1. *Escritura por rol* — `AutorizacionEscrituraBehavior` (pipeline MediatR) bloquea Commands para roles `Consulta`/`Cliente`.
2. *Alcance de datos por cartera* — `IAlcanceDatosService` restringe qué Clientes/Centros/Trabajadores ve cada rol dentro de su tenant.

**Aislamiento multi-tenant** (la propiedad de seguridad crítica): `TenantId` en las entidades de dominio + *global query filter* combinado con soft delete, centralizado en `CaeManagerDbContext.OnModelCreating` + `TenantSelladoInterceptor` que sella en escritura y rechaza modificaciones cruzadas. Almacenamiento de archivos particionado por carpeta de tenant.

**Delegación (ADR-004)**: una consultora opera sobre tenants ajenos vía `DelegacionTenant` + `AsignacionOperadorDelegado`, seleccionando un "Delegated Workspace" activo. **Contra lo que dice `CLAUDE.md`, esto está implementado**, no pendiente.

**Documentos**: subida multi-archivo → conversión a PDF único (PDFsharp para imágenes, LibreOffice headless para Word) → almacenamiento fuera de `wwwroot` → servido por endpoint autenticado.

**IA documental**: router con 3 proveedores reales (Anthropic, Gemini, Mistral OCR) con caché por SHA256 del contenido.

**Jobs de fondo**: solo existe `BackupHostedService`. No hay infraestructura de colas.

## Calidad de base (verificada, y es buena)

- Cero `TODO`/`HACK`/`FIXME`. Cero bloques de código comentado. Cero `catch` vacíos (132 bloques `catch`, todos traducen a `Result<T>` o error de validación).
- Cero SQL crudo (`FromSqlRaw`/`ExecuteSqlRaw`): 0 coincidencias.
- Cero `IgnoreQueryFilters()` en código (solo aparece en un comentario).
- Todos los paquetes NuGet tienen uso real (verificado AWSSDK.S3, PDFtoImage, Markdig, ClosedXML, PDFsharp, Sentry, Serilog).
- Dominio rico: setters privados, factorías estáticas, invariantes dentro del agregado.
- CI en GitHub Actions con build + format + tests y `-warnaserror`.
- 296 tests (126 dominio, 85 integración, 64 aplicación, 13 web, 8 E2E), incluidos 31 de aislamiento multi-tenant.

**Este no es un código en problemas.** La disciplina de ingeniería es alta y poco habitual. Los hallazgos siguientes se concentran en una zona muy concreta.

## Inconsistencias documentales detectadas

| Documento | Afirmación | Realidad |
|---|---|---|
| `ARCHITECTURE.md:134` | "columna, filtros e interceptor **todavía no existen en el código**" | Existen: 32 filtros globales + interceptor |
| `CLAUDE.md` | ADR-004 "pendiente, no implementado" | Implementado extremo a extremo (dominio, endpoints, UI) |
| `ARCHITECTURE.md:114` | queries usan `.AsNoTracking().Select(...)` | `AsNoTracking` no aparece en ningún archivo de query |

---

# FASE 1 — Auditoría técnica

## 🔴 CRÍTICO

### C-1. Toma de control total de cualquier tenant mediante falsificación de la cookie `cae_cliente_activo`

**Severidad**: Crítico

**Ubicación**
- `src/CaeManager.Web/Services/ClienteActivoSeleccionado.cs:38-46` (lectura)
- `src/CaeManager.Web/Services/TenantActual.cs:51-56` (precedencia)
- `src/CaeManager.Web/Features/Tenants/ClienteActivoEndpoints.cs:53-64` (escritura)
- `src/CaeManager.Infrastructure/Persistence/Seed/TenantSeedData.cs:17` (GUID predecible)

**Problema**

`ITenantActual.TenantId` es la única fuente que alimenta el *global query filter* de EF Core, el `TenantSelladoInterceptor` y el particionado de `DiskFileStorageService`. Su orden de resolución es:

```csharp
if (AmbitoTenantExplicito.TenantIdActual is { } t) return t;
if (clienteActivoSeleccionado.TenantIdSeleccionado is { } t) return t;   // ← COOKIE
/* solo entonces */ return claim tenant_id;
```

La cookie gana al claim firmado. Y esa cookie es **un GUID en texto plano, sin firmar ni cifrar**:

```csharp
// escritura
Response.Cookies.Append(NombreCookie, tenantId.ToString(), ...)
// lectura
var valorCookie = HttpContext?.Request.Cookies[NombreCookie];
Guid.TryParse(valorCookie, out var tenantIdCookie)
```

El endpoint **sí** autoriza correctamente antes de escribirla (`ClienteActivoEndpoints.cs:34-42`, contra `DelegacionTenant`/`AsignacionOperadorDelegado` en base de datos). El fallo es que **esa autorización solo ocurre al escribir la cookie, nunca al usarla**. `ClienteActivoSeleccionado` la lee sin revalidar — y su propio comentario lo admite: *"este servicio solo la lee, sin repetir esa comprobación"*.

`HttpOnly = true` no protege aquí: impide que JavaScript de terceros la lea, no que el propio usuario autenticado la fabrique con `curl` o las devtools usando su sesión legítima.

**Evidencia del exploit completo**

`TenantSeedData.cs:17` fija el tenant #1 en un GUID adivinable:

```csharp
public static readonly Guid IdPorDefecto = new("00000000-0000-0000-0000-000000000001");
```

Por tanto no hace falta ni filtrar información previa:

```
curl -H "Cookie: .AspNetCore.Identity.Application=<sesión propia y legítima>; \
                 cae_cliente_activo=00000000-0000-0000-0000-000000000001" \
     https://host/documentos
```

**Riesgo**

Cualquier usuario autenticado —incluido uno de rol `Consulta` de otro tenant— obtiene sobre el tenant objetivo:
- **Lectura total**: el filtro global pasa a resolver el tenant falsificado en *todas* las consultas.
- **Escritura**: el `TenantSelladoInterceptor` compara `TenantOriginal != tenantActual`; como ambos son ya el tenant falsificado, la comprobación pasa. Las entidades nuevas se sellan con el tenant ajeno.
- **Archivos**: `DiskFileStorageService.ResolverRutaSegura` construye la ruta desde `_tenantActual.TenantId`, con lo que sirve los PDFs de la carpeta del tenant objetivo — incluida **vigilancia de la salud, categoría especial del Art. 9 RGPD**.

Es la ruptura completa de la frontera de seguridad que `docs/MULTITENANCY.md` define como absoluta. Un único incidente aquí es notificable a la AEPD y termina con el caso de negocio SaaS.

**Matiz de exposición actual (honesto)**: hoy la instalación sirve una organización real; los otros tenants son de demo (`DelegacionDemoSeeder`, `SegundoTenantSeeder`). El impacto inmediato es menor que el teórico, pero el defecto es un **bloqueante absoluto** para admitir el segundo cliente real, y ya es explotable por cualquier operador delegado de demo.

**Solución**

1. **Corto plazo (correcto y suficiente)**: firmar la cookie con `IDataProtector` (ya hay `IDataProtectionProvider` inyectado en el sistema), de modo que un valor fabricado no se descifre y se descarte.
2. **Defensa en profundidad (recomendada, la de verdad)**: no confiar en la cookie como portadora de autoridad. Al cambiar de workspace, **reemitir el ticket de autenticación** con un claim adicional firmado (`tenant_efectivo`) vía `SignInAsync`; `ITenantActual` lee solo claims. Esto elimina la clase entera de bug en vez de parchear el síntoma.
3. Cambiar `TenantSeedData.IdPorDefecto` a un GUID aleatorio generado en el aprovisionamiento (reduce el objetivo trivial; no sustituye a 1 ó 2).
4. Añadir test de regresión: cookie falsificada ⇒ no se resuelve tenant ajeno. Hoy **no existe**: `TenantActualTests.cs` usa un doble que siempre devuelve `null` (`ClienteActivoSeleccionadoFalso`), por lo que la ruta vulnerable no está cubierta.

**Esfuerzo**: S (opción 1) / M (opción 2)

**Beneficio**: cierra la única vulnerabilidad crítica del sistema y desbloquea la condición de salida a producción multi-cliente.

---

## 🟠 ALTO

### A-1. `TarifaCliente` no tiene filtro global de tenant → lectura cruzada de tarifas

**Severidad**: Alto

**Ubicación**: `src/CaeManager.Infrastructure/Persistence/CaeManagerDbContext.cs:160-192`; `src/CaeManager.Application/Facturacion/Queries/ObtenerTarifasCliente/ObtenerTarifasClienteQuery.cs:23-26`

**Problema**

Censo exhaustivo: de **34 entidades concretas que heredan `EntidadConTenant`/`EntidadBase`, solo 32 tienen `HasQueryFilter`**. Faltan exactamente dos. `TarifaCliente` es una de ellas, y es la que sí tiene un lector desprotegido:

```csharp
var tarifas = await dbContext.TarifasCliente
    .Where(t => t.ClienteId == request.ClienteId)   // sin TenantId, sin EstaEliminado
    .OrderBy(t => t.Concepto).ToListAsync(cancellationToken);
```

`TarifaCliente : EntidadBase` (por tanto tiene `TenantId` y `EstaEliminado`, y su configuración declara un índice único `(TenantId, ClienteId, Concepto)`), pero ninguna de las dos condiciones se aplica. Contrasta con `ObtenerResumenFacturacionQuery`, que **sí** valida antes contra `dbContext.Clientes` (filtrado) y por eso no es vulnerable.

**Riesgo**

Divulgación cruzada de precios comerciales (`PrecioUnitario`, `MonedaIso`) — información competitiva sensible entre consultoras. Ruta de explotación realista **sin necesidad de C-1**: un operador delegado que legítimamente trabajó sobre el tenant B memoriza sus `ClienteId`; cuando se le revoca la delegación, `ObtenerTarifasClienteQuery` **sigue devolviendo las tarifas de B**, porque nada en esa consulta depende del tenant. Es una fuga persistente post-revocación. Además devuelve tarifas borradas lógicamente.

Las escrituras cruzadas sí están contenidas por `TenantSelladoInterceptor`, así que el impacto es de confidencialidad, no de integridad.

**Solución**

```csharp
builder.Entity<TarifaCliente>()
    .HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
```

Y, por consistencia con `ObtenerResumenFacturacion`, validar el `ClienteId` contra `dbContext.Clientes` en el handler.

**Esfuerzo**: XS · **Beneficio**: cierra una fuga cruzada real con una línea.

---

### A-2. Los códigos de rol se duplican como literales al cruzar la frontera de capas

**Severidad**: Alto

**Ubicación**: fuente de verdad en `src/CaeManager.Infrastructure/Identity/Roles.cs:18-23`; duplicado en ≥8 archivos de `Application`, entre ellos `Common/AutorizacionEscrituraBehavior.cs:23-24` (la puerta de escritura), `Tenants/Commands/CrearAsignacionOperadorDelegado/...:26-27`, `Clientes/Commands/ReasignarEjecutivoCliente/...:30`.

**Problema**

`Application` no puede referenciar `Infrastructure.Identity`, así que los códigos se reescriben a mano:

```csharp
private const string RolConsulta = "Consulta";
private const string RolCliente  = "Cliente";
```

El comentario lo declara deliberado. La consecuencia no lo es: **un renombrado o una errata en `Roles.cs` no produce ningún error de compilación** en ninguna de estas comprobaciones de seguridad. `Roles.cs` ya documenta un renombrado histórico (`Supervisor`→`CoordinadorCae`, `EjecutivoCae`→`GestorCae`); el próximo puede desalinear silenciosamente la puerta de escritura y conceder escritura a un rol de solo lectura.

**Solución**

Extraer **solo el catálogo de constantes** (no la maquinaria de Identity) a `CaeManager.Domain/Common/RolesCae.cs` — la identidad de rol es un concepto de dominio — y que `Infrastructure.Identity.Roles` lo referencie en vez de redeclararlo. Sustituir los literales. Esto elimina la duplicación *y* restaura la seguridad en compilación. No es abstracción especulativa: es mover una constante al lugar correcto.

**Esfuerzo**: S (1-2 h) · **Beneficio**: convierte un riesgo silencioso en error de compilación.

---

### A-3. `ObtenerDocumentos`: la paginación es una fachada; se materializa el conjunto completo

**Severidad**: Alto

**Ubicación**: `src/CaeManager.Application/Documentos/Queries/ObtenerDocumentos/ObtenerDocumentosQuery.cs:159-190`

**Problema**

La rejilla principal (`Documentos.razor`, QuickGrid con `ItemsProvider` de servidor) pide una página. El handler, en cambio, calcula el estado del semáforo en memoria y por tanto hace:

```csharp
var todos = await consulta.OrderByDescending(...).Select(...).ToListAsync(); // TODO el conjunto
var conEstado = todos.Select(d => d with { Estado = CalculadoraEstadoDocumento.Calcular(...) });
var elementos = listaFiltrada.Skip((pagina-1)*tamano).Take(tamano).ToList();  // pagina en memoria
```

`Skip`/`Take` se ejecutan **después** de `ToListAsync`, sobre una unión `Concat` de 5 ámbitos.

**Riesgo**

Cada visita a la lista más usada del producto transfiere todos los documentos del tenant al circuito, no los 20 visibles. Con 20.000 documentos, cada clic de paginación mueve 20.000 filas. Es coste lineal en el tamaño del tenant, en la ruta más caliente, y presiona la RAM del servidor Blazor (donde el circuito ya es el recurso escaso).

**Solución**

Expresar los umbrales como aritmética de fechas traducible a SQL (`FechaVencimiento < @hoy`, `< @hoy + @rojo`, ...) o persistir el estado calculado, de forma que filtro y `Skip/Take` bajen a SQL. Mínimo viable: paginar en SQL cuando `request.Estado is null` (el caso común), que hoy también materializa todo.

**Esfuerzo**: M · **Beneficio**: convierte la pantalla principal de O(documentos del tenant) a O(tamaño de página).

---

### A-4. El pipeline de IA se ejecuta en línea dentro del circuito Blazor; no hay infraestructura de jobs

**Severidad**: Alto

**Ubicación**: `src/CaeManager.Application/Documentos/Queries/DetectarCamposDocumento/DetectarCamposDocumentoQuery.cs:39` → `src/CaeManager.Application/DocumentosIa/DocumentAIRouterService.cs:49-146`

**Problema**

Al subir un documento se espera de forma síncrona a: clasificación → OCR (HTTP a Mistral) → extracción estructurada con LLM (HTTP a Anthropic/Gemini) → **y posible reintento con un segundo proveedor** (`:126-146`, otra llamada LLM completa). El único `BackgroundService` del repositorio es `BackupHostedService`; no hay colas ni `Channel<T>`.

**Riesgo**

Cada subida congela el circuito de ese usuario durante segundos o decenas de segundos, manteniendo abiertos el `DbContext` scoped y la conexión SQLite. N subidas concurrentes = N circuitos bloqueados + hasta 2N llamadas LLM salientes + N conexiones retenidas. Es el principal muro de escalabilidad del flujo documental, y agrava la contención de escritura de SQLite porque `DocumentAIRouterService.cs:236` hace `SaveChangesAsync` *dentro* de la ruta bloqueante.

**Atenuante verificado**: la caché por SHA256 (`:54-68`) con índice único `(TenantId, HashSha256)` corta en seco las resubidas idénticas. El coste es el primer procesado.

**Solución**

Mover `ProcesarAsync` a una cola en proceso (`System.Threading.Channels` + consumidor `BackgroundService`), devolver de inmediato y notificar al circuito al terminar. Es el sitio donde sí merece la pena construir la capacidad "Background Jobs" que `docs/PLATFORM.md` ya reserva — con caso de uso real, no especulativo.

**Esfuerzo**: L · **Beneficio**: desbloquea el flujo más lento y elimina la retención de conexiones bajo carga.

---

## 🟡 MEDIO

### M-1. `AprobacionDocumento` sin filtro global (hueco latente)

`CaeManagerDbContext.cs:160-192` — la segunda de las dos entidades sin filtro. Hoy no fuga: su único lector (`ObtenerEstadisticasAprobacionDocumentoQuery`) hace join contra `Documentos`, que sí está filtrado. Pero la invariante documentada ("ninguna tabla sin filtro global") está rota, y cualquier consulta futura que lea la tabla directamente cruzará tenants en silencio. Confiar en que todo lector futuro recuerde hacer el join es frágil en la frontera de seguridad más sensible. **Fix**: `builder.Entity<AprobacionDocumento>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);` · **Esfuerzo**: XS

### M-2. Ausencia total de control de concurrencia optimista

`grep` de `IsConcurrencyToken|RowVersion|IsRowVersion` → **0 coincidencias** en todo el código no-migración. Ninguna entidad tiene token de concurrencia. Dos usuarios editando el mismo Trabajador o Documento producen *lost update* silencioso: el último `SaveChanges` pisa al anterior sin aviso ni traza de conflicto. En un sistema cuyo valor es la trazabilidad documental para cumplimiento normativo, perder una edición sin rastro es un defecto de negocio, no solo técnico. **Fix**: añadir token de concurrencia a los agregados con edición concurrente realista (Documento, Trabajador, Cliente) y traducir el `DbUpdateConcurrencyException` a un `Result` con microcopy. **Esfuerzo**: M

### M-3. Claves de Data Protection sin cifrar en reposo

`InfrastructureServiceCollectionExtensions.cs:84-91`: `AddDataProtection().SetApplicationName(...).PersistKeysToFileSystem(...)` sin `ProtectKeysWith*`. El llavero es XML plano en disco y protege las credenciales de portales externos cifradas por los `ValueConverter` (`CaeManagerDbContext.cs:38-43`). Quien lea el volumen (compromiso del host, exfiltración de backup, volumen mal configurado) descifra todas las credenciales. `DEPLOY.md:111` ya reconoce la limitación conscientemente. Riesgo secundario de disponibilidad: sin volumen persistente, cada redespliegue invalida credenciales irrecuperablemente. **Fix**: `ProtectKeysWithCertificate` o KMS/Key Vault; asegurar permisos restringidos del directorio. **Esfuerzo**: S

### M-4. La puerta de escritura discrimina Command vs Query por sufijo del nombre de clase

`AutorizacionEscrituraBehavior.cs:29`: `if (!typeof(TRequest).Name.EndsWith("Command", ...)) return await next(...)`. Un control de seguridad que descansa en una convención de nombres, sin compilador ni test que la garantice. **Verificado: hoy la convención se cumple** — no encontré ningún `IRequest` de escritura que no termine en `Command`, así que **no es un bypass activo, es un riesgo latente**. El día que alguien cree `AprobarDocumentoRequest`, la escritura queda sin control de rol y nada lo avisa. **Fix**: interfaz marcadora `ICommand` y `request is ICommand`. **Esfuerzo**: S

### M-5. `AlcanceDatosService`: memoización incompleta y round-trip de IDs

`src/CaeManager.Infrastructure/Autorizacion/AlcanceDatosService.cs` — solo `_accesoTotal` y `_clienteIds` se cachean (`:20-22`). `ObtenerCentroIds/EmpresaIds/SubcontrataIds/VehiculoIds` **no**, y `ObtenerEmpresaIdsVisiblesAsync` es reinvocado por `ObtenerSubcontrataIds`, `ObtenerVehiculoIds` y directamente: en la página de Documentos se ejecuta ~3 veces y `ObtenerCentroIds` ~2 por carga. Además materializa listas de Guid (miles de trabajadores) para devolverlas a SQL vía `.Contains()`, en vez de expresar la visibilidad como subconsulta correlacionada. **Fix**: (a) memoizar los resolvers restantes; (b) componer la visibilidad como `IQueryable` donde el conjunto es grande. **Esfuerzo**: S (a) / M (b)

### M-6. KPIs del dashboard materializan una columna completa para contar

`ObtenerKpisDashboardQuery.cs:61-72`: `documentosQuery.Select(d => d.FechaVencimiento).ToListAsync()` trae todas las fechas de vencimiento visibles a memoria y luego hace `Count(...)` cuatro veces. Se ejecuta en la portada, en cada sesión. Mismo remedio que A-3 (conteos por rango de fechas en SQL). **Esfuerzo**: S

### M-7. La detección IA escanea tablas completas por cada subida

`DetectarCamposDocumentoQuery.cs:78-86`: `dbContext.Trabajadores.Select(t => new {t.Id, t.Dni}).ToListAsync()` y después normaliza el DNI en memoria (no es traducible a SQL) — escaneo completo de la tabla de trabajadores en cada subida, sumado a la ruta ya bloqueante de A-4. **Fix**: columna `DniNormalizado` indexada, calculada al guardar. **Esfuerzo**: S

### M-8. `GET /cuenta/cliente-activo/{tenantId}` cambia estado sin antiforgery

`ClienteActivoEndpoints.cs:21-71` — un `MapGet` que escribe/borra cookie y redirige. La cookie de Identity es `SameSite=Lax`, así que una subpetición no la lleva, pero una navegación de nivel superior sí. Impacto acotado: el endpoint valida autorización real y usa `LocalRedirect` (sin open redirect), así que no hay elevación — el peor caso es que un operador delegado acabe trabajando en un tenant autorizado pero equivocado, con riesgo de introducir datos donde no toca. **Fix**: convertir a POST con token antiforgery (`UseAntiforgery` ya está activo). **Esfuerzo**: XS

### M-9. Deriva documental (3 afirmaciones falsas en documentos normativos)

Ver tabla en Fase 0. `ARCHITECTURE.md:134` afirma que el multi-tenant no existe en código cuando sí existe; `CLAUDE.md` dice que ADR-004 está sin implementar cuando está implementado; `ARCHITECTURE.md:114` promete `AsNoTracking` que no se usa. En un repositorio cuya disciplina explícita es "lee la documentación antes de planificar", documentación falsa induce decisiones erróneas: es deuda activa, no cosmética. **Fix**: corregir las tres afirmaciones. **Esfuerzo**: XS

### M-10. Sin retención de datos ni derecho de supresión implementados

`grep` de `Retencion|DerechoOlvido|Anonimiza|Purga` → **0 coincidencias**. El soft delete nunca borra físicamente, de modo que hoy el sistema **no puede satisfacer un ejercicio del derecho de supresión** (Art. 17 RGPD) sobre datos de trabajadores, que incluyen categoría especial del Art. 9 (vigilancia de la salud). Es una condición de salida a producción ya recogida en `RGPD-TRATAMIENTO-DATOS.md` y `ADR-003`.

> Conforme a `CLAUDE.md`, **no propongo implementación aquí**: retención, supresión, DPIA y DPA son decisiones con componente legal que requieren tu confirmación previa. Lo reporto como brecha de cumplimiento, no como tarea técnica lista para ejecutar.

**Esfuerzo**: L (y requiere criterio legal, no solo técnico)

---

## 🟢 BAJO

- **B-1. Excepciones tragadas sin log** — `Documentos.razor.cs:160-164` marca error de carga y devuelve vacío sin registrar la excepción, aun teniendo `Logger` disponible y usado en el mismo archivo (`:352`). Igual en `PdfSharpExtractorTextoDigitalService.cs:39` y `PdfSharpClasificadorDocumentoService.cs:48` (`catch (Exception)` → `Result.Fallo` descartando `ex`). Son los únicos 3 casos de 132 `catch`; el resto es correcto. Se pierde el diagnóstico de fallos de carga y de OCR. **Fix**: `Logger.LogError(ex, ...)` antes de devolver. **Esfuerzo**: XS
- **B-2. Presentación acoplada a un tipo de opciones de Infrastructure** — `BotonAsistenteIa.razor:3,5` hace `@using CaeManager.Infrastructure.AsistenteIa` + `@inject IOptions<AnthropicOptions>` solo para decidir si pinta el botón. La llamada real sí va por `Mediator`→`IAsistenteIaService` (DIP correcto). **Fix**: exponer un booleano de disponibilidad desde Application. **Esfuerzo**: XS
- **B-3. Endpoints de exportación sin exigencia de rol** — `Program.cs:233-236` deja `/clientes/exportar.xlsx` y `/reportes/documentos.{xlsx,pdf}` solo con `FallbackPolicy`. El aislamiento por tenant se mantiene; la pregunta es intra-tenant: ¿debe un rol `Consulta` poder exportar masivamente la cartera? Solo `/auditoria/...` exige rol (`AuditoriaEndpoints.cs:51`). **Confirmar intención.**
- **B-4. Los índices compuestos no lideran por `TenantId`** — p. ej. `DocumentoConfiguration.cs:17-21` indexa `(TrabajadorId, TipoDocumentoId)`. Como toda consulta lleva el predicado `TenantId ==`, el índice ideal sería `(TenantId, TrabajadorId, ...)`. Irrelevante con un tenant dominante; progresivamente costoso al crecer el número de tenants.
- **B-5. Búsqueda no sargable** — `ObtenerDocumentosQuery.cs:148-152`: `x.Campo.ToUpper().Contains(...)` → `upper(col) LIKE '%…%'`, escaneo completo no indexable. Aceptable al volumen actual.
- **B-6. `AsNoTracking` inexistente** — 0 apariciones en ~70 archivos de query. En la mayoría es inocuo (proyectar a DTO ya no trackea), pero sí sobra tracking en las lecturas que materializan entidades completas (`ObtenerResumenFacturacionQuery.cs:60`, `ParametrosSistema.SingleAsync`).
- **B-7. `Documentos.razor.cs` concentra demasiadas responsabilidades** — 584 líneas con grid, estado del drawer (~30 campos privados), subida y conversión de archivos (`:307`), detección IA (`:370`) y alta/renovación/borrado (`:446`, `:555`). Es un patrón *consistente* en toda la app (`Proyectos.razor.cs`, 485 líneas), así que no es una anomalía; sí es el punto de mayor presión SRP. **Fix acotado**: extraer solo la secuencia subida→conversión→detección a un coordinador inyectable y testeable. No colapsar todos los code-behind: la consistencia es deliberada (`PROJECT.md`, principio 3).

---

## Hipótesis planteada y **refutada** (no actuar)

**Caché del modelo de EF y filtro global con servicio scoped.** Se planteó que, al cerrar `OnModelCreating` sobre el `ITenantActual` scoped mientras EF cachea el modelo globalmente (no hay `IModelCacheKeyFactory` registrado), el filtro pudiera evaluar una instancia obsoleta y filtrar por el tenant de la primera petición — lo que sería un fuga crítica.

**Refutada por el diseño de los propios tests**: `AislamientoMultiTenantTests.CrearContexto` (`:39-47`) construye para cada contexto una **instancia distinta** de `TenantActualAmbiental`, sobre el mismo archivo SQLite y en el mismo proceso (por tanto compartiendo el modelo cacheado), y afirma invisibilidad bidireccional además de re-verificar que A sigue viendo lo suyo (`:52-70`). Esa es exactamente la aserción que fallaría si se hubiera fijado una instancia obsoleta. Es coherente con la semántica de EF Core: el acceso al miembro se extrae como parámetro de consulta reevaluado en cada ejecución, y las referencias a la instancia del contexto se reenlazan al contexto en ejecución. Es el patrón de multi-tenancy documentado y soportado.

**Salvedad**: no he podido ejecutar la suite (sin SDK), así que la refutación se apoya en el diseño del test y en la semántica de EF Core, no en una ejecución observada. Los 31 tests de aislamiento corren en CI en cada push.

**Cobertura que sí falta**: ningún test cubre la cookie de C-1 (el doble `ClienteActivoSeleccionadoFalso` devuelve siempre `null`) ni la revocación de delegación de A-1.

---

# FASE 2 — Plan de refactorización priorizado

Un cambio por commit, atómico y revisable por separado. No mezclar.

### Quick wins (1-2 horas) — máximo ROI del informe

| # | Cambio | Commit |
|---|---|---|
| 1 | Filtro global de `TarifaCliente` (**A-1**) | `fix(multitenant): filtro global de tenant en TarifaCliente` |
| 2 | Filtro global de `AprobacionDocumento` (**M-1**) | `fix(multitenant): filtro global de tenant en AprobacionDocumento` |
| 3 | Firmar la cookie con `IDataProtector` (**C-1**, mitigación inmediata) | `fix(seguridad): firmar la cookie de cliente activo` |
| 4 | Corregir las 3 afirmaciones falsas de documentación (**M-9**) | `docs: alinear ARCHITECTURE/CLAUDE con el estado real` |
| 5 | Log de excepciones en los 3 catch mudos (**B-1**) | `fix(observabilidad): registrar excepciones tragadas` |
| 6 | Antiforgery / POST en cliente-activo (**M-8**) | `fix(seguridad): antiforgery en el cambio de workspace` |

### Corto plazo (1-2 días)

| # | Cambio |
|---|---|
| 7 | **C-1 definitivo**: claim firmado `tenant_efectivo` reemitido en `SignInAsync`; `ITenantActual` deja de leer cookie |
| 8 | Tests de regresión: cookie falsificada, y lectura de tarifas tras revocar delegación |
| 9 | Catálogo de roles compartido en Domain (**A-2**) |
| 10 | Interfaz marcadora `ICommand` en la puerta de escritura (**M-4**) |
| 11 | Memoizar los resolvers de `AlcanceDatosService` (**M-5a**) |
| 12 | `ProtectKeysWithCertificate` / KMS (**M-3**) |
| 13 | `GUID` aleatorio para el tenant por defecto (**C-1.3**) |

### Medio plazo (1-2 semanas)

| # | Cambio |
|---|---|
| 14 | Estado de Documento evaluable en SQL → paginación real (**A-3**) y conteos de dashboard (**M-6**) |
| 15 | Token de concurrencia optimista en los agregados editables (**M-2**) |
| 16 | `DniNormalizado` indexado (**M-7**) |
| 17 | Visibilidad como subconsulta en vez de `.Contains()` materializado (**M-5b**) |
| 18 | Extraer el coordinador de subida/conversión/IA de `Documentos.razor.cs` (**B-7**) |

### Largo plazo (arquitectónico)

| # | Cambio |
|---|---|
| 19 | Cola de jobs en proceso + consumidor; mover el pipeline de IA fuera del circuito (**A-4**) |
| 20 | Migración a PostgreSQL — condición de salida de `ADR-003` y techo real de escalabilidad |
| 21 | Retención / supresión RGPD (**M-10**) — *requiere decisión legal previa, no arrancar sin confirmación* |
| 22 | Índices compuestos liderados por `TenantId` (**B-4**), junto con la migración a PostgreSQL |

---

# INFORME EJECUTIVO

## Puntuaciones (0-10)

| Dimensión | Nota | Justificación |
|---|---:|---|
| **Estado general** | **7** | Ingeniería sólida y disciplinada, con un defecto crítico concentrado en un punto |
| **Calidad arquitectónica** | **8** | Capas verificadas y limpias, dominio rico, ADRs reales, decisiones justificadas |
| **Calidad del código** | **8** | 0 TODO, 0 catch vacíos, 0 SQL crudo, 0 paquetes muertos, convenciones consistentes |
| **Seguridad** | **3** | Toma de control de tenant + 2 tablas sin filtro; el resto del perímetro es bueno |
| **Rendimiento** | **5** | Paginación falsa en la pantalla principal e IA bloqueante en el circuito |
| **Escalabilidad** | **3** | SQLite escritor único en producción + Blazor Server de un solo nodo |
| **Mantenibilidad** | **8** | 296 tests, CI con `-warnaserror`, documentación abundante (aunque con deriva) |
| **Preparación para producción** | **3** | Suficiente para el piloto de un tenant; **no apto** para multi-cliente hoy |

**Nota sobre "seguridad 3/10"**: no refleja descuido general — el perímetro está por encima de la media (0 SQL crudo, path traversal correctamente contenido, credenciales enmascaradas en auditoría, sin SSRF, sin secretos en el repo, IDOR bien resuelto en documentos con doble control de tenant y alcance). La nota la determina que el único fallo crítico rompe justamente la propiedad que el producto vende como absoluta.

## Veredicto de puesta en producción

**Como está hoy: NO para multi-cliente.** C-1 permite a cualquier usuario autenticado tomar el control de otro tenant, incluido el tenant #1 cuyo GUID está fijado en el código. Ningún cliente real adicional debería incorporarse antes de cerrar C-1 y A-1.

**Para "miles de clientes"** (la pregunta planteada): no con esta infraestructura, y el proyecto ya lo sabe — `ADR-003` fija PostgreSQL como condición de salida. El orden de lo que rompe primero:

- **10 tenants**: funciona. Es aproximadamente el escenario actual.
- **100 tenants**: SQLite empieza a serializar escrituras (`SQLITE_BUSY`); A-4 agrava el problema porque cada subida retiene el bloqueo de escritura mientras espera al LLM. A-3 hace que la pantalla principal se degrade linealmente.
- **1.000 tenants**: inviable sin PostgreSQL y sin sacar la IA del circuito.
- **10.000 tenants**: exige además balanceo con afinidad de circuito o abandonar Blazor Server en la capa de presentación (migración localizada, como `ARCHITECTURE.md:18` anticipa correctamente).

## Top 10 por retorno de inversión

1. Filtro global en `TarifaCliente` — 1 línea, cierra fuga cruzada real.
2. Filtro global en `AprobacionDocumento` — 1 línea, cierra hueco latente.
3. Firmar la cookie de cliente activo — mitigación inmediata del único crítico.
4. Claim firmado en lugar de cookie — elimina la clase de bug completa.
5. Tests de regresión de cookie falsificada y revocación — impide la reaparición.
6. Corregir la deriva documental — evita decisiones futuras sobre premisas falsas.
7. Catálogo de roles compartido — convierte riesgo silencioso en error de compilación.
8. Log en los catch mudos — recupera diagnóstico perdido por ~10 líneas.
9. Antiforgery en cliente-activo — XS.
10. Memoizar `AlcanceDatosService` — elimina 3-5 consultas repetidas por carga de página.

Los puntos 1-3, 6, 8 y 9 caben en una sola sesión y resuelven ambos hallazgos de aislamiento cruzado más la mitigación del crítico.

## Lo que está bien y conviene no tocar

Auditar es también proteger lo que funciona:

- **La organización en capas y feature-first** está bien resuelta y verificada; no la rediseñes.
- **La repetición de handlers CRUD/selector es consistencia intencionada**, no copy-paste. Extraer un selector genérico acoplaría slices independientes y violaría el YAGNI del propio proyecto. Correctamente dejada como está.
- **Las interfaces de una sola implementación** sostienen la frontera Application↔Infrastructure y la testabilidad. La factoría de proveedores de IA tiene **tres** implementaciones reales: no es especulativa.
- **`DocumentAIRouterService` no es un God object**: sus 9 dependencias son colaboradores reales de un pipeline, con métodos de propósito único y `Result<T>` en todo el recorrido.
- **La caché de extracción IA por SHA256** está bien diseñada e indexada.
- **La ruta de importación** (`EjecutarImportacionCommand`) agrupa búsquedas en diccionarios y hace un único `SaveChangesAsync`: sin N+1, sin `SaveChanges` en bucle. Bien hecha.
- **El enmascarado de credenciales en auditoría** (`AuditoriaInterceptor.cs:22-27,86-88`) es correcto y deliberado.
- **`DiskFileStorageService.ResolverRutaSegura`** sanea ambos segmentos y verifica la carpeta del tenant: seguro frente a traversal y con defensa en profundidad.

---

## Estado de las fases

- **Fase 0 — Comprensión**: completada.
- **Fase 1 — Auditoría**: completada.
- **Fase 2 — Plan priorizado**: completado (arriba).
- **Fase 3 — Aprobación**: **pendiente de tu decisión.** No se ha modificado ni una línea de código de producción.
- **Fase 4 — Implementación**: solo lo que apruebes explícitamente, en commits atómicos separados.

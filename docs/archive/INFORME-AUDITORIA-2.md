# Auditoría técnica — Segunda ronda

Fecha: 2026-07-30 · Base: `bde5fc3` (main) · Ronda anterior: `INFORME-AUDITORIA-TECNICA.md`

**Limitación declarada (sin cambios)**: no hay SDK de .NET en el entorno de auditoría. **No he compilado ni ejecutado los tests.** El commit `cbaa38f` declara 357/357 en verde, `dotnet build -warnaserror` y `dotnet format` limpios; no lo he podido verificar. Todo lo demás se apoya en lectura directa con `archivo:línea`.

---

## Parte 1 — Verificación de los arreglos de la ronda 1

| Hallazgo | Veredicto |
|---|---|
| **C-1** secuestro de tenant por cookie | ✅ **Cerrado** |
| **A-1** `TarifaCliente` sin filtro | ✅ **Cerrado** |
| **M-1** `AprobacionDocumento` sin filtro | ✅ **Cerrado** |
| **M-8** GET que cambia estado | ⚠️ **Parcial** — endpoint correcto, un llamador quedó roto |
| **M-9** deriva documental | ✅ **Cerrado** (4 afirmaciones, una más de las 3 que detecté) |
| **B-1** excepciones tragadas | ⚠️ **Parcial** — corregidos los 3 señalados; el módulo nuevo reintroduce 6 |

**C-1** está bien resuelto y en la capa correcta. La cookie es un token de Data Protection con *purpose string* propio, caducidad de 12 h comprobada en servidor, **ligado al `usuarioId`**, y con fallo cerrado ante `CryptographicException`. Además se invirtió la precedencia: `TenantActual.cs:69-72` resuelve primero el claim firmado y solo aplica la selección **encima de una sesión ya válida**, de modo que la cookie puede cambiar el tenant de un usuario autenticado pero nunca crear contexto donde no había sesión. No queda ningún otro lector de cookie cruda: `ClienteActivoSeleccionado.cs:93` es el único `Request.Cookies[...]`.

**A-1/M-1**: censo completo — **38 entidades concretas con `TenantId`, 38 con filtro global. Cero excepciones.** `Tenant`, `DelegacionTenant` y `AsignacionOperadorDelegado` extienden `Entity` (no `EntidadConTenant`) y son catálogos de autorización global documentados; `AspNetUsers` está deliberadamente sin filtrar con justificación escrita. `AislamientoPorAgregadoTests` tiene ahora un test por entidad.

**Regresión de tests genuina**: `SecuestroTenantPorCookieTests.cs` no es teatro — ejercita las clases de producción contra un `DefaultHttpContext` real, con 6 casos (GUID crudo falsificado apuntando al tenant #1, token con bits alterados, token válido replicado en la sesión de otro usuario, token de otro llavero, sin sesión, y un contrapeso positivo que prueba que ADR-004 sigue funcionando). Fallaría al revertir el fix.

**Defecto latente encontrado por el fix, no por mí**: la migración `AddTarifasCliente` no llevaba `[DbContext]`/`[Migration]`, así que EF Core la saltaba en silencio y la tabla nunca se creaba contra una BD migrada desde cero. Mi auditoría fue estática y no ejecutó migraciones; este defecto solo aparece al correrlas.

---

## Parte 2 — Hallazgos nuevos

> **Nota de calibración, importante para priorizar.** Dos bloques de hallazgos graves son **inalcanzables en producción hoy**, porque los datos que los activan solo los crea un seeder condicionado a `DatosPrueba:Activo` (`DelegacionDemoSeeder.cs:50`, `ComunicacionesDatosPruebaSeeder.cs:72`). No los rebajo de severidad: son defectos de diseño reales que se vuelven explotables en cuanto llegue el commit que ya está anunciado en el propio código. Pero conviene saber que **no hay que apagar un fuego esta noche**, sino cerrar esto *antes* de esos commits. En staging/demo con `DatosPrueba:Activo=true` sí están vivos.

### 🔴 N-1 · XSS almacenado en el módulo Comunicaciones

**Severidad**: Crítico (latente en producción — ver nota) · **Ubicación**: `src/CaeManager.Web/Features/Comunicaciones/Pages/Bandeja.razor:176`

```razor
<div class="bandeja-mensaje-cuerpo">@((MarkupString)mensaje.CuerpoHtml)</div>
```

El valor no se sanea en ningún punto del recorrido: textarea (`Bandeja.razor:209`) → `ResponderConversacionCommand` (validador solo `NotEmpty`) → `MensajeCorreo.cs:35` lo guarda literal → vuelve por `ObtenerConversacionPorIdQuery.cs:58` → `MarkupString`. **No existe ningún sanitizador en la solución**: 0 coincidencias de `sanitiz`/`Ganss`/`HtmlSanitizer` en todo el repo, y no hay Content-Security-Policy en `Program.cs`.

Lo revelador es que el equipo **ya conocía el riesgo**: el otro único `MarkupString` de la app (`AsistenteIa.razor.cs:86`) es seguro precisamente porque pasa por Markdig con `.DisableHtml()`. El patrón no se trasladó aquí.

Una carga tipo `<img src=x onerror="...">` se ejecuta en el circuito autenticado de cualquier usuario que abra el hilo; los manejadores inline disparan aunque el marcado se inserte por DOM, así que la salvedad habitual de "`<script>` no corre vía innerHTML" no aplica.

**Por qué es latente hoy y urgente mañana**: no existe ningún comando de alta de conversación — solo las crea el seeder de datos de prueba. Sin conversaciones, el sumidero es inalcanzable. Pero la documentación del propio módulo (`ConversacionCorreo.cs:10-12`, `ResponderConversacionCommand.cs:21-27`) anuncia la ingesta por Microsoft Graph como siguiente iteración. En ese momento `CuerpoHtml` pasa a estar controlado por **cualquiera que sepa la dirección del buzón**, sin autenticación y sin frontera de tenant: XSS almacenado no autenticado contra todos los gestores CAE.

**Solución**: sanear con lista blanca en la frontera del DTO (`ObtenerConversacionPorIdQuery`), no en la vista, para cubrir cualquier UI futura; añadir CSP; renderizar correo entrante en iframe aislado si hace falta fidelidad. **Bloqueante de la ingesta Graph.**

### 🟠 N-2 · La bandeja no restringe rol y la triaje es visible para todos

**Severidad**: Alto (latente) · **Ubicación**: `Bandeja.razor`, `Macros.razor` (sin `@attribute [Authorize]`); `ObtenerConversacionesQuery.cs:52-56`

Ninguna de las dos páginas declara `@attribute [Authorize(...)]` — solo las cubre la `FallbackPolicy` global, que es `RequireAuthenticatedUser()` a secas. **13 páginas de otros módulos sí lo declaran** (verificado), así que es una omisión, no un criterio distinto.

Y por diseño explícito, toda conversación sin cliente asignado es visible para cualquier usuario autenticado del tenant:

```csharp
if (clienteIdsVisibles is not null)
    consulta = consulta.Where(c => c.ClienteId == null || clienteIdsVisibles.Contains(c.ClienteId!.Value));
```

Eso incluye el rol `Cliente` — un empleado de una empresa cliente externa. `NavMenu` le oculta el enlace, pero el propio comentario del menú (`NavMenu.razor:11-13`) dice que el menú decide qué pestañas existen, no qué filas se ven. Escribir `/comunicaciones` en la barra de direcciones funciona.

**Riesgo**: un contacto externo lee todo el correo entrante sin triar de *otras* empresas cliente. `AutorizacionEscrituraBehavior` sí le bloquea las escrituras, así que es solo lectura — por eso Alto y no Crítico.

### 🟠 N-3 · Siete comandos de Comunicaciones no comprueban el alcance de datos

**Severidad**: Alto (latente) · **Ubicación**: `AsignarClienteConversacionCommand.cs:28,32`, `AsignarEjecutivoConversacionCommand.cs:31`, `CambiarEstadoConversacionCommand.cs:25`, `ResponderConversacionCommand.cs:36`, `ObtenerMacrosQuery.cs:26`, `EditarMacroCommand.cs:29`, `EliminarMacroCommand.cs:15`

Todos cargan por Id a través del repositorio filtrado por tenant, así que **el cruce entre tenants está cerrado**. Lo que falta es el alcance *intra*-tenant: el lado de lectura del mismo agregado sí lo comprueba (`ObtenerConversacionPorIdQuery.cs:52`), el de escritura no.

Es exactamente la clase de fallo que `AlcanceDatosServiceExtensions.cs:9-12` documenta haber cerrado en el Issue #18 — reintroducida en las escrituras. Un `GestorCae` con el Guid de una conversación de un cliente que ya no está en su cartera (la reasignación de cartera es un flujo de primera clase, existe `ReasignarEjecutivoClienteCommand`) puede responder en el hilo de otro gestor, cerrarlo o reasignarlo.

### 🟠 N-4 · ADR-004: la delegación es *create-only*, y sus comandos son inalcanzables

**Severidad**: Alto (funcional/gobernanza, latente) · **Ubicación**: `CaeManager.Application/Tenants/Commands/`, `IDelegacionTenantRepository.cs`, `DelegacionTenant.cs:49-51`

- Solo existen dos comandos, `CrearDelegacionTenant` y `CrearAsignacionOperadorDelegado`, y **ninguno se despacha desde ningún sitio**: ni UI, ni endpoint, ni test. Son handlers huérfanos.
- Los repositorios exponen únicamente `ObtenerPorIdAsync`, `Existe*Async` y `Agregar`. **No hay borrado ni desactivación.**
- `DelegacionTenant.Desactivar()` y `Reactivar()` no los llama nadie: código muerto.
- No hay pantalla de administración de delegaciones.

En la práctica, las delegaciones solo existen porque las siembra el seeder de demo, y **no se pueden revocar por ningún camino del producto**.

`CLAUDE.md` (reescrito en el fix) afirma ahora que ADR-004 está "**implementado**" y cita "sus repositorios, configuraciones, migración, **Commands**". Los Commands existen como código pero son inalcanzables: la afirmación normativa sobrestima el estado real.

**Por qué importa**: el titular de ADR-004 es un modelo de delegación **reversible**, y es la condición declarada para el primer cliente real. Un Cliente Delegante no puede retirar el acceso de la consultora a sus datos — justo lo que un responsable del tratamiento debe poder garantizar (y lo que el DPA de `ADR-003` promete).

### 🟠 N-5 · El rol de la delegación se guarda pero nunca se aplica

**Severidad**: Alto (latente) · **Ubicación**: `AsignacionOperadorDelegado.cs:28`; `AutorizacionEscrituraBehavior.cs:32`

`AsignacionOperadorDelegado.Rol` se valida, se persiste (`HasMaxLength(50)`) y se asigna en el constructor — y **no se lee jamás para decidir nada**. Verificado: todas sus apariciones son de escritura.

La puerta de escritura decide con `ObtenerRolActualAsync()`, es decir, el rol del claim del **tenant de origen**. ADR-004 § 5.3 promete explícitamente roles por delegación ("GestorCae en un cliente, Consulta en otro"): hoy un operador asignado como `Consulta` sobre el tenant B, pero `Administrador` en el suyo, **escribe en B**.

### 🟡 N-6 · El token de workspace sobrevive a la revocación, y el logout no lo borra

**Severidad**: Medio (latente) · **Ubicación**: `ClienteActivoSeleccionado.cs:62`; `ClienteActivoEndpoints.cs:42-47`; `IdentityEndpointsExtensions.cs:16-20`

La delegación se comprueba **una sola vez, al emitir** el token; la lectura no hace I/O por diseño (decisión correcta: se evalúa dentro de `HasQueryFilter`). Consecuencia: tras una revocación, un token vivo sigue concediendo lectura y escritura sobre el ex-cliente hasta 12 h. Asimetría reveladora: `ObtenerClientesAutorizadosQuery.cs:46` **sí** revalida en cada render, así que el operador revocado desaparece del selector mientras conserva el acceso real.

Y `/cuenta/cerrar-sesion` llama a `SignOutAsync()` pero **no borra `cae_cliente_activo`**, así que al volver a entrar se reanuda el workspace revocado.

Hoy es inalcanzable porque **la revocación no existe** (N-4). Se convierte en real el mismo día que se implemente. **Solución**: acortar `Vigencia` a minutos o revalidar la delegación una vez por petición fuera del camino caliente del filtro; y borrar la cookie en el cierre de sesión.

### 🟡 N-7 · Regresión: "Ver como este cliente" en Visión de Cartera lleva a un 404

**Severidad**: Medio (funcional, **viva en producción**) · **Ubicación**: `VisionCartera.razor.cs:37`

```csharp
NavigationManager.NavigateTo($"/cuenta/cliente-activo/{tenantId}?returnUrl={returnUrl}", forceLoad: true);
```

La ruta `/cuenta/cliente-activo/{tenantId}` **ya no existe**: el endpoint pasó a `MapPost("/cuenta/cliente-activo")` sin parámetro de ruta, y no hay ningún `MapGet` ni fallback que la recoja (verificado en `Program.cs` y en todo el código). Cada fila de la tabla de riesgo (`VisionCartera.razor:65`) hace un GET a esa URL muerta y aterriza en `/not-found`.

Introducida por el propio fix de M-8: se actualizó `SelectorClienteActivo` pero no este segundo llamador. Ni el informe anterior ni `FIX-LOG.md` mencionan `VisionCartera`, y ningún E2E cubre la ruta. **Solución**: `<form method="post">` con token antiforgery, igual que el selector.

### 🟡 N-8 · La bandeja no pagina, lee todos los cuerpos y se relanza en cada tecla

**Severidad**: Medio (latente) · **Ubicación**: `ObtenerConversacionesQuery.cs:83-112`; `Bandeja.razor:54`; `CampoTexto.razor:13`

No es N+1 (son 3 viajes, bien agrupados con `Contains`). El problema es otro:

1. **No hay `Skip`/`Take` en ningún sitio.** Todas las demás listas del código paginan.
2. Se trae el `CuerpoHtml` **completo de todos los mensajes** solo para calcular una vista previa de 140 caracteres y un contador. Los cuerpos de correo son la columna más grande del esquema.
3. `CampoTexto` usa `@oninput` y `Bandeja.razor:54` conecta `ValorChanged` directo a `AplicarFiltrosAsync()`: **el buzón entero se rematerializa en cada pulsación** de la caja de búsqueda, sobre un circuito Blazor que retiene el resultado en memoria de servidor por usuario conectado.

### 🟢 Bajos

- **N-9** · Seis `catch (Exception)` sin log en `Bandeja.razor.cs:98,171,214` y `Macros.razor.cs:52,135,174` — el mismo patrón que `cbaa38f` acababa de corregir en otros tres sitios. Además quedó sin corregir `VisionCartera.razor.cs:28`.
- **N-10** · `AsignarEjecutivoConversacionCommand.cs:19-25` justifica no validar el Guid del ejecutivo con "Web ya valida que viene de un selector". Un selector de cliente no es una frontera de autorización: el servidor no revalida y se puede escribir cualquier Guid en `EjecutivoAsignadoId`.
- **N-11** · `CuerpoHtml` recibe texto plano desde un `textarea` y se renderiza como HTML: los saltos de línea se pierden y un `<` o `&` legítimo corrompe el render. Defecto de corrección, independiente del XSS.
- **N-12** · `Bandeja.razor.cs` (313 líneas) mezcla tres pantallas con 12 campos de estado entrelazados; `FormatearFechaRelativa` y `TonoBadgeDeEstado` están duplicados literalmente con `FilaConversacion.razor:26-43`. Además inyecta `UserManager<ApplicationUser>` directo en la página (`:22`), saltándose MediatR y alcanzando `Infrastructure.Identity` desde la vista.
- **N-13** · `EliminarMacroCommand` es el único comando del módulo sin validador; `Macros.razor.cs:161` atribuye el borrado a `Guid.Empty` si no resuelve el usuario en vez de fallar.
- **N-14** · `TenantSeedData.IdPorDefecto` sigue siendo `…0001` (deferido conscientemente en `FIX-LOG.md:99`). Con C-1 cerrado ya no es directamente explotable.
- **N-15** · `TenantSelladoInterceptor` solo sobrescribe `SavingChangesAsync`, no `SavingChanges`. Inocuo hoy (no hay ningún `SaveChanges()` síncrono), pero un guardado síncrono futuro saltaría el sellado por completo.
- **N-16** · `AmbitoTenantExplicito` es un `public static AsyncLocal` que **precede a todos los controles de aislamiento**. Sin abuso hoy (su único uso de cara al usuario, `ObtenerKpisGlobalesQuery.cs:49`, itera sobre la lista autorizada en BD), pero cualquier handler futuro que derive el bucle de entrada de usuario es un bypass total e instantáneo. Merece una barrera arquitectónica.

### Limpio (verificado, no reinvestigar)

- **Aislamiento entre tenants del módulo nuevo**: intacto. Las 4 entidades nuevas tienen filtro global, `TenantId` no nulo en la migración, sellado por interceptor y test de aislamiento propio. Cero SQL crudo, cero `IgnoreQueryFilters`, cero `ExecuteUpdate`/`ExecuteDelete`.
- **Sin adjuntos, sin rutas de archivo, sin `HttpClient`, sin fetch remoto** en Comunicaciones: path traversal y SSRF no aplican **todavía** (sí lo harán con la ingesta Graph).
- **Renombrado `PlataformaAcceso` → `CanalGestionDocumental`**: sin referencias colgando. El *purpose string* del protector se dejó intacto a propósito — renombrarlo habría dejado indescifrables las credenciales ya guardadas. Buen criterio.
- **Enmascarado de credenciales en auditoría**: sobrevivió al renombrado y es compile-safe (`typeof`/`nameof`), cubriendo los tres tipos.
- **`CanalGestionDocumental`**: filtro global, índice único `(TenantId, CentroId)`, `CentroVisibleAsync` comprobado, y proyecta `TieneCredenciales` en vez de los secretos. Nada que corregir.
- **Higiene**: siguen en 0 real los TODO/HACK/FIXME, el SQL crudo y los `IgnoreQueryFilters()` efectivos.

---

## Parte 3 — Puntuación revisada

| Dimensión | Ronda 1 | Ahora | Comentario |
|---|:--:|:--:|---|
| Estado general | 7 | **7** | Se cierra lo crítico anterior, entra deuda nueva del módulo sin revisar |
| Calidad arquitectónica | 8 | **8** | Aislamiento ejemplar; el módulo nuevo se salta el patrón de alcance de datos |
| Calidad del código | 8 | **7** | Reintroducidos 6 catch mudos, duplicación y una fuga de capa en la vista |
| Seguridad | 3 | **6** | C-1/A-1/M-1 cerrados de verdad; baja por N-1/N-2/N-3, mitigado por ser latentes |
| Rendimiento | 5 | **5** | Sin cambios; la bandeja añade un patrón peor pero aún sin datos |
| Escalabilidad | 3 | **3** | Sin cambios: SQLite y circuito único siguen siendo el techo |
| Mantenibilidad | 8 | **8** | +28 tests y un guardarraíl de migraciones; compensan la deuda nueva |
| Preparación producción | 3 | **5** | Ya no hay toma de control de tenant; quedan bloqueantes de ADR-004 y del módulo |

**Lo más importante de esta ronda**: el trabajo de corrección fue serio y bien ejecutado —no cosmético—, y además encontró un defecto real que yo no podía ver estáticamente. Pero se fusionó a `main` un módulo nuevo (Fase 59) que **nunca pasó por auditoría** y que reintroduce clases de fallo ya resueltas en el resto del código (alcance de datos, catch mudos, autorización por página). El proceso, no el código, es el hallazgo de fondo: el módulo entró sin la revisión de seguridad que el propio repositorio ya sabe aplicar.

## Orden recomendado

**Antes de nada (rompe algo hoy)**
1. **N-7** — arreglar `VisionCartera` (form POST). Es la única regresión viva en producción. XS.

**Antes del commit de ingesta Graph (bloqueantes duros)**
2. **N-1** — sanear `CuerpoHtml` en la frontera del DTO + CSP.
3. **N-2** — `[Authorize(Roles=…)]` en ambas páginas + acotar la visibilidad de la triaje por rol.
4. **N-3** — `ClienteVisibleAsync` en los siete handlers sin alcance.

**Antes de habilitar delegación para el primer cliente real**
5. **N-4** — comandos de revocación + UI de administración (o retirar la afirmación de `CLAUDE.md`).
6. **N-5** — aplicar `AsignacionOperadorDelegado.Rol` en la puerta de escritura.
7. **N-6** — acortar vigencia del token y borrar la cookie al cerrar sesión.

**Deuda ordinaria**
8. N-8 (paginar la bandeja), N-9 a N-16, y lo que sigue abierto de la ronda 1: A-3, A-4, M-2, M-3, M-5, M-10.

**Sigue abierto de la ronda 1** (medio/largo plazo, sin cambios): paginación en memoria de Documentos, IA en el circuito (`BackupHostedService` sigue siendo el único `BackgroundService`), cero concurrencia optimista, claves de Data Protection sin cifrar, memoización de `AlcanceDatosService`, retención/supresión RGPD.

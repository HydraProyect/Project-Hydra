# FIX-LOG — hallazgos de aislamiento multi-tenant

Fecha: 2026-07-30 · Rama: `claude/repository-technical-audit-00jdko` · Base: `36ad957`

Cierra los tres hallazgos de aislamiento entre tenants de `INFORME-AUDITORIA-TECNICA.md`:

| Hallazgo | Severidad | Estado |
|---|---|---|
| **C-1** — toma de control de tenant vía cookie `cae_cliente_activo` | 🔴 Crítico | ✅ cerrado (§ 1-7) |
| **A-1** — `TarifaCliente` sin filtro global | 🟠 Alto | ✅ cerrado (§ 8) |
| **M-1** — `AprobacionDocumento` sin filtro global | 🟡 Medio | ✅ cerrado (§ 8) |
| **M-8** — cambio de workspace por GET sin antiforgery | 🟡 Medio | ✅ cerrado (§ 15) |
| **M-9** — deriva documental | 🟡 Medio | ✅ cerrado (§ 16) |
| **B-1** — excepciones tragadas sin log | 🟢 Bajo | ✅ cerrado (§ 17) |

Con esto queda completo el tramo **Quick wins** del plan de la Fase 2 (los 6 puntos), más el #7 y el #8 del tramo "Corto plazo".

Además se corrigieron dos defectos **no recogidos en el informe**, encontrados al trabajar: la migración `AddTarifasCliente` nunca se aplicaba (§ 9) y un fallo de teardown en los E2E (§ 14).

Siguen abiertos A-2, A-3, A-4, M-2 a M-7, M-10 y B-2 a B-7.

---

## 1. Estado de partida: la suite no estaba verde en este entorno

El informe declaraba no haber podido ejecutar los tests (sin SDK). Ejecutados ahora, `dotnet test` daba **71 fallos sobre 337 tests**. Ninguno era un fallo funcional:

| Causa | Fallos | Naturaleza |
|---|---:|---|
| `IOException` al borrar el `.db` en `DisposeAsync` | 63 | Teardown, solo Windows |
| `PlaywrightException: Executable doesn't exist` | 8 | Navegadores no instalados |
| Aserciones de producto | **0** | — |

Los 63 fallaban **después** de que el cuerpo del test hubiera pasado: `Microsoft.Data.Sqlite` devuelve la conexión a su pool al disponer el `DbContext`, y ese handle abierto impide `File.Delete` en Windows (en el CI de Linux no se manifiesta, por eso estaba latente). Se corrigió con `SqliteConnection.ClearAllPools()` antes del borrado en las 9 fixtures afectadas.

Los 8 de Playwright eran navegadores no instalados. CI ya los instala en un paso explícito (`ci.yml`, "Instalar navegadores de Playwright"), así que allí nunca fallaron; el hueco era solo la máquina de un desarrollador recién clonada. Cerrado en § 14.

> **Corrección al informe**: la suite previa no eran 296 tests sino **337** (153 dominio + 76 aplicación + 13 web + 87 integración + 8 E2E). El total tras este trabajo es **343**.

## 2. El fallo, reproducido

Antes de tocar producción se escribió el test de ataque. Falla contra el código original con exactamente el síntoma descrito en C-1:

```
Una_cookie_fabricada_a_mano_no_secuestra_el_tenant_victima [FAIL]
  Expected tenantActual.TenantId to be {22222222-2222-2222-2222-222222222222},
  but found {00000000-0000-0000-0000-000000000001}.
```

Un usuario autenticado del tenant `2222…`, con solo añadir a mano `cae_cliente_activo=00000000-0000-0000-0000-000000000001` sobre su propia sesión legítima, hacía que `ITenantActual` resolviera el **tenant #1**. Como ese valor es la única fuente del filtro global de EF Core, del `TenantSelladoInterceptor` y del particionado de `DiskFileStorageService`, el aislamiento cedía entero: lectura, escritura y archivos.

El Id del objetivo no hay que averiguarlo — `TenantSeedData.IdPorDefecto` lo fija de forma determinista y pública en el código.

## 3. Cambios en producción

### `src/CaeManager.Web/Services/ClienteActivoSeleccionado.cs`

La cookie deja de ser un GUID en claro y pasa a ser un **token protegido con Data Protection**, con tres garantías:

1. **Firmado y cifrado** con un *purpose string* propio (`CaeManager.Web.ClienteActivoSeleccionado.v1`), aislado criptográficamente de los demás protectores del sistema. Un valor que no emitió este sistema no descifra.
2. **Ligado al usuario**: la carga útil es `{usuarioId}|{tenantId}` y al leer se compara contra el `NameIdentifier` de la sesión en curso. Un token legítimo copiado a la sesión de otro no le transfiere el workspace.
3. **Con caducidad en servidor** (`ITimeLimitedDataProtector`, 12 h). El `MaxAge` de la cookie es una indicación al navegador que un atacante ignora reenviando el valor; esta la comprueba el servidor al descifrar.

Cualquier fallo de descifrado se trata como "sin selección" (`CryptographicException` → `null`), no como error: es la respuesta correcta ante un valor no válido. **Se preserva la propiedad que exigía el diseño original**: la lectura sigue siendo síncrona y sin I/O, porque `ITenantActual.TenantId` se evalúa dentro de `HasQueryFilter` y una consulta a base de datos ahí sería una regresión de rendimiento severa.

Se añade `Proteger(...)`, único emisor de tokens, para uso exclusivo del endpoint.

### `src/CaeManager.Web/Services/TenantActual.cs`

Se invierte el orden de resolución. Antes la cookie **precedía** al claim firmado:

```csharp
if (AmbitoTenantExplicito.TenantIdActual is { } t) return t;
if (clienteActivoSeleccionado.TenantIdSeleccionado is { } t) return t;  // ← cookie ganaba
/* solo entonces */ claim tenant_id
```

Ahora el claim firmado es la base y la selección solo se aplica **encima** de él:

```csharp
if (AmbitoTenantExplicito.TenantIdActual is { } t) return t;
/* resolver claim firmado */
if (_tenantId is null) return null;                                     // fallo cerrado
return clienteActivoSeleccionado.TenantIdSeleccionado ?? _tenantId;
```

La consecuencia de seguridad es la que interesa: la selección puede **cambiar** el tenant de un usuario ya autenticado —que es lo que ADR-004 necesita— pero nunca **crear** un contexto de tenant donde no había ninguno. Una petición sin sesión válida resuelve a `null` por mucha cookie que traiga.

### `src/CaeManager.Web/Features/Tenants/ClienteActivoEndpoints.cs`

Escribe el token protegido en vez del GUID. La validación de la delegación contra `DelegacionTenant`/`AsignacionOperadorDelegado` no cambia — lo que cambia es que ahora ese control **sirve de algo al leer**: el sellado criptográfico es lo que hace que la comprobación hecha al escribir siga vigente después. Antes era trivialmente esquivable escribiendo la cookie a mano.

## 4. Sobre las dos opciones del informe

El informe proponía (1) firmar la cookie como mitigación corta y (2) reemitir el ticket de autenticación con un claim `tenant_efectivo` como defensa en profundidad. Lo implementado es (1) **endurecido con el vínculo al usuario y la caducidad en servidor**, más la inversión de precedencia de (2).

No se implementó la reemisión del ticket vía `SignInAsync`: es un cambio de mayor alcance sobre el ciclo de vida de la sesión, y con el token ligado al usuario y la precedencia invertida la superficie explotable ya queda cerrada. Queda como mejora pendiente, no como riesgo abierto.

**Sigue pendiente C-1.3** (GUID aleatorio para el tenant por defecto en vez de `…0001`). Reduce la trivialidad del objetivo pero no era la vulnerabilidad; se deja fuera por afectar a la siembra y al backfill, que son un cambio independiente.

## 5. Tests nuevos

`tests/CaeManager.Web.Tests/SecuestroTenantPorCookieTests.cs` — 6 tests que recorren la ruta real de producción (el `ClienteActivoSeleccionado` de verdad sobre un `HttpContext` de verdad), no el doble que devuelve siempre `null` y que dejaba esta ruta sin cubrir:

| Test | Qué fija |
|---|---|
| `Una_cookie_fabricada_a_mano_no_secuestra_el_tenant_victima` | El ataque de C-1. **Es el que fallaba antes del fix.** |
| `Una_cookie_manipulada_tras_haber_sido_emitida_se_descarta` | Alterar un token válido lo invalida entero |
| `Un_token_legitimo_de_otro_usuario_no_es_reutilizable` | El vínculo al usuario |
| `Un_token_emitido_con_otro_llavero_no_vale_en_este` | Fabricar tokens propios no sirve sin las claves |
| `Sin_sesion_autenticada_la_cookie_no_abre_ningun_tenant` | Fallo cerrado |
| `Un_token_emitido_por_el_propio_sistema_si_selecciona_el_workspace` | Contrapeso: **ADR-004 sigue funcionando** |

El último es deliberado: sin él, el fix pasaría igual rompiendo por completo el Delegated Workspace.

## 6. Verificación de C-1

Ver § 10 para el recuento final tras A-1/M-1.

**Falta la verificación end-to-end en navegador** del cambio de Delegated Workspace, que `CLAUDE.md` exige antes de cerrar una tarea de producto. Los tests cubren la lógica de emisión y lectura del token, pero no el viaje real cookie→reload→circuito nuevo. Conviene hacerla antes de dar C-1 por cerrado.

## 7. Nota de despliegue

El formato de la cookie cambia: los valores en circulación dejan de descifrar y se descartan en silencio. El efecto para un operador delegado con sesión abierta es volver a su tenant de origen y tener que reelegir el workspace. No hay pérdida de datos ni hace falta migración.

Depende del llavero de Data Protection, que hoy se persiste en disco sin cifrar (**M-3**, sigue abierto): si ese directorio no es un volumen persistente, cada redespliegue invalidará los tokens en circulación — mismo efecto acotado de arriba.

---

# A-1 y M-1 — los dos filtros globales que faltaban

## 8. Los cambios

`CaeManagerDbContext.OnModelCreating` tenía filtro global en 32 de las 34 entidades que heredan de `EntidadConTenant`/`EntidadBase`. Faltaban exactamente dos:

```csharp
builder.Entity<TarifaCliente>().HasQueryFilter(e => !e.EstaEliminado && e.TenantId == tenantActual.TenantId);
builder.Entity<AprobacionDocumento>().HasQueryFilter(e => e.TenantId == tenantActual.TenantId);
```

`TarifaCliente` hereda de `EntidadBase`, así que le corresponden ambas condiciones (tenant y soft delete); `AprobacionDocumento` hereda de `EntidadConTenant`, solo la de tenant.

**Verificado que eran las dos únicas**: censo de las 34 clases que heredan de una u otra, contrastado con las 34 líneas de filtro. La invariante de `docs/MULTITENANCY.md` ("ninguna tabla sin filtro global") ahora se cumple sin excepciones. Se corrigieron de paso los recuentos del comentario, que decían 9 + 16 cuando eran 12 + 20 (ahora 13 + 21).

**A-1 (`ObtenerTarifasClienteQuery`)** llevaba además la segunda mitad del arreglo que pedía el informe: cargar antes el `Cliente` (que sí está filtrado) en vez de fiarse solo del `ClienteId` recibido, igual que hace `ObtenerResumenFacturacion`. Son dos capas independientes y cada una cierra la fuga por su cuenta.

**M-1 (`AprobacionDocumento`)** no fugaba todavía: su único lector hace join contra `Documentos`, que sí estaba filtrado. Era un hueco latente, y así se ha tratado.

## 9. Defecto encontrado de paso: `AddTarifasCliente` no se aplicaba nunca

Al escribir el test de A-1 falló con `SQLite Error 1: 'no such table: TarifasCliente'`.

Causa: `20260726120000_AddTarifasCliente.cs` no tiene `.Designer.cs` (es una migración escrita a mano, no generada) **y tampoco llevaba los atributos `[DbContext]` / `[Migration]` inline**. Sin ellos EF Core no descubre la clase como migración y la salta en silencio — `dotnet ef migrations list` la omitía, confirmando el diagnóstico.

Consecuencia: en cualquier base de datos migrada desde cero, la tabla `TarifasCliente` **no existe**, y el módulo de Facturación falla al leer o escribir tarifas.

Arreglado añadiendo los dos atributos, exactamente como ya los lleva `20260730120000_AddProyectos` (la otra migración escrita a mano, que sí los tenía — de ahí el patrón).

Esto matiza la severidad real de A-1: la fuga de tarifas descrita en el informe requería que la tabla existiera, y como shipped no existía. Los dos defectos son reales y ambos quedan cerrados.

Cubierto ahora por dos tests (§ 11).

## 10. Tests de A-1 y M-1 (5)

| Test | Fichero |
|---|---|
| `Aislamiento_TarifaCliente` | `Tenants/AislamientoPorAgregadoTests.cs` |
| `Aislamiento_AprobacionDocumento` | `Tenants/AislamientoPorAgregadoTests.cs` |
| `Un_tenant_ajeno_no_lee_las_tarifas_aunque_conozca_el_ClienteId` | `TarifasClienteAislamientoTests.cs` |
| `El_tenant_propietario_sigue_leyendo_sus_tarifas` | `TarifasClienteAislamientoTests.cs` |
| `Una_tarifa_borrada_logicamente_deja_de_devolverse` | `TarifasClienteAislamientoTests.cs` |

**Comprobado que detectan el defecto**: quitando temporalmente cada filtro, fallan `Aislamiento_TarifaCliente`, `Una_tarifa_borrada_logicamente_deja_de_devolverse` y `Aislamiento_AprobacionDocumento`, con el mensaje esperado. El test cruzado del handler se reforzó tras esa comprobación: pasaba incluso sin el filtro, porque la validación de `Cliente` ya cerraba esa ruta sola; ahora afirma también sobre el `DbSet` directo, para que ninguna de las dos capas pueda romperse amparada por la otra.

---

# Cierre de las dos brechas de cobertura

## 11. Guard de descubrimiento de migraciones

Dos tests nuevos en `MigracionesTests.cs`, para que el defecto de § 9 no pueda repetirse:

| Test | Qué fija |
|---|---|
| `EF_descubre_todas_las_clases_Migration_del_ensamblado` | Compara por reflexión las clases que heredan de `Migration` con las que devuelve `IMigrationsAssembly.Migrations`. Cualquier migración manual a la que le falten `[DbContext]`/`[Migration]` sale a la luz. |
| `Las_tablas_de_las_migraciones_manuales_existen_tras_migrar` | Comprobación de resultado, no de metadatos: tras `MigrateAsync`, las tablas `TarifasCliente` y `Proyectos` existen. |

**Comprobado que detectan el defecto**: quitando otra vez los atributos de `AddTarifasCliente`, fallan los dos — el primero con "expected 24 items but found 23" listando las descubiertas, el segundo con la tabla ausente del `sqlite_master`.

## 12. Cobertura de aislamiento completa: 34/34

`AislamientoPorAgregadoTests` cubría 27 de las 34 entidades. Añadidos los 7 que faltaban:

`Proyecto` · `Evaluacion` · `Incidencia` · `ExtraccionIaCache` · `AuditoriaExtraccionIa` · `RevisionIaDocumento` · `ProyectoTecnico`

Todas tenían ya su filtro global; lo que faltaba era el test que lo fije. Ahora hay **una línea de `HasQueryFilter` y un test de aislamiento por cada una de las 34 entidades**, sin excepciones. Actualizado el docstring del fichero, que seguía diciendo "25 tipos", con una nota de por qué se quedó atrás.

## 13. Verificación final

```
Domain.Tests         153/153  ✅
Application.Tests     76/76   ✅
Web.Tests             19/19   ✅   (13 previos + 6 de C-1)
IntegrationTests     101/101  ✅   (87 previos + 5 de A-1/M-1 + 2 de migraciones + 7 de aislamiento)
E2ETests               8/8    ✅
                     ─────────
TOTAL                357/357  ✅
```

`dotnet build -warnaserror` → 0 errores, 0 warnings. `dotnet format --verify-no-changes` → limpio.

Partiendo de 337 tests (71 de ellos rojos por entorno), quedan **357 en verde**: 20 nuevos, todos verificados contra el defecto que fijan.

**Sigue pendiente** la verificación end-to-end en navegador del cambio de Delegated Workspace (§ 6).

---

# 14. Que los rojos de entorno no vuelvan

Los 63 de SQLite quedaron cerrados de raíz en § 1 (el `ClearAllPools` está en el código). Faltaba el otro grupo.

## Auto-instalación de los navegadores de Playwright

`WebAppFixture` detecta ahora el `PlaywrightException` de "Executable doesn't exist", instala Chromium en proceso (`Microsoft.Playwright.Program.Main(["install", "chromium"])`) y reintenta el lanzamiento una vez. Un `dotnet test` en una máquina recién clonada pasa sin que nadie tenga que leer nada; la primera ejecución tarda lo que tarde la descarga.

Detalles que importan:

- **Sin `--with-deps`** a propósito: eso necesita `sudo` en Linux, y en CI ya se hace en su propio paso.
- **No depende de `pwsh`**: usa el instalador en proceso, no el `playwright.ps1` del output.
- **Serializado y una sola vez por proceso** (`Lock` + bandera): `WebAppFixture` y `WebAppFixtureConSegundoTenant` se inicializan en paralelo y las dos fallan al lanzar antes de que ninguna llegue a instalar. Sin el guard, la segunda relanzaba el instalador de forma redundante — observado en la primera prueba, corregido.
- Si la instalación automática falla (proxy corporativo, red cortada), el mensaje dice el comando manual exacto en vez de morir con el error críptico de Playwright.

## Defecto de teardown encontrado al probarlo

Con la descarga de por medio cambiaron los tiempos y afloró un fallo latente en `WebAppFixture.DisposeAsync`: borraba el SQLite temporal justo después de matar el proceso hijo de la app, y Windows aún no había soltado el handle → `IOException` → `[Test Collection Cleanup Failure]` con los 8 tests ya pasados. Es la misma clase de defecto que los 63 y el mismo criterio: **la limpieza de un temporal no puede tumbar una suite que ha pasado**. Ahora reintenta 10 veces con 100 ms y, si no lo consigue, deja el fichero en la carpeta de temporales sin fallar.

Es un fallo real y preexistente, dependiente de la velocidad de la máquina — no introducido por la auto-instalación, solo destapado por ella.

## Verificación

Prueba de máquina limpia de verdad: renombrados **los dos** builds de Chromium (`chromium-1228` y `chromium_headless_shell-1228`) fuera de la caché de Playwright, y ejecutados los E2E.

```
Navegadores de Playwright no encontrados. Descargando chromium (~300 MB, solo la primera vez)...
Chrome for Testing ... downloaded to ...\ms-playwright\chromium-1228
Chrome Headless Shell ... downloaded to ...\ms-playwright\chromium_headless_shell-1228

Superado: 8, Total: 8  ✅
```

Suite completa después: **357/357 ✅**, `dotnet build -warnaserror` sin warnings, `dotnet format --verify-no-changes` limpio.

> Aviso para el primer `dotnet test` de cualquiera: descarga ~300 MB sin preguntar. Es el coste acordado de esta opción frente a documentarlo y que cada uno lo instale.

---

# Cierre del tramo Quick wins

## 15. M-8 — antiforgery en el cambio de workspace

`GET /cuenta/cliente-activo/{tenantId}` escribía cookie y redirigía. La cookie de Identity es `SameSite=Lax`, así que una subpetición no la lleva, pero **una navegación de nivel superior sí**: bastaba con que alguien hiciera seguir un enlace a un operador delegado para cambiarle el workspace activo sin que lo notara. No hay elevación (el endpoint valida la delegación de verdad y usa `LocalRedirect`), pero sí el riesgo de acabar metiendo datos en el cliente equivocado.

Ahora es `POST /cuenta/cliente-activo` con parámetros `[FromForm]`. Al aceptar formulario, `UseAntiforgery` —ya activo— valida el token automáticamente.

En la interfaz, `SelectorClienteActivo` pasa de `@onchange` + `NavigationManager.NavigateTo(..., forceLoad: true)` a un `<form method="post">` con el token antiforgery en un campo oculto y `onchange="this.form.submit()"` en el `<select>`. Se usa un `onchange` inline en vez de interop de JS porque no hay CSP que lo impida y evita arrastrar un fichero `.js` para un único submit; se añade un `<noscript>` con botón para que el selector siga siendo utilizable sin JS.

**Efecto secundario que merece la pena**: el selector ya no necesita circuito interactivo — el envío lo hace el navegador, no SignalR. Eso elimina exactamente la intermitencia que había obligado al helper E2E `CambiarClienteActivoAsync` a **saltarse la interfaz** y navegar a mano al endpoint (su comentario lo documentaba: "a veces el evento nunca llega a dispararse desde Playwright"). El helper usa ahora `SelectOptionAsync` sobre el `<select>` real, que es lo que hace el usuario.

## 16. M-9 — deriva documental (eran 4, no 3)

Verificada cada afirmación contra el código antes de tocar nada. Las tres del informe se confirmaron, y apareció **una cuarta que el informe no detectó**:

| Documento | Decía | Realidad |
|---|---|---|
| `ARCHITECTURE.md` § Multi-tenancy | "columna, filtros e interceptor **todavía no existen en el código**" | Existen: 34 filtros + interceptor + 34 tests de aislamiento |
| `CLAUDE.md` | ADR-004 "pendiente, no implementado" | Implementado extremo a extremo (22 ficheros: dominio, repos, migración, Commands, query, endpoint, UI) |
| `ARCHITECTURE.md:114` | queries usan `.AsNoTracking().Select(...)` | `AsNoTracking` no aparece en ningún fichero de código |
| **`ARCHITECTURE.md:127`** | "Roles semilla: `Administrador`, `Supervisor`, `EjecutivoCae`, `Consulta` — alineados 1:1 con los cuatro dashboards" | **Seis** roles, y dos con otro nombre: `Administrador`, `DireccionCae`, `CoordinadorCae`, `GestorCae`, `Consulta`, `Cliente` (`Roles.cs`; `MigracionesTests` ya afirmaba 6) |

La cuarta es la más engañosa de las cuatro: nombra roles que ya no existen (`Supervisor`, `EjecutivoCae`) como si fueran la fuente de verdad, justo el tipo de dato del que dependería alguien al escribir una comprobación de seguridad por rol — que es el riesgo que describe A-2. Corregida apuntando a `Roles.cs` como fuente de verdad y dejando constancia del renombrado.

En la corrección de `AsNoTracking` no basta con borrar la promesa: se explica **por qué** no se usa (proyectar a DTO no engancha nada al change tracker) y dónde sí haría falta, para que nadie "arregle" la ausencia añadiéndolo en masa.

## 17. B-1 — excepciones tragadas sin log

Tres `catch` de los 132 del repositorio descartaban la excepción tras traducirla a `Result`/estado de error. El microcopy que ve el usuario es deliberadamente no técnico, así que sin el log **no queda ningún rastro** de la causa:

| Fichero | Qué se perdía |
|---|---|
| `Documentos.razor.cs` | Por qué falló la carga de la rejilla más usada del producto |
| `PdfSharpClasificadorDocumentoService.cs` | Por qué un PDF concreto no se pudo clasificar (cifrado, corrupto, formato raro) |
| `PdfSharpExtractorTextoDigitalService.cs` | Ídem para la extracción de texto |

Los dos servicios de Infrastructure no tenían logger: se les añadió por constructor primario (`ILogger<T>`), resuelto por DI sin tocar el registro. Eso obligó a actualizar dos call sites en tests, que ahora pasan `NullLogger<T>.Instance`.

## 18. Verificación

```
Domain.Tests         153/153  ✅
Application.Tests     76/76   ✅
Web.Tests             19/19   ✅
IntegrationTests     101/101  ✅
E2ETests               8/8    ✅
                     ─────────
TOTAL                357/357  ✅
```

`dotnet build -warnaserror` → 0 errores, 0 warnings. `dotnet format --verify-no-changes` → limpio.

## 19. La verificación en navegador de C-1 ya no está pendiente

`AlcanceRolesTests.Administrador_ve_los_200_clientes_sembrados` recorre, con Chromium real contra la app real: iniciar sesión → **cambiar de Delegated Workspace desde el `<select>` de la interfaz** → navegar a `/clientes` → comprobar que se ven los ~200 clientes que viven en el tenant delegado.

Eso ejercita de extremo a extremo justo lo que quedaba por confirmar de C-1: que el token protegido se emite, sobrevive al reload y se lee de vuelta para resolver el tenant delegado. Y desde § 15 pasa además por el `<select>` real y por el POST con antiforgery, no por una navegación fabricada por el test. Pasa en verde.

**Matiz honesto**: esto verifica el camino legítimo. El camino del ataque (cookie fabricada, manipulada, de otro usuario o de otro llavero) está cubierto por los tests de `SecuestroTenantPorCookieTests`, no por el navegador.

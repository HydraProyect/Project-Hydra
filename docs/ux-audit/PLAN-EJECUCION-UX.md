# Plan de ejecución del roadmap UX (post-auditoría) + Centro 360 + Acreditación MVP1

> **Tipo**: Operativo — es el plan que consumen las sesiones de implementación de los arreglos
> de la auditoría (`ROADMAP-UX.md`). Decisiones de alcance registradas en
> `docs/business/DECISION_LOG.md` (entradas 2026-08-05). Actualizar el estado aquí y en
> `ROADMAP-UX.md` (✅ + nº de PR) al completar cada ítem.

## Contexto (no re-auditar)

La auditoría integral está terminada y mergeada (`docs/ux-audit/`: `00-INVENTARIO.md` con el
marco de alcance § 0 — MVP = CAE Outbound, vara "¿más rápido y fiable que Excel + operar los
portales?" —, fichas `01..16-*.md` con evidencia `archivo:línea`, y `ROADMAP-UX.md`). Ya
resuelto en main — no repetir:

- Bug P0 de `/centros` (`ObtenerCentrosQuery`) arreglado con tests de integración.
- Quick win "Sin cartera asignada" (Dashboard/workspace delegado) ejecutado y mergeado (PR #100).

La ficha de cada hallazgo es la fuente de verdad; si el código contradice a la ficha,
verificar en ejecución antes de decidir.

## Orden de prioridad (actualizado 2026-08-05)

La **Parte 0 — Centro 360** pasa a ser la primera pieza a implementar, por delante incluso de
los quick wins sueltos de la Parte 1: es el rediseño de `/centros` en un panel operativo único
(asignaciones, visitas, requisitos, accesos y % de cumplimiento) y el propietario del producto
lo considera el quick win de mayor impacto. Orden real de ejecución: **Parte 0 → Parte 1 →
Parte 2**.

## Modo de trabajo (innegociable)

1. **Una sesión = un lote coherente**, en rama nueva desde main, con PR propio. **Merge en
   verde antes de abrir el siguiente** — nunca apilar PRs.
2. **Los transversales se arreglan una vez como pieza compartida** y se aplican a todas las
   pantallas en el mismo PR (paginador único, patrón de export, overflow de acciones). No
   parchear pantalla a pantalla.
3. No mezclar refactors independientes en un PR. YAGNI: lo que pide la ficha, nada más.
4. Seguir `UX_PATTERNS.md`, `DESIGN_SYSTEM.md`, `CODING_STANDARDS.md`. Si un arreglo cambia o
   crea un patrón, **actualizar el documento normativo en el mismo PR**.
5. Reglas de repo de siempre (CLAUDE.md): `Version` en Commands de edición, sin SQL crudo ni
   `IgnoreQueryFilters()`, ninguna tabla nueva sin `TenantId`, cargar Ids con el filtro de
   tenant activo.
6. **Verificación antes de cerrar cada ítem**: build + tests (añadir test si el hallazgo era
   una rotura) y verificación end-to-end en navegador con datos de demo.
7. Decisiones de producto o con componente legal: **preguntar antes** (la excepción ya
   decidida es el bloque Acreditación de la Parte 2).

## Entorno de verificación

- `dotnet build` + perfil `hydra-web` (`.claude/launch.json`, puerto 5186).
- Datos de demo: `"DatosPrueba": { "Activo": true }` en
  `src/CaeManager.Web/appsettings.Development.json` (local, fuera de git) y reiniciar.
- Usuarios: `admin@caemanager.local` / `CaeManager#2026` (TOTP dev `JBSWY3DPEHPK3PXP`);
  con datos: `prueba.direccioncae1@caemanager.local` / `Prueba#2026` (tenant demo Dexter).

## Parte 0 — Centro 360 (PRIORIDAD 1, decidido el 2026-08-05)

Rediseño de `/centros` (fichas 04, 05-H1, 06, 08-H1, 15) en el panel desde el que el gestor
opera un centro sin salir de la pantalla: quién está de alta, con qué estado, si hay visita
próxima, qué exige el centro y por dónde se gestiona. Sustituye la vista plana de
`/asignaciones`, que desaparece como página independiente.

> **Secuenciación en lotes (añadido en la sesión de implementación de 0.1/0.2; reajustado en la
> de 0.5 y de nuevo tras la sesión de mockup de Empresa/Centro del 2026-08-06)**: los 11
> sub-ítems no caben en un único PR sin mezclar refactors independientes (regla
> del propio "Modo de trabajo" de este documento) — 0.5 retira una entidad completa con
> migración, 0.6 construye desde cero la capa de Application para `CanalGestionDocumental` (hoy
> solo existe el modelo de dominio) y cambia su cardinalidad, y 0.4 decide una semántica de
> modelo nueva para `TipoDocumentoCentro`. Se ejecuta en lotes ordenados, cada uno su propia
> rama/PR, merge en verde antes del siguiente: **Lote 0-A** = 0.1 + 0.2 (✅ hecho) · **Lote 0-B**
> = 0.3 (✅ hecho, visita) · **Lote 0-C** = 0.5 (✅ hecho, retirada de Evaluaciones + % de
> cumplimiento) · **Lote 0-D** = 0.4 (documentación requerida del centro, ahora sobre
> `TipoDocumento`/`TipoDocumentoCentro`, retira `RequisitoDocumental` — redacción reajustada
> 2026-08-06) · **Lote 0-F** = 0.8 (✅ hecho, badge circular de % de cumplimiento, Empresa/
> Centro/Trabajador — se adelantó a 0-E porque dependía de 0-D) · **Lote 0-E** = 0.6 + 0.7
> (✅ hecho, N accesos de plataforma + copy de criterios de validación) · **Lote 0-G** = 0.9
> (✅ hecho, selección múltiple oculta tras toggle + densidad de fila, transversal a las 9
> listas con selección en lote) · **Lote 0-H** = 0.10 (✅ hecho, "Ver" → "Detalles" universal +
> edición inline, Drawer de edición retirado en las 5 entidades) · **Lote 0-I** = 0.11 (✅ hecho,
> migrar `/empresas` al patrón Centro 360 — último lote de la Parte 0). 0-D es prerequisito
> real de 0-F (el % necesita el universo correcto de documentos requeridos por centro) y
> conviene que 0-G vaya antes que 0-I (Empresa hereda el patrón de fila ya resuelto por Centro
> en vez de inventarlo dos veces). El plan original agrupaba 0.4+0.5 en el mismo lote asumiendo
> que 0.5 dependía del modelo que decide 0.4 — al implementar se confirmó que el acoplamiento es
> más débil de lo previsto: el % de cumplimiento (0.5) pudo calcularse con la semántica actual de
> `TipoDocumentoCentro` (solo restricción/allow-list) sin esperar a 0.4. Separarlos redujo el
> riesgo de cada PR — mismo criterio aplicado ahora a 0.8/0.9/0.10/0.11, que son independientes
> entre sí y salen de la misma sesión de mockup (2026-08-06) pero no deben mezclarse en un PR.

### (0.1) Acordeón de asignaciones dentro de `/centros` — ✅ hecho (Lote 0-A)

- Cada fila de Centro es un `<details>`/acordeón — **contraído por defecto**, sin coste de
  render hasta que se expande (carga perezosa al abrir, mismo criterio de "no pagar por lo que
  no se ve" que ya usa `NavMenu.razor` para sus grupos). Con 48+ centros la lista no debe
  paginar mentalmente al usuario: se despliega solo el que se está mirando.
- Al expandir: lista de Trabajadores con Asignación activa en ese Centro, con su estado
  **calculado solo sobre lo que ESE centro exige** (reutilizar `IDocumentosFaltantesService` /
  `TipoDocumentoCentro`, no un cálculo nuevo).
- Acciones en la cabecera del acordeón: **"+ Asignar trabajador"** (abre el mismo drawer N×M
  con matriz y preflight de `/asignaciones` hoy — se conserva íntegro, solo cambia dónde vive)
  y **"Dar de baja seleccionados"** (conecta `DarDeBajaAsignacionesCommand`, hoy sin caller —
  cierra el quick win 9 de la Parte 1 en el mismo movimiento).
- Se conserva un **export plano** de todas las asignaciones activas (mismo dato, vista tabla)
  para auditoría/"dónde está Juan hoy" — no todo uso es por-centro.
- **Estado**: hecho. `Centros.razor` pasó de `QuickGrid` a lista paginada en servidor (mismo
  patrón que `Usuarios.razor`) con cada fila envuelta en `SeccionColapsable`; el drawer N×M de
  `Asignaciones.razor` se trasladó a `AcordeonAsignacionesCentro.razor` (con el Centro de la
  fila pre-marcado); `DarDeBajaAsignacionesCommand` tiene ya su primer caller; export nuevo en
  `/asignaciones/exportar.xlsx`. `/asignaciones` se retiró (página, entrada de menú, atajo `g a`
  ahora apunta a `/centros`). Verificado en navegador con datos de demo (34 centros, 268
  trabajadores): alta, baja en lote y export probados end-to-end.
- **Hallazgo de datos, no de código**: el catálogo maestro de `TipoDocumento`
  (`TipoDocumentoSeedData.cs`) tiene `EsObligatorio = false` en **todos** los tipos de Trabajador
  — como el tercer nivel (0.2) reutiliza `IDocumentosFaltantesService` tal como pedía este ítem,
  hoy muestra "Este centro no exige documentación específica" para prácticamente cualquier
  trabajador, aunque el badge de cumplimiento del Centro sí refleje vencimientos reales (ese
  cálculo no filtra por obligatorio). Es el comportamiento correcto dado el modelo actual — no
  se tocó `EsObligatorio` en este lote (decisión de datos/negocio, fuera de alcance) — pero
  conviene que el propietario lo revise: si se espera que el tercer nivel muestre algo en la
  mayoría de centros, hace falta marcar como obligatorios los tipos que correspondan.

### (0.2) Documentación requerida como tercer nivel, dentro del acordeón de cada trabajador — ✅ hecho (Lote 0-A)

- Al expandir un trabajador dentro de un centro: sus documentos **requeridos por ese centro**
  (base + adicionales, ver 0.4), con su estado. Igual que el nivel de centro, **contraído por
  defecto** — validar en implementación que no rompe la densidad visual (el propio propietario
  lo marca como "a probar", no como cerrado).
- **Estado**: hecho, degradación a resumen no hizo falta — con los datos de demo (hasta 14
  trabajadores por centro) la densidad se sostiene bien anidada; a revisar de nuevo si en
  producción aparece un centro con muchos más trabajadores y documentos con estado real (ver
  hallazgo de `EsObligatorio` arriba, que hoy oculta la mayoría de filas).
- Acción "Gestionar" reutiliza el patrón crear-desde-faltante ya existente
  (`?trabajadorId=&tipoDocumentoId=`).

### (0.3) Semáforo con ventana de visita (badge nuevo del Design System) — ✅ hecho (Lote 0-B)

- Cada Centro con visita programada muestra un badge clicable "Visita dd/mm–dd/mm" en la
  cabecera del acordeón (proyección de `/visitas`, sin modelo nuevo).
- El estado de cada documento, dentro de ese contexto, gana un **modificador visual**
  (verde con borde ámbar) cuando el documento es válido hoy pero **caduca dentro de la ventana
  `[hoy, FechaFin]` de la próxima visita del centro** — comparar el resultado de
  `CalculadoraEstadoDocumento` con fecha de referencia = hoy vs. fecha de referencia = fin de
  la visita. Es un **modificador contextual, no un estado nuevo de `Documento`**: sigue siendo
  100% derivado, solo cambia la fecha de referencia según el centro/visita que se está mirando
  — no toca la regla central ni el resto de pantallas que usan hoy como referencia.
  Dar de alta el patrón en `DESIGN_SYSTEM.md`/`UX_PATTERNS.md` (badge "vigente con riesgo en
  ventana"): se reutilizará después en el preflight de asignaciones y en la Bandeja.
- **Asignación rápida desde visita**: si un trabajador que la visita indica que asistirá no
  tiene asignación activa en ese centro, aviso con acción que abre `SelectorEntidad` (ya
  soporta "+ Crear «nombre»" en modal, `UX_PATTERNS.md:26`) para elegir un trabajador existente
  o crear uno nuevo, disparando `CrearAsignacionCommand` con el preflight de siempre.
- **Estado**: hecho, con una simplificación de la última viñeta — `VisitaTrabajador` ya
  referencia un `Trabajador` real (no un nombre suelto de origen Inbound), así que el aviso
  identifica al trabajador sin ambigüedad y el botón "Asignar" dispara `CrearAsignacionCommand`
  directamente para ese Id; no hizo falta `SelectorEntidad` (no hay nada que buscar o crear —
  el candidato ya es conocido). Batch por página (`ObtenerProximaVisitaPorCentroQuery`), no
  N+1 por fila. Verificado en navegador: badge con fechas correctas, click filtra `/visitas`
  por el centro; el aviso de asignación rápida no se pudo ver en acción con los datos de demo
  porque `DatosPruebaSeeder` construye cada `VisitaTrabajador` a partir de trabajadores que
  YA tienen asignación activa en ese centro (nunca deja el hueco) — revisado el código de la
  query y es correcto por inspección, pero queda pendiente una verificación visual con datos
  reales donde sí exista el hueco.

### (0.4) Documentación requerida del Centro — configurable en ambos sentidos — ✅ hecho (Lote 0-D)

**Redacción reajustada 2026-08-06** (sesión de mockup Empresa/Centro): se decidió **retirar
`RequisitoDocumental`** en vez de mantenerlo en paralelo como preveía la redacción anterior de
este punto (texto libre "sigue viva para lo verdaderamente ad-hoc"). Motivo del propietario: el
texto libre no permite que un documento de Requisitos del Centro aparezca automáticamente en la
lista de documentos del Trabajador ni dispare su estado "Pendiente de Subir" — solo un
`TipoDocumento` real, con `AmbitoAplicacion`, puede alimentar ese cruce. Mantener las dos tablas
en paralelo (una estructurada, una de texto libre, ambas modelando "qué exige este centro") es
la duplicación que el comentario original de `RequisitoDocumental.cs:5-19` ya advertía.

**Hallazgo de modelo real** (no solo diseño de pantalla): hoy `TipoDocumentoCentro` es una
lista de permiso (*allow-list*) — un `TipoDocumento` sin ninguna fila ahí aplica a **todos**
los centros; con filas, se restringe a esos centros (`TipoDocumentoCentro.cs:5-9`,
`DocumentosFaltantesService.cs:48-55,74-76`). Este mecanismo permite que un centro exija **más**
tipos de los que son obligatorios por defecto, pero **no permite que un centro exija menos** —
para excluir un tipo global-obligatorio de un único centro habría que añadir filas de
restricción a todos los demás centros del tenant, impracticable. Y el caso real que reporta
el propietario existe: plataformas Inbound que solo piden EPIS + Apto médico, sin el resto del
paquete estándar (Art. 18/19, etc.).

- **`TipoDocumentoCentro` gana dos campos**: `PeriodicidadEspecial` (int? meses — override de la
  periodicidad de renovación del `TipoDocumento` solo para ese centro; `null` = no vence, igual
  semántica que hoy tiene `RequisitoDocumental.PeriodicidadEspecial`) y `BloqueaAcceso` (bool —
  sustituye la causa `Bloqueante` que hoy aporta `RequisitoDocumental` a
  `CalculoEstadoCentroService.AgregarCausasDeRequisitosBloqueantesAsync`). El adjunto de
  plantilla en blanco (Word/PDF, `ArchivoUrl`/`NombreArchivoOriginal` de `RequisitoDocumental`)
  se traslada igual a `TipoDocumentoCentro` — sigue siendo la plantilla a rellenar, no un
  justificante con caducidad, mismo criterio que ya documentaba `RequisitoDocumental.cs:12-19`.
- **Vista "Documentación requerida en este centro"** (sustituye a la pestaña "Requisitos del
  Centro" tal cual existe hoy en `CentroWorkspacePanel.razor`): selector de `TipoDocumento`
  existentes del catálogo del tenant (filtrado por `AmbitoAplicacion` Trabajador/Empresa — el
  propietario pide explícitamente que se muestren también los de Empresa, no solo Trabajador),
  con toggle **incluido/excluido para este centro**, más los dos campos nuevos por fila. Ya no
  hay "añadir requisito con descripción libre" — solo elegir de la lista de `TipoDocumento`, o
  crear un `TipoDocumento` nuevo desde el propio selector si no existe (mismo patrón
  `PermiteCrear`/`OnCrearSolicitado` que ya usa `SelectorEntidad` en otras pantallas).
  Requiere una tabla de exclusión explícita por centro (o invertir la semántica de
  `TipoDocumentoCentro` a "incluye/excluye" en vez de solo "restringe") — decisión de modelo a
  tomar en la sesión de implementación, con test que cubra ambos sentidos.
- **Catálogo mínimo por defecto**: todo Centro nuevo recibe automáticamente 4 filas de
  `TipoDocumentoCentro` (incluido) al crearse, para los `TipoDocumento` "Reconocimiento médico
  (Apto)", "Entrega de EPIs", "Formación sobre riesgos en el puesto de trabajo (Artículo 19)" e
  "Información de riesgos en el puesto de trabajo (Artículo 18)" — creando esos `TipoDocumento`
  en el seed/migración si no existen ya en el catálogo del tenant (verificar contra
  `TipoDocumentoSeedData.cs`: "Apto médico laboral" ya existe con ese nombre, puede que solo
  falten los otros tres). Se hace en el mismo Command que crea el Centro (`CrearCentroCommand`),
  no como paso manual aparte.
- **Auto-población en la lista de documentos del Trabajador**: cuando un Trabajador tiene
  asignación activa en un Centro, sus documentos requeridos por ese Centro (vía
  `TipoDocumentoCentro`, ya es lo que hace hoy el tercer nivel del acordeón, 0.2) deben aparecer
  aunque el Trabajador no tenga ningún `Documento` de ese `TipoDocumento` todavía — hoy si no hay
  `Documento` real, la fila simplemente no aparece. Estado nuevo **"Pendiente de Subir"** (gris,
  distinto de "Faltante") para esas filas sin `Documento`. Definir si es una fila puramente de
  UI (no crea un `Documento` en BD hasta que se suba algo) o si genera un `Documento` placeholder
  — la primera opción es la que respeta mejor "no validar/crear lo que no existe todavía" del
  propio `CLAUDE.md`, y es coherente con cómo `ObtenerAlertasQuery` ya trata los huecos
  obligatorios como cálculo derivado, no como fila persistida.
- Este es también el prerequisito real para el % de cumplimiento circular del punto (0.8): sin
  poder decir "este centro solo exige 2 tipos", el % estaría contando de más para los centros
  que piden menos.
- **Migración de datos**: los `RequisitoDocumental` que existan hoy en producción (probablemente
  pocos o ninguno, verificar antes de escribir la migración) se migran a `TipoDocumentoCentro`
  creando el `TipoDocumento` correspondiente si su `Descripcion` no matchea ninguno existente;
  los que tengan adjunto de plantilla lo conservan. Tabla `RequisitosDocumentales` se retira con
  `DropTable` reversible, mismo patrón que la migración `RetirarEvaluaciones` del Lote 0-C.

- **Estado**: hecho, 2026-08-06. Decisión de modelo tomada durante la implementación (resuelve la
  pregunta abierta arriba): `TipoDocumentoCentro` gana `Incluido` (bool) — fila explícita para el
  par (Tipo, Centro) manda; sin fila, sigue `TipoDocumento.EsObligatorio` — en vez de la semántica
  antigua "cualquier fila restringe el tipo a solo esos centros en todo el tenant", que no
  permitía excluir un único centro. Consolidada en `ResolucionTipoDocumentoCentro` (Application)
  y aplicada en los 8 sitios que antes duplicaban la lógica de allow-list (`CalculoEstadoCentroService`
  ×2, `ObtenerAsignacionesDocumentacionPorCentroQuery`, `ObtenerDocumentacionVisitaQuery`,
  `IDocumentosFaltantesService`, `ObtenerFormatosRequeridosCentroQuery`, `ObtenerTiposDocumentoQuery`,
  `ObtenerTipoDocumentoPorIdQuery`). `RequisitoDocumental` retirado íntegro (dominio, Application,
  Infrastructure, endpoint de plantilla reapuntado a `TipoDocumentoCentro`); migración
  `RetirarRequisitoDocumental` traslada filas existentes antes del `DropTable` (creando el
  `TipoDocumento` que falte por `Descripcion`, sin trasladar `PeriodicidadEspecial` textual ni
  `Cumplido` — ver comentario en la propia migración). `BloqueaAcceso` pasó de check manual a
  nivel de Centro a detección automática por trabajador (mismo criterio que el resto de
  `CalculoEstadoCentroService`): la Bandeja usa la nueva `ObtenerDocumentacionBloqueantePendienteQuery`
  en vez de `ObtenerRequisitosDocumentalesPendientesQuery`. Catálogo mínimo por defecto
  (`CrearCentroCommandHandler`) busca los 4 `TipoDocumento` por `Nombre` dentro del tenant, no por
  Id fijo (los Id de `TipoDocumentoSeedData` son solo del catálogo semilla del tenant #1). UI de
  Requisitos del Centro reescrita como selector sobre el catálogo (`CampoSelect` para la
  periodicidad, con opciones predefinidas + personalizado + vacío = no vence). "Pendiente de
  Subir" (gris) es una reinterpretación puramente de UI del mismo `EstadoDocumento.Faltante` que ya
  generaba `ObtenerAsignacionesDocumentacionPorCentroQuery` — no hay entidad ni estado nuevo.
  Verificado con los 3 suites de test (339+199+294, todas en verde) y en navegador con datos de
  demo: alta/baja de Incluido, periodicidad y bloqueo desde el drawer, badge "Pendiente de Subir"
  apareciendo para un trabajador sin el documento recién exigido, % de cumplimiento recalculando
  correctamente tras el cambio.

### (0.5) % de cumplimiento — sustituye al módulo Evaluaciones — ✅ hecho (Lote 0-C)

**Decisión del propietario, 2026-08-05: el módulo Evaluaciones se retira.** La puntuación
manual 0-100 (`Evaluacion`, consumida hoy por `CatalogoKpis`/Dashboard Ejecutivo — sesión 08
ya señalaba "sin semántica visible") nunca reflejó uso real: la puntuación **siempre debió ser
automática**, derivada de la documentación requerida pendiente/vencida, no un juicio manual
aparte. No se sustituye por una feature equivalente; se calcula donde hace falta:
- **Por trabajador dentro de un centro**: `documentos al día / documentos requeridos por ese
  centro` (usa 0.4) — junto al nombre en el acordeón, ej. "7/9".
- **Por centro**: agregado de sus trabajadores — junto a la cabecera del acordeón.
- Reutiliza el cálculo ya existente de `CalculoEstadoCentroService` (mergeado en `def009c`,
  hoy expresado como badge Bloqueado/Faltante/Vencido/…) llevándolo a fracción/porcentaje.
- **Retirar**: ruta `/evaluaciones`, entidad `Evaluacion` y sus Commands (con migración de
  baja, no solo ocultar el menú — nada de "pantalla fantasma", `NavMenu.razor:2-4`), y las
  referencias en `CatalogoKpis`/Dashboard Ejecutivo (`ObtenerDashboardEjecutivoQuery.cs`,
  `CatalogoKpis.cs`) — sustituir esos KPI por el nuevo % agregado de cumplimiento.
- **Estado**: hecho. `ICalculoEstadoCentroService` gana `CalcularCumplimientoAsync` (método
  aparte de `CalcularAsync` a propósito — mismas fuentes de datos, pero sin arriesgar la lógica
  de badge ya en producción). El % de centro se muestra en `Centros.razor` junto al badge de
  cumplimiento (`CentroListaDto.CumplimientoPorcentaje`, `null` cuando `Requeridos == 0` — "sin
  requisitos" en vez de un 0%/100% engañoso); el "7/9" por trabajador en `AcordeonAsignacionesCentro`
  se deriva sin consulta nueva de los `Documentos` que ya carga el Lote 0-A. `Evaluacion` retirada
  íntegra: dominio, Commands/Queries, repositorio, configuración EF, DbSet, DI, seeding, página,
  entrada de menú, y migración `RetirarEvaluaciones` (`DropTable`, con `Down()` reversible).
  `CatalogoKpis`/`ObtenerCatalogoKpisQuery`/`ObtenerDashboardEjecutivoQuery` sustituyen
  `eval.puntuacion-media`/`eval.centros-riesgo` por `doc.pct-cumplimiento-trabajadores`/
  `doc.centros-menor-cumplimiento`, reutilizando el mismo `CalcularCumplimientoAsync`.
  Verificado en navegador con datos de demo: badge oculto correctamente cuando no hay tipos
  obligatorios (el hallazgo de `EsObligatorio=false` de los lotes 0-A/0-B sigue aplicando —
  el % de cumplimiento hoy siempre da "sin requisitos" hasta que el propietario marque algún
  `TipoDocumento` como obligatorio), `/evaluaciones` devuelve 404, KPIs nuevos visibles y
  correctos en Dashboard Ejecutivo tras personalizar la selección.

### (0.6) N accesos de plataforma por Centro, con etiqueta de propósito — ✅ hecho (Lote 0-E)

**Hallazgo de modelo real**: `CanalGestionDocumental` es hoy 1:1 con el Centro
(`Centro.razor` / `CentroWorkspacePanel.razor` pestaña "Plataforma", sesión 04-H2/H3). Caso
real reportado: un mismo Centro puede tener **el mismo link con credenciales distintas** según
a quién gestiona (ej. trabajadores extranjeros de una empresa del mismo grupo pero entidad
legal distinta — "Iberojet Alemania S.L." trabajando para "Iberojet S.L." con el canal de
Iberojet S.L., solo cambia la credencial) — y también el caso de credenciales separadas para
"gestión del día a día" vs. otro colectivo.
- `CanalGestionDocumental` pasa de 1:1 a **N por Centro**, cada uno con proveedor (del catálogo
  de Parte 2), URL, credencial y **etiqueta de propósito en texto libre** (ej. "Gestión
  general", "Trabajadores extranjeros — Iberojet Alemania") — **no catálogo cerrado de
  propósitos**: son ad-hoc por cliente. Uno marcado como principal/por defecto.
- La pestaña "Plataforma" del panel pasa a listar N accesos en vez de uno.
- **Estado**: hecho, 2026-08-06. `CanalGestionDocumental` pasa a `EntidadBase` (gana `Version` y
  soft delete: hasta ahora la tabla **no tenía ningún escritor** —ni Command ni seeder, solo la
  Query de lectura—, y este lote la vuelve editable desde la UI). Campos nuevos
  `EtiquetaProposito` (obligatorio, texto libre) y `EsPrincipal`; el índice único de
  `(TenantId, CentroId)` que imponía el 1:1 se sustituye por uno normal más un **único filtrado**
  `WHERE "EsPrincipal" AND NOT "EstaEliminado"` — "a lo sumo un principal" se sostiene en la base
  de datos, no solo en el Command (dos peticiones concurrentes pasarían las dos la comprobación
  en memoria). Commands `CrearCanalGestion` (el primer canal de un Centro se marca principal
  solo), `EditarCanalGestion` (el `Tipo` no se edita — Plataforma y Email no comparten campos; y
  un flag `CambiarCredenciales` porque la pantalla nunca muestra las guardadas y guardar en
  blanco las habría borrado en silencio), `MarcarCanalGestionPrincipal` y `EliminarCanalGestion`
  (rechaza borrar el principal habiendo otros: cuál pasa a serlo es decisión del gestor, no del
  orden de inserción). `ObtenerCanalesGestionDeCentroQuery` sustituye a la singular, con el
  principal primero. `ObtenerBorradorPedirPrioridad` deja de asumir el 1:1 y prefiere el canal
  Email principal. **El proveedor sigue siendo texto libre**: el catálogo es Parte 2 y todavía
  no existe (YAGNI). Verificado en navegador: dos accesos al mismo portal con credenciales
  distintas en un mismo centro (el caso real reportado), cambio de principal, rechazo de borrar
  el principal, y el destinatario de "Pedir prioridad" resuelto desde el canal de correo.

### (0.7) Criterios de validación — puente con la documentación Inbound (sin modelo nuevo) — ✅ hecho (Lote 0-E)

**Ya existe**: `TipoDocumento.CriteriosValidacion` (`TipoDocumento.cs:16,26,142-152`) es
exactamente el campo "términos de validación" que describe el propietario — hoy expuesto como
textarea en `/tipos-documento` (`TiposDocumento.razor:202`) pero sin ningún hilo hacia el
origen Inbound. No requiere modelo nuevo, solo dos ganchos de flujo:
- En la ficha del `TipoDocumento`, texto de ayuda que invite explícitamente a pegar ahí los
  criterios/términos de validación tal como los describe la plataforma Inbound del cliente
  (mismo copy que ya usa `Facturacion.razor` citando su origen — "Corresponde a la sección…").
- Este campo queda marcado como **fuente de referencia para la automatización de lectura IA**
  (`VerificacionIaDocumentoService`) — sin construir la integración ahora, pero documentando la
  intención en el propio código para que la sesión de IA que lo use no tenga que redescubrirla.
- **Estado**: hecho, 2026-08-06. Texto de ayuda bajo el textarea de `/tipos-documento` (clase
  nueva `.texto-ayuda-campo`, genérica para ayuda de campo de formulario) invitando a pegar los
  criterios del portal del cliente sin reescribirlos. La intención queda anotada en los dos
  sitios donde hace falta: el XML doc de `TipoDocumento.CriteriosValidacion` y el de
  `VerificacionIaDocumentoService`, que hoy solo compara tipo/fecha/firma. Sin modelo nuevo,
  como decía el plan.

### (0.8) Badge circular de % de cumplimiento — Empresa/Centro/Trabajador — ✅ hecho (Lote 0-F)

**Origen**: sesión de mockup Empresa/Centro, 2026-08-06 (imagen de referencia adjunta por el
propietario). Sustituye el % en texto plano que ya existe (`Centros.razor:81-86`,
`CumplimientoPorcentaje`) y el "7/9" del acordeón de trabajador
(`AcordeonAsignacionesCentro.razor:83-88`) por un componente visual — no cambia el cálculo, solo
la representación.

- **Componente nuevo del Design System**: anillo/círculo de progreso SVG inline, coherente con
  el resto de iconografía del proyecto (`Icono.razor`, chevron de `SeccionColapsable`) — sin
  añadir dependencias externas (decisión 2026-08-06: no se usa el paquete de iconos Flaticon
  mencionado en el mockup original). Color del anillo según el mismo criterio de `Tono` que ya
  usa `Badge` (verde/ámbar/rojo). Está listado como "Progress" en `DESIGN_SYSTEM.md` §
  "Pendientes" — al construirlo, documentarlo ahí con su estructura fija (Do/Don't/accesibilidad).
- **Tres niveles, cálculo acumulativo solo del padre que se consulta** (no global):
  - **Trabajador dentro de un Centro**: ya existe como fracción (`DocumentosAlDia`/
    `trabajador.Documentos.Count`), solo cambia a badge circular.
  - **Centro**: ya existe (`CalcularCumplimientoAsync`), solo cambia a badge circular.
  - **Empresa**: **cálculo nuevo** — agregado de todos los Centros donde la Empresa tiene
    actividad real (mismo universo que ya usa `ObtenerCentrosConActividadDeEmpresaQuery` en
    `EmpresaWorkspacePanel.razor`), sumando `AlDia`/`Requeridos` de `CalcularCumplimientoAsync`
    para esos Centros. No es la media de los porcentajes de cada Centro (sesgaría a favor de
    centros con pocos requisitos) — es la fracción total de pares Trabajador×TipoDocumento.
- Mismo criterio ya establecido en 0.5: `null`/oculto cuando `Requeridos == 0`, nunca un 0%/100%
  engañoso.
- **Estado**: hecho, 2026-08-06. `AnilloCumplimiento.razor` (+ `AnilloCumplimientoEnums.cs`) —
  SVG con `stroke-dasharray` sobre un `<circle>`, sin dependencias, umbral propio (100% Exito,
  ≥50% Advertencia, resto Peligro — no reutiliza `EstadoCentroUi`/`EstadoDocumentoUi`, traducen
  preguntas distintas). Documentado en `DESIGN_SYSTEM.md` (movido de "Pendientes" a la tabla de
  componentes). `ObtenerCumplimientoEmpresaQuery` nueva (universo de Centros vía el mismo join
  que `ObtenerCentrosConActividadDeEmpresaQuery`, suma `AlDia`/`Requeridos` — no media). Verificado
  en navegador con datos de demo: los tres niveles (Centro 41%/peligro, Trabajador 50%/0%, Empresa
  41% agregado) muestran el color y el `stroke-dasharray` correctos.
  **Corregido después (Lote 0-E, 2026-08-06)**: el aro no llegaba a dibujarse. El radio se
  interpolaba como `double` directamente en el atributo, así que salía con la coma decimal de
  la cultura de la petición (`r="15,5"`) — no es una longitud SVG válida, el navegador la
  descartaba y quedaba radio 0: solo se veía el número. `stroke-dasharray` sí se formateaba en
  cultura invariante, de ahí que la verificación original lo diera por bueno. Ahora el radio
  también (`RadioSvg`). Lección para cualquier SVG inline futuro del Design System: **todo
  número que vaya a un atributo SVG se formatea en `InvariantCulture`**, no solo los que uno
  recuerda.

### (0.9) Selección múltiple oculta tras toggle + densidad de fila — transversal — ✅ hecho (Lote 0-G)

**Origen**: mismo mockup 2026-08-06. Aplica a **todas** las listas con checkboxes hoy
(`Centros.razor`, `Empresas.razor`, y el resto de listas con selección en lote — Clientes,
Trabajadores, Subcontratas, Vehículos), no solo a Centro.

- Los checkboxes de fila (y el "Seleccionar todos" de cabecera) dejan de estar siempre visibles.
  Un botón **"Selección múltiple"** junto a la barra de filtros los muestra/oculta.
- **Mientras están ocultos**, en la posición donde hoy vive el checkbox (antes del nombre) va el
  control de expandir/colapsar (`SeccionColapsable`) — contraído = flecha derecha, expandido =
  flecha abajo. **Al activar selección múltiple**, el checkbox se añade a la izquierda de esa
  misma flecha, sin desplazarla ni ocultarla — ambos controles conviven en la fila.
- **Botón "Colapsar todos" / "Expandir todos"** junto al toggle de selección múltiple, para las
  listas con acordeón (Centros; Empresas tras el Lote 0-I).
- **Densidad de fila** (mismo mockup, segunda tanda de instrucciones): reducir altura vertical y
  padding de `.tarjeta-fila-acordeon-cabecera`, tamaño de `Badge` y tamaño de los indicadores
  tipo badge/chip ("bullets" — confirmado con el propietario: son los `Badge` de estado tipo
  `badge-peligro`/`badge-visita`, no viñetas de lista) para priorizar más información por
  pantalla. Se hace en el mismo lote porque toca el mismo componente/CSS compartido
  (`tarjeta-fila-acordeon*` en `list-page.css`) que el resto de este punto — hacerlo en un PR
  aparte tocaría el mismo archivo dos veces sin necesidad.
- Actualizar `UX_PATTERNS.md` con el patrón "selección múltiple tras toggle" — se reutilizará en
  cualquier lista nueva a partir de ahora.
- **Estado**: hecho, 2026-08-06. `BarraHerramientasLista.razor` (Design System) es la pieza
  compartida por las **9** listas con selección en lote — el plan nombraba 6, son 9: además de
  Centros/Empresas/Clientes/Trabajadores/Subcontratas/Vehículos, también Documentos, Incidencias
  y Visitas. **Hallazgo de alcance**: solo `/centros` tiene hoy filas-acordeón; las otras 8 son
  `QuickGrid`, así que el toggle de selección múltiple es transversal a las 9 pero el chevron en
  la posición del checkbox y el "Expandir/Colapsar todos" solo aplican a Centros (y a Empresas
  tras 0-I). Apagar el toggle limpia la selección, para no dejar `BarraAccionesLote` apuntando a
  filas invisibles. La expansión pasa de vivir dentro de cada `SeccionColapsable` (estado
  interno, inalcanzable desde fuera) a un `HashSet` de la página: era la única forma de que
  "Expandir todos" pudiera existir. El acordeón sigue montando su contenido solo al expandirse,
  como antes. Densidad: `TamanoBadge.Pequeno` nuevo en `Badge` (solo métrica, el color del
  semáforo no se toca) replicado en `.badge-visita`, más padding vertical y gap reducidos en
  `.tarjeta-fila-acordeon-cabecera` — el padding horizontal se deja igual, apretar los lados no
  gana filas.

### (0.10) "Ver" → "Detalles" universal + edición inline — ✅ hecho (Lote 0-H)

**Origen**: mismo mockup 2026-08-06. Aplica a **Empresa, Centro, Subcontrata, Trabajador y
Vehículo** — todas las entidades con acciones "Ver"/"Editar" hoy (`Centros.razor:95-100`,
`Empresas.razor:70-75`, y equivalentes en Clientes/Trabajadores/Vehículos).

- El botón "Ver" se retira; el botón que abre el Context Workspace pasa a llamarse **"Detalles"**
  (mismo destino: `WorkspaceService.AbrirAsync(...)`, pestaña "Información").
- La pestaña "Información" de cada panel de Workspace (`CentroWorkspacePanel.razor`,
  `EmpresaWorkspacePanel.razor`, etc.) gana un icono de edición junto al título de la pestaña —
  al pulsarlo, los `CampoInfo` de esa pestaña pasan a ser campos editables in situ, con los
  mismos Commands `Editar*` que hoy dispara el Drawer de "Editar".
- **Decisión pendiente de la sesión de implementación**: si el Drawer de edición actual
  desaparece del todo (queda solo para "Nuevo X") o si se conserva como alternativa. El
  propietario no lo especificó — preguntar antes de retirar el Drawer, es una superficie que
  varias fichas de la auditoría (04, 05-H1) ya dan por existente.
- **Decisión tomada (2026-08-06, confirmada con el propietario)**: el Drawer de edición se
  retira del todo — queda solo para "Nuevo X" (alta). Ningún camino alternativo a la edición in
  situ.
- **Estado**: hecho, 2026-08-06. Icono `editar` nuevo en `Icono.razor`. Las 5 entidades
  (`Centros`, `Empresas`, `Subcontratas`, `Trabajadores`, `Vehiculos` — no Clientes, fuera de
  alcance) llevan un icono de lápiz en la cabecera del Workspace, visible en la pestaña
  "Información", que activa edición in situ con los mismos `Editar*Command` que usaba el Drawer
  — versión optimista incluida (`_detalle.Version`, ya cargada por la pestaña, sin fetch
  adicional al entrar en modo edición). DNI de Trabajador y Empleador de Trabajador/Vehículo
  quedan en solo lectura: no forman parte de sus `Editar*Command` (identidad/vínculo fijado al
  crear). **Empresa y Subcontrata conservan las credenciales de acceso a plataforma externa**
  (antes solo visibles en el Drawer de "Editar", con su propio botón "Guardar credenciales") —
  se trasladan al mismo panel de edición en vez de perderse, como una segunda sección
  independiente con su propio guardado, igual que ya funcionaban. Los Drawers de las 5 listas
  quedan solo-creación (título fijo "Nuevo X", sin rama `_editandoId`); en Empresa, la creación ya
  no deja el Drawer abierto en "modo edición" para rellenar credenciales — se cierra igual que el
  resto, las credenciales se rellenan después desde el Workspace. `FlujoCriticoTests` (E2E)
  actualizado: ya no espera el título "Editar empresa" tras crear. Verificado en navegador con
  datos de demo en las 5 entidades: "Detalles"/sin "Editar" en la lista, edición in situ guardada
  y reflejada al volver a modo lectura, credenciales de Empresa persistidas tras recargar, DNI/
  Empleador de Trabajador y Empleador de Vehículo confirmados de solo lectura, y el Drawer "Nuevo
  X" con título fijo en las 5.

### (0.11) Migrar `/empresas` al patrón Centro 360 — ✅ hecho (Lote 0-I)

**Origen**: mismo mockup 2026-08-06. `Empresas.razor` hoy sigue en `QuickGrid` clásico (tabla,
sin acordeón, sin badge de cumplimiento) — no tiene nada del rediseño que sí recibió `/centros`
en los Lotes 0-A/0-B/0-C. Este lote lo alinea, reutilizando los componentes que 0-G y 0-F ya
habrán construido (fila-tarjeta-acordeon, checkbox tras toggle, badge circular de %) en vez de
reinventarlos para Empresa.

- `Empresas.razor` pasa de `QuickGrid` a lista paginada en servidor con `SeccionColapsable` por
  fila, mismo patrón que `Centros.razor` (`ObtenerCentrosQuery` → equivalente para
  `ObtenerEmpresasQuery`, ya pagina en servidor, solo cambia el render).
- **Único desplegable por fila de Empresa**: los Centros donde esa Empresa tiene actividad real
  (mismo query que ya usa `EmpresaWorkspacePanel.razor` — `ObtenerCentrosConActividadDeEmpresaQuery`).
  Al hacer click en un Centro del desplegable, navega a `/centros` **prefiltrado** por ese
  Centro — requiere que `Centros.razor` soporte un filtro por Id vía query string (hoy el
  filtro de Empresa en el Drawer de creación usa `_empresaId` interno, no hay prefiltro por URL
  todavía; verificar/añadir soporte de `?centroId=` o similar en `Centros.razor.cs`).
- Badge de % de cumplimiento de Empresa (0.8) junto al nombre, igual posición que en Centro.
- Checkbox oculto tras "Selección múltiple" (0.9), misma cabecera transversal.
- "Ver" → "Detalles" (0.10) ya heredado si 0-H se hizo antes.
- **Estado**: hecho, 2026-08-06. `Empresas.razor` pasó de `QuickGrid` a lista paginada en
  servidor con `tarjeta-fila-acordeon` (mismo markup que `Centros.razor`, sin `SeccionColapsable`
  — el chevron y la expansión los lleva la página, igual que Centro desde el Lote 0-G).
  `ObtenerEmpresasQuery` gana `CumplimientoPorcentaje` en `EmpresaListaDto`, calculado en lote
  para toda la página (mismo cálculo que `ObtenerCumplimientoEmpresaQuery` — Empresa → Trabajadores
  → Asignaciones activas → Centro, suma AlDia/Requeridos, no media — pero una sola consulta de
  actividad + una llamada a `CalcularCumplimientoAsync` para las 20 filas, no 20 consultas
  sueltas). El desplegable de "Centros con actividad" carga perezosamente por fila al expandir
  (`ObtenerCentrosConActividadDeEmpresaQuery`, la misma Query que ya usaba la pestaña "Centros"
  del Workspace) — "Expandir todos" lanza las cargas pendientes en paralelo
  (`Task.WhenAll`), no una tras otra. **Prefiltro por Id añadido a `ObtenerCentrosQuery`**
  (`CentroId`, opcional, se combina con el resto de filtros) y a `Centros.razor`
  (`[SupplyParameterFromQuery] Guid? CentroId`) — clic en un Centro del desplegable navega a
  `/centros?centroId=…`, exacto por Id y no por nombre (`?q=`), que sería ambiguo entre Centros
  con nombre parecido. Documentado como patrón nuevo en `UX_PATTERNS.md` § "Drill-down entre
  listas con filtro exacto por Id". Verificado en navegador con datos de demo: acordeón con
  badge de cumplimiento y desplegable de Centros, clic en un Centro navega a `/centros` mostrando
  solo esa fila, selección múltiple/expandir todos probados, y "Nueva empresa"/"Detalles"/edición
  in situ (heredados de 0-H) sin regresión.

## Parte 1 — Horizonte 1, quick wins (en orden salvo indicación del propietario)

> El ítem 9 ya no incluye "baja en lote de Asignaciones" — se resuelve dentro de (0.1), porque
> `/asignaciones` como página independiente deja de existir (absorbida por el acordeón de
> Centro 360).

| # | Ítem | Ficha | Estado |
|---|---|---|---|
| 2 | Restaurar desde Auditoría (los `Restaurar*Command` existen) o, mínimo inmediato, corregir el copy del borrado en lote (`Empresas.razor:181`) | 14-H1 | ✅ hecho (#113) |
| 3 | Página Forbidden propia (`AccessDeniedPath` + pantalla con siguiente paso) | 13-H1/16-H1 | ✅ hecho (#114) |
| 4 | Paginador único localizado (12 listas QuickGrid + Usuarios) | 02-H2/14-H3 | ✅ hecho (#116) |
| 5 | Overflow menu (⋯) en Acciones — densidad de una línea por fila | 05-H2 | ✅ hecho (#118) |
| 6 | Export en Empresas, Centros, Incidencias y Auditoría + resumen de facturación (patrón `/clientes/exportar.xlsx`; el export de Asignaciones queda cubierto por (0.1)) | 03-H7·08-H4·11-H2·14-H2 | ✅ hecho (#120) |
| 7 | Detecciones de personal visibles: badge en Empresas + tipo nuevo en Bandeja | 03-H2 | ✅ hecho (#121) |
| 8 | Bandeja: contadores por tipo · Calendario: tema oscuro de celdas + leyenda · Dashboard Ejecutivo: colapsar "Personalizar" + tema DS en ApexCharts | 10-H2/10-H3/01-H5/01-H6 | ✅ hecho (#122) |
| 9 | Lote de remates (un PR): filtros completos en URL + chips + borrar filtros guardados · placeholder "—" · header "RazonSocial" · label Notas del alta guiada · quitar pestaña "Citas" · fila clicable en Clientes · selector de tamaño de página · catch `JSDisconnectedException` (3 Dispose) · atribuir/resolver error CSP | 02·03·05·16 | ✅ hecho (#123) |

## Parte 2 — Bloque Acreditación por plataforma destino (alcance MVP1)

Decisión registrada en `docs/business/DECISION_LOG.md` (2026-08-05). Se ejecuta **después**
de los quick wins. Referencias obligatorias: `docs/business/inbound/` (MARKET_CATALOG,
INBOUND_DOMAIN_GLOSSARY — estados/sinónimos y la colisión "Incidencia" —,
CANONICAL_MODEL_DRAFT § equivalencias) y `ARQUITECTURA-INTEGRACIONES.md`. Motivación: un
mismo documento puede estar vigente en Hydra, aceptado en Dokify y pendiente en Nalanda —
sin visión por plataforma, Hydra no puede ser la única pantalla del gestor.

> **Secuenciación en lotes (añadido en la sesión de implementación de (a), 2026-08-07)**:
> igual que la Parte 0, el sub-ítem (a) no cabe en un único PR sin mezclar refactors
> independientes — construir el catálogo es un cambio de dominio autocontenido; migrar
> `CanalGestionDocumental.NombrePlataforma` a referenciarlo toca una entidad de producción
> ya en uso y merece su propia verificación. **Lote 2-A** = catálogo `ProveedorPlataformaCae`
> + `DominioProveedorPlataformaCae` + servicio de resolución por URL (✅ hecho, ver estado
> abajo) · **Lote 2-B** = migrar `CanalGestionDocumental.NombrePlataforma` a referenciar el
> catálogo, con matching sugerido de los valores existentes (pendiente). Durante 2-A se
> encontraron y resolvieron dos veces antes de escribir código: (1) el nombre
> `ProveedorIntegracion` ya lo usa un enum existente (conector de mensajería Email/WhatsApp)
> — la entidad nueva se llama `ProveedorPlataformaCae` para no colisionar; (2) la
> clasificación "Global + extensión por tenant" que pedía la redacción original no es
> construible todavía (el aprovisionamiento automático de tenant que ese patrón necesita no
> existe) — decisión con el propietario: catálogo **global puro**, documentada en
> `docs/MULTITENANCY.md` § 7.

### (a) Catálogo `ProveedorPlataformaCae` — Lote 2-A ✅ hecho ([#124](https://github.com/christopherjp1-jpg/Project-Hydra/pull/124)), Lote 2-B pendiente

**Redacción original de esta ficha, superada por la decisión de la sesión de implementación
(ver nota de secuenciación arriba)**: hablaba de reutilizar tal cual la entidad
`ProveedorIntegracion` de `ARQUITECTURA-INTEGRACIONES.md` con clasificación "Global +
extensión por tenant". Se implementó como `ProveedorPlataformaCae` (nombre distinto, sin
`VersionApiProveedor` ni aparato de conector) y **global puro** (sin extensión por tenant) —
motivos en la nota de secuenciación y en `docs/MULTITENANCY.md` § 7.

Con dominios para identificación por URL. Migrar
`CanalGestionDocumental.NombrePlataforma` (texto libre) a referencia del catálogo con matching
sugerido de los strings existentes — con el cambio de (0.6), cada uno de los N accesos por
Centro referencia su propio proveedor del catálogo, no solo el canal único de antes.
**CTAIMACAE (legacy), Twind y e-coordina son TRES proveedores separados** (hay empresas que hoy
operan solo en una), unidos por el grupo "Twind (CTAIMA Group)"; el campo "grupo empresarial"
es solo para analítica, nunca lógica operativa.

**Semilla de dominios (verificada por el propietario, 2026-08-05).** La resolución matchea
por dominio y sufijo (subdominios incluidos); multi-match ⇒ elegir entre candidatos; sin
match ⇒ selección manual / alta por tenant. Dominios editables desde el catálogo.

| Proveedor | Dominios | Grupo | Notas |
|---|---|---|---|
| Nalanda | nalandaglobal.com | Once For All | |
| Dokify | dokify.net | Once For All | |
| Twind | app.twind.io, ctaima.com | Twind (CTAIMA Group) | Marca unificada CTAIMA + e-coordina; destino esperado de migraciones |
| CTAIMACAE (legacy) | ctaimacae.net | Twind (CTAIMA Group) | Nunca objetivo de conector (`ARQUITECTURA-INTEGRACIONES.md` § 5.1) |
| e-coordina | e-coordina.es | Twind (CTAIMA Group) | Empresas aún operando aquí; migración esperada a Twind |
| Metacontratas | metacontratas.com | — | |
| CoordinaPlus | adding-plus.com, coordinaplus.net | Addingplus | |
| UCAE | ucae.es | — | |
| Validate | validate.es, validate.network | — | |
| eGestiona | egestiona.com, egestiona.es | — | |
| SmartOSH | smartosh.com | Prevencontrol | Suite HSE |
| EcoGestor | ecogestor.com | Eurofins | Suite HSE |
| Sabentis | sabentis.com, quironprevencion.com | — | ⚠️ dominio compartido con Quirón ⇒ multi-match |
| Unifikas | unifikas.com | — | |
| Quirón Prevención | quironprevencion.com | Quirónprevención | ⚠️ dominio compartido con Sabentis ⇒ multi-match |
| Previntegral | previntegral.com | — | |
| Norprevención | vithas.es | Vithas | Canales migrados tras integración empresarial |
| Ergasia | ergasia.es + subdominios de cliente | — | Match por sufijo |
| Valora | valoraprevencion.es + subdominios de cliente | — | Match por sufijo |
| PlayCAE | playcae.com + subdominios de cliente | — | Match por sufijo |
| DocuPRL | (sin dominio genérico) | — | Solo subdominios de cliente ⇒ alta manual |
| Arch | archbus.com | — | Foco real: mantenimiento de activos — sembrar inactivo |
| Opground | opground.com | — | Foco real: reclutamiento — sembrar inactivo |

- **Estado (Lote 2-A)**: hecho, 2026-08-07. `ProveedorPlataformaCae` + `DominioProveedorPlataformaCae`
  (Domain, catálogo global — extiende `Entity` directamente, como `Tenant`, no `EntidadBase`:
  sin `TenantId`, sin soft delete, `Activo` en su lugar). Migración `AgregarCatalogoProveedoresPlataformaCae`
  siembra los 23 proveedores y 27 dominios de la tabla de arriba con `HasData` (mismo patrón
  que `TipoDocumentoSeedData`, Id deterministas). `IResolucionProveedorPlataformaCaeService`
  (Application) resuelve una URL/host contra el catálogo — coincidencia exacta o por sufijo de
  subdominio, multi-match cuando dos proveedores comparten dominio (caso real Sabentis/Quirón
  Prevención) — con la lógica de matching extraída a un método estático puro
  (`ResolucionProveedorPlataformaCaeService.Resolver`, mismo patrón que
  `ObtenerBandejaGestorQueryHandler.Fusionar`) para no depender de EF Core en el test. Un bug
  real de la primera versión del `DerivarIdDominio` de la migración (dos proveedores
  consecutivos —…001 y …002— colisionaban al mismo Id de dominio para el mismo índice) se
  detectó al generar la migración, no en producción — corregido antes del primer commit.
  Verificado con 357+236+13+325 tests (Domain/Application/Architecture/Integration, todos en
  verde) y `dotnet ef migrations has-pending-model-changes` limpio. **Sin UI todavía**: no hay
  pantalla de administración del catálogo ni selector en `CanalGestionDocumental` — llegan con
  el Lote 2-B, cuando haya un consumidor real que los necesite (YAGNI); por eso este lote no
  llevó verificación end-to-end en navegador (nada cambia en la UI).
- **Estado (Lote 2-B)**: hecho, 2026-08-07. `CanalGestionDocumental.NombrePlataforma`
  (texto libre) sustituido por `ProveedorPlataformaCaeId` (Guid?, FK simple al catálogo global —
  sin componer con `TenantId`, a diferencia de la FK a Centro). Migración
  `MigrarCanalGestionAProveedorPlataformaCae`: añade la columna, hace un backfill por
  coincidencia exacta de nombre (recortado, sin distinguir mayúsculas) contra
  `ProveedoresPlataformaCae.Nombre` **antes** de tirar la columna vieja — sin inventar
  coincidencias por similitud parcial; lo que no matchea exacto queda `NULL`, resoluble luego
  por URL o a mano. `CrearCanalGestionCommand`/`EditarCanalGestionCommand` cargan y validan el
  Id contra el catálogo antes de usarlo (regla de CLAUDE.md); un canal de Plataforma sin
  proveedor resuelto no puede guardarse. **Decisión de producto tomada con el propietario
  antes de construir la UI**: sin alta inline de proveedores nuevos en el selector — el
  catálogo es global y curado por producto (`docs/MULTITENANCY.md` § 7, mismo criterio que
  Roles), no se amplía desde un formulario de tenant; si falta un proveedor real es un cambio
  de producto (migración nueva), no un botón en la UI.
  **UI resuelta por URL, no por selector manual de entrada** (petición explícita del
  propietario, para evitar que el gestor confunda el subdominio local del cliente con la
  plataforma real): al perder el foco el campo "URL de acceso" se llama a
  `IResolucionProveedorPlataformaCaeService.ResolverPorUrlAsync` — 1 candidato se
  auto-selecciona (badge de confirmación + "¿No es correcta? Elegir manualmente"); 0 o varios
  candidatos abren un `SelectorEntidad` sin `PermiteCrear` (solo los candidatos si hubo
  multi-match, el catálogo completo si no hubo ninguno). Antes de resolver, no se muestra
  ningún selector de plataforma — el único control manual es el tipo de canal
  (Plataforma/Correo), como pidió el propietario. Un canal migrado sin match automático
  reintenta la resolución contra su URL ya guardada al abrir "Editar", antes de caer al
  selector manual. Documentado como patrón nuevo en `UX_PATTERNS.md` § "Resolución automática
  de proveedor desde URL". Verificado con las 4 suites (358 Domain/236 Application/13
  Architecture/44 tests de aislamiento del bloque afectado, todos en verde) y
  `dotnet ef migrations has-pending-model-changes` limpio.

### (b) Entidad `AcreditacionDocumentoPlataforma`

Documento × plataforma, estados: **Pendiente de subir / Subida (en validación) / Aceptada /
Rechazada / No requerida**. El estado Rechazada exige **siempre** causa tipificada (Ilegible,
Documento equivocado, Datos erróneos, Caducado al presentar, Falta firma/sello, Formato no
admitido, Otro) + **motivo literal de la plataforma** (texto libre). Invariantes: renovar
Documento ⇒ acreditaciones a "Pendiente de subir"; los rechazos anteriores se conservan como
**historial**, nunca se sobreescriben. Qué plataformas aplican a un documento se deriva de la
operación real (trabajador → asignaciones activas → centros → canal; documento de Empresa →
centros con actividad de esa empresa — reutilizar la query de `EmpresaWorkspacePanel`). Alta
del término "Acreditación" en `UBIQUITOUS_LANGUAGE.md`; **jamás "Incidencia"** para lo
documental (colisión real, `INBOUND_DOMAIN_GLOSSARY.md`).

### (c) Superficies de edición

Badges por plataforma en la lista de Documentos y en `PestanaDocumentacion`
(Trabajador/Empresa) con edición manual del estado. En **Eliminar y Renovar** de un Documento
con acreditaciones: preguntar si es a raíz de un rechazo y capturar causa+motivo antes del
soft delete. Historial de rechazos visible en el panel del Documento.

### (d) Pestaña de acreditación en el Centro

"De lo que este centro exige, qué está acreditado en su plataforma."

### (e) Dashboard y uso por plataforma

Tarjeta "Pendiente por plataforma" + vista de uso (centros/empresas/pendientes por
proveedor) — insumo de la decisión de conector, que sigue rigiéndose por
`ARQUITECTURA-INTEGRACIONES.md` § 5.1 (Twind primero).

### (f) Registro de la decisión

Hecho — `docs/business/DECISION_LOG.md`, entrada 2026-08-05 (commiteada junto a este plan).

### (g) Query de calidad documental

Agregación "rechazos por causa × plataforma × Empresa" (solo query, sin pantalla nueva) —
insumo del futuro KPI de calidad (¿falló Hydra o el documento vino mal de origen?) y de la
reclamación saliente con motivo precargado.

### (h) Acción "Migrar a [plataforma]"

Sobre **cada acceso de plataforma del Centro** (ver (0.6), N por centro): repunta ese acceso
al proveedor destino con su nueva URL, opción de **conservar o sustituir credenciales** (caso
típico: solo cambia el link), y pregunta "¿la plataforma destino migró la documentación
presentada?" — Sí ⇒ transferir estados de acreditación; No ⇒ todo a "Pendiente de subir". Las
acreditaciones de la plataforma origen quedan como historial. Cada migración persiste un
registro (Centro, acceso concreto, Cliente, origen→destino, fecha, quién, qué se conservó) y
se deja preparada la query "migraciones por plataforma destino × periodo" — inteligencia
interna para priorizar conectores. **Límite**: "migrar" = repuntar el acceso y re-etiquetar
acreditaciones en Hydra; nunca mover documentación entre plataformas (eso es Fase 2
"Orquestador") — el copy debe dejarlo claro.

## Límites

- El resto del Horizonte 2 de `ROADMAP-UX.md` (reclamación saliente, reportes parametrizados,
  bandeja agregada, agregados SQL) requiere petición explícita del propietario.
- Nada de conectores/scraping contra plataformas externas — Fase 2, fuera de este alcance.
- Evaluaciones se retira (0.5) como decisión ya tomada — no reabrir el debate; si en el futuro
  hiciera falta un juicio de campo manual distinto del % documental, es una decisión nueva.
- (0.2) — el tercer nivel colapsable (documentación por trabajador dentro del centro) se marca
  explícitamente como **a validar en implementación**: si la densidad visual no aguanta un
  tercer nivel, degradar a un resumen ("7/9 al día") con enlace a la ficha del trabajador en
  vez de expandir inline — decisión de la sesión que lo construya, con captura de ambas
  opciones para decidir.

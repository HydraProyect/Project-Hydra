# Design System 3.2 — CAE Manager (identidad ProjectHydra)

## Estado de este documento

Este documento es la fuente de verdad de identidad visual y tokens para **todo** el sistema desde el día uno. El catálogo de componentes, en cambio, se documenta en detalle **a medida que cada componente se implementa de verdad** — especificar hoy el Do/Don't/accesibilidad/responsive de 30 componentes que no existen todavía es trabajo especulativo que se desactualiza antes de usarse (YAGNI). La Fase 0 (ver `ROADMAP.md`) implementó y documentó aquí el primer conjunto real; el resto se añade componente a componente conforme se construye.

En 2026-07 el sistema se realineó con la identidad de marca de ProjectHydra (la empresa que opera CAE Manager) — ver el bloque "Historial" al final de este documento para qué cambió y por qué.

## Filosofía

Ver `PROJECT.md` para la filosofía de producto completa. En términos visuales: simplicidad sobre decoración, espacio en blanco generoso, jerarquía clara, consistencia absoluta entre pantallas, accesibilidad primero, color usado con intención (nunca decorativo), animación sutil que comunica estado — nunca decoración. El objetivo es un producto atemporal y funcional, no una moda visual pasajera. Ante la duda, la solución más simple gana.

Referencia de mezcla: ~60% identidad corporativa de ProjectHydra (profesional, humana, confiable), ~20% estructura de información al estilo Stripe Dashboard, ~10% minimalismo/espaciado de Notion, ~10% pulido/microinteracciones de Linear. El resultado debe sentirse inmediatamente como un producto de ProjectHydra que cumple estándares modernos de SaaS — nunca "startup experimental".

## Identidad visual

### Color

Identidad propia (ver Historial — realineación 2026-07, salida de la paleta heredada de una marca externa): CAE Manager habla de seguridad, control, cumplimiento y documentación crítica, no de creatividad ni de SaaS genérico. Inspiración: ingeniería, acero, planos, documentación técnica — nunca fintech, nunca salud, nunca marketing. Paleta neutra cálida + acento primario (azul acero) + secundario (slate) + un acento puntual (cobre) + colores semánticos para los semáforos de vigencia documental (el patrón visual más importante del producto, dado que todo el negocio gira en torno a vigente/próximo/urgente/vencido — **estos tres no forman parte de la identidad libre, su mapeo verde/ámbar/rojo es fijo**).

```
color.primary.50   #EEF5FF
color.primary.100  #DCEBFF
color.primary.300  #8EC0FF
color.primary.500  #2F6FDD   ← acento de marca — azul acero, no azul SaaS brillante — modo claro
color.primary.600  #235BC2   ← hover — modo claro
color.primary.700  #1E4A9E   ← activo/pressed — modo claro

color.primary.500  #5CA2F4   ← acento de marca — modo oscuro (más claro que en modo día: el acero #2F6FDD pierde contraste sobre fondo oscuro)
color.primary.600  #8EC0FF   ← hover — modo oscuro (se aclara, no se oscurece: oscurecer más reduce visibilidad contra un fondo ya oscuro)
color.primary.700  #BDD9FF   ← activo/pressed — modo oscuro

color.secondary.50   #F1F4F7
color.secondary.100  #E3E8EE
color.secondary.300  #AEB9C7
color.secondary.500  #475569  ← acento secundario — slate profundo (usos puntuales, nunca sustituye al primario)
color.secondary.600  #35404F

color.accent.50   #FBF1E7
color.accent.500  #C97B2A   ← cobre — únicamente detalles puntuales (subrayado, ícono destacado). Nunca color de acción ni de estado.
color.accent.600  #A9631E

color.neutral.0    #FFFFFF   ← superficie
color.neutral.50   #FAFBFC   ← fondo de página (cálido, no gris de librería genérica)
color.neutral.100  #F4F6F8
color.neutral.200  #E8EDF2   ← borde
color.neutral.300  #D5DCE5
color.neutral.500  #738196   ← texto secundario
color.neutral.700  #404D60
color.neutral.900  #161E27   ← texto principal

color.success.500  #22C55E   ← VIGENTE
color.warning.500  #F59E0B   ← PRÓXIMO (ámbar, ≤30 días)
color.danger.500   #EF4444   ← URGENTE / VENCIDO (rojo, ≤15 días o vencido)
color.info.500      = color.secondary.500 (reutilizado; el brief no define un color de info separado)
```

Reglas:
- El color de semáforo (`success` / `warning` / `danger`) se usa **exclusivamente** para estado de vigencia documental y sus KPIs asociados en Dashboard — nunca como color decorativo en otro contexto, para que el usuario lo reconozca instantáneamente en cualquier pantalla. Es la única parte de la paleta que no se toca en un rediseño de identidad. Único modificador admitido sobre uno de los tres: **"vigente con riesgo en ventana"** (Centro 360, `PLAN-EJECUCION-UX.md` § 0.3) — un documento `Vigente` hoy que caducaría antes de que termine la próxima visita programada al centro gana un borde `warning.500` encima del badge `success` (clase `.badge-riesgo-ventana-visita`, `list-page.css`). No es un cuarto color de estado ni cambia `EstadoDocumento`: sigue siendo 100% derivado, solo cambia la fecha de referencia de `CalculadoraEstadoDocumento` (hoy vs. fin de la visita) para decidir si se pinta el borde.
- El azul primario se reserva para acciones e interacción (botones primarios, enlaces, foco, estado activo de navegación) — nunca para grandes bloques de color sólido decorativo.
- El cobre (`accent`) es exclusivamente para detalles puntuales de bajo tráfico visual — nunca botones, nunca estados, nunca un segundo color de acción compitiendo con el primario.
- Blanco/neutro cálido es el fondo dominante de la interfaz. Nunca a rendition de color como único portador de información (ver Accesibilidad).

Modo oscuro: **identidad propia, no una inversión automática del claro** — fondo `#0E141B` con 3 escalones de superficie (`#17212C` / `#202B36` / borde `#293644`), nunca negro puro. Cada token tiene su contraparte `dark` (se define en CSS con `prefers-color-scheme` + `[data-theme='oscuro']`); no se documentan valores duplicados aquí, se derivan del mismo sistema de tokens — ver `tokens.css`.

### Tipografía

Fuente: **Inter** (variable), fallback a system-ui. Una sola familia tipográfica en todo el sistema — ninguna pantalla usa una fuente distinta.

```
H1 / heading.xl   36px / 44px / 700   — títulos de página
H2 / heading.lg   30px / 38px / 700   — títulos de sección
H3 / heading.md   24px / 32px / 600   — títulos de card/modal
H4 / heading.sm   20px / 28px / 600   — subtítulos, cabeceras de bloque
Body / body.lg    16px / 24px / 400   — cuerpo destacado
Small / body.md   14px / 20px / 400   — cuerpo estándar (tablas, formularios) — tamaño base de la app
Caption / body.sm 12px / 16px / 500   — metadatos, timestamps
label.md          13px / 16px / 600   — labels de formulario, uppercase opcional en badges
```

La app usa **Small (14px)** como cuerpo base, no Body (16px) — es una convención deliberada de dashboard denso en información (mismo criterio que Stripe/Linear), no una desviación del brief: la escala H1–H4/Body/Small/Caption completa está disponible y se usa donde corresponde (KPIs grandes, títulos, metadatos).

### Espaciado

Escala de 8px (con medios pasos de 4px donde hacen falta), nombrada por múltiplo de 4px:

```
space.1  4px   space.2  8px   space.3  12px   space.4  16px
space.5  20px  space.6  24px  space.8  32px   space.10 40px
space.12 48px  space.16 64px  space.24 96px
```

Layouts respiran: ante la duda entre aumentar o comprimir espaciado, se aumenta.

### Radius, borde, elevación

```
radius.sm        6px    — chips, elementos pequeños
radius.md         10px  — botones, inputs
radius.card-sm    12px  — tarjetas pequeñas / KPI
radius.lg         16px  — tarjetas principales, diálogos, drawers
radius.full       9999px — avatares, pills, badges

border.default  1px solid color.neutral.200
border.focus    2px solid color.primary.500 (siempre visible; nunca el único indicador — se refuerza con un glow sutil)

shadow.card     0 4px 14px rgba(22,30,39,.07)   — casi invisible, crea profundidad sin llamar la atención
shadow.overlay  0 20px 50px rgba(22,30,39,.16)  — modales/drawers, algo más presente
```

Nunca esquinas totalmente rectas en componentes interactivos o contenedores de contenido.

### Motion

Transiciones sutiles, nunca decorativas — comunican estado, no llaman la atención:

```
transition.fast  150ms ease  — hover de icono/color
transition.base  200ms ease  — hover de botón/campo, elevación de tarjeta
transition.slow  250ms ease  — entrada/salida de toast, overlays
```

Incluye: elevación sutil en hover de botón, transición de color en foco/hover, toasts con fade+slide de entrada, skeleton loading (shimmer) en estados de carga. Nunca animación que retrase la percepción de velocidad.

### Micro-interacciones (catálogo de animación)

Set de 7 patrones de animación explorados y confirmados para dar al front una sensación más premium ("look fluido") — los 7 implementados. Todos son CSS puro (keyframes/transitions) salvo el ripple del botón y el buscador global, que usan un único listener JS global delegado en `document` (ver más abajo) — nunca JS interop por instancia.

**Implementados:**

1. **Botón con rebote elástico (spring) + ripple con posición real de clic** (`Boton.razor.css` + `wwwroot/js/microinteracciones.js`). El `transform` en hover/active usa `cubic-bezier(0.34, 1.56, 0.64, 1)` (overshoot ligero) en vez de un ease lineal. El ripple (`<span class="boton-ondulacion">`) sí sigue el punto exacto de clic: un único `document.addEventListener('click', ...)` delegado (con `.closest('.boton')`) calcula `clientX/clientY` relativos al botón y crea el `<span>` en esa posición — patrón tomado del reference `commandpalette.js` aportado por el usuario. Se eligió un listener global en vez de JS interop por instancia porque `Boton.razor` se renderiza decenas de veces por página (tablas, formularios); un módulo por botón habría ido en contra del propio objetivo de fluidez, y el listener delegado tiene coste ~cero por instancia adicional. `prefers-reduced-motion` lo desactiva.
2. **Revelado escalonado** (`.stagger-item` en `list-page.css`, aplicado también a `.tabla-datos tbody tr` y a las tarjetas KPI del Dashboard). Cada fila/tarjeta entra con `animation-delay` creciente por `nth-child` (0–275ms) — sin JS, sin índice explícito por elemento. Blazor reutiliza las filas existentes al paginar o filtrar en vivo (mismo nodo DOM, solo cambia el texto), así que el efecto se ve en la carga inicial de una pantalla, no se repite en cada tecla de un buscador (repetirlo ahí sería ruido, no pulido).
3. **Toast con barra de progreso que se autodescarta** (`AnfitrionToasts.razor`/`.css`). Barra `scaleX(1)→scaleX(0)` sincronizada con `ToastService.DuracionAutoDescarte` (5s) vía `animation-duration` inline — una sola fuente de verdad para la duración, no un valor repetido en CSS y en C#. Los toasts de error no la llevan porque no se autodescartan.
4. **Command palette (buscador global, ⌘K/Ctrl+K)** — ya existía (`BuscadorGlobal.razor`, Fase 6/23, con debounce de 250ms y búsqueda real vía `BuscarGlobalQuery` sobre Cliente/Empresa/Subcontrata/Centro/Trabajador). Esta ronda añadió navegación con ↑/↓ + Enter (índice plano sobre las 5 categorías, coincide con el orden de renderizado), resaltado del elemento seleccionado, pie con los atajos visibles, y una entrada con overshoot sutil (`cubic-bezier(0.22, 1, 0.36, 1)`) en vez de un fade plano.
5. **Bordes muy redondeados / sombras suaves** — no es una animación, pero es la misma ronda de decisión: `radius.*` sube un escalón completo (ver tabla arriba) para un look más "fluido"; `shadow.card`/`shadow.overlay` no cambian (ya eran suaves y extendidas desde la realineación de 2026-07).
6. **Gradient mesh animado en tarjetas destacadas** (`Dashboard.razor.css`, confirmado por el usuario limitado a las tarjetas KPI del Dashboard — no al resto de la app operativa). Dos `radial-gradient` de opacidad muy baja (8–10%, vía `color-mix(in srgb, var(--color-primary-500) 10%, transparent)`) en un `::before` detrás del contenido, con `translate`/`scale` oscilando en 16s (`@keyframes flotar-gradiente`). Vive en `z-index: 0` con la etiqueta/valor de la tarjeta en `z-index: 1`, para que el borde de color semántico (vencido/urgente/próximo/vigente) siga siendo el portador principal de significado — el gradiente es textura, no señal. `prefers-reduced-motion` lo desactiva.
7. **Glow sutil en hover de enlaces** (`base.css`, `a:hover { text-shadow: ... }`, confirmado "muy sutil solo en hover de enlaces" — no en títulos estáticos). `color-mix(in srgb, var(--color-primary-500) 35%, transparent)` con 12px de blur, excluido explícitamente en `.nav-item`/`.buscador-item` (que ya tienen su propio tratamiento de hover por fondo, no por texto, y duplicar el efecto ahí sería ruido). `prefers-reduced-motion` lo desactiva.

Fuera de este catálogo de animación (feature de producto distinta, no micro-interacción, pendiente): un **widget de notificaciones diarias** — el usuario confirmó que lo quiere en el roadmap pero no para esta ronda ("no por ahora pero añádelo... pendiente de definir bien"). No tiene equivalente en el dominio actual de CAE Manager (el prototipo adjunto usa `TareaPendiente`, una entidad que no existe aquí) — ver `ROADMAP.md` → "Backlog pendiente" para el punto de seguimiento; necesita su propia decisión de producto (¿qué notifica? ¿vencimientos próximos, tareas manuales, ambos? ¿reinicio diario o histórico?) antes de diseñarse.

### Iconografía

Un único set de iconos **outline** en todo el sistema (trazo 1.75px, sin relleno, esquinas y remates redondeados — misma convención visual que Lucide/Heroicons). Implementado como componente `Icono.razor` con SVG inline (sin dependencia de una librería externa ni de una fuente de iconos). Nunca mezclar con emojis ni con un segundo estilo de icono en la misma pantalla.

## Catálogo de componentes

### Implementados

| Componente | Archivo |
|---|---|
| Boton | `src/CaeManager.Web/Components/DesignSystem/Boton.razor` |
| Badge | `src/CaeManager.Web/Components/DesignSystem/Badge.razor` — `Tamano="TamanoBadge.Pequeno"` para la densidad de fila de las listas (Centro 360 § 0.9). **Don't**: usarlo para "quitar peso" a un estado — solo cambia padding y tipo, el color del semáforo es el mismo y significa lo mismo. |
| BarraHerramientasLista | `src/CaeManager.Web/Components/DesignSystem/BarraHerramientasLista.razor` — controles que actúan sobre la lista entera (toggle de selección múltiple, expandir/colapsar todos), bajo la barra de filtros. Compartido por las 9 listas: el patrón está en `UX_PATTERNS.md` § "Selección múltiple tras toggle". |
| Icono | `src/CaeManager.Web/Components/DesignSystem/Icono.razor` |
| Tarjeta / TarjetaMetrica | `src/CaeManager.Web/Components/DesignSystem/Tarjeta.razor`, `TarjetaMetrica.razor` |
| CampoTexto / CampoSelect / CampoTextarea | `src/CaeManager.Web/Components/DesignSystem/CampoTexto.razor`, `CampoSelect.razor`, `CampoTextarea.razor` |
| EstadoVacio | `src/CaeManager.Web/Components/DesignSystem/EstadoVacio.razor` |
| EstadoCargando (skeleton) | `src/CaeManager.Web/Components/DesignSystem/EstadoCargando.razor` |
| Toast (ToastService + AnfitrionToasts) | `src/CaeManager.Web/Components/DesignSystem/ToastService.cs`, `AnfitrionToasts.razor` |
| Modal / DialogoConfirmacion | `src/CaeManager.Web/Components/DesignSystem/Modal.razor`, `DialogoConfirmacion.razor` |
| Drawer | `src/CaeManager.Web/Components/DesignSystem/Drawer.razor` |
| SelectorMultiple (checkboxes con buscador + paginación + "solo relacionados") | `src/CaeManager.Web/Components/DesignSystem/SelectorMultiple.razor` |
| FiltroEstado | `src/CaeManager.Web/Components/DesignSystem/FiltroEstado.razor` (+ `OpcionEstado.cs`) — filtro de estado de una lista, en la barra de filtros y con las opciones de peor a mejor. Las opciones concretas las aporta el `Estado*Ui` de cada entidad, que ya es el único sitio donde un estado se traduce a texto y color; el componente solo fija el sitio, la opción "Todos" y el microcopy. |
| IndicadorPasos | `src/CaeManager.Web/Components/DesignSystem/IndicadorPasos.razor` (+ `PasoDefinicion.cs`) — stepper genérico, hasta ahora inexistente (los 3 flujos de importación lo hacían a mano con `@if` sobre estado local). Solo indica progreso (activo/completado, opcionalmente clicable con `PermitirVolver`); el contenido de cada paso lo decide quien lo usa. Primer consumidor: `/clientes/alta-guiada`. |
| SelectorEntidad | `src/CaeManager.Web/Components/DesignSystem/SelectorEntidad.razor` — selector con búsqueda que, a diferencia de `CampoBuscarSelect` (basado en `<datalist>`), pinta su propia lista y puede ofrecer una fila de acción "+ Crear «texto»" cuando `PermiteCrear` y lo escrito no coincide con ninguna opción. Sin JS interop: las opciones seleccionan con `@onmousedown:preventDefault` para que el input nunca pierda el foco al hacer clic, evitando la carrera con el blur del input sin necesitar temporizadores. |
| Pestanas (Tabs) | `src/CaeManager.Web/Components/DesignSystem/Pestanas.razor` (+ `PestanaDefinicion.cs`) — patrón ARIA Tabs con activación manual (mover el foco con flechas no cambia de pestaña; hay que confirmar con Enter/Espacio, para no disparar la carga de cada pestaña solo por hojear con teclado). Sin dependencia del Context Workspace. |
| BarraAccionesLote | `src/CaeManager.Web/Components/DesignSystem/BarraAccionesLote.razor` — barra flotante al seleccionar filas; no dispara el Command ella misma, cada página confirma antes de llamarlo. `AccionesExtra` (`RenderFragment?`, Fase 87) es un hueco aditivo entre el contador y "Cancelar"/"Eliminar" para acciones de lote propias de una pantalla (p. ej. "Asignar a centro…" en `/trabajadores`) — opcional, los consumidores que no lo pasan no cambian. |
| PanelResolverItem | `src/CaeManager.Web/Features/Bandeja/Components/PanelResolverItem.razor` — una tarjeta = un ítem de la Bandeja del gestor (Fase C): badge de tipo, título/subtítulo, fecha y una única acción primaria (`AccionesBandeja.AbrirAsync`, compartida entre `/bandeja` y el panel montado en `/alertas` para que ambos abran exactamente el mismo sitio). No es genérico del Design System — depende de `ItemBandejaDto` (Application) y de `ContextWorkspaceService`, vive en `Features/Bandeja/` a propósito. |
| AtajosGlobales | `src/CaeManager.Web/Features/AtajosGlobales/AtajosGlobales.razor` (+ `CatalogoAtajos.cs`, `wwwroot/js/atajos-globales.js`) — atajos tipo Linear (Fase D): `g`+letra navega, `n` crea en la página actual si la soporta, `?` abre el chuleta modal. Un único listener de `keydown` a nivel de documento, montado una vez en `MainLayout` (no por página) — mismo patrón JS-interop que `buscador-global.js`/`atajos-lista.js`. `CatalogoAtajos` es la fuente de verdad única de destinos y texto del chuleta. |
| Layout (barra lateral + barra superior) | `src/CaeManager.Web/Components/Layout/MainLayout.razor`, `NavMenu.razor` |
| DataTable | Se adopta `Microsoft.AspNetCore.Components.QuickGrid` (oficial de .NET) en vez de reimplementar ordenamiento/paginación — se tematiza con la clase compartida `tabla-datos` (`wwwroot/css/list-page.css`), consumida tanto por QuickGrid como por tablas HTML simples. |
| SeccionColapsable (Accordion) | `src/CaeManager.Web/Components/DesignSystem/SeccionColapsable.razor` — cabecera con título + contenido opcional (Badge de estado) + chevron, colapsada por defecto. |
| ZonaSoltarArchivo (File Upload) | `src/CaeManager.Web/Components/DesignSystem/ZonaSoltarArchivo.razor` — arrastrar/soltar/pegar, envuelve `InputFile` sin tocar su pipeline (ver `wwwroot/js/zona-soltar-archivo.js`); generaliza el patrón de subida de PDF que antes vivía suelto en Documentos/Requisitos/Subida Masiva. |
| AnilloCumplimiento (Progress circular) | `src/CaeManager.Web/Components/DesignSystem/AnilloCumplimiento.razor` (+ `AnilloCumplimientoEnums.cs`) — SVG inline (`stroke-dasharray` sobre un `<circle>`), sin dependencia externa. Recibe `int? Porcentaje` y no pinta nada si es `null` ("sin requisitos" no es un 0%, Centro 360 § 0.5/0.8). Tono propio por umbral (100% Exito, ≥50% Advertencia, resto Peligro) — no reutiliza `EstadoCentroUi`/`EstadoDocumentoUi`: esos traducen el peor caso documental, esto una fracción, son preguntas distintas. Usado en Centro (`Centros.razor`), Trabajador dentro de un Centro (`AcordeonAsignacionesCentro.razor`) y Empresa (`EmpresaWorkspacePanel.razor`, cálculo agregado nuevo en `ObtenerCumplimientoEmpresaQuery`). **Don't**: interpolar un `double` directamente en un atributo SVG — sale con la coma decimal de la cultura de la petición (`r="15,5"`), el navegador lo descarta y el aro no se dibuja (bug real, corregido en el Lote 0-E). Todo número que vaya a un atributo SVG se formatea en `InvariantCulture`. |

Documentación detallada (Do/Don't/accesibilidad) pendiente de completar por componente a medida que se usan en más de un contexto — ver nota al inicio de esta sección.

**Attribute splatting** (P2 #28 de `docs/business/MATURITY_REVIEW.md`): patrón establecido en Boton/Badge/CampoTexto — `[Parameter(CaptureUnmatchedValues = true)] public Dictionary<string, object>? AtributosAdicionales { get; set; }` capturado en el elemento raíz con `@attributes="AtributosAdicionales"`. Blazor **no** combina automáticamente un `class`/`style` splatteado con uno explícito del elemento — el último atributo emitido gana entero (reemplaza, no concatena), a diferencia de lo que la posición sugeriría; verificado con un test que falló en CI al asumir lo contrario. Por eso `class` se saca de `AtributosAdicionales` y se combina a mano (`ClaseCompleta`/`AtributosSinClase` en cada componente) — el resto de atributos sí puede pisar el valor por defecto del componente si hace falta. Deja pasar `aria-*`/`data-*`/cualquier atributo HTML no reconocido sin tener que añadir un `[Parameter]` nuevo por cada caso. El resto de componentes de la tabla de arriba todavía no lo tienen — aplicar el mismo patrón (incluido el merge manual de `class`) cuando haga falta pasar un atributo que hoy no admiten, no antes (YAGNI).

Cada uno, al implementarse, se documenta en este archivo con esta estructura fija:

```
### <Componente>
Descripción — qué es y cuándo usarlo.
Uso — ejemplo de código real del proyecto.
Do / Don't
Variantes y estados (incluye estado de carga, error, disabled)
Accesibilidad — rol ARIA, navegación por teclado, contraste verificado
Responsive — comportamiento en breakpoints
```

### Pendientes (se documentan cuando se construyan)

Chip (distinto de Badge — seleccionable/removible), Tooltip, Popover, Tabs, Breadcrumb, Charts (Dashboard hoy no tiene visualizaciones gráficas, solo KPIs numéricos y tablas — pendiente evaluar una librería de gráficos ligera cuando haya una necesidad real de visualización, no antes), DatePicker, Calendar (distinto del módulo Calendario de negocio), Avatar, Dropdown, Timeline, Activity Feed, Filters panel avanzado, Pagination avanzada.

Selector de tema claro/oscuro en la UI: los tokens ya están preparados para modo oscuro (`prefers-color-scheme`, ver `tokens.css`) pero no existe todavía un control visible para forzar el tema — pendiente de una necesidad real de usuario.

Fotografía corporativa (personas reales, luz natural, entornos de oficina auténticos): no aplica a las pantallas actuales de CAE Manager (es una herramienta operativa densa en datos, no un sitio de marketing) — guía reservada para si el producto añade alguna pantalla de bienvenida/marketing en el futuro.

## Accesibilidad (WCAG AA) — no negociable

- Contraste mínimo 4.5:1 en texto normal, 3:1 en texto grande y componentes UI.
- Todo elemento interactivo alcanzable y operable por teclado (Tab, Enter/Espacio, Escape para cerrar overlays, flechas en listas/menús).
- Foco siempre visible (`border.focus`, outline sólido de 2px de alto contraste) — el glow decorativo alrededor del outline es un extra, nunca el único indicador de foco.
- Todo componente con estado (badge de semáforo, icono de alerta) lleva también texto o `aria-label` — el color nunca es el único portador de significado.
- Los iconos son siempre decorativos (`aria-hidden="true"` en el SVG) y van acompañados de texto visible o `aria-label` en el elemento interactivo que los contiene.

## Responsive

Desktop-first: se diseña primero para escritorio/portátil (el uso real es en oficina), y se adapta hacia abajo. Breakpoints:

```
desktop  ≥1280px  — layout completo, sidebar expandido
laptop   1024–1279px — sidebar colapsable
tablet   768–1023px  — sidebar oculto tras trigger, tablas con scroll horizontal
mobile   <768px      — navegación por drawer, tablas → tarjetas apiladas
```

## Historial

- **2026-07 — Realineación de identidad ProjectHydra.** Se actualizó la paleta de color (primario navy `#0D4E89` + secundario teal `#2FB8C6`, reemplazando el azul genérico anterior), la escala tipográfica (H1–H4/Body/Small/Caption explícitos), el sistema de radios (reestructurado en 5 niveles: `sm`/`md`/`card-sm`/`lg`/`full`, antes 3 niveles), sombras (más suaves y extendidas, `0 8px 30px` en vez de sombras de doble capa más duras) y se añadió un token de `motion` explícito. Se introdujo el primer set de iconos outline (`Icono.razor`) — hasta entonces la navegación no tenía iconos. Los **nombres** de las variables CSS existentes se mantuvieron estables (más de 50 archivos ya las consumían); solo cambiaron sus valores, más la incorporación de tokens nuevos donde el spec de marca no mapeaba 1:1 con el sistema anterior. Ver `ROADMAP.md` para el detalle de qué se retocó pantalla por pantalla y qué queda pendiente (Chips, Tooltips, Tabs, Breadcrumbs, Charts, selector de tema).
- **2026-07 — Look más premium: paleta real de ProjectHydra.com + catálogo de micro-interacciones.** `color.primary.500` pasa de `#0D4E89` a `#0A63F6` (modo claro) / `#5B93FF` (modo oscuro, nuevo — antes el primario no tenía variante de tema oscuro) — tomado de la identidad real de ProjectHydra.com, no de una elección genérica. La escala de `radius` sube un escalón completo (`sm` 8→10px, `md` 12→14px, `card-sm` 16→18px, `lg` 20→24px) para el look "fluido" pedido. Se añadió la sección "Micro-interacciones (catálogo de animación)" arriba con 5 patrones implementados (botón spring+ripple, revelado escalonado, toast con barra de progreso, command palette con navegación por teclado, radios más pronunciados) y 2 pendientes de confirmación explícita por chocar con la filosofía "color con intención, nunca decorativo" del documento (gradiente de fondo animado, glow de texto). Partió de un CLAUDE.md/brief de diseño que originalmente venía con stack Next.js/Supabase/Prisma/cmdk — no aplica a este repo (ASP.NET Core/Blazor Server); se tradujo la intención visual a los tokens y componentes Razor reales, y no se integraron los modelos `Entidad`/`Documento`/`SearchController` de ese prototipo porque este repo ya tiene equivalentes más completos (Cliente/Empresa/Subcontrata/Centro/Trabajador/Documento + `BuscarGlobalQuery`).
- **2026-07 — Cierre del catálogo de micro-interacciones: gradient mesh, glow y ripple con posición real.** El usuario confirmó los 2 puntos que habían quedado pendientes de confirmación explícita: gradiente animado sí, pero acotado a las tarjetas KPI destacadas del Dashboard (no al resto de la app); glow de texto sí, pero "muy sutil solo en hover de enlaces" (no en títulos). Ambos implementados con `color-mix()` sobre los tokens existentes en vez de valores nuevos hardcodeados — ver detalle en el catálogo de arriba. El usuario también subió `commandpalette.js`/`READMEintegracionblazor.md` del prototipo de referencia, que reveló que el patrón correcto para el ripple con posición real de clic es un único listener JS delegado en `document` (no JS interop por instancia de Blazor) — se creó `wwwroot/js/microinteracciones.js` con ese patrón, sustituyendo el ripple centrado CSS-only de la ronda anterior. El widget de notificaciones diarias queda explícitamente fuera de esta ronda ("no por ahora... pendiente de definir bien") y pasa al backlog de `ROADMAP.md` como feature de producto pendiente de definición, no como ajuste visual.
- **2026-07 — Segunda ronda: patrón de modales/drawers, ajuste fino de tipografía/radios, arquitectura explícitamente diferida.** El usuario compartió una segunda entrega de la misma auditoría externa (patrón "Entity Workspace" y patrón "Modales y Drawers", con especificaciones técnicas exactas) y pidió aplicar todo salvo lo que tocara arquitectura. Aplicado: overlay `rgba(15,23,42,.45)` y stacking `Drawer(1040) < Modal(1050) < Toast(1100)` en `Modal.razor.css`/`Drawer.razor.css`/`AnfitrionToasts.razor.css` (corrige un bug real: un toast con un modal abierto quedaba tapado, z-index 1000 contra 1100); `Modal.razor` gana un parámetro `Ancho` (`AnchoModal.cs`: Pequeño 420/Mediano 520 default/Grande 720) para que futuros modales de solo lectura no fuercen todo a 520px; ajuste fino de tipografía (H2 30→28, H3 24→22, H4 20→18, con el mismo patrón tamaño+8 de line-height que ya tenía la escala) y de `radius.lg` (16→14) para terminar de alinear con los valores exactos de la hoja de tokens de esta ronda. **Explícitamente diferido por tocar arquitectura o dominio, no solo estilo**: ensanchar el Context Workspace a ~90%/1440px (revertiría la decisión cerrada de panel angosto ~480px + tabla compacta pendiente en `PLAN-CONTEXT-WORKSPACE.md` § 12/§ 289 — sigue sin construirse la tabla compacta que esa decisión dejó pendiente); selector de Tenant para cambiar de tenant en la misma sesión (contradice la Tenant Resolution Strategy de `docs/MULTITENANCY.md`, es exactamente lo que diseña — y no implementa todavía — `ADR-004-delegacion-consultoras-cae.md`); módulos nuevos de menú sin agregado de dominio (Evaluaciones, Incidencias, Informes, Integraciones expuesto en nav principal); un modal de "cambiar estado" (Empresa no tiene esa máquina de estados — Activo/Inactivo/Pendiente/Vencido — en `DOMAIN.md`); "Modal de filtros avanzados" (choca con la regla ya escrita en `UX_PATTERNS.md`, "panel de filtros junto a la tabla, nunca oculto"); acciones masivas sobre tablas (ninguna tabla tiene hoy selección múltiple de filas, sería un componente sin consumidor real). El componente `Chip` sigue pendiente del catálogo por el mismo motivo — `UX_PATTERNS.md` describe chips de filtro removibles pero ningún listado los implementa todavía, así que construirlo ahora sería un átomo sin ningún uso real.
- **2026-07 — Identidad propia, salida de la paleta heredada de ProjectHydra; jerarquía visual del Dashboard.** El usuario encargó una auditoría de diseño externa (agente GPT, con el briefing y las capturas reales del sistema como contexto) y decidió aplicar el bloque de tokens/paleta y la reorganización del Dashboard; **no** aplicó la propuesta de fusionar Empresas+Centros ni de sustituir el Drawer por un "Workspace" de pantalla — eso colisiona con `ContextWorkspace.razor` (`src/CaeManager.Web/Components/Workspace/`) y con las decisiones ya cerradas en `PLAN-CONTEXT-WORKSPACE.md` (2026-07-25), que tiene una divergencia doc/código pendiente de auditar (ver `ROADMAP.md`) — no es terreno para tocar sin esa auditoría previa. Cambios aplicados: paleta nueva y propia (`color.primary` azul acero `#2F6FDD`, `color.secondary` slate `#475569`, nuevo `color.accent` cobre `#C97B2A` para detalles puntuales, neutros más cálidos) — el mapeo semántico del semáforo (`success`/`warning`/`danger`) no cambió, sigue siendo la única parte no negociable de la paleta. Modo oscuro reconstruido como identidad propia en vez de inversión del claro (fondo `#0E141B`, 3 escalones de superficie, nunca negro puro). `radius` baja un escalón completo en sentido contrario a la ronda anterior (`sm` 10→6px, `md` 14→10px, `card-sm` 18→12px, `lg` 24→16px) — la identidad de "ingeniería/documentación" pedía menos redondeo, no más. Sombras más contenidas (`shadow.card` de `0 8px 30px .05` a `0 4px 14px .07`). Dashboard reorganizado en dos niveles de KPI (crítico: vencidos/urgentes/próximos/SLA, con el tratamiento visual completo — degradado, borde de color, revelado escalonado — vs. secundario/discreto: trabajadores/centros/vigentes/visitas, sin degradado ni borde de color, valor más pequeño — nuevo parámetro `Discreta` en `TarjetaMetrica.razor`) y con "Documentos que requieren atención" promovido a sección propia justo debajo de los KPI críticos, antes que los gráficos/desgloses secundarios (antes competían en la misma rejilla) — la cabecera pasa de un título estático "Dashboard" a un saludo según la hora del día. Tipografía, espaciado y motion no se tocaron en esta ronda (el diagnóstico externo los daba por correctos).

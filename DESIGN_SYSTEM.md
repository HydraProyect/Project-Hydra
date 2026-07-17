# Design System 3.1 — CAE Manager (identidad ProjectHydra)

## Estado de este documento

Este documento es la fuente de verdad de identidad visual y tokens para **todo** el sistema desde el día uno. El catálogo de componentes, en cambio, se documenta en detalle **a medida que cada componente se implementa de verdad** — especificar hoy el Do/Don't/accesibilidad/responsive de 30 componentes que no existen todavía es trabajo especulativo que se desactualiza antes de usarse (YAGNI). La Fase 0 (ver `ROADMAP.md`) implementó y documentó aquí el primer conjunto real; el resto se añade componente a componente conforme se construye.

En 2026-07 el sistema se realineó con la identidad de marca de ProjectHydra (la empresa que opera CAE Manager) — ver el bloque "Historial" al final de este documento para qué cambió y por qué.

## Filosofía

Ver `PROJECT.md` para la filosofía de producto completa. En términos visuales: simplicidad sobre decoración, espacio en blanco generoso, jerarquía clara, consistencia absoluta entre pantallas, accesibilidad primero, color usado con intención (nunca decorativo), animación sutil que comunica estado — nunca decoración. El objetivo es un producto atemporal y funcional, no una moda visual pasajera. Ante la duda, la solución más simple gana.

Referencia de mezcla: ~60% identidad corporativa de ProjectHydra (profesional, humana, confiable), ~20% estructura de información al estilo Stripe Dashboard, ~10% minimalismo/espaciado de Notion, ~10% pulido/microinteracciones de Linear. El resultado debe sentirse inmediatamente como un producto de ProjectHydra que cumple estándares modernos de SaaS — nunca "startup experimental".

## Identidad visual

### Color

Paleta neutra + un acento de marca primario + un acento secundario + colores semánticos para los semáforos de vigencia documental (el patrón visual más importante del producto, dado que todo el negocio gira en torno a vigente/próximo/urgente/vencido).

```
color.primary.50   #EBF2F8
color.primary.100  #D3E3F0
color.primary.300  #7BA3C7
color.primary.500  #0D4E89   ← acento de marca (acciones primarias, foco, enlaces)
color.primary.600  #0A3E6F   ← hover
color.primary.700  #072C4E

color.secondary.50   #EAFAFB
color.secondary.100  #C7EFF2
color.secondary.300  #7DD9E1
color.secondary.500  #2FB8C6  ← acento secundario (usos puntuales, nunca sustituye al primario)
color.secondary.600  #229CA9

color.neutral.0    #FFFFFF   ← superficie
color.neutral.50   #F8FAFC   ← fondo de página
color.neutral.100  #F1F5F9
color.neutral.200  #E5E7EB   ← borde
color.neutral.300  #D1D5DB
color.neutral.500  #6B7280   ← texto secundario
color.neutral.700  #374151
color.neutral.900  #1F2937   ← texto principal

color.success.500  #22C55E   ← VIGENTE
color.warning.500  #F59E0B   ← PRÓXIMO (ámbar, ≤30 días)
color.danger.500   #EF4444   ← URGENTE / VENCIDO (rojo, ≤15 días o vencido)
color.info.500      = color.secondary.500 (reutilizado; el brief no define un color de info separado)
```

Reglas:
- El color de semáforo (`success` / `warning` / `danger`) se usa **exclusivamente** para estado de vigencia documental y sus KPIs asociados en Dashboard — nunca como color decorativo en otro contexto, para que el usuario lo reconozca instantáneamente en cualquier pantalla.
- El azul primario se reserva para acciones e interacción (botones primarios, enlaces, foco, estado activo de navegación) — nunca para grandes bloques de color sólido decorativo.
- Blanco/neutro es el fondo dominante de la interfaz. Nunca a rendition de color como único portador de información (ver Accesibilidad).

Modo oscuro: cada token tiene su contraparte `dark` (se define en CSS con `prefers-color-scheme`); no se documentan valores duplicados aquí, se derivan del mismo sistema de tokens — ver `tokens.css`.

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
radius.sm        8px    — chips, elementos pequeños
radius.md         12px  — botones, inputs
radius.card-sm    16px  — tarjetas pequeñas / KPI
radius.lg         20px  — tarjetas principales, diálogos, drawers
radius.full       9999px — avatares, pills, badges

border.default  1px solid color.neutral.200
border.focus    2px solid color.primary.500 (siempre visible; nunca el único indicador — se refuerza con un glow sutil)

shadow.card     0 8px 30px rgba(15,23,42,.05)   — casi invisible, crea profundidad sin llamar la atención
shadow.overlay  0 20px 60px rgba(15,23,42,.14)  — modales/drawers, algo más presente
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

### Iconografía

Un único set de iconos **outline** en todo el sistema (trazo 1.75px, sin relleno, esquinas y remates redondeados — misma convención visual que Lucide/Heroicons). Implementado como componente `Icono.razor` con SVG inline (sin dependencia de una librería externa ni de una fuente de iconos). Nunca mezclar con emojis ni con un segundo estilo de icono en la misma pantalla.

## Catálogo de componentes

### Implementados

| Componente | Archivo |
|---|---|
| Boton | `src/CaeManager.Web/Components/DesignSystem/Boton.razor` |
| Badge | `src/CaeManager.Web/Components/DesignSystem/Badge.razor` |
| Icono | `src/CaeManager.Web/Components/DesignSystem/Icono.razor` |
| Tarjeta / TarjetaMetrica | `src/CaeManager.Web/Components/DesignSystem/Tarjeta.razor`, `TarjetaMetrica.razor` |
| CampoTexto / CampoSelect / CampoTextarea | `src/CaeManager.Web/Components/DesignSystem/CampoTexto.razor`, `CampoSelect.razor`, `CampoTextarea.razor` |
| EstadoVacio | `src/CaeManager.Web/Components/DesignSystem/EstadoVacio.razor` |
| EstadoCargando (skeleton) | `src/CaeManager.Web/Components/DesignSystem/EstadoCargando.razor` |
| Toast (ToastService + AnfitrionToasts) | `src/CaeManager.Web/Components/DesignSystem/ToastService.cs`, `AnfitrionToasts.razor` |
| Modal / DialogoConfirmacion | `src/CaeManager.Web/Components/DesignSystem/Modal.razor`, `DialogoConfirmacion.razor` |
| Drawer | `src/CaeManager.Web/Components/DesignSystem/Drawer.razor` |
| Layout (barra lateral + barra superior) | `src/CaeManager.Web/Components/Layout/MainLayout.razor`, `NavMenu.razor` |
| DataTable | Se adopta `Microsoft.AspNetCore.Components.QuickGrid` (oficial de .NET) en vez de reimplementar ordenamiento/paginación — se tematiza con la clase compartida `tabla-datos` (`wwwroot/css/list-page.css`), consumida tanto por QuickGrid como por tablas HTML simples. |

Documentación detallada (Do/Don't/accesibilidad) pendiente de completar por componente a medida que se usan en más de un contexto — ver nota al inicio de esta sección.

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

Chip (distinto de Badge — seleccionable/removible), Tooltip, Popover, Tabs, Breadcrumb, Charts (Dashboard hoy no tiene visualizaciones gráficas, solo KPIs numéricos y tablas — pendiente evaluar una librería de gráficos ligera cuando haya una necesidad real de visualización, no antes), DatePicker, Calendar (distinto del módulo Calendario de negocio), Avatar, Accordion, Dropdown, Timeline, Activity Feed, Filters panel avanzado, Progress, File Upload (ya existe el patrón de subida de PDF en Documentos; falta generalizarlo a componente), Pagination avanzada.

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

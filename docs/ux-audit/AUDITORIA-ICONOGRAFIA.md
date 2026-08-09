# Auditoría de iconografía — inventario, viabilidad Flaticon y animación por interacción

> **Capa de auditoría, no normativa** (DDL-055: la autoridad viene de la posición en la cadena).
> Este documento inventaría lo que existe, señala lo que falta y analiza la viabilidad de dos
> propuestas del propietario del producto: sustituir la iconografía por iconos de diseñador de
> Flaticon (estáticos o animados) y añadir animación por interacción para dar sensación de
> fluidez. **No decide nada**: cualquier cambio de fuente de glifos o de motion exige su entrada
> previa en `DESIGN_DECISION_LOG.md` (DDL-024, DDL-040, `07` § 8). Nada de lo aquí descrito como
> "recomendado" está construido (DDL-023).
>
> Fecha del análisis: 2026-08-09 · Rama: `claude/platform-iconography-audit-2u1vfz`

---

## 1. Inventario de la iconografía existente

### 1.1 El sistema

**No hay ninguna librería de iconos externa.** Ni Bootstrap Icons, ni Font Awesome, ni
Lucide/Heroicons como dependencia, ni CDN, ni fuente de iconos, ni ficheros `.svg` en `wwwroot`
(el único asset gráfico del proyecto es `favicon.png`, 1,1 KB). Toda la iconografía pasa por un
único componente propio:

- `src/CaeManager.Web/Components/DesignSystem/Icono.razor` — catálogo cerrado de **38 iconos**
  SVG inline (viewBox 24, trazo 1,75 px, outline, remates redondeados — la convención de
  `02` § 7), seleccionados por `switch` sobre el parámetro `Nombre` (string).
- `IconoEnums.cs` — `TamanoIcono { Pequeno, Medio, Grande }` → 16/20/24 px (`Icono.razor.css`).
- `aria-hidden="true"` fijo: los iconos son decorativos; el nombre accesible lo aporta quien los
  contiene (`02` § 7, `08` § 4.5).

**Uso**: 85 ocurrencias de `<Icono>` en 23 de 127 ficheros `.razor` (18 %). La concentración es
extrema: `NavMenu.razor` (sidebar) acumula 43 usos — el 51 % del total —, el módulo de
Comunicaciones otros 24, y el resto de la aplicación es prácticamente texto sin iconos.

### 1.2 Catálogo (38 iconos, por categoría)

| Categoría | Iconos |
|---|---|
| Entidades de dominio (14) | `clientes` · `empresas` · `centros` · `subcontratas` · `trabajadores` · `vehiculos` · `documentos` · `tipos-documento` · `proyectos` · `visitas` · `asignaciones` ⚠ · `evaluaciones` ⚠ · `incidencias` · `cartera` |
| Navegación / estructura (7) | `dashboard` · `menu` · `chevron` (una variante, rotada por CSS) · `volver` · `reportes` · `calendario` · `buscar` |
| Estados / avisos (5) | `alertas` (campana) · `advertencia` (triángulo) · `reloj` · `check` · `imagen` (empty state) |
| Acciones (4) | `editar` · `importar` · `enviar` · `cerrar-sesion` |
| Comunicaciones (3) | `chat` · `correo` · `ia` (destello — marca de procedencia, DDL-032) |
| Administración / seguridad (5) | `configuracion` · `usuarios` · `roles` · `seguridad` · `auditoria` |

⚠ `asignaciones` y `evaluaciones` están definidos pero **ningún consumidor los usa** (código
muerto del catálogo).

### 1.3 Deuda técnica del sistema actual

1. **`Nombre` es un string mágico** sin constantes ni enum, escrito literal en 32 sitios y
   compuesto por ternario en 3. El caso por defecto (`_ => <g></g>`) hace que un typo renderice
   un SVG **vacío en silencio**, sin error ni aviso.
2. **Glifos de texto haciendo de icono** en 15+ sitios, inconsistentes con el set outline:
   `×` como cierre en 10 sitios de 8 ficheros (Modal, Drawer, ContextWorkspace, toasts, chips de
   filtro, adjuntos…), `⋯` en `MenuAcciones`, `←`/`→` en `PaginadorSimple`, `/` como separador de
   `Breadcrumb`.
3. **Duplicidad visual**: `auditoria` y `reloj` son casi idénticos (círculo + manecillas) siendo
   entradas distintas.
4. **Sin logo ni assets de marca** más allá del favicon: el branding es puramente tipográfico.

---

## 2. Iconografía faltante (huecos reales, ordenados por prioridad)

| # | Hueco | Superficie | Por qué importa |
|---|---|---|---|
| 1 | **Semáforo documental solo por color** | `Badge.razor` (Éxito/Advertencia/Peligro) | Único hueco con componente de accesibilidad: WCAG 1.4.1 (uso del color). Un icono de tono junto al color resolvería la distinción para daltonismo. `04` § 7.1 ya pide "icono y color semántico coherentes" en avisos |
| 2 | **`EstadoVacio` con slot de icono sin usar** | 189 invocaciones de `<EstadoVacio>`; ninguna rellena su `RenderFragment? Icono` | La mayor oportunidad de aprovechamiento inmediato del sistema existente, sin decisión normativa nueva |
| 3 | **Toasts sin icono de tono** | `AnfitrionToasts.razor` | Solo color + `×` textual (la rama de gamificación añade el trazado al de Éxito; el resto de tonos sigue sin icono) |
| 4 | **Glifos Unicode → set** | Los 15+ sitios de § 1.3.2 | Consistencia de peso y trazo; hoy `×`/`⋯`/`←`/`→` no comparten métrica con el set 1,75 px |
| 5 | **`Boton` sin slot de icono** | `Boton.razor` | Los CTAs con icono lo insertan a mano como `ChildContent` (5 sitios detectados); un parámetro opcional normalizaría el patrón |
| 6 | **Stepper sin check de completado** | `IndicadorPasos.razor` | Círculos numerados con CSS; el paso completado no muestra marca |
| 7 | Tablas/acciones de fila sin iconos | QuickGrid y list-pages | Hueco menor; el texto funciona, pero es superficie natural si el set crece |

---

## 3. Iconografía de gamificación v1 (DDL-068)

Contrastado con el plan real: rama `claude/hydra-gamification-discussion-feg5ob`
(`PLAN-GAMIFICACION-V1.md` + DDL-068, 2026-08-09).

**Lo primero que hay que decir es lo que NO se necesita.** DDL-068 rechaza de forma permanente
—con el mismo carácter que `07` § 7— puntos y niveles, badges, leaderboards entre usuarios o
tenants, rachas/streaks con pérdida y métricas de velocidad. Es decir: **la iconografía "clásica"
de gamificación (medallas, trofeos, llamas de racha, podios, estrellas de nivel) queda
explícitamente fuera** y ningún encargo o compra de iconos debe incluirla — sería comprar
glifos para mecánicas prohibidas.

Lo que gamificación v1 sí necesita es acotado:

| Pieza (DDL-068) | Necesidad iconográfica | Estado |
|---|---|---|
| Trazado de confirmación en toast de Éxito | Marca de check que se dibuja (Tier C, `07` § 6) | Implementado en esa rama como **SVG suelto en `AnfitrionToasts.razor`, fuera del catálogo `Icono.razor`** — inconsistencia a resolver cuando esa rama se integre (podría reutilizar el path de `check` del catálogo) |
| Tarjeta "Pulso del equipo" | Un icono para la tarjeta si se quiere (opcional) — `TarjetaMetrica` ya acepta `NombreIcono` | Candidatos del catálogo: `check`, `visitas`; o un glifo nuevo "pulso" si se decide |
| Estado de cierre verificado en Dashboard | `check` existente | Cubierto |
| Medidor de completitud por contrata/centro | Ya existe `AnilloCumplimiento.razor` (SVG propio, no es icono) | Cubierto — DDL-068 registra que no se duplica |
| Backlog: ranking de cumplimiento de contratas | Si algún día se construye ("puesto 12 de 40"), necesitará iconografía de posición/tendencia (flecha sube/baja) — **no** podio ni trofeo | Backlog, no diseñar aún |

Nota para el análisis de animación (§ 5): el trazado de confirmación es el **único "icono
animado" sancionado por la normativa**, y gamificación v1 es su primer portador. El precedente
demuestra el patrón correcto: una vez por evento, `prefers-reduced-motion` lo muestra ya trazado,
y no se añadió patrón nuevo al catálogo de `07`.

---

## 4. Viabilidad de sustitución por iconografía de Flaticon

### 4.1 Factualidad (verificado 2026-08-09)

- **Estáticos**: Flaticon distribuye SVG, PNG, EPS, PSD y Base64 (SVG/EPS/PSD requieren Premium
  o atribución según el recurso).
- **Animados**: existe un catálogo de ~57.000 iconos animados, descargables como **Lottie JSON,
  GIF, vídeo y proyecto After Effects** (más el SVG estático del mismo glifo).
- **Licencia**: la gratuita exige **atribución visible** ("designed by {autor} from Flaticon")
  en cada proyecto — inviable en un producto SaaS comercial serio. La **Premium**
  (~12,99 USD/mes, ~8,25 USD/mes en anual) elimina la atribución y permite uso comercial, con
  límites de descarga diarios. Para uso en Hydra la vía realista es Premium.
- **Estilo**: gran parte del catálogo es flat/filled o multicolor; existen colecciones lineales
  (outline), pero **no hay garantía de trazo 1,75 px editable** — muchos SVG llevan el trazo
  expandido a relleno, lo que impide ajustar el grosor con `stroke-width` y rompe
  `stroke="currentColor"`.

### 4.2 Lo que dice la normativa vigente

- `02_BRAND_AND_VISUAL_IDENTITY.md` § 7: "un único set outline en todo el sistema… implementado
  como SVG inline, **sin dependencia de librería ni de fuente de iconos**". § 12 declara **cero
  preguntas abiertas** — cambiar el set no es rellenar un hueco, es **reabrir una decisión
  cerrada**.
- Ya existe una decisión previa contra Flaticon: 2026-08-06, `docs/ux-audit/PLAN-EJECUCION-UX.md`
  ("no se usa el paquete de iconos Flaticon mencionado en el mockup original"). No está elevada
  a DDL, pero la autoridad real la da `02` § 7, que dice lo mismo.
- Proceso obligado para cambiar esto: registrar el conflicto y la decisión nueva en
  `DESIGN_DECISION_LOG.md` (DDL-024), y solo entonces tocar `02` § 7 y `08` § 4.5. Prohibido
  contradecir en silencio (DDL-024); prohibido colar la librería "rellenando una celda"
  (DDL-058, tercera forma de contaminación).

### 4.3 Veredicto: dos vías

**Vía A — Flaticon como librería/dependencia (paquete npm/uicons, CDN, fuente de iconos):
no viable sin reabrir la normativa.** Choca frontalmente con `02` § 7 y con la decisión
2026-08-06. Además introduce lo que el sistema evita a propósito: peticiones de red por iconos,
versionado de terceros y mezcla de estilos (hoy: cero peticiones, SVG inline servidor).

**Vía B — licenciar iconos de diseñador y absorberlos en el sistema propio: viable y
recomendada si se quiere elevar la calidad de los glifos.** Consiste en:

1. Suscripción Premium (elimina la atribución; verificar en el momento de la compra que la
   licencia cubre la modificación del SVG y su uso en SaaS comercial — los términos vigentes lo
   permiten para Premium).
2. Seleccionar **una única colección lineal** cuyo estilo case con el set actual (outline,
   remates redondeados) — nunca mezclar colecciones (`02` § 7: un solo estilo).
3. **Normalizar cada SVG** al contrato del catálogo: viewBox 24, `fill="none"`,
   `stroke="currentColor"`, `stroke-width="1.75"`, `stroke-linecap/linejoin="round"`. Esto es
   trabajo real por icono: si el SVG comprado lleva el trazo expandido a relleno, hay que
   redibujarlo o descartarlo.
4. Integrarlos como casos nuevos (o sustituciones) en `Icono.razor`.

La vía B **preserva la regla vigente** ("set único, SVG inline, sin dependencia"): el resultado
no es "usar Flaticon" sino "más/mejores glifos del mismo sistema, con origen licenciado". Aun
así, si sustituye glifos existentes o cambia el estilo percibido, sigue siendo un cambio de
identidad visual → DDL primero (§ 6, paso a).

**Coste/beneficio honesto**: el set actual (38 glifos dibujados a mano con convención
Lucide/Heroicons) es competente y coherente. El beneficio de la vía B no está en sustituir lo que
hay, sino en **cubrir los huecos de § 2 y el crecimiento futuro** (Comunicaciones, gamificación,
integraciones) sin dibujar cada glifo a mano.

---

## 5. ¿Animados o estáticos? Y la animación por interacción

### 5.1 Iconos animados de Flaticon (Lottie/GIF): no viables por defecto

- **Formato**: Lottie exige un player JS (dependencia nueva) o `lottie-web`; GIF/vídeo no
  respetan `currentColor`, ni tokens de tema claro/oscuro, ni escalan como el set. Ambos rompen
  "SVG inline sin dependencia" (`02` § 7).
- **Normativa de motion**: `07` § 2 — la decoración "se rechaza por defecto; no hay prueba que
  pueda pasar". Un icono animado repetido por la interfaz es el caso de libro: "un efecto cuya
  función declarada sea 'énfasis' y que aparezca en más de una pantalla es, casi siempre,
  decoración mal etiquetada". El precedente es directo: DDL-045 retiró el glow de enlaces
  precisamente por "énfasis anulado por repetición".
- **Presupuesto**: aunque un icono animado concreto declarase función real (estado vivo,
  feedback causal), entraría como Tier C: **1–2 usos por pantalla**, nunca un set entero.

Conclusión: **comprar el catálogo animado de Flaticon no tiene encaje**. Si se compra algo, que
sean los SVG estáticos.

### 5.2 La sensación de fluidez, con estáticos: lo que ya permite el sistema

La "fluidez" que busca el propietario del producto no requiere iconos animados; se consigue con
lo que `07` ya cataloga, aplicado de forma consistente:

| Qué | Patrón de `07` § 6 | Tokens | Estado hoy |
|---|---|---|---|
| Color/estado del icono en hover, foco y activo | "Cambio de estado de control" (Tier A, ~120 ms) | `--motion-fast` + `--ease-standard` | **Solo en el sidebar** (`NavMenu.razor.css`: color en hover/activo, `scale(0.97)` en `:active`, rotación del chevron). Extenderlo a botones con icono, `MenuAcciones`, buscador, FAB del asistente es aplicar un patrón **ya catalogado** — no exige decisión nueva, solo consistencia |
| Marca que se dibuja al completar | "Trazado de confirmación" (Tier C) | — | Primer portador: toast de Éxito (rama gamificación). Otros portadores candidatos (validación de documento, sincronización) deben respetar el presupuesto 1–2 Tier C/pantalla y "una vez por evento, nunca al repintar" |
| Proceso en curso | "Pulso de estado vivo" (Tier C, ciclo ~2–2,5 s) | `--motion-live-cycle` | Disponible; prohibido en estados críticos ("la urgencia es del semáforo, no del movimiento", `07` § 6.1) |

Reglas transversales que cualquier aplicación debe cumplir: `prefers-reduced-motion` desactiva
Tier B/C y degrada Tier A a cambio instantáneo (DDL-020); ningún efecto es el único portador de
significado; nada supera 500 ms salvo ciclo vivo.

### 5.3 Lo que exigiría decisión nueva

Cualquier microinteracción icónica que no sea lo anterior — morphing (hamburguesa→X,
campana que oscila al llegar una alerta, sobre que se abre), "wiggle" de atención, icono que
celebra — es un **patrón nuevo**: procedimiento de `07` § 9 completo (declarar función
comunicativa → tier y presupuesto → degradación → tokens existentes → registrar en el Log). El
paso 1 ya filtra la mayoría: si la respuesta honesta a "¿qué comunica?" es "da sensación de
fluidez", eso es decoración y se rechaza (`07` § 2). La campana que oscila al **llegar** una
alerta sí podría defenderse como feedback causal (Tier A, una vez por evento) — pero es una
decisión a registrar, no un default.

---

## 6. Hoja de ruta recomendada (si el propietario del producto decide avanzar)

Nada de esto se ejecuta sin su decisión; el orden importa:

- **(a) Decisión sobre la fuente de glifos** — entrada nueva en `DESIGN_DECISION_LOG.md` que
  resuelva: ¿se mantiene el set propio dibujado a mano, o se adopta la vía B (colección lineal
  licenciada de Flaticon, normalizada al contrato del catálogo)? Debe citar y superar la decisión
  2026-08-06 y, si procede, actualizar `02` § 7. Sin esta entrada, no se compra ni se importa nada.
- **(b) Rellenar los huecos de § 2 con el sistema actual** — no depende de (a): icono de tono en
  `Badge` (prioridad accesibilidad), slot de icono en `EstadoVacio` aprovechado, iconos de tono
  en toasts, sustituir `×`/`⋯`/`←`/`→` por glifos del set (`cerrar`, `mas-opciones`,
  `anterior`/`siguiente`), slot de icono en `Boton`, check de paso completado en
  `IndicadorPasos`. Limpiar o dar uso a `asignaciones`/`evaluaciones`.
- **(c) Si (a) elige la vía B**: compra Premium, selección de una colección lineal, normalización
  SVG por lotes e integración en `Icono.razor` — empezando por los huecos de (b) y las
  necesidades de gamificación v1 (§ 3), no por sustituir lo que ya funciona.
- **(d) Microinteracciones Tier A consistentes** — extender "cambio de estado de control" del
  sidebar al resto de superficies con iconos interactivos (tokens existentes, patrón catalogado).
  Cualquier efecto más allá: `07` § 9 + DDL, uno a uno.
- **(e) Endurecer el catálogo** (técnico, oportunista): constantes/enum para `Nombre` y aviso en
  desarrollo cuando un nombre no exista, para eliminar el fallo silencioso.

---

## Fuentes externas consultadas (Flaticon, 2026-08-09)

- Catálogo animado y formatos: flaticon.com/animated-icons (~57.000 animados; Lottie JSON, GIF,
  vídeo, After Effects).
- Precios y licencia Premium: flaticon.com/pricing · support.flaticon.com ("What are Flaticon
  Premium licenses?") · flaticon.com/legal (atribución obligatoria en licencia gratuita;
  Premium sin atribución, uso comercial, SVG/EPS/PSD/Base64/PNG).

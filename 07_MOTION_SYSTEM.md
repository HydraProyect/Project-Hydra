# 07 — Motion System

**Estado**: Normativo · **Implementado hasta**: parcialmente y con desviaciones. El código actual
contiene el catálogo de micro-interacciones de 2026-07, que **incumple este documento en tres
puntos** (§ 5). Las retiradas y acotaciones son fase de implementación posterior.

**Autoridad**: define **qué movimiento existe, qué comunica y dónde se permite**.
`06_DESIGN_SYSTEM.md` tokeniza las duraciones y curvas que este documento fija (§ 8) — no las
inventa ni las amplía. `04_UX_PATTERNS.md` define cuándo ocurre una interacción; `07` define qué
hace la interfaz mientras ocurre.

**Por qué es un documento aparte**: separado del Design System, el motion se acumula. Cada
componente añade "un detalle" y en unos meses la interfaz es un catálogo de efectos sin que
nadie haya tomado esa decisión. Ha pasado ya una vez (§ 5). Este documento existe para que
añadir movimiento cueste una decisión, no una línea de CSS.

---

## 1. La regla madre

> **El movimiento comunica causalidad.** Responde qué acaba de pasar, qué está pasando o qué ha
> terminado. Si no responde ninguna de las tres, no se añade.

Corolario operativo, heredado del test de `01` § 2: ante cualquier animación propuesta, *¿reduce
trabajo, lo explica, o lo decora?* Solo la tercera necesita justificarse — y en motion, la
tercera se rechaza por defecto.

## 2. Las cinco funciones comunicativas

Todo movimiento de Hydra pertenece a una de estas cinco. Declarar cuál es el primer paso para
aceptarlo o rechazarlo:

| Función | Qué comunica | Prueba de validez |
|---|---|---|
| **Feedback causal** | "Ha pasado esto porque hiciste eso" | Sin él, el usuario duda de si su acción tuvo efecto |
| **Transición espacial** | "Esto viene de aquí y va allí" | Sin él, un elemento aparece o desaparece sin explicación de dónde |
| **Estado vivo** | "Esto está ocurriendo ahora mismo" | Sin él, no se distingue "en curso" de "terminado" |
| **Énfasis** | "Mira aquí" | Excepcional; requiere justificar por qué el orden visual no bastaba |
| **Decoración** | Nada | **Prohibida por defecto.** No hay prueba que pueda pasar |

Regla de revisión: un efecto cuya función declarada sea "énfasis" y que aparezca en más de una
pantalla es, casi siempre, decoración mal etiquetada.

## 3. Los tres tiers (DDL-016)

| Tier | Duración | Función que puede portar | Frecuencia |
|---|---|---|---|
| **A — Operativo** | 120–250 ms | Feedback causal · estado vivo | Siempre disponible; es la base |
| **B — Transición** | 300–500 ms | Transición espacial | Selectivo: cambios de contexto, no cambios de valor |
| **C — Signature** | según el caso | Estado vivo · énfasis | **1–2 usos por pantalla**, presupuestados |

**Curva**: Tier A usa una aceleración estándar. Tier B usa la curva fluida
`cubic-bezier(0.16, 1, 0.3, 1)` — entrada rápida y asentamiento largo, que es lo que hace que un
panel se sienta "colocado" y no "disparado". Tier C define la suya por caso.

**Nada supera los 500 ms** salvo un estado vivo cíclico, que por definición no bloquea nada.

## 4. Presupuesto y límites

- **Presupuesto de Tier C: 1–2 usos excepcionales por pantalla** (DDL-016). Es el único límite de
  cantidad que este sistema declara. Tier A es la base y **no se cuenta**: un resalte de fila, un
  control cambiando de estado y un proceso en curso pueden coexistir sin que eso sea un exceso.
- **El movimiento nunca retrasa la percepción de velocidad.** Si una animación de entrada hace
  que el contenido tarde en ser legible, la animación sobra.
- **Ningún efecto es el único portador de significado.** Si al desactivar el movimiento la
  interfaz deja de entenderse, no era un efecto: era información mal implementada, y hay que
  darle forma estable.
- **`prefers-reduced-motion` desactiva Tier B y Tier C siempre** (DDL-020). Tier A puede
  degradarse a cambio instantáneo, nunca desaparecer: el feedback causal no es opcional.

## 5. Veredicto de los efectos heredados (2026-07)

El catálogo de micro-interacciones de julio se juzga aquí uno a uno, como exige el cierre de
OD-10. Los tres primeros veredictos vienen de DDL-045; los tres siguientes se deciden en este
documento (DDL-054).

| # | Patrón heredado | Función real | Tier | Permitido en | Prohibido en | Veredicto |
|---|---|---|---|---|---|---|
| 1 | **Ripple con posición de clic** | Feedback causal | A | Acciones **primarias** | Botones de fila, acciones secundarias, controles de filtro | **Acotado** (DDL-045) |
| 2 | **Revelado escalonado** de filas y tarjetas | Transición espacial | B | **Primera carga** de una superficie | Repaginar, filtrar en vivo, reordenar | **Conservado, acotado** (DDL-054) |
| 3 | **Toast con barra de progreso** | Estado vivo | A | Avisos autodescartables | Avisos de error, que no se autodescartan | **Conservado** (DDL-054) |
| 4 | **Entrada del buscador global** con asentamiento | Transición espacial | B | Apertura del buscador | Cada pulsación o resultado | **Conservado** (DDL-054) |
| 5 | **Radios y sombras** | — | — | — | — | **No es motion.** Se traslada a `06` (§ 5.1) |
| 6 | **Gradient mesh animado** en tarjetas KPI | Decoración | — | — | Todo | **Retirado** (DDL-045) |
| 7 | **Glow en hover de enlaces** | Decoración | — | — | Todo | **Retirado** (DDL-045) |

### 5.1 Nota sobre el punto 5

Radios y sombras entraron en el catálogo de julio porque salieron de la misma sesión, pero **no
son movimiento**: son propiedades de superficie. Pertenecen a `02` (profundidad) y `06` (tokens).
Mantenerlos aquí es lo que hace que un documento de motion se convierta en un catálogo de
efectos.

### 5.2 Por qué se retiran 6 y 7

Ninguno responde qué pasó, qué pasa ni qué terminó. El gradiente oscila permanentemente sin
relación con ningún evento; el glow aparece en cada enlace de cada pantalla, con lo que su
supuesta función de énfasis se anula por repetición. Ambos fallan la regla madre y el test de
`01` § 2.

**Consecuencia de implementación** (fase posterior): afecta a `base.css` (glow de enlaces),
`Dashboard.razor.css` (gradient mesh) y `Boton.razor.css` más `wwwroot/js/microinteracciones.js`
(acotar el ripple).

## 6. Catálogo vigente

Lo que este sistema permite, incluidos los patrones surgidos del banco visual de la Fase 2:

| Patrón | Función | Tier | Permitido en | Prohibido en |
|---|---|---|---|---|
| **Resalte de fila al apuntar** | Feedback causal | A | Listas de datos con filas anchas | — |
| **Cambio de estado de control** (hover, foco, activo) | Feedback causal | A | Todo elemento interactivo | — |
| **Ripple** | Feedback causal | A | Acciones primarias | Resto |
| **Toast con barra de progreso** | Estado vivo | A | Avisos autodescartables | Errores |
| **Apertura de ventana de contexto** | Transición espacial | A | Marcas de procedencia, recuentos, badges con detalle | — |
| **Entrada y salida de overlay** (panel, modal, drawer) | Transición espacial | B | Cambio de contexto | Cambio de valor dentro de la misma superficie |
| **Revelado escalonado** | Transición espacial | B | Primera carga de una superficie | Paginación, filtrado en vivo |
| **Expansión y colapso de sección** | Transición espacial | B | Acordeones y niveles de un workspace | — |
| **Pulso de estado vivo** | Estado vivo | C | Indicadores de proceso en curso | Estados críticos: la urgencia es del semáforo, no del movimiento |
| **Trazado de confirmación** (dibujo de una marca al completar) | Feedback causal | C | Cierre de una operación relevante: validación, sincronización terminada | Cada render del componente — **una vez por evento**, nunca al repintar |

### 6.1 Dos reglas específicas

- **El pulso significa "vivo", no "crítico".** Asociar movimiento continuo a la gravedad genera
  ansiedad visual y compite con el semáforo, que ya porta esa información. Un elemento vencido no
  parpadea; se muestra en rojo.
- **El trazado de confirmación se dibuja una vez por evento.** Si se repite en cada render,
  deja de significar "acaba de completarse" y pasa a ser decoración.

## 7. Rechazos permanentes

Registrados para que ningún brief futuro los reintroduzca como novedad:

| Rechazado | Motivo | Decisión |
|---|---|---|
| **Magnetic CTA** — el control se desplaza hacia el cursor | Rompe la memoria espacial y la precisión motora. Es lo contrario de Precision | DDL-017 |
| **Glow que sigue al puntero** | Ornamento puro; compite con la información en superficies densas | DDL-018 |
| **Gradientes animados de fondo** | Decoración sin relación con ningún evento | DDL-045 |
| **Glow de texto en hover** | Énfasis anulado por repetición | DDL-045 |
| **Retraso de entrada que crece con el número de filas** | Si la última fila espera proporcionalmente a la longitud de la lista, el movimiento retrasa la legibilidad — justo lo que una lista operativa no puede permitirse. **No aplica al revelado escalonado de § 6**, cuyo retraso está topado (DDL-054) | DDL-016 (regla madre) |

## 8. Tokens que `06` debe declarar

`07` fija los valores; `06` los nombra y los expone. **No se añade ningún token de motion que no
aparezca en esta tabla**; si hace falta uno nuevo, se decide aquí primero.

| Token conceptual | Valor | Uso |
|---|---|---|
| Duración A rápida | ~120 ms | Cambios de color y de estado de control |
| Duración A base | ~180 ms | Feedback causal, resalte de fila, ventana de contexto |
| Duración A lenta | ~250 ms | Toasts y avisos |
| Duración B | 300–500 ms | Entrada y salida de overlay, expansión, revelado |
| Curva estándar | aceleración simple | Tier A |
| Curva fluida | `cubic-bezier(0.16, 1, 0.3, 1)` | Tier B |
| Ciclo de estado vivo | ~2–2,5 s | Pulso de proceso en curso |

Los tres escalones de Tier A sustituyen a los tokens de duración actuales, que existen pero no
declaran función. **Un token de duración sin función asignada es una invitación a usarlo en
cualquier sitio**, que es como se acumula el motion.

## 9. Cómo se añade movimiento nuevo

1. Declarar la **función comunicativa** (§ 2). Si es decoración, se rechaza aquí.
2. Asignar **tier** y comprobar el presupuesto de la pantalla (§ 4).
3. Verificar que **al desactivarlo la interfaz sigue entendiéndose** (§ 4).
4. Comprobar que usa un **token existente** (§ 8). Si necesita uno nuevo, se decide antes.
5. Registrar la decisión en el Log si crea o modifica un patrón.

## 10. Decisiones que respaldan este documento

| Decisión | Aporta |
|---|---|
| DDL-016 | Los tres tiers y la regla de causalidad (§ 1, § 3) |
| DDL-045 | Veredicto de ripple, gradient mesh y glow (§ 5) |
| DDL-054 | Veredicto de revelado escalonado, toast y buscador (§ 5) |
| DDL-017 · DDL-018 | Rechazos permanentes (§ 7) |
| DDL-019 | El ripple deja de ser universal (§ 5) |
| DDL-020 | `prefers-reduced-motion` obligatorio (§ 4) |
| DDL-001 | El test que filtra la decoración (§ 1) |

## 11. Preguntas abiertas

Ninguna.

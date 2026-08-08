# 06 — Design System

**Estado**: Normativo · **Implementado hasta**: nada. `tokens.css` sigue con los valores de
2026-07. La migración está especificada en § 12 y es una fase posterior.

**Autoridad**: este documento **materializa en tokens y reglas de sistema** decisiones tomadas en
otros documentos. **No crea decisiones de producto ni de UX.** Si al leerlo aparece una regla que
no procede de una fuente trazable, es un error de este documento, no una decisión nueva.

### Trazabilidad de cada bloque

| Bloque | Fuente |
|---|---|
| Personalidad y roles de color | `02` § 1, § 3.1 |
| Valores de Blue, Cyan, semáforo, info | `02` § 3.2–3.5 (banco visual: DDL-025, DDL-027, DDL-010, DDL-030) |
| Superficies y elevación | `02` § 4 (DDL-013, DDL-028, DDL-026) |
| Radios y sombras | Trasladado desde `07` § 5.1 (DDL-054): no son motion |
| Motion | `07` § 8 — **contrato cerrado**: no se añade ningún token que no esté ahí |
| Tipografía e iconografía | `02` § 6, § 7 |
| Umbrales de contraste | `02` § 8 |
| Estados de componente | `04` cuando corresponda; aquí solo los tokens que los expresan |

---

## 1. Principios del sistema

1. **Los nombres de variable son estables; lo que cambia son los valores.** Es la propiedad que
   permite cambiar de identidad sin tocar los más de cincuenta archivos que consumen tokens. No
   se abandona.
2. **Ningún componente codifica un valor a mano.** Ni un color, ni un tamaño de fuente, ni un
   espaciado, ni una duración.
3. **Un token por decisión, no por uso.** Si dos usos comparten decisión, comparten token; si un
   uso necesita un valor propio, es que hay una decisión que no se ha tomado.
4. **Un token de duración declara su función.** Un token sin función asignada es una invitación a
   usarlo en cualquier sitio (`07` § 8).
5. **Todo token nace contemplando los dos temas.** Nunca uno con el otro como parche (DDL-021).

## 2. Color

### 2.1 Hydra Blue — acción humana

La escala **ya existe en el producto y el banco visual la ratificó**: `#235BC2` era el primario
vigente y ganó la comparación contra la alternativa más luminosa (DDL-025). No cambia.

| Token | Claro | Uso |
|---|---|---|
| `--color-primary-50` | `#EEF5FF` | Fondo de énfasis muy suave |
| `--color-primary-100` | `#DCEBFF` | Fondo de selección |
| `--color-primary-200` | `#BDD9FF` | Bordes de énfasis |
| `--color-primary-300` | `#5CA2F4` | Enlaces sobre superficie oscura |
| `--color-primary-400` | `#2F6FDD` | Hover de acción primaria |
| **`--color-primary-500`** | **`#235BC2`** | **Acción primaria, enlaces, foco, estado activo** |
| `--color-primary-600` | `#1E4A9E` | Activo / pressed |
| `--color-primary-700` | `#163A7D` | Reservado |

En tema oscuro los escalones se desplazan: `500` toma el valor de `300`, `600` el de `200` y
`700` el de `100` — el acero pierde contraste sobre fondo oscuro y hay que aclararlo, no
oscurecerlo.

### 2.2 Hydra Cyan — el sistema actuando

Familia **nueva** y **asimétrica por tema** (DDL-027). Se nombra por su rol, no por su matiz:

| Token | Claro | Oscuro | Uso |
|---|---|---|---|
| `--color-system-text` | `#0C7792` | `#2BD4F0` | Texto e iconos de procedencia o actividad del sistema |
| `--color-system-indicator` | `#0E96B4` | `#2BD4F0` | Punto de estado vivo, bordes, marcas no textuales |
| `--color-system-tint` | derivado al 10 % de `indicator` | ídem | Fondo de marcas y chips de sistema |

**No existe un cian brillante válido en modo claro** — medido: `#00F0FF` 1.41:1, `#00C2E0`
2.14:1, `#06B6D4` 2.43:1, los tres por debajo de su umbral. Cualquier propuesta futura en esa
dirección está respondida con datos.

**Restricciones de uso** (DDL-009): el cian nunca rellena un botón sólido, nunca es color de
acción, nunca sustituye a `info`, nunca es decoración.

### 2.3 Semáforo de cumplimiento

| Token | Valor | Estado |
|---|---|---|
| `--color-success-500` | `#22C55E` | Vigente / al día |
| `--color-warning-500` | `#F59E0B` | Próximo a vencer · Riesgo en visita |
| `--color-danger-500` | `#EF4444` | Urgente · vencido · acceso bloqueado |

Cada uno lleva su fondo (`-50`) y su tono de texto (`-700`) por tema, para badges legibles sobre
superficie clara y oscura.

**Innegociable** (DDL-010): exclusivo de vigencia y cumplimiento, nunca decorativo, y domina
sobre la marca.

### 2.4 Info — contexto neutral

`--color-info-500` se apoya en la escala secundaria (slate). Debe **distinguirse tonalmente del
cian**: si se confunden, el cian acaba absorbiendo el rol de información y pierde su significado
de agencia.

### 2.5 Neutros y texto

| Token | Claro | Oscuro |
|---|---|---|
| `--color-text` | `#0F1720` | `#E7EAEE` |
| `--color-text-muted` | **`#5F6E84`** | `#8592A3` |
| `--color-border` | `#E2E8EC` | `#293644` |
| `--color-border-strong` | `#CBD5E1` | `#3A4A5C` |

`--color-text-muted` **cambia de valor**: el actual `#738196` da 3.96:1 sobre blanco e incumple
el umbral para el cuerpo de 14 px que usa toda la aplicación (DDL-029). No es preferencia: es un
defecto en producción desde 2026-07.

### 2.6 Lo que se retira

`--color-accent-*` (cobre) **desaparece del sistema** (DDL-012). No se sustituye: con la dualidad
azul/cian, un tercer acento compite y diluye.

## 3. Superficies y elevación

Cuatro niveles (DDL-013). La profundidad se expresa por **fondo, borde y contraste**; la sombra
**solo en Overlay**.

| Token | Claro | Oscuro | Nivel |
|---|---|---|---|
| `--color-canvas` | `#F6F8FA` | `#0E141B` | Fondo de página |
| `--color-surface` | `#FFFFFF` | `#17212C` | Contenido |
| `--color-surface-subtle` | `#F1F5F7` | `#131B24` | Islas internas, agrupaciones |
| `--color-elevated` | `#FFFFFF` + `--color-border-strong` | `#202B36` | Panel contextual, inspector |
| `--color-overlay` | `#FFFFFF` + `--shadow-overlay` | `#202B36` + sombra | Modal, drawer, popover |

En claro, **Elevated y Overlay comparten fondo** y se distinguen por borde reforzado frente a
sombra: es lo que impide que la interfaz se convierta en una colección de tarjetas flotantes
(DDL-014).

## 4. Radios

Trasladados desde el catálogo de julio por no ser motion (DDL-054). **No cambian de valor**: la
identidad de ingeniería pide contención, y estos ya la tienen.

| Token | Valor | Uso |
|---|---|---|
| `--radius-sm` | 6 px | Chips, badges, elementos pequeños |
| `--radius-md` | 10 px | Botones, campos |
| `--radius-card-sm` | 12 px | Tarjetas pequeñas, tiles |
| `--radius-lg` | 14 px | Tarjetas principales, diálogos, paneles |
| `--radius-full` | 9999 px | Píldoras, avatares |

Ningún componente interactivo o contenedor de contenido usa esquinas totalmente rectas.

## 5. Sombras

| Token | Valor | Uso |
|---|---|---|
| `--shadow-overlay` | sombra amplia y suave | **Único** uso permitido: nivel Overlay |

**`--shadow-card` se retira.** Aplicar sombra a tarjetas contradice DDL-013: la elevación se
expresa por fondo y borde, y la sombra queda reservada a la relación espacial real de un overlay
sobre el contenido. Es el cambio de este documento con más superficie de impacto en el código.

## 6. Tipografía

Familia única: **Inter** variable, con `system-ui` de reserva. Cuerpo base de la aplicación:
**14 px** — convención deliberada de producto denso, no desviación (`02` § 6).

| Token | Tamaño / interlineado / peso | Uso |
|---|---|---|
| `--font-h1` | 36 / 44 / 700 | Título de página |
| `--font-h2` | 28 / 36 / 700 | Título de sección |
| `--font-h3` | 22 / 30 / 600 | Título de tarjeta o diálogo |
| `--font-h4` | 18 / 26 / 600 | Cabecera de bloque |
| `--font-body` | 16 / 24 / 400 | Cuerpo destacado |
| `--font-small` | 14 / 20 / 400 | **Base de la aplicación**: tablas, formularios |
| `--font-caption` | 12 / 16 / 500 | Metadatos, marcas de tiempo |
| `--font-label` | 13 / 16 / 600 | Etiquetas de formulario y badges |

**Numeración tabular obligatoria** en toda cifra que se compare en columna: fracciones de
cumplimiento, recuentos, fechas, importes. La desalineación de dígitos es ruido de lectura en una
superficie densa.

## 7. Espaciado

Escala de 8 px con medios pasos, nombrada por múltiplo de 4:

```
space-1  4     space-2  8     space-3  12    space-4  16
space-5  20    space-6  24    space-8  32    space-10 40
space-12 48    space-16 64    space-24 96
```

Regla de aplicación por contexto (`01` § 5.6): en **superficies operativas**, la densidad manda —
cuántas filas caben es medida de calidad. En **superficies de lectura o estados vacíos**, el aire
sí es un valor.

## 8. Motion

Contrato cerrado con `07` § 8. **No se declara ningún token que no aparezca aquí**; si hace falta
uno nuevo, se decide primero en `07`.

| Token | Valor | Función |
|---|---|---|
| `--motion-fast` | ~120 ms | Cambio de color y de estado de control (Tier A) |
| `--motion-base` | ~180 ms | Feedback causal, resalte de fila, ventana de contexto (Tier A) |
| `--motion-slow` | ~250 ms | Toasts y avisos (Tier A) |
| `--motion-transition` | 300–500 ms | Entrada y salida de overlay, expansión, revelado (Tier B) |
| `--ease-standard` | aceleración simple | Tier A |
| `--ease-fluid` | `cubic-bezier(0.16, 1, 0.3, 1)` | Tier B |
| `--motion-live-cycle` | ~2–2,5 s | Pulso de estado vivo (Tier C) |

Los tres escalones de Tier A **sustituyen** a los tokens de duración actuales, que existen pero
no declaran función.

`prefers-reduced-motion` desactiva Tier B y Tier C; Tier A se degrada a cambio instantáneo, nunca
desaparece (DDL-020).

## 9. Layout

| Token | Valor |
|---|---|
| `--sidebar-width` | 260 px |
| `--navbar-height` | 64 px |

Breakpoints, desktop-first (el uso real es de oficina):

```
desktop  ≥1280    layout completo, barra lateral expandida
laptop   1024–1279 barra lateral colapsable
tablet   768–1023  barra lateral tras disparador; tablas con desplazamiento horizontal
mobile   <768      navegación en panel; tablas apiladas
```

## 10. Estructura de temas

- `:root` define el **tema claro** como base.
- `:root[data-theme='oscuro']` y `:root[data-theme='claro']` aplican la elección **explícita** del
  usuario.
- `prefers-color-scheme` permanece **desactivado** hasta que el modo oscuro esté rediseñado
  (DDL-043). Es una decisión con fecha de caducidad, no un estado permanente: un usuario con el
  sistema en oscuro no debe aterrizar en un tema sin terminar.

Los componentes se estilan **siempre a través de los tokens semánticos** (`--color-surface`,
`--color-text`), nunca dentro de un bloque de tema. Un componente no debería saber qué tema está
activo.

## 11. Accesibilidad: umbrales y pares verificados

| Umbral | Aplica a |
|---|---|
| 4.5:1 | Texto normal |
| 3:1 | Texto grande (≥24 px, o ≥18.66 px en negrita) |
| 3:1 | Componentes de interfaz y bordes significativos |

Pares medidos en el banco visual:

| Par | Claro | Oscuro |
|---|---|---|
| Texto principal sobre superficie | 15.98:1 ✓ | 11.40:1 ✓ |
| Texto secundario sobre superficie | 5.18:1 ✓ | ≈6.5:1 ✓ |
| Acción primaria (texto y botón) | 6.27:1 ✓ | — |
| Enlace sobre superficie oscura | — | >5.5:1 ✓ |
| Texto de sistema (cian) | 5.58:1 ✓ | >8:1 ✓ |
| Indicador de sistema (UI) | 3.48:1 ✓ | >8:1 ✓ |

Las **reglas permanentes** de accesibilidad —color que nunca es el único portador de
significado, foco siempre visible, nada que se abra solo con el puntero— viven en `02` § 8 y no
se reenuncian aquí: este documento aporta los **valores** con los que se cumplen, no las reglas.
El único valor que fijan es el grosor del outline de foco: **2 px**.

## 12. Migración desde `tokens.css`

El cambio de identidad es **menor de lo que parece**, porque el banco ratificó el azul que ya
estaba. Lo que cambia:

| Acción | Token | Detalle |
|---|---|---|
| **Sin cambios** | `--color-primary-*` | Ratificado por DDL-025 |
| **Sin cambios** | `--radius-*`, escala tipográfica, espaciado, layout | — |
| **Cambia de valor** | `--color-text-muted` (claro) | `#738196` → `#5F6E84`; corrige incumplimiento de contraste |
| **Nuevos** | `--color-system-text`, `--color-system-indicator`, `--color-system-tint` | Familia cian, asimétrica por tema |
| **Nuevos** | `--color-surface-subtle`, `--color-elevated`, `--color-overlay`, `--color-border-strong` | Cuarto nivel de profundidad |
| **Renombrados por función** | duraciones de motion | Los actuales no declaran para qué sirven |
| **Nuevos** | `--motion-transition`, `--ease-fluid`, `--motion-live-cycle` | Tier B y estado vivo |
| **Se retira** | `--color-accent-*` (cobre) | DDL-012 |
| **Se retira** | `--shadow-card` | La elevación se expresa por fondo y borde (DDL-013) |

**Impacto medido en el código actual** (verificado el 2026-08-08 sobre `src/CaeManager.Web`):

| Cambio | Archivos afectados | Lectura |
|---|---|---|
| `--color-primary-*` | 0 | El azul ya era `#235BC2`: el banco lo ratificó, no lo cambió |
| `--color-accent-*` | 2 | Retirada trivial |
| `--color-text-muted` | 1 (definición) | Cambia un valor; el consumo es indirecto |
| `--shadow-card` | **23 hojas de estilo** | El cambio de mayor superficie del reset |

La conclusión práctica es que **el rediseño de identidad casi no toca color**: lo caro es la
retirada de la sombra de tarjeta, porque afecta a la expresión de profundidad de toda la
aplicación. Conviene tratarlo como un lote propio y verificarlo pantalla por pantalla, no
mezclado con los cambios cromáticos.

**23 archivos no son 23 usos equivalentes.** Un reemplazo mecánico produciría exactamente el
resultado que DDL-013 quiere evitar. Cada uso se clasifica antes de tocarlo:

| Uso real de la sombra | Migración |
|---|---|
| Tarjeta corriente que solo necesita separarse del fondo | Fondo y borde: `--color-surface` o `--color-surface-subtle` |
| Elemento que sí flota sobre el contenido (modal, drawer, popover) | `--shadow-overlay` — es el caso legítimo |
| Sombra usada para crear jerarquía que el orden visual ya daba | Se elimina sin sustituto |
| Composición particular del componente | Revisión individual; puede requerir decisión propia |

El criterio de clasificación es el de DDL-013: **¿existe una relación espacial real?** Si el
elemento no está por encima de nada, no proyecta sombra.

**Orden recomendado de ejecución**, para que cada paso sea verificable por separado:

1. Corregir `--color-text-muted` — corrige un incumplimiento real y no depende de nada más.
2. Añadir los tokens nuevos sin consumirlos todavía.
3. Migrar superficies al modelo de cuatro niveles.
4. Retirar `--shadow-card` y `--color-accent-*`, que es donde aparecerán los usos huérfanos.
5. Renombrar los tokens de motion y aplicar los veredictos de `07` § 5.

Cada paso cierra con verificación end-to-end en navegador, como exige `CLAUDE.md`.

## 13. Decisiones que respaldan este documento

| Decisión | Aporta |
|---|---|
| DDL-025 · DDL-027 · DDL-030 | Valores de Blue, Cyan y taxonomía por rol (§ 2) |
| DDL-010 · DDL-039 | Semáforo y su modificador (§ 2.3) |
| DDL-012 | Retirada del cobre (§ 2.6) |
| DDL-029 | Corrección del texto secundario (§ 2.5) |
| DDL-013 · DDL-028 · DDL-026 · DDL-014 | Superficies, elevación y la regla de la sombra (§ 3, § 5) |
| DDL-054 | Radios y sombras llegan desde `07` (§ 4, § 5) |
| DDL-016 · DDL-020 | Tokens de motion y reducción de movimiento (§ 8) |
| DDL-021 · DDL-043 | Estructura de temas y estado de `prefers-color-scheme` (§ 10) |
| DDL-041 | Densidad única, que fija el criterio de espaciado (§ 7) |

## 14. Preguntas abiertas

Ninguna.

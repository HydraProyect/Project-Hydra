# Registro de la sesión de migración al sistema de diseño — 2026-08-09

**Qué es esto**: bitácora de una sesión de migración del portal a la normativa `01`–`08`. **No es
un documento normativo**: no decide nada, no crea autoridad y no puede citarse como fuente. Lo
que se implementa lleva su fuente al lado; lo que no está decidido se registra y no se toca.

**Rama**: `claude/portal-design-system-migration-fa18fc`

**Regla de trabajo aplicada**: equivalencia canónica inequívoca → migrar · ambigüedad semántica →
registrar · mejora arquitectónica → proponer, nunca ejecutar.

**Un fallback no es una prueba semántica.** Demuestra qué apariencia pretendía conservar el
código, no cuál es el token canónico de destino. Cada sustitución de este registro se justifica
por rol de uso, y el fallback aparece solo como evidencia secundaria.

---

## 1. Completado

### 1.1 `--color-text-muted` pasa a `#5F6E84` — DDL-029

`06` § 12 paso 1. El valor anterior (`#738196`, el escalón `--color-neutral-500`) daba 3.96:1
sobre blanco: incumplía el 4.5:1 exigido al cuerpo de 14 px de toda la aplicación. Defecto en
producción desde 2026-07. El escalón `#738196` sigue vivo como `--color-border-control`, donde su
criterio es el 3:1 (DDL-063).

`BUILD PASSED` · sin verificación visual.

### 1.2 Tokens del reset declarados sin consumir — `06` § 12 paso 2

| Token | Claro | Oscuro | Fuente |
|---|---|---|---|
| `--color-system-text` | `#0C7792` | `#2BD4F0` | `02` § 3.3, DDL-027 |
| `--color-system-indicator` | `#0E96B4` | `#2BD4F0` | `02` § 3.3, DDL-027 |
| `--color-system-tint` | 10 % de `indicator` | ídem | `06` § 2.2 |
| `--color-surface-subtle` | `#F1F5F7` | `#131B24` | `02` § 4.1, DDL-028/DDL-026 |
| `--color-elevated` | `#FFFFFF` | `#202B36` | ídem |
| `--color-overlay` | `#FFFFFF` | `#202B36` | ídem |
| `--color-border-strong` | `#CBD5E1` | `#3A4A5C` | ídem |
| `--motion-fast` / `-base` / `-slow` | 120 / 180 / 250 ms | — | `07` § 8, Tier A |
| `--motion-transition` | 360 ms | — | `07` § 8, Tier B |
| `--ease-standard` / `--ease-fluid` | `ease` / `cubic-bezier(0.16,1,0.3,1)` | — | `07` § 3, § 8 |
| `--motion-live-cycle` | 2,2 s | — | `07` § 8, Tier C |

`--transition-fast/-base/-slow` quedan como alias deprecados apuntando a los nuevos.

`--motion-transition` fija 360 ms dentro del rango 300–500 ms que declara `07` § 8: el rango es la
decisión, el valor concreto es implementación. Mismo criterio para `--motion-live-cycle` (2–2,5 s).

`BUILD PASSED` · sin verificación visual.

### 1.3 Restauración del contrato de tokens — 17 referencias inexistentes

Seis hojas de estilo referenciaban variables que **nunca se declararon** en `tokens.css`. Sin
fallback, `var(--inexistente)` es *invalid at computed-value time*: `gap`, `margin` y `padding`
caían al valor inicial (0) y `color` heredaba. **Facturación y Proyectos se renderizaban sin
ningún espaciado** y con el texto secundario en color de texto principal.

Mapeo aplicado (equivalencias confirmadas):

| Legacy | Canónico | Fuente del destino |
|---|---|---|
| `--color-texto-secundario` · `--color-text-secondary` | `--color-text-muted` | `02` § 4.1 "Texto secundario" |
| `--color-texto-principal` | `--color-text` | `02` § 4.1 "Texto principal" |
| `--color-borde` | `--color-border` | `02` § 4.1 |
| `--color-accion-primaria` | `--color-primary-500` | `02` § 3.2 "Acción primaria" |
| `--color-superficie-secundaria` | `--color-surface-subtle` | `06` § 3 "islas internas, agrupaciones" |
| `--color-peligro` | `--color-danger-500` | equivalencia confirmada |
| `--color-peligro-hover` | `--color-danger-700` | equivalencia confirmada |
| `--espaciado-xs/sm/md/lg/xl` | `--space-1/2/4/6/8` | `06` § 7 |

El escalón de espaciado se ancla en el único fallback que el código documentaba
(`--espaciado-md, 1rem` = `--space-4`) y la escala de 8 px de `06` § 7 fija el resto desde ahí.

**Registro de cada sustitución** (archivo:línea, estado tras el commit):

| Ubicación | Declaración resultante |
|---|---|
| `Features/Facturacion/Pages/Facturacion.razor.css:2` | `color: var(--color-text-muted)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:3` | `margin-bottom: var(--space-8)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:10` | `gap: var(--space-1)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:11` | `margin-bottom: var(--space-8)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:22` | `gap: var(--space-1)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:23` | `border-bottom: 1px solid var(--color-border)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:24` | `margin-bottom: var(--space-6)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:28` | `padding: var(--space-2) var(--space-4)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:32` | `color: var(--color-text-muted)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:43` | `color: var(--color-text)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:47-48` | `color` y `border-bottom-color: var(--color-primary-500)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:77` | `background: var(--color-surface-subtle)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:84-85` | `gap: var(--space-4)` · `margin-bottom: var(--space-6)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:108` | `gap: var(--space-1)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:113` | `gap: var(--space-2)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:117` | `margin-top: var(--space-6)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:124-125` | `gap: var(--space-4)` · `margin-bottom: var(--space-8)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:132` | `gap: var(--space-1)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:147` | `border-top: 2px solid var(--color-border)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:151` | `color: var(--color-primary-500)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:156` | `color: var(--color-text-muted)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:158` | `margin-top: var(--space-4)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:163` | `color: var(--color-danger-500)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:167` | `color: var(--color-danger-700)` (hover) |
| `Features/Facturacion/Pages/Facturacion.razor.css:171` | `color: var(--color-danger-500)` |
| `Features/Facturacion/Pages/Facturacion.razor.css:173` | `margin-bottom: var(--space-4)` |
| `Features/Proyectos/Pages/Proyectos.razor.css:2-3` | `color: var(--color-text-muted)` · `margin-bottom: var(--space-8)` |
| `Features/Proyectos/Pages/Proyectos.razor.css:9` | `margin-bottom: var(--space-8)` |
| `Features/Proyectos/Pages/Proyectos.razor.css:13` | `margin-bottom: var(--space-6)` |
| `Features/Proyectos/Pages/Proyectos.razor.css:24` | `background: var(--color-surface-subtle)` |
| `Features/Proyectos/Pages/Proyectos.razor.css:31-32` | `gap: var(--space-4)` · `margin-bottom: var(--space-6)` |
| `Features/Proyectos/Pages/Proyectos.razor.css:54-55` | `gap` y `margin-top: var(--space-2)` |
| `Features/Proyectos/Pages/Proyectos.razor.css:59` | `color: var(--color-danger-500)` |
| `Features/Proyectos/Pages/Proyectos.razor.css:63` | `color: var(--color-danger-700)` (hover) |
| `Features/Proyectos/Pages/Proyectos.razor.css:67` | `color: var(--color-danger-500)` |
| `Features/Proyectos/Pages/Proyectos.razor.css:69` | `margin-bottom: var(--space-4)` |
| `Features/DashboardEjecutivo/Pages/DashboardEjecutivo.razor.css:11-12` | `gap` y `margin-bottom: var(--space-4)` |
| `Features/DashboardEjecutivo/Pages/DashboardEjecutivo.razor.css:20` | `color: var(--color-text-muted)` |
| `Features/Integraciones/Pages/Conexiones.razor.css:7` | `color: var(--color-text-muted)` |
| `Features/Delegaciones/Pages/Delegaciones.razor.css:12, 35, 46, 55, 60` | `color: var(--color-text-muted)` |
| `Features/Retencion/Pages/Retencion.razor.css:12, 29` | `color: var(--color-text-muted)` |

`BUILD PASSED` · `STATIC CHECK PASSED` (barrido cruzado de `var(--…)` contra los tokens
declarados: solo queda `--color-info-600`, registrado como ODM-05) · **sin verificación visual**.

> **Las siete páginas afectadas siguen considerándose afectadas funcionalmente hasta que haya
> verificación visual.** La corrección restaura espaciado que llevaba tiempo colapsado: el
> resultado es distinto del que se está viendo hoy en producción, y esa diferencia es el arreglo,
> no una regresión — pero hay que mirarlo.

### 1.4 Superficies migradas al modelo de cuatro niveles — `06` § 12 paso 3

`--color-bg` pasa de `#FAFBFC` a `#F6F8FA` y `--color-border` de `#E8EDF2` a `#E2E8EC`, los
valores que declara `02` § 4.1. **Solo cambia el valor; ningún token se renombra** (ODM-04).

| Nivel | Token | Superficies migradas |
|---|---|---|
| Overlay | `--color-overlay` | `Modal:15` · `Drawer:16` · `MenuAcciones:36` · `SelectorEntidad:14` · `BarraAccionesLote:9` · `BuscadorGlobal:16` · `AsistenteIa:21` |
| Elevated | `--color-elevated` | `ContextWorkspace:14` (ver ODM-01) |
| Subtle | `--color-surface-subtle` | `ConfigurarAutenticadorDosFactores:18` · `Badge:20` · `ProgresoConMensajes:15` · `ZonaSoltarArchivo:65` · `AtajosGlobales:25` · `BotonBuscadorGlobal:5` · `UnifiedTimeline:5, 100, 245, 256` · `Bandeja:266, 475` · `Dashboard:20` · `VisorDocumento:6` |

Las doce superficies del nivel Subtle usaban `--color-neutral-50/-100`: **paleta cruda que ningún
tema redefine**, así que en tema oscuro se pintaban gris claro sobre fondo oscuro. Es el mismo
defecto ya corregido una vez en el calendario (H3 de `docs/ux-audit/10-…`) y que `06` § 10
prohíbe de raíz — un componente se estila siempre a través de los tokens semánticos.

Deliberadamente **fuera** del barrido, por no ser superficie: `--color-neutral-0` como color de
texto sobre relleno, los trazos decorativos (`-300`/`-400`) y el degradado del esqueleto de carga.

`BUILD PASSED` · **sin verificación visual**.

### 1.5 `--shadow-card` y el acento cobre retirados — `06` § 12 paso 4

DDL-013 y DDL-012. Los **20 usos** de `--shadow-card` en **11 hojas** se clasificaron uno a uno
con la tabla de `06` § 12 antes de tocarlos. Resultado: **ninguno era el caso legítimo** — no hay
una sola sombra que se convierta en `--shadow-overlay`.

| Clasificación | Migración | Dónde |
|---|---|---|
| Tarjeta que solo necesita separarse del fondo | Fondo y borde, sin sustituto | `Tarjeta` · `TarjetaMetrica` · las cinco tarjetas de Account · burbuja de `UnifiedTimeline` · las tres columnas de `Bandeja` |
| Jerarquía que el orden visual ya daba | Se elimina | Hover de `Boton` (primario, secundario, destructivo) · `.boton-primario:hover` de las cinco páginas de Account · `.nav-item:hover` |

Se retira además `--shadow-overlay` del hover de `TarjetaMetrica`: ese token tiene **un único uso
permitido**, el nivel Overlay (`06` § 5), y una tarjeta métrica no lo es. El `translateY(-2px)`
que lo acompaña **no se toca** (ODM-06).

Nueve declaraciones `transition` dejaban de animar nada y pierden su término `box-shadow`. Las de
`Campo*` se conservan: allí la sombra sigue siendo el anillo de foco, que sí cambia.

El **único** consumo real del cobre era `.timeline-sugerencia-icono-ia`. Pasa a
`--color-system-text`: el presupuesto de uso del cian cubre literalmente *"IA cuando actúa"*
(`02` § 3.3), así que la equivalencia es inequívoca.

`BUILD PASSED` · `E2E VERIFIED`.

### 1.6 Veredictos de `07` § 5 sobre los efectos de julio

Los tres que `07` § 5.2 nombra por archivo, con el veredicto que ya tenían decidido (DDL-045):

| Efecto | Veredicto | Archivo |
|---|---|---|
| Gradient mesh animado de las tarjetas KPI | **Retirado** | `wwwroot/css/dashboard.css` |
| Glow en hover de enlaces | **Retirado** (con sus dos excepciones) | `wwwroot/css/base.css` |
| Ripple | **Acotado** a `.boton-primario` | `wwwroot/js/microinteracciones.js` |

`BUILD PASSED` · `E2E VERIFIED` (el `::before` del mesh computa `content: none`).

### 1.7 Duraciones con función declarada — `06` § 12 paso 5

Cada sustitución se decide por la **función** que `07` § 8 asigna al token, no por coincidencia de
nombre: `--transition-base` **no** se convierte en `--motion-base` por llamarse igual.

| Función (`07` § 8) | Token | Qué se migró |
|---|---|---|
| Cambio de estado de control | `--motion-fast` + `--ease-standard` | Los 44 usos de `--transition-fast` y `--transition-base`. Todos resultaron ser lo mismo: hover, foco, activo o arrastre |
| Resalte de fila | `--motion-base` + `--ease-standard` | `.tabla-datos tr:hover td` |
| Toasts y avisos | `--motion-slow` + `--ease-standard` | Entrada del toast |
| Transición espacial (Tier B) | `--motion-transition` + `--ease-fluid` | Entrada de Drawer, ContextWorkspace y panel del Asistente; entrada del buscador global (`07` § 5 #4); revelado escalonado de listas y dashboard (DDL-054 — los retrasos topados no se tocan) |

Valores a mano sustituidos por su token: los 100 ms de NavMenu y los 0,15 s del botón del
Asistente y de las pestañas de Facturación.

Dos correcciones contra el **contrato cerrado** de `07` § 8:

- La curva *spring* con overshoot de `Boton` (`cubic-bezier(0.34, 1.56, 0.64, 1)`) no existe en
  ese contrato. Tier A usa aceleración estándar (`07` § 3), y añadir una curva se decide en `07`.
- El ripple duraba **550 ms**. Es feedback causal de Tier A, cuyo techo son 250 ms (`07` § 3).

`--transition-fast` y `--transition-base` quedan retirados. `--transition-slow` sobrevive con un
único consumidor por ODM-07.

`BUILD PASSED` · `E2E VERIFIED`.

### 1.8 Borde de control y foco visible fuera del sistema de diseño

Los componentes de `DesignSystem/` ya cumplían DDL-063; los controles que viven en features, no.

**Borde que identifica un control → `--border-control`** (3:1, `02` § 8): `.asistente-textarea` ·
`.composer-whatsapp-input` · `.bandeja-toggle` · `.paginador-tamano-select` · `.enlace-hoy` ·
`.boton-buscador-global`. Se aplicó la regla de decisión de `02` § 8 —*si el borde desaparece,
¿deja de distinguirse dónde empieza y acaba el control?*—: quedan fuera tarjetas, paneles,
columnas, chips, tablas y separadores, que agrupan contenido y no están sujetos al 3:1 (DDL-062).

**Foco visible** (`02` § 8, `08` § 3.2 punto 8):

- `.buscador-input` hacía `outline: none` **sin sustituto**: el foco del buscador global era
  literalmente invisible al navegar con teclado.
- `.asistente-textarea` sustituía el outline por un cambio de borde —justo lo que `08` § 3.2
  prohíbe— y de paso desplazaba el contenido al pasar el borde de 1 px a 2 px.

Ambos pasan al patrón que ya usan `CampoTexto` y `CampoTextarea`.

`BUILD PASSED` · `E2E VERIFIED`.

### 1.9 Enlace global y `prefers-reduced-motion` del Drawer

`02` § 3.2 asigna `#235BC2` a *"acción primaria, enlaces, foco, estado activo"*. El enlace global
de `base.css` usaba `--color-primary-600`, que es el escalón de activo/pressed y que `06` § 2.1
marca además como **no ratificado**. Los usos de `-600` como **fondo** de hover/pressed se
conservan: ahí sí es su rol. Los ocho usos restantes como color de **texto** quedan registrados
en ODM-09.

El `Drawer` no tenía bloque `prefers-reduced-motion`: era la única entrada de overlay del sistema
que seguía animándose con el movimiento reducido activo, y DDL-020 lo desactiva **siempre** para
Tier B.

`BUILD PASSED`.

---

## 2. Pendientes de decisión

Numeradas `ODM-nn` (Open Decision de Migración) para no colisionar con la serie `OD-nn` del
Decision Log. **Ninguna se ha resuelto por inferencia.** Una recomendación no es una decisión.

### ODM-01 · ¿El Context Panel es Elevated u Overlay?

- **Ubicación**: `Components/Workspace/ContextWorkspace.razor.css:14`
- **Evidencia**: `06` § 3 asigna literalmente "panel contextual, inspector" al nivel **Elevated**,
  que se define **sin sombra** (la sombra queda reservada a Overlay, DDL-013). Pero el panel es
  `position: fixed` sobre el contenido, con `z-index: 1050` — hay relación espacial real, que es
  justamente el criterio con el que DDL-013 justifica la sombra.
- **Por qué requiere decisión**: las dos lecturas son legítimas y la norma no desempata. Resolverlo
  por inferencia sería exactamente el modo de fallo que OD-24 cerró.
- **Alternativas**: (A) Elevated estricto — se retira `--shadow-overlay` y se refuerza el borde con
  `--color-border-strong`. (B) Overlay — se conserva la sombra y el fondo pasa a `--color-overlay`.
- **Impacto**: nulo en claro (Elevated y Overlay comparten `#FFFFFF`); en oscuro ambos son
  `#202B36`, así que **la decisión solo afecta a sombra y borde**, no al fondo.
- **Estado**: el fondo ya usa `--color-elevated` porque el valor es idéntico en las dos lecturas.
  Sombra y borde quedan como estaban.
- **Recomendación**: (A). El Context Panel no se abre sobre una superposición que oscurezca el
  contenido; se acopla al lateral. Es un inspector, no un modal.

### ODM-02 · Contrato de los tokens tipográficos: `font` shorthand o propiedades sueltas

- **Ubicación**: `Facturacion.razor.css:35, 152, 157, 172` · `Proyectos.razor.css:68` (marcadas
  `PENDIENTE ODM-02` en el código).
- **Evidencia**: los tokens `--font-*` son **shorthands completos**
  (`--font-body-md: 400 0.875rem/1.25rem var(--font-family)`). Cinco declaraciones legacy hacen
  `font-size: var(--tipografia-sm)`, y `--tipografia-*` no existe: la declaración queda inválida y
  el tamaño hereda. No se puede sustituir por `font-size: var(--font-body-md)` — `font-size` espera
  un `<length>`, no un shorthand.
- **Por qué requiere decisión**: pasar de `font-size` a `font:` **cambia también peso,
  interlineado y familia**, no solo el tamaño. Es un cambio de contrato, no una sustitución.
- **Alternativas**: (A) usar `font: var(--font-*)`, que es lo que ya hacen ~50 declaraciones del
  repositorio y la única forma de consumir estos tokens hoy. (B) declarar tokens de tamaño sueltos
  (`--font-size-*`) — arquitectura nueva, requiere decisión de `06`. (C) dejarlo y aceptar que esas
  cinco reglas no fijan tamaño.
- **Impacto**: cinco declaraciones, dos páginas. Bajo, pero elegir (B) redefine la arquitectura
  tipográfica entera.
- **Recomendación**: (A), por consistencia con el resto del código; pero **es decisión de `06`**,
  no de esta migración.

### ODM-03 · Destino canónico de `--color-success-600`

- **Ubicación**: `Features/Trabajadores/Pages/Trabajadores.razor.css:18`
- **Evidencia**: `.pista-documento-exito` usa `var(--color-success-600, var(--color-success-500))`.
  El escalón `600` **no existe** en la escala del semáforo, que solo declara `-50`, `-500` y
  `-700`. Hoy resuelve por el fallback a `-500`.
- **Por qué requiere decisión**: el fallback no demuestra el destino. El uso es **foreground de
  texto**, y `06` § 2.3 dice que cada color del semáforo lleva "su fondo (`-50`) y su tono de texto
  (`-700`)": eso apunta a `-700`, no a `-500`. Pero cambiarlo altera el color que se está viendo
  hoy, y el semáforo es innegociable (DDL-010): no se toca sin decisión.
- **Alternativas**: (A) `--color-success-700`, coherente con `06` § 2.3 y con el 4.5:1 —
  `#22C55E` sobre blanco da 1.99:1. (B) `--color-success-500`, que conserva la apariencia actual.
- **Impacto**: una declaración. Pero fija precedente para todo texto de estado del producto.
- **Recomendación**: (A). El mismo argumento que ya cerró el hallazgo P0-8 en `Badge.razor.css`,
  cuyo comentario explica que `-500` sobre `-50` incumplía el AA no negociable.

### ODM-04 · El token de Canvas se llama distinto en la norma y en el código

- **Ubicación**: `wwwroot/css/tokens.css` · `06` § 3
- **Evidencia**: `06` § 3 nombra el token `--color-canvas`; el código lo llama `--color-bg` y lo
  consumen más de cincuenta archivos. El **valor** sí está ratificado (`#F6F8FA`, `02` § 4.1) y ya
  está aplicado.
- **Por qué requiere decisión**: `06` § 1 declara que los nombres de variable son estables y que lo
  que cambia son los valores. Renombrar contradice ese principio; mantener dos nombres crea un
  alias, que es arquitectura nueva. Ninguna de las dos se decide en una migración.
- **Alternativas**: (A) `06` § 3 corrige el nombre a `--color-bg`, que es el implementado.
  (B) se renombra el código y se toca todo el consumo. (C) alias permanente.
- **Impacto**: documental si (A); ~50 archivos si (B).
- **Recomendación**: (A). El principio de nombres estables es del propio `06`.

### ODM-05 · Qué rol cromático porta un acuse de entrega externo

- **Ubicación**: `Features/Comunicaciones/Components/UnifiedTimeline.razor.css:130`
- **Evidencia**: `.timeline-ticks.leido` usa `var(--color-info-600, #0284c7)`. `--color-info-600`
  no existe; hoy se pinta el literal `#0284c7`, que es un azul-cian ajeno a la paleta. El uso es
  **foreground**, sobre el estado de lectura de un mensaje de WhatsApp.
- **Por qué requiere decisión**: hay tres lecturas legítimas y ninguna es inferible. `02` § 3.1
  diría **cian** (el dato procede del sistema / de un conector). `02` § 3.5 diría **info**
  (contexto neutral). DDL-037/DDL-048 prohíben el color de marca de terceros, y el azul de
  doble-tick es reconociblemente el de WhatsApp.
- **Alternativas**: (A) `--color-system-text` — es el sistema informando de actividad externa.
  (B) `--color-info-500`. (C) neutro `--color-text-muted`, distinguiendo el estado solo por la
  forma del icono, que es lo que DDL-048 hace con los canales.
- **Impacto**: una declaración, pero fija cómo se representan los acuses de entrega de todos los
  conectores futuros (`ARQUITECTURA-INTEGRACIONES.md`).
- **Estado**: **sin tocar**. Es la única referencia inexistente que sobrevive al barrido.
- **Recomendación**: sin recomendación fuerte. (C) es la más conservadora con DDL-048.

### ODM-06 · ¿Es el "lift" de la tarjeta métrica un elemento que se desplaza bajo el cursor?

- **Ubicación**: `Components/DesignSystem/TarjetaMetrica.razor.css:63-66`
- **Evidencia**: `.tarjeta-metrica-clicable:hover` aplica `transform: translateY(-2px)` **y**
  `box-shadow: var(--shadow-overlay)`. `02` § 9 rechaza "Elementos que se desplazan bajo el cursor"
  citando DDL-017, pero el alcance literal de DDL-017 es el *Magnetic CTA* — un control que se
  mueve **hacia** el cursor, que no es lo mismo que un realce vertical al apuntar.
- **Por qué requiere decisión**: extender DDL-017 a cualquier desplazamiento en hover sería
  generalizar una regla más allá de su alcance, que es justo lo que la serie OD-22→OD-34 corrigió.
- **Alternativas**: (A) se retira el `translateY`. (B) se conserva como feedback causal de Tier A.
- **Impacto**: un componente, presente en todos los dashboards.
- **Estado**: la **sombra** sí se retira (ver § 1 del lote de sombras: `--shadow-overlay` tiene un
  único uso permitido, el nivel Overlay, y una tarjeta no lo es). El `translateY` no se toca.

### ODM-07 · Duraciones sin función asignada en el contrato cerrado de `07` § 8

- **Ubicación**: `Features/Dashboard/Pages/Dashboard.razor.css:25` (barra comparativa que se
  rellena) · `Components/DesignSystem/AnilloCumplimiento.razor.css:28` (`stroke-dasharray`, el
  anillo dibujándose) · `Boton.razor.css:132` (giro del spinner, 0.6 s) ·
  `EstadoCargando.razor.css:17` (brillo del esqueleto, 1.4 s) ·
  `ProgresoConMensajes.razor.css:27` (progreso indeterminado, 1.6 s).
- **Evidencia**: `07` § 8 declara siete tokens y cierra el contrato: *"no se declara ningún token
  de motion que no aparezca en esta tabla"*. Ninguno de esos siete cubre "un valor que se anima
  hasta su medida" ni "un ciclo de espera indeterminado". `--motion-live-cycle` (2–2,5 s) tiene
  función asignada —*pulso de estado vivo*— y un spinner a 2,2 s por vuelta no es eso.
- **Por qué requiere decisión**: mapearlos a un token existente sería usar un token para una
  función que no declara, que es exactamente lo que `07` § 8 quiere impedir. Y declarar uno nuevo
  se decide en `07`, no en una hoja de estilo.
- **Alternativas**: (A) `07` añade una función y `06` un token para "carga indeterminada".
  (B) se asigna una de las existentes por decisión expresa. (C) se acepta que estas cinco quedan
  fuera del sistema de motion y se documenta por qué.
- **Estado**: **sin tocar**. `--transition-slow` sobrevive en `tokens.css` como token deprecado
  con un único consumidor por esta razón.
- **Impacto**: cinco declaraciones. Bajo en superficie, alto en criterio: fija si el contrato de
  `07` § 8 es exhaustivo o admite huecos declarados.

### ODM-08 · El velo de los overlays no tiene token

- **Ubicación**: `Modal.razor.css:4` · `Drawer.razor.css:4` · `AsistenteIa.razor.css:13`
  (`rgba(15, 23, 42, 0.45)`) · `BuscadorGlobal.razor.css:4` (`rgba(15, 23, 42, 0.5)`).
- **Evidencia**: cuatro velos a mano, con dos opacidades distintas y un color que no pertenece a
  ninguna rampa del sistema (`#0F172A` es slate de librería). `06` § 1.2 prohíbe que un componente
  codifique un color a mano, pero `06` § 3 no declara ningún token de velo.
- **Por qué requiere decisión**: el token no existe y crearlo es ampliar el sistema, no migrar.
  Además hay que elegir **una** opacidad: hoy el buscador global usa 0.5 y los demás 0.45, sin que
  ninguna decisión respalde la diferencia.
- **Alternativas**: (A) `06` declara `--color-scrim` con un valor derivado de la rampa neutra.
  (B) se declara por nivel (modal frente a buscador) si la diferencia es intencionada.
- **Recomendación**: (A) con un solo valor. La diferencia actual parece deriva, no decisión.

### ODM-09 · `--color-primary-700` y `--color-primary-600` como color de texto

- **Ubicación**: `UnifiedTimeline.razor.css:219` (`-700`) · `NavMenu.razor.css:127`,
  `UnifiedTimeline.razor.css:189, 267`, `Bandeja.razor.css:89, 166, 278, 387, 428` (`-600`).
- **Evidencia**: `06` § 2.1 declara `-600` como *"Activo / pressed"* y `-700` como *"Reservado"*,
  y marca **ambos como no ratificados**. `02` § 3.2 solo decide tres valores por rol: acción
  (`-500`), acento no textual (`-400`) y enlace sobre superficie oscura (`-300`).
- **Por qué requiere decisión**: el enlace global sí se corrigió a `-500` porque `02` § 3.2 lo
  nombra literalmente. Estos nueve no son todos enlaces —hay títulos de sección, elementos activos
  de navegación y remitentes— y decidir su rol uno a uno es asignar significado, no migrar.
- **Impacto**: nueve declaraciones, concentradas en Comunicaciones.
- **Contraste**: ninguno falla — `-600` da 8.59:1 y `-700` 11.35:1 sobre Surface. Es un problema
  de **rol**, no de accesibilidad.

### ODM-10 · Foco suprimido en el panel de pestañas

- **Ubicación**: `Components/DesignSystem/Pestanas.razor.css:49`
- **Evidencia**: `.pestanas-panel:focus-visible { outline: none }`. `02` § 8 dice "el foco siempre
  visible" sin excepciones, pero `08` § 3.2 acota los requisitos 7–9 a **superficies
  interactivas**, y un `tabpanel` que recibe foco programático para que el teclado entre en su
  contenido no es un control.
- **Por qué requiere decisión**: las dos lecturas son defendibles y el resultado es opuesto.
- **Estado**: **sin tocar**. Los otros dos `outline: none` de la aplicación sí se corrigieron
  porque estaban sobre controles reales (input de búsqueda y textarea del asistente).

---

## 3. Mejoras propuestas

Ninguna implementada. Todas son opcionales y ninguna bloquea nada.

### MP-01 · `.enlace-accion` debería ser `MenuAcciones`

- **Problema**: Facturación y Proyectos pintan sus acciones de fila como botones sueltos con una
  clase sin estilos (ver BUG-01), mientras el resto del producto usa **MenuAcciones**.
- **Mejora**: sustituirlos por `MenuAcciones`, que ya resuelve agrupación, opción destructiva
  distinguida y teclado (`08` § 4.1).
- **Beneficio**: dos páginas dejan de ser la excepción visual del catálogo.
- **Coste**: bajo — dos ficheros `.razor`. **Riesgo**: bajo. `08` § 4.1 advierte que **no** se usa
  MenuAcciones con una sola acción: Facturación tiene una sola ("Eliminar"), así que ahí la
  respuesta correcta puede ser estilar el botón, no agrupar.

### MP-02 · El esqueleto de carga no cambia con el tema

- **Problema**: `EstadoCargando.razor.css:12-14` construye su degradado con
  `--color-neutral-100/-200`, paleta cruda. En tema oscuro el esqueleto brilla en gris claro sobre
  fondo oscuro — el mismo defecto que este lote corrigió en otras doce superficies.
- **Mejora**: reconstruirlo sobre `--color-surface-subtle` y `--color-border`.
- **Beneficio**: el esqueleto deja de ser el único elemento que ignora el tema.
- **Coste**: bajo. **Riesgo**: bajo, pero **no se hizo** porque un degradado no es "una
  superficie" y elegir sus dos paradas es una decisión visual, no una sustitución.

### MP-03 · Un solo sitio para el tamaño de fuente fuera de escala

- **Problema**: 26 declaraciones `font-size` con valor literal (`10px`, `11px`, `0.78rem`,
  `0.85rem`, `0.6875rem`, `1.15rem`…). Algunas son deliberadas —el `1rem` de los campos en móvil
  evita el zoom automático de iOS—, pero la mayoría son escalones inventados.
- **Mejora**: cruzarlas contra la escala de `06` § 6 y dejar solo las que tengan una razón escrita.
- **Beneficio**: la escala tipográfica vuelve a ser la única fuente.
- **Coste**: medio. **Riesgo**: medio — cambia tamaños visibles. Depende además de ODM-02.

### MP-04 · El botón flotante del Asistente lleva sombras propias

- **Problema**: `BotonAsistenteIa.razor.css:15, 23` declara dos `box-shadow` a mano.
- **Mejora**: si flota sobre el contenido es nivel Overlay y le corresponde `--shadow-overlay`;
  si no, no le corresponde ninguna (`06` § 5 le da un único uso permitido a ese token).
- **Coste**: trivial. **Riesgo**: bajo. **No se hizo** porque decidir si un FAB es Overlay es la
  misma pregunta que ODM-01, y conviene responderlas juntas.

### MP-05 · Fallbacks muertos en ComposerBar

- **Problema**: `ComposerBar.razor.css:15-21` usa `var(--color-success-50, #ecfdf5)` y tres más.
  Los tokens existen, así que el fallback nunca se aplica — pero su valor **no coincide** con el
  del token (`#ECFDF5` frente a `#EEFDF3`). Es una segunda fuente silenciosa del mismo valor.
- **Mejora**: retirar los cuatro fallbacks.
- **Coste**: trivial. **Riesgo**: ninguno. **No se hizo** por mantener el lote acotado.

---

## 4. Bugs confirmados

### BUG-01 · `.enlace-accion` no tiene estilos en ninguna hoja

- **Evidencia**: la clase se usa en `Facturacion.razor:110` y `Proyectos.razor:93, 188`, sobre
  elementos `<button>`. No existe ninguna regla `.enlace-accion` en ninguna hoja del proyecto
  (verificado con barrido completo, incluidos los bundles generados).
- **Consecuencia**: esos "Eliminar" / "Dar de baja" se pintan como botones nativos del navegador
  dentro de una tabla, no como acciones de fila.
- **Regla incumplida**: `08` § 4.1 — las acciones de fila se agrupan en **MenuAcciones**; y `06`
  § 1.2, ningún componente fuera del sistema de tokens.
- **No corregido en esta sesión**: elegir entre estilar `.enlace-accion` o sustituirlo por
  `MenuAcciones` es una decisión de patrón, no una sustitución de token.

### BUG-02 · `--color-danger-500` como color de texto incumple el 4.5:1

- **Evidencia**: once declaraciones usan `--color-danger-500` (`#EF4444`) como `color` de texto.
  Sobre Surface da **3.76:1**, por debajo del 4.5:1 que `02` § 8 exige al texto normal. `06` § 2.3
  es explícito: cada color del semáforo lleva "su fondo (`-50`) y **su tono de texto (`-700`)**".
- **Ubicaciones**: `Login.razor.css:59, 66` y sus cuatro gemelas de Account ·
  `CampoTexto.razor.css:42` (mensaje de validación) · `AsistenteIa.razor.css:160` ·
  `Facturacion.razor.css:164, 172` · `Proyectos.razor.css:59, 67` ·
  `Trabajadores.razor.css:22` · `list-page.css:144`.
- **No corregido**: es el mismo eje que ODM-03 y el semáforo es innegociable (DDL-010). Corregir
  once sitios de golpe cambia el rojo de todos los mensajes de error del producto: merece decisión
  expresa, no un barrido nocturno. La corrección es mecánica una vez decidido.
- **Precedente a favor de `-700`**: `Badge.razor.css` ya lleva un comentario explicando que
  `-500` sobre `-50` incumplía el AA "no negociable" (hallazgo P0-8), y `MenuAcciones` usa `-700`
  para su opción destructiva.

### BUG-03 · Un color de texto a mano en la traza de soporte

- **Evidencia**: `Components/Layout/TrazaSoporte.razor.css:7` declara `color: #1a1206`, un valor
  que no pertenece a ninguna rampa del sistema. Incumple `06` § 1.2.
- **No corregido**: la traza de soporte se pinta sobre un fondo ámbar propio, y elegir su token de
  texto (¿`--color-warning-700`? ¿`--color-text`?) es una decisión cromática, no una sustitución.

---

## 5. Deuda técnica

### DT-01 · La suite de integración es inestable

Dos ejecuciones completas de `CaeManager.IntegrationTests` fallaron con **tests distintos**
(`Aislamiento_Subcontrata` la primera, `El_desempate_por_Id_hace_estable_la_paginacion` la
segunda). **Ambos pasan al ejecutarse en solitario.** Esta rama no toca **ni un solo archivo
`.cs`** —57 archivos modificados, todos `.css`, `.js` o `.md`—, así que la inestabilidad es
previa y no la introduce la migración. Apunta a estado compartido de base de datos entre tests en
paralelo.

### DT-02 · Los `<button>` de la aplicación no heredan estilo del sistema

`base.css` solo declara `button { font-family: inherit }`. Cualquier `<button>` que no lleve la
clase `.boton` se pinta con el estilo nativo del navegador — así se explica BUG-01. Un reseteo
mínimo evitaría que el próximo botón sin clase pase inadvertido.

### DT-03 · `--color-neutral-*` sigue siendo consumido directamente

Después del barrido quedan 30 referencias directas a la rampa neutra desde componentes:
`-0` como texto sobre relleno (correcto), `-300`/`-400` como trazos decorativos y `-700` como
texto de badge. `06` § 10 pide consumir siempre tokens semánticos. No hay hoy token semántico para
"trazo decorativo" ni para "texto sobre relleno", así que cerrar esta deuda **empieza por una
decisión en `06`**, no por un reemplazo.

### DT-04 · `06` § 12 declara 23 hojas afectadas por `--shadow-card`; son 11

La medición de 2026-08-08 contó también los artefactos generados en `obj/` (los `.rz.scp.css` y
los bundles). Los usos reales eran 20 declaraciones en 11 hojas de código fuente. No cambia
ninguna conclusión del documento —sigue siendo el cambio de mayor superficie del reset— pero la
cifra conviene corregirla si `06` § 12 se vuelve a citar.

---

## 6. Bloqueos reales

**Ninguno.** Las diez ODM abiertas afectan a declaraciones concretas; ninguna impide continuar con
el resto del portal. Los cinco pasos de `06` § 12 están ejecutados.

---

## 7. Verificación

| Nivel | Alcance |
|---|---|
| `BUILD PASSED` | Solución completa, tras cada uno de los ocho commits. |
| `TESTS PASSED` | 384 Domain · 256 Application · 13 Architecture · 111 Web · 12 E2E · 349/350 Integration (ver DT-01). |
| `STATIC CHECK PASSED` | Barrido cruzado de todo `var(--…)` contra los tokens declarados: cero referencias inexistentes salvo `--color-info-600` (ODM-05). Cero `--shadow-card`, cero `--color-accent-*`, cero `--transition-fast/-base`. |
| `E2E VERIFIED` | Aplicación levantada contra Postgres local y recorrida autenticada: login, dashboard, clientes, facturación, proyectos, comunicaciones y apertura de Drawer. |

Lo verificado en navegador, con valores computados reales:

- Los 19 tokens del reset resuelven a su valor normativo (`--color-bg` `#F6F8FA`, `--color-border`
  `#E2E8EC`, `--color-text-muted` `#5F6E84`, la familia cian, los siete de motion).
- `--shadow-card`, `--color-accent-500`, `--transition-fast` y `--transition-base` ya **no
  resuelven a nada**: están retirados de verdad, no solo sin usar.
- `.tarjeta` y `.tarjeta-metrica` con `box-shadow: none`; el `::before` del gradient mesh con
  `content: none`.
- Facturación y Proyectos con espaciado real (32 px donde antes el navegador computaba 0) y el
  texto secundario en `rgb(95, 110, 132)`.
- Los toggles inactivos de la bandeja con borde `rgb(115, 129, 150)` y el activo con
  `rgb(35, 91, 194)`: el borde de control y la marca de estado no se pisan.
- El Drawer entra en 0,36 s con `cubic-bezier(0.16, 1, 0.3, 1)` y conserva `--shadow-overlay`,
  que es su único uso legítimo.
- Consola sin errores atribuibles a la migración; las nueve hojas de estilo sirven 200.

**Lo que NO se ha verificado**: tema oscuro (sigue sin rediseñar, DDL-043) y comportamiento
responsive por debajo de 1280 px.

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

---

## 3. Mejoras propuestas

*(en curso)*

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

*(en curso)*

---

## 5. Deuda técnica

*(en curso)*

---

## 6. Bloqueos reales

Ninguno. Ninguna de las ODM abiertas impide continuar con el resto de superficies.

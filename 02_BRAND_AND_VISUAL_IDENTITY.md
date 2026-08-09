# 02 — Brand and Visual Identity

**Estado**: Normativo · **Implementado hasta**: **solo el borde de control** `#738196` (DDL-063,
2026-08-09), que sí está en `tokens.css`. El resto de valores de este documento **no lo están**
todavía; el producto sigue con la paleta de 2026-07. La implementación es una fase posterior
(ver § 11).

**Autoridad**: este documento define **cómo debe sentirse y reconocerse** Hydra: personalidad,
sistema cromático, superficies, tipografía e iconografía. `01` define qué debe conseguir la
experiencia; `06_DESIGN_SYSTEM.md` deriva de aquí los tokens concretos y las escalas completas.
Si `02` y `06` divergen sobre un valor, manda `02`; sobre cómo se nombra o se estructura el
token, manda `06`.

**Origen de los valores**: todos los de las secciones 3 a 5 salen del banco visual de la Fase 2
—dos rondas de láminas comparativas con contenido idéntico, criterios escritos antes de mirar y
contraste medido—, y están registrados en
[`DESIGN_DECISION_LOG.md`](DESIGN_DECISION_LOG.md) como DDL-025 a DDL-030. **No se re-abren sin
pasar por el Log.**

---

## 1. Personalidad

La personalidad de Hydra son **cuatro atributos**, y no hay una segunda lista (DDL-008):

**Precision · Calm · Trust · Intelligence**

| Atributo | Qué significa en la interfaz |
|---|---|
| **Precision** | Rejillas estables, columnas que no bailan, cifras alineadas, estados exactos. Nada se mueve bajo el cursor. |
| **Calm** | Fondos neutros, ausencia de ruido, un solo foco de movimiento por pantalla. La urgencia la marca el semáforo, no la interfaz entera. |
| **Trust** | Feedback inequívoco, procedencia visible, ningún dato presentado con más certeza de la que tiene. |
| **Intelligence** | El sistema trabaja y lo dice: actividad en curso, sugerencias con confianza, contexto resuelto por adelantado. |

"Ingeniería", "documentación técnica" y "profesionalidad" **no son atributos de personalidad**:
son **expresiones** por las que esos cuatro se manifiestan. Esta distinción es normativa —
tratarlas como personalidad es lo que permitiría re-derivar otra paleta dentro de seis meses,
que es el modo de fallo que este reset existe para cerrar.

La cadena es siempre: **Personalidad → atributos → expresión visual → paleta → tokens.** Nunca a
la inversa.

## 2. La sensación objetivo

Un gestor abre Hydra y percibe: *"tengo el control; el sistema está trabajando conmigo"*.

No: *"qué bonito"*. No: *"cuántos datos"*.

## 3. Sistema cromático

### 3.1 Los roles mandan sobre los colores

El color de Hydra se define por **rol**, no por gusto. Antes de elegir un valor se responde qué
representa (DDL-030):

| Caso | Rol |
|---|---|
| El usuario puede actuar | **Hydra Blue** |
| Hydra está actuando, o el dato procede del sistema | **Hydra Cyan** |
| Información neutral o contextual | **Info** |
| Cumplimiento y riesgo | **Semáforo** |
| Error | **Error** |
| Advertencia | **Warning** |

**Regla de agencia** (DDL-009), aplicable en revisión de código y de diseño:

> Si actúa el usuario → azul. Si actúa Hydra → cian. Si es cumplimiento o riesgo → semáforo.

Todo uso nuevo de cian debe poder responder *"¿quién actúa aquí?"* con *"Hydra"*. Si no puede,
no es cian.

**El cian nunca es el nuevo `info`.** "Sincronización completada" es Hydra actuando → cian.
"Este centro pertenece a Barcelona" es contexto → info. Confundirlos es cómo el cian se diluye
hasta volverse un azul secundario.

### 3.2 Hydra Blue — acción humana (DDL-025)

| Uso | Valor | Contraste |
|---|---|---|
| Acción primaria, enlaces, foco, estado activo | `#235BC2` | 6.27:1 sobre blanco ✓ |
| Variante / acento de interacción | `#2F6FDD` | 4.31–4.73:1 según superficie ✓ (criterio 3:1) |
| Enlaces sobre superficie oscura | `#5CA2F4` | >5.5:1 ✓ |

**`#2F6FDD` es un acento no textual** (DDL-061). Su rol es señalar interacción sin portar texto:
borde o marca de estado interactivo, indicador visual de foco, e iconografía decorativa donde
corresponda. Su criterio es el **3:1** de elemento no textual, que cumple sobre todas las
superficies claras. **No se usa como color de texto ni de enlace**: sobre Canvas daría 4.44 y
sobre Subtle 4.31, por debajo del 4.5 exigido al texto normal.

Escala completa 50–900: se deriva de este anclaje en `06`. **No se inventa ni se importa de una
librería** (DDL-011).

El azul se reserva para acción e interacción. Nunca es un bloque grande de color decorativo.

### 3.3 Hydra Cyan — el sistema actuando (DDL-027)

El cian es **asimétrico por tema**, y no por capricho: es un hallazgo medido del banco.

| Uso | Claro | Oscuro |
|---|---|---|
| Texto e iconos de sistema | `#0C7792` (5.16:1 ✓) | `#2BD4F0` (>8:1 ✓) |
| Indicador no textual (punto "en curso", borde, tinte) | `#0E96B4` (3.48:1 ✓) | `#2BD4F0` |

**No existe un cian brillante válido en modo claro.** Medido: `#00F0FF` da 1.41:1, `#00C2E0`
da 2.14:1 y `#06B6D4` da 2.43:1 — los tres por debajo de su umbral. Cualquier propuesta futura
de "cian eléctrico" para modo claro está ya respondida con datos.

La asimetría además es coherente con el significado: el sistema **brilla en oscuro y se contiene
en claro**.

Presupuesto de uso del cian, estricto: IA cuando actúa, automatización, sincronización,
conectores, estados en tiempo real y procedencia de datos. **Nunca** rellena un botón sólido,
nunca es color de acción, nunca es decoración.

### 3.4 Semáforo de cumplimiento — innegociable (DDL-010)

| Estado | Valor |
|---|---|
| `Vigente` | `#22C55E` |
| `Proximo` | `#F59E0B` |
| `Urgente` · `Vencido` · acceso bloqueado | `#EF4444` |

Es la única parte de la paleta que **no se toca en ningún rediseño de identidad**, y **domina
sobre la marca**: si en una pantalla el azul o el cian compiten en peso visual con un rojo o un
ámbar, la pantalla está mal.

Se usa **exclusivamente** para estado de vigencia y cumplimiento. Nunca como color decorativo en
otro contexto, para que el usuario lo reconozca al instante en cualquier pantalla.

Modificador admitido: **"Riesgo en visita"** (DDL-039) — ámbar, para un documento vigente hoy
que caducaría antes de que termine la próxima visita al centro. **No es un cuarto estado**:
sigue siendo `Vigente`, solo cambia la fecha de referencia del cálculo. Está registrado como
**modificador**, no como valor de estado, en `UBIQUITOUS_LANGUAGE.md`, que enlaza a la definición
de `DOMAIN.md` § 68 (DDL-052, DDL-066).

### 3.5 Info — contexto neutral

`Info` es el neutral de contexto: slate, sin carga de agencia ni de cumplimiento. Su valor
exacto se fija en `06` sobre la escala neutra.

Restricción: **debe distinguirse tonalmente del cian**, o el cian acabará filtrándose por ahí.

### 3.6 Lo que sale de la paleta

- **El cobre `#C97B2A` deja de formar parte de la UI** (DDL-012). Con la dualidad azul/cian un
  tercer acento compite y diluye. Puede conservarse como recurso de marca fuera del producto.
- **Ninguna escala de librería se adopta literalmente** como identidad (DDL-011).
- **Ningún color de marca de terceros entra en la UI**: el verde de WhatsApp colisiona con el
  semáforo y su variante verde-azulada con el cian. Los canales se identifican **solo por la
  forma del icono**, en **color neutro** — el matiz de marca queda descartado, ni siquiera
  desaturado en el trazo (DDL-037 en su parte de forma; DDL-048 para la cromática).

## 4. Superficies y profundidad

### 4.1 Cuatro niveles (DDL-013)

```
Canvas  →  Surface  →  Elevated  →  Overlay
```

La profundidad **comunica jerarquía, no decoración**. Se expresa por **fondo, borde y
contraste**; la **sombra solo cuando existe una relación espacial real**, es decir, en Overlay.

| Nivel | Claro (DDL-028) | Oscuro (DDL-026) | Uso |
|---|---|---|---|
| Canvas | `#F6F8FA` | `#0E141B` | Fondo de página |
| Surface | `#FFFFFF` | `#17212C` | Superficie de contenido |
| Surface-subtle | `#F1F5F7` | `#131B24` | Islas internas, agrupaciones |
| Elevated | `#FFFFFF` + borde reforzado, **sin sombra** | `#202B36` | Panel contextual, inspector |
| Overlay | `#FFFFFF` + **sombra** | `#202B36` + sombra | Modales, drawers, popovers |

Bordes: `#E2E8EC` (normal) y `#CBD5E1` (reforzado) en claro; `#293644` y `#3A4A5C` en oscuro.
Los dos son **estructurales**: agrupan y expresan profundidad, y no están sujetos al 3:1
(DDL-062).

**Borde de control** (DDL-063): `#738196`, **el mismo valor en ambos temas**. Es el borde que
identifica un control interactivo y por tanto sí está sujeto al 3:1 — da 3.61:1 en claro y 3.64:1
en oscuro contra el fondo más restrictivo de cada tema. Es el escalón que DDL-029 retiró del
**texto** por quedarse en 3.96:1: insuficiente para cuerpo de 14 px, holgado para un elemento no
textual.

**Texto principal** (DDL-057): `#161E27` en claro — 16.81:1 sobre Surface ✓, 15.79:1 sobre
Canvas ✓; `#E7EAEE` en oscuro (§ 5). **Texto secundario**: `#5F6E84` en claro (DDL-029) y
`#8592A3` en oscuro (§ 5).

### 4.2 Islas, no card-ificación (DDL-014)

Las "islas" son **jerarquía de zonas funcionales** dentro de una superficie. Hydra **no se
convierte en una colección de tarjetas flotantes**: si todo flota, nada destaca y el resultado
es un dashboard SaaS genérico.

Prueba práctica: al entrecerrar los ojos ante una pantalla, deben distinguirse dos o tres
bloques, no quince rectángulos iguales.

### 4.3 Shell (DDL-015)

La sidebar global se mantiene. No se sustituye por un dock flotante. Su colapso a iconos puede
evolucionar; su existencia no se discute en este ciclo.

## 5. Modo oscuro (DDL-026, DDL-021)

La identidad oscura es **slate** — valores en la tabla de § 4.1 —, elegida sobre la alternativa
grafito por transmitir plataforma operativa antes que herramienta informática.

Texto principal `#E7EAEE` (13.49:1 sobre Surface ✓); texto secundario `#8592A3` (5.14:1 sobre
Surface ✓, 4.55:1 sobre Elevated ✓ — es el par más ajustado del tema).

**El modo oscuro no está rediseñado todavía**, y `prefers-color-scheme` sigue desactivado a
propósito: un usuario con el sistema operativo en oscuro no debe aterrizar ahí sin elegirlo. La
reactivación ocurre después del reset, no antes (DDL-043). Lo que **sí** es obligatorio desde
ya: **toda arquitectura de tokens nace contemplando los dos temas**, nunca uno con el otro como
parche.

## 6. Tipografía

Una sola familia en todo el sistema: **Inter** (variable), con `system-ui` de reserva. Ninguna
pantalla usa una fuente distinta.

El cuerpo base de la aplicación es **14px**, no 16px. Es una convención deliberada de producto
denso en información —el mismo criterio de Stripe o Linear—, no una desviación: la escala
completa de títulos, cuerpo y metadatos existe y se usa donde corresponde. La escala concreta se
fija en `06`.

Cifras que se comparan en columna usan **numeración tabular**, sin excepción: en una lista de
fracciones de cumplimiento o de fechas, la desalineación de dígitos es ruido de lectura.

## 7. Iconografía

Un **único set outline** en todo el sistema: trazo 1,75, sin relleno, remates redondeados.
Implementado como SVG inline, sin dependencia de librería ni de fuente de iconos.

- **Nunca se mezcla con emojis** ni con un segundo estilo de icono.
- Los iconos son decorativos (`aria-hidden`) y van acompañados de texto visible o de nombre
  accesible en el elemento interactivo que los contiene.
- **La iconografía es obligatoria en superficies de resumen** (DDL-036): sin ella, un número de
  color sobre fondo blanco lee como una hoja de cálculo, que es exactamente lo que Hydra no debe
  parecer.

## 8. Accesibilidad como parte de la identidad

No es un requisito externo que se comprueba al final: una identidad que falla contraste no es
una identidad válida.

| Umbral | Aplica a |
|---|---|
| **4.5:1** | Texto normal |
| **3:1** | Texto grande (≥24px, o ≥18.66px en negrita) |
| **3:1** | Componentes de interfaz: el borde que **identifica un control** y los indicadores de estado |

**Qué borde está sujeto al 3:1** (DDL-062). El umbral aplica al borde que constituye el **límite
visual de un control interactivo** —campo de texto, área de texto, selector, zona de soltar, botón
que no lleva relleno— y a los **indicadores de estado**, incluido el de foco. **No aplica** a
separadores, contornos de agrupación ni bordes de tarjeta, panel o modal: agrupan contenido, no
identifican un control, y exigirles 3:1 obligaría a un contorno oscuro en toda la interfaz —
exactamente la card-ificación que DDL-014 prohíbe.

Regla de decisión ante un borde nuevo: *si el borde desaparece, ¿deja de poder distinguirse dónde
empieza y acaba un control?* Si la respuesta es sí, está sujeto al 3:1.

Reglas permanentes:

- **El color nunca es el único portador de significado.** Todo estado lleva texto, nombre
  accesible o ventana de contexto. Un recuento numérico sin etiqueta es admisible **solo** si
  abre una ventana con el desglose literal (DDL-033).
- **El foco siempre visible**, con outline sólido de 2px y alto contraste. Un glow es un extra,
  jamás el indicador.
- **Nada que se abra solo con hover.** Toda ventana de contexto se abre también con foco de
  teclado y lleva nombre accesible (DDL-032).
- **Defecto corregido y a no repetir** (DDL-029): el texto secundario del modo claro pasa de
  `#738196` (3.96:1 — incumplía para el cuerpo de 14px) a `#5F6E84` (5.18:1). Estuvo en
  producción desde 2026-07 sin detectarse; lo encontró el protocolo de contraste del banco.

## 9. Lo que esta identidad rechaza

| Rechazado | Motivo |
|---|---|
| Neón, cian eléctrico como acento general | Falla contraste en claro y desplaza al semáforo |
| Glassmorphism, fondos translúcidos | Reduce legibilidad sobre datos densos |
| Glow ambiental y luz que sigue al cursor | Ornamento; compite con la información (DDL-018) |
| Elementos que se desplazan bajo el cursor | Rompe memoria espacial y puntería — lo contrario de Precision (DDL-017) |
| Gradientes como decoración | Textura sin significado |
| Sombra como recurso general | Solo Overlay tiene relación espacial real |
| Tarjetas flotantes para todo | Destruye la jerarquía que la profundidad debía crear |
| Emojis como iconografía | Rompe el set único y el tono |

## 10. Decisiones que respaldan este documento

| Decisión | Aporta |
|---|---|
| DDL-008 | Personalidad y su taxonomía única (§ 1) |
| DDL-009 · DDL-030 | Dualidad de agencia y taxonomía por rol (§ 3.1) |
| DDL-025 | Valores de Hydra Blue (§ 3.2) |
| DDL-027 | Familia cian asimétrica y su justificación medida (§ 3.3) |
| DDL-010 · DDL-039 | Semáforo y modificador "Riesgo en visita" (§ 3.4) |
| DDL-011 · DDL-012 | Sin escalas de librería; retirada del cobre (§ 3.6) |
| DDL-037 | Canal por icono, no por color de marca (§ 3.6) |
| DDL-013 · DDL-028 · DDL-026 | Superficies y sus valores (§ 4, § 5) |
| DDL-014 · DDL-015 | Islas y shell (§ 4.2, § 4.3) |
| DDL-021 | Estado transitorio del modo oscuro (§ 5) |
| DDL-029 · DDL-032 · DDL-033 | Accesibilidad (§ 8) |
| DDL-017 · DDL-018 · DDL-036 | Rechazos e iconografía (§ 7, § 9) |

## 11. Alcance ya decidido y camino a implementación

No quedan preguntas abiertas sobre esta identidad. Lo decidido y pendiente de ejecutar:

| Decisión | Efecto |
|---|---|
| DDL-043 | El **modo oscuro** se implementa después del reset; `prefers-color-scheme` se reactiva entonces. Los valores de § 5 ya son definitivos: es calendario, no identidad |
| DDL-048 | **Canal por icono neutro** — el matiz de marca queda descartado: no hay hueco cromático libre entre el semáforo y el cian (§ 3.6) |
| DDL-052 | **Diccionario cerrado de estados** en `UBIQUITOUS_LANGUAGE.md`, con "Riesgo en visita" registrado como **modificador**, no como estado — impide que una integración lo persista como `EstadoDocumento` (§ 3.4) |
| DDL-045 | Se **retiran** el glow en hover de enlaces y el gradient mesh de los KPI; el ripple queda acotado a acciones primarias. Detalle en `07` |

**Camino a implementación** (fase posterior, no ahora): los valores de este documento se
convierten en `tokens.css` respetando la regla que ya demostró funcionar — **los nombres de
variable se mantienen estables entre rediseños; lo que cambia son los valores**. Esa propiedad
es la que hace que un cambio de identidad no obligue a tocar los más de cincuenta archivos que
consumen los tokens, y no se abandona.

## 12. Preguntas abiertas

Ninguna.

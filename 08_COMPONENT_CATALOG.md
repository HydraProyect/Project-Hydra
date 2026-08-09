# 08 — Component Catalog

**Estado**: Normativo · **Implementado hasta**: los 32 componentes de § 4 existen en
`src/CaeManager.Web/Components/DesignSystem/`. Los de § 6 **no existen** y están declarados como
tales. Ninguno consume todavía los tokens de `06`.

**Autoridad**: define **qué componentes existen, cuándo se usan y qué comportamiento normativo
tienen**. Es el último eslabón de la normativa: por debajo solo hay blueprints e implementación.

**Este documento no responde**:

| Pregunta | Documento |
|---|---|
| Qué componentes necesita una pantalla concreta | `docs/blueprints/` |
| Cómo se compone una superficie de trabajo | `05` |
| Qué valores visuales usa el componente | `06` |
| Cómo se comporta la interacción que ejecuta | `04` |
| Qué decisión llevó a crearlo | `DESIGN_DECISION_LOG.md` |
| Cómo está implementado | el código |

**Regla anti-duplicación**: si un comportamiento ya está gobernado por `04` o `06`, este
documento **lo referencia y no lo repite**. Solo documenta lo específico del componente. Un
catálogo que resume `04` y `06` se desincroniza de ambos en la primera semana.

---

## 1. Regla de admisión

No todo lo reutilizable es componente del sistema.

| Es componente del sistema | No lo es |
|---|---|
| Resuelve un problema **transversal** a varias features | Depende de un tipo de datos de un módulo |
| Su contrato es **estable** | Su contrato aún se está descubriendo |
| Tiene **al menos dos consumidores reales**, o es evidente que los tendrá | Tiene uno solo y podría ser el único |

**Un patrón no es un componente.** `05` define patrones —Action Center, Unified Timeline— cuyo
contrato técnico **no se congela hasta que exista un segundo consumidor real** (DDL-007).
Extraerlos ahora produciría una abstracción equivocada. Hasta entonces viven en su feature, no
aquí.

Una pieza que depende de un DTO o de un servicio de un módulo concreto **vive en ese módulo**,
aunque se parezca a un componente. La cercanía visual no es reutilización.

## 2. Cuándo se documenta un componente

**Cuando se construye, nunca antes.** Especificar por adelantado componentes que no existen es
trabajo que se desactualiza antes de usarse, y produce un catálogo teórico que nadie implementa.

La disciplina ya estaba en el sistema anterior y se conserva porque funcionó: § 6 lista lo que el
reset **exige** sin describir su API.

## 3. Contrato común

No hace falta repetir esto en la ficha de cada componente. Se divide en dos bloques porque
**exigir foco visible a una tarjeta no interactiva sería una regla imposible de cumplir**, y una
norma que no se puede aplicar literalmente termina ignorándose entera.

### 3.1 Universal — todo componente

1. **Consume tokens** (`06`). No codifica ningún valor a mano.
2. **El color nunca es su único portador de significado** (`02` § 8).
3. **Su movimiento pertenece a un tier** de `07` y respeta `prefers-reduced-motion`.
4. **Acepta atributos adicionales**, para que un consumidor pueda pasar `aria-*`, `data-*` o
   cualquier atributo HTML sin ampliar su API. *Nota técnica que ahorra un fallo real: al
   combinar clases, el atributo emitido en último lugar reemplaza al anterior en vez de
   fusionarse; la clase propia y la recibida deben unirse explícitamente.*
5. **Responsive** según los breakpoints de `06`.
6. **Contempla los estados que puede alcanzar**: cargando y error cuando pueda producirlos,
   vacío cuando pueda no tener contenido.

### 3.2 Adicional — componentes con superficie interactiva

Aplica **a cada superficie interactiva** que el componente exponga, no al componente como bloque:
una tarjeta con un enlace dentro no es interactiva, pero ese enlace sí lo es.

7. **Estados de interacción**: reposo, hover, foco, activo y deshabilitado si aplica.
8. **Foco visible** con outline sólido; nunca sustituido por un efecto.
9. **Operable con teclado** y con **nombre accesible**.

Un componente puramente presentacional cumple 1–6 y nada más. Los requisitos 7–9 no se le
"perdonan": simplemente no tienen sujeto.

## 4. Catálogo vigente

32 componentes. Se listan por función, con **solo lo específico** de cada uno.

### 4.1 Acción

| Componente | Cuándo se usa | Específico |
|---|---|---|
| **Boton** | Toda acción | La variante primaria es la única que porta el ripple (`07` § 5). Una pantalla tiene **una** acción primaria por zona |
| **BotonCopiar** | Copiar un valor al portapapeles | Confirma la copia; sin confirmación el usuario no sabe si funcionó |
| **MenuAcciones** / **ItemMenuAccion** | Agrupar 2 o más acciones de fila | **No usar con una sola acción**: abrir un menú para una opción es más lento que verla. La opción destructiva se distingue del resto |
| **BarraAccionesLote** | Acciones sobre una selección | Muestra el recuento y **no ejecuta**: confirma quien la usa (`04` § 3.6) |
| **DialogoConfirmacion** | Confirmar lo destructivo | El cuerpo declara la consecuencia real (`04` § 2.3) |

### 4.2 Formulario

| Componente | Cuándo se usa | Específico |
|---|---|---|
| **CampoTexto · CampoTextarea · CampoSelect** | Entrada básica | Validación inline al salir del campo (`04` § 5.1) |
| **CampoBuscarSelect** | Elegir de una lista conocida y acotada | Sugerencia nativa del navegador; para listas grandes o con creación, usar el siguiente |
| **SelectorEntidad** | Elegir una entidad con búsqueda, con o sin creación | Pinta su propia lista; puede ofrecer crear lo que no existe (`04` § 2.1). La selección no debe depender de temporizadores que compitan con la pérdida de foco |
| **SelectorMultiple** | Elegir varios de un conjunto grande | Búsqueda, paginación y filtro de relacionados dentro del propio selector |
| **ZonaSoltarArchivo** | Adjuntar archivos | Arrastrar, soltar y pegar; declara formato y tamaño máximo **antes** de intentar la subida |

### 4.3 Contenedores y superficie

| Componente | Cuándo se usa | Específico |
|---|---|---|
| **Modal** | Confirmación o formulario corto sobre el contenido | Nivel Overlay (`06` § 3). Devuelve el foco a quien lo abrió |
| **Drawer** | Alta de un agregado, o formulario lateral | **Solo alta**: la edición es in situ (`04` § 2.2). Nunca anidado |
| **Tarjeta** | Agrupar contenido relacionado | **Sin sombra** (`06` § 5). La agrupación se expresa por fondo y borde |
| **TarjetaMetrica** | Mostrar una cifra con su etiqueta | Cifra con numeración tabular; la variante discreta existe para no dar el mismo peso a todo |
| **Pestanas** | Facetas de una misma entidad | **Activación manual**: mover el foco no cambia de pestaña, hay que confirmar. Evita disparar la carga de cada pestaña al hojear con teclado |
| **SeccionColapsable** | Contenido secundario o niveles de un workspace | Su contenido se monta **al expandir**. Cuando la página necesita expandir o colapsar todo, el estado lo lleva la página, no el componente |

### 4.4 Estado y feedback

| Componente | Cuándo se usa | Específico |
|---|---|---|
| **EstadoCargando** | Espera | Esqueleto con la forma del contenido final, no un indicador genérico |
| **EstadoVacio** | Sin datos | Causa y siguiente paso. **Un vacío nunca se presenta como éxito** (`04` § 6) |
| **AnfitrionToasts** | Avisos | Máximo tres visibles; los de error no se autodescartan (`04` § 7.1) |
| **ProgresoConMensajes** | Proceso largo con etapas | Dice **en qué va**, no solo que sigue trabajando |
| **IndicadorPasos** | Progreso de un flujo | Solo indica progreso; el contenido de cada paso lo aporta quien lo usa (`05` § 6) |
| **Badge** | Estado o recuento | Léxico cerrado (`04` § 4.1). En variante de solo recuento, **exige ventana de contexto** (`04` § 4.2) |
| **AnilloCumplimiento** | Fracción de cumplimiento | **No pinta nada si no hay universo**: "sin requisitos" no es un 0 %. Cortes de tono: `100 %` Éxito · `≥50 %` Advertencia · resto Peligro — **decisión propia, no derivada del semáforo documental** (DDL-067) |
| **FiltroEstado** | Filtrar por estado | Opciones **de peor a mejor**; "sin datos" es opción propia (`04` § 3.2) |

### 4.5 Navegación y soporte

| Componente | Cuándo se usa | Específico |
|---|---|---|
| **Breadcrumb** | Camino recorrido | En Context Panel refleja **el recorrido**, no la jerarquía (`05` § 3.3) |
| **PaginadorSimple** | Paginación | **Único paginador** de la plataforma (`04` § 3.4) |
| **BarraHerramientasLista** | Controles sobre la lista entera | Alberga el modo de selección y expandir/colapsar; apagar la selección la limpia (`04` § 3.5) |
| **Icono** | Iconografía | Set outline único, sin emojis. Decorativo: el nombre accesible lo aporta quien lo contiene (`02` § 7) |
| **AtajosListaTeclado** | Navegación por teclado en listas | Se ignora mientras el foco está en un campo de texto |

## 5. Ficha de un componente

Cuando un componente se documenta en detalle, siempre con esta estructura:

```
Qué es y cuándo usarlo
Cuándo NO usarlo          ← lo que más ahorra: evita el uso por parecido visual
Variantes y estados
Accesibilidad             ← rol, teclado, nombre accesible
Responsive
Contratos que hereda      ← de 04 y 06, referenciados, no repetidos
```

**"Cuándo NO usarlo" es obligatorio.** Casi todos los usos equivocados de un sistema de diseño
vienen de elegir el componente que se parece, no el que corresponde.

## 6. Componentes que el reset exige y aún no existen

Se declaran, **no se especifican**, hasta que se construyan (§ 2):

| Componente | Lo exige | Nota |
|---|---|---|
| **Ventana de contexto** | DDL-033, DDL-032 | La pieza más urgente: `04` § 4.2 la hace obligatoria en recuentos y marcas de procedencia, y hoy no existe como componente. Debe abrirse con puntero **y** con foco |
| **Marca de procedencia** | DDL-032 | Icono más ventana de contexto; sin etiqueta de texto |
| **Badge de solo recuento** | DDL-036 | Variante de Badge, no componente nuevo. No se admite sin su ventana de contexto |

**Deliberadamente fuera** hasta tener un segundo consumidor real (DDL-007, § 1): tarjeta de
Action Center y Unified Timeline. Son patrones de `05`, no componentes.

## 7. Componentes que se retiran o cambian con la implementación

| Elemento | Qué pasa |
|---|---|
| Ripple universal en **Boton** | Se acota a la variante primaria (DDL-045) |
| Sombra en **Tarjeta** | Desaparece; la elevación se expresa por fondo y borde (DDL-013) |
| Gradiente animado en **TarjetaMetrica** | Se retira (DDL-045) |
| Modo de edición del **Drawer** | Ya retirado en el producto: el Drawer es solo alta |

## 8. Decisiones que respaldan este documento

| Decisión | Aporta |
|---|---|
| DDL-007 | Patrones sin contrato congelado no son componentes (§ 1, § 6) |
| DDL-032 · DDL-033 | Ventana de contexto y marca de procedencia (§ 6) |
| DDL-036 | Badge de recuento y reglas de acción (§ 4.1, § 4.4) |
| DDL-013 · DDL-045 | Retirada de sombra, gradiente y ripple universal (§ 7) |
| DDL-053 | Los catálogos se construyen con estos componentes, sin arquetipo (§ 4) |
| DDL-041 | Densidad única, que fija la métrica de las variantes compactas |

## 9. Preguntas abiertas

Ninguna.

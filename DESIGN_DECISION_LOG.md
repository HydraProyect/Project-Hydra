# Hydra — Design Decision Log

**Estado**: Normativo · **Implementado hasta**: ninguna decisión de este registro está
implementada en código todavía — este documento cierra la Fase 1.5 y la Fase 2 (banco visual)
del Design & UX Architecture Reset; la redacción de los documentos 01–08 y su implementación
son fases posteriores.

**Qué es**: registro de decisiones de experiencia y diseño — qué se decidió, por qué, y qué
decisión anterior reemplaza. **Qué no es**: una especificación paralela. El contenido de una
regla vive en su documento normativo; aquí viven su autoridad y su historia.

## Modelo de autoridad

- **Decision Log** — autoridad sobre las *decisiones*: qué se decidió, por qué, qué reemplaza.
  No contiene especificaciones.
- **Documentos 01–08** — autoridad sobre las *especificaciones normativas* derivadas de esas
  decisiones.
- **Blueprints** (`docs/blueprints/`) — especificación concreta de una superficie, subordinada
  a la normativa.
- **Implementación** — realidad técnica; nunca fuente de autoridad sobre las capas anteriores.

Si este Log y un documento 01–08 divergen sobre *qué se decidió*, manda el Log; sobre *cómo se
especifica*, manda el documento — y la divergencia se registra como conflicto en ambos casos.

**Regla de precedencia entre fuentes**: Normativa vigente → Blueprint → Implementación.
Ante conflicto entre documentos normativos: no resolver en silencio; registrar el conflicto
aquí, revisar la decisión señalada, actualizar el documento y anotar el cambio. Nunca usar un
blueprint o código anterior para contradecir en silencio una regla vigente.

**Disciplina de estado**: todo documento normativo declara su estado (Normativo | Operativo |
Histórico) y su límite de implementación ("implementado hasta: X"). Ningún documento presenta
como implementado o presente algo que no está construido.

**Estados de una entrada**: Vigente · Vigente (valores pendientes → OD-x) · Sustituida (por DDL-x).

---

## Producto

### DDL-001 — Definición de "Premium"
- **Decisión**: Premium = principalmente operacional (reducción de trabajo, claridad, precisión,
  confianza, baja carga cognitiva, automatización, inteligencia contextual). La calidad SaaS
  (consistencia, tipografía, spacing, accesibilidad) es su expresión; la estética visual es
  condimento, nunca núcleo. **Test de revisión**: ante cualquier propuesta visual — "¿reduce
  trabajo, lo explica, o lo decora?"; solo la tercera respuesta necesita justificarse.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: existían tres interpretaciones de "premium" compitiendo (visual, SaaS, operacional);
  la ambigüedad causó el ciclo de rediseños de 2026-07.
- **Impacto**: criterio raíz de todo el reset; filtra cada decisión visual posterior.
- **Reemplaza**: la interpretación implícita "premium = look fluido / micro-interacciones" de las
  rondas de 2026-07 (histórico `DESIGN_SYSTEM.md` § Historial, archivado).
- **Documentos afectados**: `01_PRODUCT_EXPERIENCE.md`.

### DDL-002 — El CRUD deja de ser el paradigma mental principal
- **Decisión**: CRUD, tablas, selección masiva e imports/exports siguen siendo capacidades
  fundamentales, pero pasan a ser la capa profunda de operación, no el modelo organizador de la
  experiencia.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: el modelo lista+Drawer ya no describe ni el código actual (Centro 360,
  Communication Workspace); la operativa masiva es "lingua franca del sector" (auditoría UX) y
  no debe degradarse.
- **Reemplaza**: el paradigma implícito de `UX_PATTERNS.md` § Crear/Editar como modelo por
  defecto de toda entidad.
- **Documentos afectados**: `01`, `04_UX_PATTERNS.md`.

## Arquitectura UX

### DDL-003 — Modelo mental principal
- **Decisión**: Contexto → Estado → Atención → Acción → Workflow → Automatización → IA. La
  interfaz responde en orden: ¿qué está pasando? → ¿qué requiere atención? → ¿qué puedo hacer?
  → ¿qué datos consulto o modifico?
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: validado empíricamente por las dos superficies mejor resueltas del producto
  (Centro 360, Communication Workspace) — se promueve a norma lo ya construido.
- **Reemplaza**: el modelo Entidad → Lista → Tabla → Crear/Editar → Drawer.
- **Documentos afectados**: `01`, `03`, `04`, `05`.

### DDL-004 — Cuatro arquetipos de superficie
- **Decisión**: Operational Home · Entity Workspace · Context Panel · Flow Surface. Toda
  **superficie operativa** se clasifica en uno de los cuatro (**enmendado por DDL-053**: los
  catálogos y la administración quedan fuera del sistema de arquetipos por diseño).
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: los arquetipos existían sin nombre; su ausencia causó el conflicto "panel angosto
  vs workspace ancho".
- **Documentos afectados**: `03_INFORMATION_ARCHITECTURE.md`, `05_WORKSPACE_PATTERNS.md`.

### DDL-005 — Centro 360 es el patrón maestro de Entity Workspace
- **Decisión**: el patrón Centro 360 es la referencia canónica del arquetipo Entity Workspace.
  **Reserva**: el tercer nivel anidado queda a validar con datos reales de producción
  (`PLAN-EJECUCION-UX.md` § Límites) antes de canonizarse. Ver DDL-030 y DDL-031, que resuelven
  dos huecos del patrón detectados en el banco visual.
- **Estado**: Vigente (con reserva) · **Fecha**: 2026-08-08 (origen: repriorización 2026-08-05)
- **Motivo**: es el experimento real con mejor resultado operativo; ya absorbió `/asignaciones`
  y `/evaluaciones` y se replicó en `/empresas`.
- **Documentos afectados**: `05`; blueprint retroactivo de Centro 360.

### DDL-006 — Context Panel ≠ Entity Workspace
- **Decisión**: son patrones distintos y formalmente separados. Context Panel = consultar sin
  perder el contexto (peek lateral, pila-breadcrumb). Entity Workspace = operar en profundidad
  (página). Regla: **panel para consultar, workspace para operar**.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: la falta de frontera generó propuestas contradictorias (ensanchar el panel a 90%
  vs panel angosto ~480-520px, decisión cerrada).
- **Documentos afectados**: `05`; el contenido del histórico `PLAN-CONTEXT-WORKSPACE.md` (archivado) migra a `05`.

### DDL-007 — Action Center: patrón de plataforma, contrato sin congelar
- **Decisión**: el Action Center (decisiones, no información; confianza visible;
  Confirmar/Editar/Descartar; la IA propone, el humano confirma) se adopta como patrón
  conceptual de plataforma. Su **contrato técnico reutilizable no se congela** hasta que exista
  un segundo consumidor real (Documentos o Visitas, `docs/COMUNICACIONES.md` § 16.7).
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: generalizar desde un único consumidor produce abstracciones equivocadas; escribir
  la norma sí, congelar el framework no.
- **Documentos afectados**: `05`; blueprint de Comunicaciones.

## Identidad visual

### DDL-008 — Personalidad de marca
- **Decisión**: la personalidad de Hydra son cuatro atributos, únicos y sin lista paralela:
  **Precision · Calm · Trust · Intelligence**. "Ingeniería", "documentación técnica" y
  "profesionalidad" **no** son atributos de personalidad: son expresiones mediante las que la
  personalidad se manifiesta. Estructura fijada: Personalidad → atributos → expresión visual →
  paleta → tokens.
- **Estado**: Vigente · **Fecha**: 2026-08-08 (revalida la realineación de 2026-07)
- **Motivo**: dos taxonomías coexistiendo ("ingeniería" como personalidad aparte) es la
  ambigüedad que permitiría re-derivar otra paleta en seis meses — el modo de fallo de 2026-07.
- **Documentos afectados**: `02_BRAND_AND_VISUAL_IDENTITY.md`.

### DDL-009 — Dualidad de agencia: Hydra Blue / Hydra Cyan
- **Decisión**: Azul = acción o decisión **humana**. Cian = acción, actividad o **procedencia
  del sistema** (automatización, sincronización, workflows, integraciones, IA cuando actúa,
  tiempo real). El cian **no** significa exclusivamente IA y **no** sustituye a `info`: un
  mensaje sin agente ni estado de cumplimiento es información, no actividad del sistema.
  **Regla de agencia**: si actúa el usuario → azul; si actúa Hydra → cian; si representa
  cumplimiento o riesgo → semántico. Todo uso nuevo de cian debe poder responder "¿quién actúa
  aquí?" con "Hydra", o se rechaza en revisión.
  **Ejemplo canónico**: tarjeta del Action Center — la sugerencia lleva marca cian, el botón
  Confirmar es azul.
  **Refinamiento por escala (DDL-032)**: la procedencia se marca, no se etiqueta.
- **Estado**: Vigente · **Fecha**: 2026-08-08 · valores en DDL-025 y DDL-027
- **Motivo**: identidad funcional, no solo cromática; diferenciador frente al sector; evita que
  el cian degenere en "otro azul secundario".
- **Documentos afectados**: `02`, `06_DESIGN_SYSTEM.md`, `07_MOTION_SYSTEM.md`.

### DDL-010 — El semáforo de cumplimiento es innegociable
- **Decisión**: verde/ámbar/rojo con su mapeo actual, exclusivo del estado de vigencia y
  cumplimiento, nunca decorativo, y **domina sobre la identidad de marca**. Incluye el
  modificador "vigente con riesgo en visita".
- **Estado**: Vigente · **Fecha**: 2026-08-08 (fundacional, ratificado en cada generación)
- **Documentos afectados**: `02`, `06`.

### DDL-011 — No se adopta ninguna escala de librería como identidad
- **Decisión**: la identidad cromática de Hydra no será la escala azul por defecto de Tailwind
  ni ninguna paleta de librería sin transformación propia.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: ratifica la decisión de 2026-07 ("no azul SaaS genérico"); la identidad sale de la
  personalidad (DDL-008), no de un preset.
- **Documentos afectados**: `02`, `06`.

### DDL-012 — El cobre sale de la UI funcional
- **Decisión**: el acento cobre deja de formar parte de la UI del producto; puede conservarse
  como recurso de branding no-producto.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: con la dualidad azul/cian, un tercer acento compite y diluye; su uso real era casi nulo.
- **Reemplaza**: `DESIGN_SYSTEM.md` § Color, reglas de `color.accent` (2026-07).
- **Documentos afectados**: `02`, `06`; limpieza futura de `--color-accent-*`.

## Diseño espacial

### DDL-013 — Modelo de superficies de cuatro niveles
- **Decisión**: Canvas → Surface → Elevated → Overlay. La profundidad comunica jerarquía, no
  decoración, y se expresa principalmente por fondo, borde y contraste; la **sombra solo cuando
  existe una relación espacial real** (Overlay).
- **Estado**: Vigente · **Fecha**: 2026-08-08 · valores en DDL-028
- **Reemplaza**: la dicotomía de dos niveles fondo/superficie de los tokens actuales.
- **Documentos afectados**: `02`, `06`.

### DDL-014 — Islas = jerarquía interna, no card-ificación
- **Decisión**: las "islas" son jerarquía de zonas funcionales dentro de una superficie. **Hydra
  no se convierte en una colección de cards flotantes.**
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Documentos afectados**: `02`, `05`, `06`.

### DDL-015 — El shell global se mantiene
- **Decisión**: no se sustituye el shell por un Smart Dock flotante; la sidebar global se
  mantiene. Su colapso a iconos puede evolucionar.
- **Estado**: Vigente · **Fecha**: 2026-08-08 (ratifica `docs/COMUNICACIONES.md` § 10.6)
- **Documentos afectados**: `03`.

## Motion

### DDL-016 — Motion System propio, separado, con tres tiers
- **Decisión**: `07_MOTION_SYSTEM.md` separado del Design System. **Tier A Operativo**
  (120–250ms, siempre) · **Tier B Transición** (300–500ms, easing fluido cuando corresponda,
  selectivo) · **Tier C Signature** (1–2 usos excepcionales por pantalla). Regla madre: **el
  movimiento comunica causalidad** (qué pasó, qué pasa, qué terminó).
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: tres filosofías de motion convivían sin árbitro; sin documento propio, los efectos
  se acumulan (evidencia: rondas de 2026-07).
- **Reemplaza**: el catálogo de micro-interacciones de 2026-07 como marco normativo — sus 7
  patrones se reevalúan uno a uno bajo los tiers al redactar `07` (OD-10).
- **Documentos afectados**: `07`, `06`.

### DDL-017 — Magnetic CTA: rechazado
- **Decisión**: ningún elemento interactivo se desplaza persiguiendo al cursor.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: interfiere con la memoria espacial y la precisión motora — lo contrario de
  Precision. Registrado para que ningún brief futuro lo reintroduzca.
- **Documentos afectados**: `07`.

### DDL-018 — Cursor-following glow: rechazado en superficies operativas
- **Decisión**: el glow que sigue al puntero queda excluido de toda superficie operativa.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Documentos afectados**: `07`.

### DDL-019 — Ripple de clic: deja de ser universal
- **Decisión**: el ripple no se elimina automáticamente, pero deja de aplicarse a todo botón; se
  restringe a usos donde comunique feedback real. La lista concreta se decide al redactar `07`
  (OD-10).
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Reemplaza**: la decisión de 2026-07 de ripple universal en `Boton.razor`.
- **Documentos afectados**: `07`.

### DDL-020 — `prefers-reduced-motion` obligatorio
- **Decisión**: Tier B y C se desactivan siempre con `prefers-reduced-motion`.
- **Estado**: Vigente · **Fecha**: 2026-08-08 (ratifica práctica de 2026-07)
- **Documentos afectados**: `07`.

## Dark Mode

### DDL-021 — El dark mode actual no es identidad definitiva
- **Decisión**: el tema oscuro actual no se considera definitivo. `prefers-color-scheme` no se
  reactiva hasta disponer de un dark mode realmente diseñado. El rediseño completo es posterior
  al reset documental, pero **la arquitectura de tokens debe contemplar ambos temas desde el
  diseño**.
- **Estado**: Vigente · **Fecha**: 2026-08-08 · identidad resuelta en DDL-026; momento en OD-08
- **Documentos afectados**: `02`, `06`.

## Gobernanza

### DDL-022 — Arquitectura documental del reset
- **Decisión**: ocho documentos normativos (01–08) + `docs/blueprints/` +
  `DESIGN_DECISION_LOG.md`. Los blueprints son proyectos/especificaciones por módulo (mockup +
  spec + estado de implementación); no sustituyen la normativa. El Decision Log registra
  decisiones, motivos y revisiones; no es una especificación paralela.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Reemplaza**: la estructura documental actual (`DESIGN_SYSTEM.md` + `UX_PATTERNS.md` +
  planes en raíz) — migración por fases, con redirecciones.
- **Documentos afectados**: todos; `CLAUDE.md` (tabla de lecturas por tarea).

### DDL-023 — Disciplina de estado documental
- **Decisión**: todo documento declara su estado y su límite de implementación. Ningún documento
  presenta como implementado algo no construido.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: la afirmación "todo el portal migró a Adaptive Layout"
  (`docs/COMUNICACIONES.md` § 10.5) y el § 8.1 "cerrado pero no implementado" de
  del histórico `PLAN-CONTEXT-WORKSPACE.md` (archivado) demuestran el modo de fallo.
- **Documentos afectados**: todos.

### DDL-024 — Regla de precedencia y resolución de conflictos
- **Decisión**: modelo de autoridad de cuatro capas (ver cabecera) y precedencia entre fuentes
  Normativa → Blueprint → Implementación. Conflictos entre normativa: registrar, revisar,
  actualizar, anotar. Prohibido contradecir en silencio una regla vigente.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Documentos afectados**: cabecera de este Log; `CLAUDE.md`.

---

## Decisiones cerradas por el banco visual (Fase 2)

> Método: dos rondas de láminas comparativas con contenido idéntico y solo la identidad
> variando; criterios escritos antes de mirar; contraste medido. Ronda 1 redujo candidatos;
> Ronda 2 validó el set ganador contra los 10 criterios de aprobación — **los 10 aprobados por
> el propietario el 2026-08-08**. Evidencia: láminas L1–L8 del banco visual (a archivar en
> `docs/blueprints/validation/`).

### DDL-025 — Hydra Blue = acero `#235BC2` (cierra OD-01)
- **Decisión**: el azul de acción humana es el acero `#235BC2`, con `#2F6FDD` como
  hover/variante. La escala 50–900 se deriva de este anclaje en `06`, no se inventa.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: juicio del propietario sobre L7 + Centro 360 — "se siente con más peso y transmite
  acción real". Coherente con Trust y Precision (DDL-008) y con la continuidad de la identidad
  revalidada. Contraste 6.27:1 sobre blanco, cómodo margen sobre AA.
- **Reemplaza**: descarta el candidato B2 "acero luminoso" `#2B6BEA` (4.78:1, margen ajustado).
- **Documentos afectados**: `02`, `06`.

### DDL-026 — Identidad oscura = slate D-B (cierra OD-05)
- **Decisión**: Canvas `#0E141B` · Surface `#17212C` · Elevated `#202B36` · Borde `#293644` ·
  Texto `#E7EAEE` · Muted `#8592A3`.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: juicio del propietario — "se siente más como plataforma operativa, no solo
  informática". Ambos candidatos pasaban las métricas; la elección fue de identidad, con el
  mismo cian en los dos para que la comparación no favoreciera a ninguno.
- **Reemplaza**: descarta el candidato D-A "grafito cálido" (`#0D1011`/`#161C1E`).
- **Nota**: la reactivación de `prefers-color-scheme` sigue sujeta a DDL-021 y OD-08.
- **Documentos afectados**: `02`, `06`.

### DDL-027 — Hydra Cyan: familia asimétrica por tema (cierra OD-02)
- **Decisión**: modo claro — texto/icono de sistema `#0C7792` (5.58:1), indicador no textual
  `#0E96B4` (3.48:1). Modo oscuro — `#2BD4F0` para ambos usos. El cian **nunca** rellena un
  botón sólido ni actúa como color de acción.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: hallazgo objetivo del triaje — **no existe un cian brillante válido en modo
  claro**: `#00F0FF` da 1.41:1, `#00C2E0` 2.14:1 y `#06B6D4` 2.43:1, todos por debajo de su
  umbral. La asimetría por tema es además coherente con la semántica: el sistema "brilla" en
  oscuro y se contiene en claro.
- **Documentos afectados**: `02`, `06`.

### DDL-028 — Superficies claras (cierra OD-04)
- **Decisión**: Canvas `#F6F8FA` · Surface `#FFFFFF` · Surface-subtle `#F1F5F7` · Elevated =
  blanco con borde reforzado y **sin sombra** · Overlay = blanco **con** sombra (única sombra
  permitida). Bordes `#E2E8EC` / `#CBD5E1`.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: expresa DDL-013 por fondo/borde/contraste, coherente con la personalidad; evita el
  look de "todo flotando" que DDL-014 prohíbe.
- **Documentos afectados**: `06`.

### DDL-029 — Corrección del texto secundario (defecto AA detectado)
- **Decisión**: el muted del modo claro pasa de `#738196` a `#5F6E84`.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: **defecto objetivo, no preferencia**: `#738196` da 3.96:1 sobre blanco, por debajo
  del 4.5:1 que exige el cuerpo de 14px que usa toda la aplicación; cumplía solo como texto
  grande. El candidato da 5.18:1. Presente en el producto desde 2026-07 sin detectar — lo
  encontró el protocolo de contraste del banco, que es justamente su razón de existir.
- **Documentos afectados**: `06`; corrección de `tokens.css` en fase de implementación.

### DDL-030 — Taxonomía de color por semántica y agencia (cierra OD-03)
- **Decisión**: el usuario puede actuar → **Blue** · Hydra está actuando → **Cyan** ·
  información neutral o contextual → **Info** · cumplimiento y riesgo → **Semáforo** · error →
  **Error** · advertencia → **Warning**. Regla de discriminación: "Sincronización completada"
  → Hydra actuó → cian; "Este centro pertenece a Barcelona" → contexto → info.
  **El cian jamás se usa como el nuevo `info`.** El valor concreto de `info` se fija en `02`.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Documentos afectados**: `02`, `06`.

### DDL-031 — El recuento de un Centro agrega Empresa y Trabajadores (cierra OD-13)
- **Decisión**: el badge de recuento de una fila de Centro agrega **ambos ámbitos**; su ventana
  de contexto declara cuántos son de cada uno, y al desplegar aparecen separados: bloque
  **Empresa** primero — visible **solo cuando le falta documentación** — y **Trabajadores**
  debajo.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: hueco real del patrón maestro detectado en el banco: "Vencido 3" no decía de
  quién, y sin esa respuesta la pantalla es ilegible para alguien que no participó en el
  desarrollo. Resuelve parcialmente la reserva de DDL-005.
- **Asimetría deliberada entre ámbitos**: el bloque **Empresa** lista **solo los documentos con
  incidencia** (vencidos, faltantes, en riesgo); los vigentes no aparecen y se consultan desde
  "Detalles". El bloque **Trabajadores** sí muestra toda la documentación exigida por ese
  centro. Motivo: el Centro es el lugar donde se opera la documentación del trabajador, mientras
  que la de la Empresa tiene sus propias superficies; repetirla aquí añadiría filas que casi
  siempre estarían al día. La lista lleva una nota explícita para que la ausencia no se lea como
  "esta empresa solo tiene un documento".
- **Consecuencia aceptada**: con la empresa al día, su documentación no es visible desde el
  centro; el camino es "Detalles" de la Empresa.
- **Documentos afectados**: `05`.

### DDL-032 — La procedencia del sistema se marca, no se etiqueta
- **Decisión**: la procedencia (dato leído por IA, evento generado por Hydra) se representa con
  una **marca mínima** — icono + cian —, nunca con una etiqueta de texto por fila. El detalle
  (fuente, confianza, quién confirmó) vive en una **ventana de contexto bajo demanda**.
  **Restricción no negociable**: la ventana se abre con hover **y** con foco de teclado, y la
  marca lleva nombre accesible; hover-solo incumpliría la regla de accesibilidad del proyecto.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: razonamiento de escala del propietario — cuando la IA lea todos los documentos,
  una etiqueta de texto por fila sería ruido puro. Anticipa el problema antes de tenerlo.
- **Distinción asociada**: por fila = **marca**; superficie de decisión (Action Center,
  extracción con confianza por campo) = **tarjeta** expandida.
- **Documentos afectados**: `05`, `06`, `08`.

### DDL-033 — Los recuentos de estado llevan ventana de contexto
- **Decisión**: un badge de recuento (p. ej. "3") abre una ventana con el desglose literal de
  qué documentos y de quién. Con ella el número deja de depender del color para ser
  comprensible — resuelve el caso de daltonismo y el de quien no conoce el código cromático.
  El título de esa ventana **no va en cian**: un recuento de vencidos no es Hydra actuando.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: el badge numérico sin etiqueta gana densidad pero dejaría el color como único
  portador de significado, lo que choca con la regla WCAG del proyecto.
- **Documentos afectados**: `05`, `06`, `08`.

### DDL-034 — Estructura del tercer nivel de Centro 360 (cierra OD-12)
- **Decisión**: variante **A** — expandido con columnas fijas (Documento · Estado · Vigencia ·
  Acción). Se descarta la variante B (resumen + ficha) y **se descarta el toggle A/B**
  medido por uso.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo del descarte del toggle**: no hay telemetría de UI y la base de usuarios real es de
  unas pocas personas, así que "qué se usa más" mediría el valor por defecto, no la preferencia;
  dos estructuras son dos caminos de código permanentes; y la segunda profundidad **ya existe
  con nombre propio** (DDL-006: Context Panel para consultar). Un toggle sin fecha de retirada
  es una decisión no tomada disfrazada de funcionalidad.
- **Documentos afectados**: `05`.

### DDL-035 — Varias visitas en un Centro no se fusionan en un rango (cierra OD-18)
- **Decisión**: con dos o más visitas, el badge muestra el **recuento** ("2 visitas") y la
  ventana de contexto detalla cada rango por separado.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: fusionar 21–23 y 29–30 en "21 al 30" **inventa continuidad** — afirma presencia
  del 24 al 28, que es falsa —, y "riesgo en visita" se calcula precisamente contra esa ventana:
  un documento que caduque el 26 aparecería en riesgo sin estarlo. La unión de dos rangos no es
  el rango de la unión.
- **Documentos afectados**: `05`.

### DDL-036 — Reglas de presentación derivadas del banco
- **Decisión** (conjunto de reglas menores acordadas durante la validación):
  - **Acción única "Gestionar"** para el mismo destino — nunca dos verbos ("Subir"/"Gestionar")
    para el mismo fin.
  - **Toda acción siempre disponible, atenuada cuando no hace falta** — un documento al día
    también se gestiona; el gris guía sin prohibir. Nunca `disabled` real.
  - **Recuento separado del estado**: «Vencido | 3», porque el número es un recuento de
    documentos, no parte del nombre del estado. En superficies densas, solo el número, con
    ventana de contexto (DDL-033); el texto completo se conserva donde hay una columna "Estado".
  - **«Acceso bloqueado»**, no «Bloquea acceso» — se nombra el estado del trabajador, no lo que
    el documento provoca.
  - **`|` separa títulos** (Empresa | Asunto, Visita | Centro); **`·` se reserva** para
    nombre · hora y para metadatos.
  - **Fracción sin sufijo** («7/9», no «7/9 al día») y **vigencias en mayúscula inicial**.
  - **Guía de lectura por fila**: al apuntar una fecha o una acción, la fila entera se resalta.
    Es **Tier A operativo**, no adorno — sin ella, en una fila ancha hay que recorrer el renglón
    dos veces para confirmar que se está en el documento correcto.
  - **Ordenar por cumplimiento** en las listas de Centro, para atacar los peores sin depender de
    que haya visita.
  - **Iconografía obligatoria en superficies de resumen** — sin ella, un número de color sobre
    fondo blanco lee como una hoja de cálculo. Set outline único, nunca emoji.
  - **Los elementos de "Próximamente" llevan estado de preparación** y dicen qué falta: una
    fecha futura sin estado no es información operativa.
  - **El porcentaje va dentro del anillo**, sin repetir la palabra "Cumplimiento" cuando el
    patrón ya se ha enseñado en la misma sesión.
  - **Ranuras fijas por tipo de indicador** en las filas de lista, para que cada indicador caiga
    siempre en la misma vertical esté o no presente en esa fila.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Nota de gobernanza (2026-08-08)**: era la única entrada del Log que contenía
  **especificación** (microcopy literal, reglas de formato) y no solo decisión. **Migración
  ejecutada**: su contenido vive ya en `04` (§ 2.4, § 2.5, § 3.3, § 3.8, § 4.1, § 9) y en `06`
  (radios, sombras y densidad). Esta entrada se conserva como registro de que las reglas se
  adoptaron y de dónde viven ahora — **no como su especificación**. El Log no debe volverse una
  especificación paralela (DDL-022).
- **Documentos afectados**: `04`, `05`, `06`, `08`.

### DDL-039 — "Riesgo en visita" se muestra solo, con la vigencia en la ventana de contexto
- **Decisión**: el modificador se representa con un **único badge ámbar "Riesgo en visita"**.
  La palabra "Vigente" se retira del badge: el término ya presupone vigencia — un documento
  caducado diría "Vencido". El hecho completo (hasta cuándo es válido y por qué eso choca con
  la visita) vive en la **ventana de contexto**, mismo patrón que DDL-032 y DDL-033.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: la información era inferible del propio término, así que mostrarla ocupaba el
  doble de ancho en la columna más disputada de la fila sin añadir nada. Coherente además con la
  tarjeta de visita del Home, que ya usaba el badge simple.
- **Reemplaza**: DDL-038 (badge de dos segmentos) y, antes que él, la expresión "badge `success`
  con borde `warning.500`" del histórico `DESIGN_SYSTEM.md` § Color (archivado).
- **Riesgo asumido y cómo se mitiga**: al aparecer solo en la columna "Estado", el modificador
  puede leerse como un **estado base** más, cuando en el modelo es un modificador contextual de
  `Vigente` (mismo `EstadoDocumento`, distinta fecha de referencia). Se mitiga por dos vías:
  la ventana de contexto dice literalmente "vigente hoy", y **OD-20 debe registrar el término
  como modificador, no como estado**, para que las integraciones no lo persistan ni lo
  transmitan como un valor de `EstadoDocumento`. Sin esa entrada en el diccionario, esta
  decisión introduciría deuda de dominio.
- **Documentos afectados**: `06`, `08`; `UBIQUITOUS_LANGUAGE.md` (entrada obligatoria).

### DDL-038 — Badge de dos segmentos para "Vigente · Riesgo en visita"
- **Decisión**: mitad verde "Vigente" + mitad ámbar "Riesgo en visita", con pared neutra.
- **Estado**: **Sustituida por DDL-039** · **Fecha**: 2026-08-08
- **Por qué duró horas**: se propuso para hacer visibles las dos verdades simultáneas, y era
  correcta en el fondo; al revisarla se concluyó que la primera de las dos ya es inferible del
  término, de modo que el badge partido pagaba ancho por información redundante. Nunca llegó a
  implementarse en código.
- **Se conserva la entrada** en lugar de borrarla: registrar la reversión es lo que impide que
  la propuesta vuelva a plantearse dentro de seis meses como si fuera nueva.

### DDL-037 — Identificación de canal por icono, no por color de marca
- **Decisión**: el canal (correo, WhatsApp) se identifica por la **forma del icono**; el matiz
  de marca solo se admite desaturado en el trazo del icono, nunca como relleno del chip.
- **Estado**: Vigente (matiz exacto pendiente → OD-15) · **Fecha**: 2026-08-08
- **Motivo**: el verde de WhatsApp colisiona con el verde del semáforo de cumplimiento y su
  variante verde-azulada con Hydra Cyan. En una plataforma donde el verde significa "vigente",
  un chip verde de canal enseña al ojo lo contrario de lo que el sistema quiere decir.
- **Documentos afectados**: `02`, `08`.

### DDL-040 — El banco visual valida; no rediseña
- **Decisión**: un banco de validación solo puede cerrar las Open Decisions que tiene asignadas.
  Si una lámina revela un problema que **no** pertenece a esas OD, se **registra como decisión
  abierta nueva y queda fuera del banco** — no se arregla dentro de la lámina. El flujo es
  *validación → evidencia → cierre de OD*, nunca *validación → descubrimiento → rediseño → nueva
  exploración*.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: la regla existía en el Visual Validation Brief, pero en la ejecución del banco se
  cumplió a medias: los hallazgos estructurales sí se registraron (OD-12 … OD-20), y sin embargo
  varios cambios ajenos a OD-01…05 —ámbitos de Empresa, badge partido y su reversión,
  unificación de léxico, iconografía del Home— se aplicaron dentro de las láminas. El resultado
  fue bueno, pero el método se relajó, y un método que se relaja bajo presión de "ya que
  estamos" es el que produjo el ciclo de rediseños de 2026-07.
- **Consecuencia operativa**: las decisiones tomadas así (DDL-031 … DDL-039) **se mantienen
  vigentes** — están registradas, razonadas y aprobadas —, pero a partir de aquí ningún banco
  vuelve a incorporarlas sobre la marcha.
- **Documentos afectados**: `docs/blueprints/` (método), este Log.

---

### DDL-054 — Veredicto de los tres efectos heredados restantes
- **Decisión**: al redactar `07` se juzgan los patrones que DDL-045 dejó pendientes.
  **Revelado escalonado**: se conserva como Tier B, **acotado a la primera carga** de una
  superficie — no en paginación, filtrado en vivo ni reordenación. **Toast con barra de
  progreso**: se conserva como Tier A (estado vivo), y no aplica a errores, que no se
  autodescartan. **Entrada del buscador global con asentamiento**: se conserva como Tier B,
  solo en la apertura, nunca por pulsación o resultado.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: los tres portan una función comunicativa real —transición espacial los dos
  primeros de su categoría, estado vivo el toast—, a diferencia del gradient mesh y el glow, que
  no responden qué pasó, qué pasa ni qué terminó. Lo que se corrige es su **ámbito**: el
  escalonado repetido en cada tecla de un buscador deja de ser transición y pasa a ser ruido.
- **Reclasificación**: el punto 5 del catálogo de julio (radios y sombras) **no es motion** y se
  traslada a `06`; mantenerlo en un documento de movimiento es lo que convierte a `07` en un
  catálogo de efectos.
- **Documentos afectados**: `07` § 5, `06` § tokens de superficie.

---

### DDL-055 — La autoridad viene de la posición en la cadena, no de la antigüedad ni del detalle
- **Decisión**: se congela como regla de gobernanza del repositorio:
  > **Un documento no obtiene autoridad por ser más antiguo, más detallado, más cercano al
  > código ni por contener una especificación más concreta. La autoridad viene exclusivamente de
  > su posición en la cadena normativa vigente.**

  Y su corolario operativo: **un documento histórico no puede utilizarse como fuente para una
  decisión de diseño o de implementación.** Si una regla necesaria no está en la cadena vigente,
  **no se recupera del histórico**: se localiza su decisión en este Log o se registra una nueva.
  Encontrarla en un documento archivado es contexto, no autorización.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: archivar los documentos superados no basta. Sin esta regla, dentro de seis meses
  alguien abre un documento histórico, lee "los drawers deben…" y lo copia a código — y la
  cabecera de "histórico" no lo impide por sí sola.
- **Consecuencia sobre el código**: si el código contradice la normativa, **el código no modifica
  la normativa**. Se registra como divergencia; cambiar la norma exige una decisión nueva que
  sustituya explícitamente a la anterior.
- **Se hace verificable**: `scripts/validar-gobernanza-docs.py`, ejecutado en CI, comprueba las
  seis propiedades de la frontera (`docs/README.md` § 5). Un fallo no es un problema de formato:
  significa que la frontera de autoridad se ha roto.
- **Documentos afectados**: `docs/README.md` (nuevo), `CLAUDE.md`, todos los archivados.

### DDL-056 — Migración documental ejecutada (cierra la parte pendiente de DDL-022)
- **Decisión**: los cuatro documentos de diseño anteriores (`DESIGN_SYSTEM.md`, `UX_PATTERNS.md`,
  `PLAN-CONTEXT-WORKSPACE.md`, `PLAN-MASTER-DETAIL-WORKSPACE.md` — todos históricos y archivados)
  se trasladan a `docs/archive/design/` con cabecera de **no
  normativo** que declara estado, sustituto, decisiones relacionadas y por qué se conservan. No
  se borran: cada uno guarda evidencia que la normativa cita.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: DDL-022 decidió la sustitución pero **no se había ejecutado**. Mientras tanto
  `CLAUDE.md` seguía mandando leer los documentos antiguos "antes de escribir código o UI nueva",
  de modo que el reset no gobernaba nada y el repositorio tenía **dos sistemas vivos a la vez** —
  peor que antes de empezar.
- **Alcance**: `docs/ux-audit/**` se queda donde está como evidencia de la auditoría;
  `docs/COMUNICACIONES.md` es mixto (Parte I vigente, Parte II es un blueprint) y queda pendiente
  de alinear con `docs/blueprints/`.
- **Documentos afectados**: `CLAUDE.md`, `README.md`, `PROJECT.md`, `ARCHITECTURE.md`,
  `CODING_STANDARDS.md`, `docs/MULTITENANCY.md`, `docs/ux-audit/PLAN-EJECUCION-UX.md`.

---

### DDL-053 — "Administrativa" es una clasificación funcional, no un arquetipo (cierra OD-21)
- **Decisión**: **no se crea un quinto arquetipo.** El sistema de DDL-004 gobierna las
  superficies **operativas**. Los catálogos, la configuración y la administración se construyen
  con la capa CRUD (DDL-002) y los patrones comunes de `04` — lista, drawer, modal, Context
  Panel — **sin declarar arquetipo**. "Administrativa" describe quién usa la pantalla y con qué
  frecuencia, no cómo se estructura.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo — evidencia, no criterio**: se auditaron las once pantallas del grupo y **no comparten
  patrón**. Conviven cinco comportamientos distintos: CRUD de catálogo (Usuarios, Tipos de
  documento, Roles), consulta de solo lectura sin acciones (Auditoría, Auditoría IA), flujo con
  estado irreversible (Retención, Claves API), máquina de estados (Delegaciones, Conexiones) y
  formulario singleton (Configuración). Solo 4 de 11 usan la UI de lista compartida y solo 3 un
  Drawer. Un arquetipo que abarcase a la vez "tabla sin acciones" y "destruir datos
  definitivamente con autorización previa" no sería una categoría, sería una carpeta.
- **Reclasificación derivada**: **Retención y Claves API son Flow Surface** — proceso guiado,
  validación intermedia y resultado irreversible al final. Que estuvieran bajo "Administración"
  era un accidente del menú, no una propiedad suya. Se documentan como tal en `03` y `05`.
- **Enmienda a DDL-004**: donde decía "toda pantalla se clasifica en uno de los cuatro
  arquetipos", debe leerse "**toda superficie operativa** se clasifica en uno de los cuatro".
- **Riesgo asumido**: sin arquetipo, estas pantallas dependen solo de `04` para su consistencia.
  Es aceptable porque su volumen y frecuencia son bajos, pero significa que `04` debe cubrir
  bien lista, formulario y confirmación destructiva — no solo los patrones operativos.
- **Documentos afectados**: `03` § 2, `04`, `05`; enmienda anotada en DDL-004.

---

## Cierre de las Open Decisions restantes (2026-08-08)

> Cerradas en una sola sesión tras aprobarse `01`. Cuatro las decidió el propietario; ocho se
> resolvieron por derivación de decisiones ya tomadas y se confirmaron sin objeción.

### DDL-041 — Una sola densidad, compacta (cierra OD-06)
- **Decisión**: la densidad de Centro 360 es la de toda la plataforma. **No** hay modos
  compacto/cómodo por usuario.
- **Motivo**: un solo camino de código, una sola verificación visual, ninguna preferencia que
  mantener ni que se pise con otros ajustes de presentación. Si aparece una necesidad real de
  modo cómodo, es una decisión nueva (YAGNI, `PROJECT.md`).
- **Documentos afectados**: `04`, `05`, `06`.

### DDL-042 — Trabajador se promueve a Entity Workspace (cierra OD-07)
- **Decisión**: además de Centro y Empresa, **Trabajador** pasa a ser Entity Workspace de
  página. El resto de entidades se consultan con Context Panel.
- **Motivo**: el Trabajador concentra documentación, asignaciones y visitas — es la entidad con
  más relaciones tras Centro y donde más se opera en profundidad.
- **Condición de ejecución**: es una migración con coste real (pantalla, queries, verificación
  end-to-end). Se planifica como fase propia **después** del reset documental, nunca mezclada
  con él.
- **Documentos afectados**: `03`, `05`; blueprint propio cuando se construya.

### DDL-043 — El modo oscuro se implementa después del reset (cierra OD-08)
- **Decisión**: la implementación del tema oscuro y la reactivación de `prefers-color-scheme`
  son fase posterior al reset documental. El estado actual (auto-seguimiento desactivado, oscuro
  disponible solo por elección explícita) queda documentado como **transitorio**.
- **Motivo**: los valores ya están fijados (DDL-026), así que lo que queda es coste de
  ejecución, no decisión pendiente. Reactivar antes dejaría a usuarios con el sistema en oscuro
  aterrizando en un tema sin rediseñar.
- **Documentos afectados**: `02` § 5, `06`.

### DDL-044 — La columna contextual de Comunicaciones se llama Context Panel (cierra OD-09)
- **Decisión**: la columna derecha del Communication Workspace pasa a llamarse **Context
  Panel**, alineada con el arquetipo de DDL-004. El panel de entidad conserva su nombre. Ambos
  términos se dan de alta en `docs/business/UBIQUITOUS_LANGUAGE.md`.
- **Motivo**: el mismo nombre designaba dos cosas distintas, y `UBIQUITOUS_LANGUAGE.md` ya
  resolvió una colisión equivalente con "Workspace" — no se deja una nueva sin registrar.
- **Documentos afectados**: `05`, `UBIQUITOUS_LANGUAGE.md`, `docs/COMUNICACIONES.md`.

### DDL-045 — Destino de los efectos heredados de 2026-07 (cierra OD-10)
- **Decisión**: **se retiran** el glow en hover de enlaces y el gradient mesh animado de las
  tarjetas KPI. **El ripple de clic se conserva pero deja de ser universal**: solo en acciones
  primarias.
- **Motivo**: glow y mesh son decoración sin causalidad y no superan el test de DDL-001
  ("¿reduce trabajo, lo explica, o lo decora?"). El ripple sí comunica feedback en el punto de
  contacto, pero aplicado a cada botón de cada tabla era Tier C ejecutado globalmente.
- **Reemplaza**: los patrones 6 y 7 del catálogo de micro-interacciones de 2026-07, y acota el
  patrón 1. Los demás (revelado escalonado, toast con barra, command palette) se reevalúan al
  redactar `07`.
- **Consecuencia de implementación**: afecta a `base.css` (glow de enlaces),
  `Dashboard.razor.css` (mesh) y `Boton.razor.css` + `microinteracciones.js` (ripple). Fase
  posterior.
- **Documentos afectados**: `07`.

### DDL-046 — El Operational Home no se fusiona con la Bandeja (cierra OD-11)
- **Decisión**: son piezas distintas. El **Home** es una superficie de entrada (arquetipo); la
  **Bandeja** es una cola priorizada. El Home **consume** esa cola y la presenta resumida; no la
  sustituye ni la duplica.
- **Motivo**: una sola fuente de verdad con dos presentaciones. Fusionarlas crearía o bien una
  pantalla que hace dos trabajos a medias, o bien dos colas que se desincronizan.
- **Documentos afectados**: `03`, `05`.

### DDL-047 — Cada nivel suma exactamente el ámbito que declara (cierra OD-14)
- **Decisión**: regla de recuentos: **Centro** = Empresa + Trabajadores con actividad en él;
  **Trabajador** = sus documentos exigidos por ese centro; **Empresa** = su propia documentación.
  Todo recuento abre su desglose (DDL-033), y el desglose declara los ámbitos que suma.
- **Motivo**: sin regla escrita, cada pantalla inventaría la suya y dos cifras del mismo dato
  dejarían de cuadrar entre niveles.
- **Documentos afectados**: `05`.

### DDL-048 — El canal se identifica por icono neutro (cierra OD-15)
- **Decisión**: se descarta el matiz de marca. El canal se distingue **solo por la forma del
  icono**, en color neutro.
- **Motivo**: no hay hueco cromático libre — el verde de WhatsApp colisiona con el semáforo y su
  variante verde-azulada con Hydra Cyan. Un chip verde de canal enseñaría al ojo lo contrario de
  lo que el sistema significa.
- **Reemplaza**: la parte abierta de DDL-037.
- **Documentos afectados**: `02` § 3.6, `08`.

### DDL-049 — Alias de Cliente (cierra OD-16)
- **Decisión**: el Cliente gana un **alias corto**, sugerido por el sistema a partir de la razón
  social y editable por el gestor. Se usa en listas, cabeceras y superficies densas.
- **Restricción no negociable**: el alias es **solo de presentación**. Informes, exportaciones y
  cualquier documento con valor legal muestran siempre la **razón social completa**. En un
  producto de cumplimiento, un informe con un nombre comercial en lugar del legal es un
  problema, no una comodidad.
- **Documentos afectados**: `04`, `05`, dominio (`Cliente`), `UBIQUITOUS_LANGUAGE.md`.

### DDL-050 — "Hydra trabajó mientras no estabas": resumen al volver (cierra OD-17)
- **Decisión**: se construye el **resumen de ausencia** — qué avanzó el sistema y qué llegó sin
  ver, priorizado. **No** se construye todavía el aviso que interrumpe en plena jornada.
- **Motivo**: cierra el hueco principal ("esto llegó y no lo has visto") con el menor riesgo. El
  aviso interruptor exige un umbral de precisión que hoy no se puede calibrar sin datos reales,
  y un falso positivo que interrumpe destruye la confianza más rápido de lo que un acierto la
  construye.
- **Prerrequisitos**: (1) modelo de **"visto" por usuario**, que hoy no existe; (2) el resumen
  consume **la misma cola** que la Bandeja (DDL-046), nunca una cuarta bandeja; (3) qué define
  "ausencia" se fija al diseñarlo.
- **Documentos afectados**: `01` § 5.3, `05`; dominio.

### DDL-051 — El proveedor del buzón se muestra en la cabecera, no por mensaje (cierra OD-19)
- **Decisión**: el proveedor (Outlook, Gmail, futuras redes) se muestra en la **cabecera de la
  conversación** y en el selector "Responder como…". Nunca repetido en cada mensaje.
- **Motivo**: es atributo de la **conexión**, no del mensaje. Canal más proveedor por fila serían
  dos iconos por línea — ruido en la superficie más densa del módulo.
- **Documentos afectados**: `05`, `docs/COMUNICACIONES.md`.

### DDL-052 — Diccionario cerrado de estados (cierra OD-20)
- **Decisión**: se crea en `docs/business/UBIQUITOUS_LANGUAGE.md` un diccionario **mínimo y
  cerrado** de estados, declarando para cada término qué abarca y **en qué eje vive**. Ejes
  separados: documental (Al día · Próximo · Vencido · Faltante · Pendiente de subir) y de aviso
  (p. ej. Falta notificar) — no comparten semáforo ni se mezclan.
- **Requisito derivado de DDL-039**: **"Riesgo en visita" se registra como modificador
  contextual de `Vigente`**, nunca como valor de `EstadoDocumento`. Sin esa entrada, una
  integración lo persistiría como estado y rompería la regla del estado derivado.
- **Motivo**: las integraciones exigen que el concepto viaje idéntico; un término que significa
  dos cosas en dos sistemas es un error de datos esperando a ocurrir.
- **Documentos afectados**: `UBIQUITOUS_LANGUAGE.md`, `04`.

---

## Open Decisions

**Ninguna.** Las veintiuna Open Decisions del reset quedan cerradas el 2026-08-08.

La frontera de autoridad se verifica en CI (DDL-055): `scripts/validar-gobernanza-docs.py`.

| Grupo | ODs | Cerradas por |
|---|---|---|
| **A — bloqueaban la normativa visual** | OD-01 · OD-02 · OD-03 · OD-04 · OD-05 | Banco visual → DDL-025 · DDL-027 · DDL-030 · DDL-028 · DDL-026 |
| **Halladas durante la validación** | OD-12 · OD-13 · OD-18 | DDL-034 · DDL-031 · DDL-035 |
| **B — producto y arquitectura UX** | OD-06 · OD-07 · OD-09 · OD-11 · OD-14 · OD-16 · OD-17 · OD-19 · OD-20 | DDL-041 · DDL-042 · DDL-044 · DDL-046 · DDL-047 · DDL-049 · DDL-050 · DDL-051 · DDL-052 |
| **C — implementación y prioridad** | OD-08 · OD-10 · OD-15 | DDL-043 · DDL-045 · DDL-048 |

Consecuencia: **la redacción de `03` a `08` no depende de ninguna decisión pendiente.** Toda
pregunta nueva que surja al escribirlos se registra aquí como OD nueva y se decide; no se
resuelve dentro del documento (DDL-024, DDL-040).

Decisiones que quedan **condicionadas a una fase posterior**, no abiertas — su contenido está
decidido y solo falta ejecutarlo: implementación del modo oscuro (DDL-043), migración de
Trabajador a Entity Workspace (DDL-042), retirada de los efectos heredados (DDL-045), alias de
Cliente (DDL-049) y resumen de ausencia (DDL-050).


## Orden de trabajo acordado

```
Fase 1   Auditoría ..................................... ✅ cerrada
Fase 1.5 Decisiones de arquitectura (DDL-001…024) ...... ✅ congelada
Fase 2   Banco visual → OD-01…05 (DDL-025…037) ......... ✅ cerrada 2026-08-08
Fase 3   Redacción normativa:
         01 → 02 → 03 → 05 → 07 → 06 → 04 → 08
Fase 4   Implementación (tokens, componentes, pantallas)
```

Ninguna decisión de este registro se implementa en código hasta la Fase 4, y cada cambio de
implementación se verifica end-to-end en navegador antes de darse por cerrado
(regla vigente de `CLAUDE.md`).

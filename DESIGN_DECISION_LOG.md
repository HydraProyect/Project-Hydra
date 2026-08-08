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

### DDL-058 — Las tres formas de contaminación de autoridad son metodología permanente
- **Decisión**: la clasificación de las tres formas en que la autoridad fluye al revés deja de ser
  la observación de una auditoría y pasa a ser **parte permanente de la metodología de
  gobernanza**, junto a DDL-024 y DDL-055:

  | Forma | Enunciado del error | Cómo se detecta |
  |---|---|---|
  | **Código → normativa** | "Está implementado, luego está decidido" | El valor del documento coincide literalmente con el del código y no existe decisión que lo fije |
  | **Documento → documento** | "Este documento lo cita, luego el anterior lo decidió" | Se sigue la cadena de citas hasta el final: si termina en un archivado, en un histórico o en nada, no había autoridad |
  | **Redacción → normativa** | "Hay que rellenar esta celda o esta regla, ponemos X" | El valor no existe en ninguna capa anterior; su primera aparición en el historial es el commit que redactó el documento |

- **Estado**: Vigente · **Fecha**: 2026-08-09
- **Motivo**: la auditoría de trazabilidad de `01`–`08` demostró que OD-22 no era un caso aislado
  sino una de tres variantes, cada una con su propia mecánica y su propia forma de pasar
  desapercibida. Sin la clasificación, cada hallazgo se trata como incidente suelto y se corrige
  sin ver que responde a un patrón.
- **Por qué la tercera es la más peligrosa**: produce una cadena documental **aparentemente
  trazable**. Cada documento cita al anterior y ninguno miente; el fallo está en el origen, no en
  la cadena. Es la única de las tres que no se detecta leyendo el documento — hay que ir al
  historial.
- **Consecuencia operativa**: ante cualquier regla o valor concreto de un documento normativo, la
  pregunta de control no es "¿tiene una cita?" sino **"¿dónde termina la cadena de citas?"**. Una
  cita a un documento archivado, a un histórico o al código no cierra la cadena: la traslada.
- **Verificación automática**: la frontera con los archivados ya se comprueba en CI
  (`scripts/validar-gobernanza-docs.py`, DDL-055) y detectó por sí sola dos infracciones durante
  esta auditoría. Las otras dos formas **no** están automatizadas todavía.
- **Documentos afectados**: cabecera de este Log; `CLAUDE.md` (pendiente de reflejar la regla).

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
- **Decisión**: modo claro — texto/icono de sistema `#0C7792` (**5.16:1**), indicador no textual
  `#0E96B4` (3.48:1). Modo oscuro — `#2BD4F0` para ambos usos. El cian **nunca** rellena un
  botón sólido ni actúa como color de acción.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: hallazgo objetivo del triaje — **no existe un cian brillante válido en modo
  claro**: `#00F0FF` da 1.41:1, `#00C2E0` 2.14:1 y `#06B6D4` 2.43:1, todos por debajo de su
  umbral. La asimetría por tema es además coherente con la semántica: el sistema "brilla" en
  oscuro y se contiene en claro.
- **Corrección (2026-08-09, OD-24)**: esta entrada declaraba **5.58:1** para `#0C7792`. El valor
  real es **5.16:1**; se corrige aquí y en `02` § 3.3 y `06` § 11. **La decisión no cambia**:
  5.16 sigue por encima del 4.5:1 exigido al texto normal, y ninguno de los candidatos descartados
  se acerca. Fue la única de las nueve mediciones de este Log que no reproducía — las otras cuatro
  de esta misma entrada (3.48, 1.41, 2.14, 2.43) son exactas.
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
  ejecutada**: su contenido vive ya en `04` (§ 2.4, § 2.5, § 3.3, § 3.8, § 4.1, § 9) y en `08`
  (§ 4.4, anillo y badge). **Corrección (2026-08-09, DDL-059)**: esta nota afirmaba además "y en
  `06` (radios, sombras y densidad)". Es falso: DDL-036 son once reglas de presentación —verbos,
  badges, microcopy, ranuras, iconografía, anillo—, **ninguna dimensional**; no decidió radios ni
  sombras, y la densidad la decide DDL-041. Era una sobreatribución en la capa de máxima
  autoridad, y es lo que hacía parecer que los radios estaban respaldados desde dos sitios cuando
  no lo estaban desde ninguno. Esta entrada se conserva como registro de que las reglas se
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

## Decisiones posteriores al reset (Fase 4)

### DDL-057 — Texto principal en tema claro (cierra OD-23)
- **Decisión**: se declara **`#161E27`** como valor normativo del texto principal en tema claro,
  y su sitio es `02` § 4.1 — la capa de identidad —, no `06`.
- **Estado**: Vigente · **Fecha**: 2026-08-08
- **Motivo**: **continuidad de identidad visual y suficiencia de contraste** (16.81:1 sobre
  Surface, 15.79:1 sobre Canvas; el umbral de `02` § 8 es 4.5:1). El reset no aportó evidencia
  de querer un tono nuevo para el texto principal: es el mismo criterio que DDL-025 aplicó al
  azul — se ratifica lo que ya expresaba la identidad, no se rediseña sin motivo.
- **Lo que esta decisión explícitamente NO afirma**: que `#161E27` sea "el valor correcto", ni
  que quede ratificado por el histórico o por el código. **El valor histórico y el código
  existente son evidencia de continuidad, no autoridad.** La autoridad la crea esta decisión, y
  vive desde ahora en `02`.
- **Efecto sobre la cadena**: repara la única celda de la paleta clara que no tenía fuente aguas
  arriba. `02` § 4.1 declara → `06` § 2.5 consume → `tokens.css` implementa. La permanencia del
  valor la respalda la tabla "Lo que cambia" de `06` § 12, que no lo enumera. Esto es distinto de
  "el código ya usa `#161E27`, así que lo dejamos": el valor coincide, la dirección de autoridad
  no.
- **Consecuencia en código**: ninguna ahora. `tokens.css` ya resuelve `#161E27` vía
  `--color-neutral-900`, de modo que la implementación pasa a estar respaldada por normativa sin
  necesidad de cambiarla. Que siga en código no es una decisión aparte: es la consecuencia de
  esta.
- **Documentos afectados**: `02` § 4.1; `06` § 2.5 (ya corregido al cerrar OD-22).

### DDL-063 — `--color-border-control`: el token del borde que identifica un control (cierra OD-31)
- **Decisión**: se crea `--color-border-control` con valor **`#738196`**, **el mismo en ambos
  temas**. Lo consumen los bordes clasificados por DDL-062 como límite visual de un control; los
  estructurales siguen con `--color-border`.
- **Estado**: Vigente · **Implementado**: sí, 2026-08-09 — **es el primer cambio de código de esta
  serie**.
- **Motivo**: ningún token existente cumplía. Evaluados por su **peor** caso contra todos los
  fondos efectivos: `--color-border` 1.13/1.17, `--color-border-strong` 1.35/1.58, `neutral-300`
  1.26, `neutral-400` 1.92 en claro. **`neutral-500` es el único escalón que cumple en los dos
  temas** (3.61 claro, 3.64 oscuro). `--color-text-muted` también cumpliría, pero un borde tan
  oscuro como el texto secundario da a cada campo un peso de caja que compite con DDL-014.
- **Por qué el mismo valor en ambos temas**: no es simetría buscada sino consecuencia — es el
  único escalón que satisface las dos condiciones a la vez. Ningún otro token de color del sistema
  se comporta así.
- **De dónde sale el valor**: es el escalón que **DDL-029 retiró del texto** por dar 3.96:1,
  insuficiente para cuerpo de 14 px. Como elemento **no textual** su umbral es 3:1, no 4.5:1, así
  que el mismo valor que era inválido para texto es holgado para un borde. No se recicla por
  comodidad: se aplica donde su contraste sí es el correcto.
- **Por qué no se reutiliza `--color-border-strong`**: DDL-028 le da semántica **estructural** —
  el borde reforzado que expresa el nivel Elevated sin sombra. Fundir ambos usos obligaría a que
  la expresión de profundidad y el límite de un control compartan valor para siempre.
- **Alcance de la implementación**: solo los consumidores clasificados como límite de control —
  los cinco componentes de entrada, `Boton` en su variante secundaria (las demás se identifican
  por relleno o por texto), `BotonCopiar`, `ZonaSoltarArchivo` en reposo, los `input` de las
  páginas de Account y los tres controles del shell. **Los 93 consumidores no se tocaron
  indiscriminadamente**: separadores, tarjetas, modales y paneles siguen con `--color-border`.
- **Verificación end-to-end** (`CLAUDE.md`): comprobado en navegador sobre `/cuenta/iniciar-sesion`.
  Claro 3.96 contra el relleno del campo y 3.82 contra la página; oscuro 4.11 y 4.68. La tarjeta
  contenedora conserva `#E8EDF2`, el borde estructural — la distinción se ve, no solo se declara.
  Sin errores de consola.
- **Documentos afectados**: `02` § 4.1; `06` § 2.5, § 11, § 13; `tokens.css` y catorce hojas de
  estilo de componente.

---

### DDL-062 — Alcance del 3:1 para bordes (resuelve la mitad normativa de OD-31)
- **Decisión**: `02` § 8 acota el umbral de 3:1 al borde que constituye el **límite visual de un
  control interactivo** —campo de texto, área de texto, selector, zona de soltar, botón sin
  relleno— y a los **indicadores de estado**, incluido el de foco. **No aplica** a separadores,
  contornos de agrupación ni bordes de tarjeta, panel o modal.
- **Estado**: Vigente · **Fecha**: 2026-08-09
- **Motivo**: la redacción anterior decía "bordes significativos" **sin definir cuáles lo son**, y
  con los valores actuales ningún borde podría serlo. La lectura estricta —todo borde visible a
  3:1— obligaría a un contorno oscuro en las 93 aplicaciones del token, que es exactamente la
  card-ificación que DDL-014 prohíbe. El criterio correcto no es la visibilidad del borde sino su
  **función**.
- **Regla de decisión que deja instalada**: *si el borde desaparece, ¿deja de poder distinguirse
  dónde empieza y acaba un control?* Si la respuesta es sí, está sujeto al 3:1.
- **Lo que NO resuelve**: qué token usan los bordes de control. **Ninguno de los dos actuales
  sirve** —`--color-border` da 1.18:1 como está implementado y `--color-border-strong` daría
  1.48:1—, así que hace falta un valor nuevo. Esa mitad de OD-31 **sigue abierta** a propósito: la
  norma se fija antes que el token, y el token antes que el código.
- **Documentos afectados**: `02` § 8; `06` § 11.

---

### DDL-061 — `--color-primary-400` se define por su rol real, no por una intención no implementada (cierra OD-30)
- **Decisión**: `#2F6FDD` queda definido como **acento no textual**. Rol: señalar interacción sin
  portar texto. Usos permitidos: borde o marca de estado interactivo, indicador visual de foco,
  iconografía decorativa donde corresponda. **Criterio: 3:1** de elemento no textual, que cumple
  sobre todas las superficies claras (4.31–4.73). **No se usa como color de texto ni de enlace**:
  daría 4.44 sobre Canvas y 4.31 sobre Subtle, bajo el 4.5 del texto normal. Se retira la etiqueta
  "Hover de acción primaria" de `06` § 2.1 y "Hover / variante" de `02` § 3.2.
- **Estado**: Vigente · **Fecha**: 2026-08-09
- **Qué corrige exactamente**: la investigación de OD-30 confirmó que el problema **no era un
  incumplimiento de contraste sino una discrepancia entre el rol declarado y el uso implementado**.
  El token estaba etiquetado como hover de acción primaria aunque **ningún consumidor lo utiliza
  para ese propósito**. La corrección modifica únicamente la semántica documental: **no modifica el
  valor, ni los consumidores, ni los requisitos de contraste**. Queda escrito para que quien vea
  dentro de meses que el nombre cambió no lo interprete como un rediseño.
- **Por qué la etiqueta falsa era el riesgo**: "Hover de acción primaria" con "4.73:1 sobre blanco
  ✓" al lado invita a aplicarlo como color de texto sobre el fondo de página, donde da 4.44. La
  cifra era cierta y el uso que sugería, incorrecto — el caso exacto que DDL-060 describe.
- **Lo que esta decisión NO hace**: no convierte los cuatro consumidores actuales en lista
  normativa. La normativa define **token → rol semántico → usos permitidos → restricción de
  contraste**; el código puede tener cuatro consumidores o treinta mientras respeten ese contrato.
  Acoplar el Design System a la implementación concreta sería reintroducir la contaminación
  código → normativa que DDL-059 acaba de cerrar.
- **Documentos afectados**: `02` § 3.2; `06` § 2.1, § 11.

---

### DDL-060 — El contraste pertenece al par **y al uso** (cierra OD-24)
- **Decisión**: `06` § 11 deja de presentarse como matriz de accesibilidad y pasa a declarar
  **pares normativos representativos, con su uso**. Tres consecuencias:
  1. **La tabla lleva columna de Uso/Contexto.** Un ratio no es una propiedad intrínseca de un
     color: pertenece al par **y al uso**. `#2F6FDD` con 4.73:1 "sobre blanco" es válido como
     relleno con texto blanco y no necesariamente como texto sobre el fondo de página — una tabla
     sin contexto induce precisamente al uso incorrecto.
  2. **Declara explícitamente que no es exhaustiva.** Ningún consumidor puede inferir conformidad
     para un uso distinto a partir de un par medido, ni suponer válida una combinación por el
     hecho de no figurar.
  3. **Incluye los casos límite**, que son los que importan: muted sobre Subtle claro (4.73) y
     sobre Elevated oscuro (4.55), sistema sobre Subtle claro (4.70), indicador sobre Subtle
     (3.17).
- **Estado**: Vigente · **Fecha**: 2026-08-09
- **Dirección de verificación** (lo más importante de esta decisión): *`02` y `06` declaran qué
  usos son legales → la auditoría cruza esos usos contra todos los fondos → aparecen las
  combinaciones no cubiertas.* **Nunca** *`06` enumera combinaciones → lo no enumerado se supone
  válido.* La segunda dirección es la que produjo este problema, y con una tabla más grande lo
  habría reproducido igual dentro de unos meses.
- **La matriz completa no se convierte en normativa**: `06` crecería con combinaciones que no
  representan usos reales, y cada uso nuevo obligaría a ampliar una tabla que debe describir
  decisiones, no ser un motor de combinatoria. Queda como **evidencia de auditoría** bajo OD-24.
- **Correcciones de cifra ejecutadas**: `06` § 11 y `02` § 5 pasan de 15.98 → 16.81, de 11.40 →
  13.49 y de ≈6.5 → 5.14; DDL-027, `02` § 3.3 y `06` § 11 pasan de 5.58 → 5.16. La decisión de
  DDL-027 **no cambia**: 5.16 sigue sobre el umbral.
- **No cierra** OD-30 ni OD-31, que salieron del recálculo y quedan abiertas y referenciadas desde
  la propia tabla para que nadie las lea como resueltas.
- **Documentos afectados**: `06` § 11; `02` § 3.3, § 5; entrada DDL-027 de este Log.

---

### DDL-059 — Código existente ≠ decisión (cierra OD-25)
- **Decisión**: **un valor puede permanecer implementado sin tener autoridad normativa.** Su
  presencia en producción constituye evidencia del estado actual, **no autorización para
  conservarlo**. `06` puede documentar un valor existente como tal, pero no presentarlo como
  especificación cerrada. Estatus de los cuatro bloques auditados en OD-25:

  | Bloque | Estatus | Alcance real de la autoridad |
  |---|---|---|
  | Rampa `--color-primary-*` | **Parcialmente ratificado** | `500` por DDL-025; `400` y `300` por `02` § 3.2 (hover y enlace sobre oscuro). `50`, `100`, `200`, `600`, `700`: **no ratificados** |
  | Radios | **No ratificados — valores existentes** | DDL-054 decide **dónde viven**, no cuánto miden |
  | Escala de espaciado | **No ratificada — valores existentes** | La **regla de densidad** sí es normativa (`01` § 5.6, DDL-041); la escala numérica no |
  | Layout y breakpoints | **No ratificados — valores existentes** | El shell como estructura está decidido (DDL-015); sus dimensiones no |

- **Estado**: Vigente · **Fecha**: 2026-08-09
- **Qué NO hace esta decisión**: no borra ningún valor del código, no los ratifica en silencio, no
  los sustituye y **no usa el histórico como autoridad**. Ninguno de los tres bloques no
  ratificados genera un DDL automáticamente: una decisión posterior determinará explícitamente si
  se ratifican los valores existentes o se diseñan nuevos. Mientras tanto, el código permanece
  intacto.
- **Diferencia con OD-22**: allí había un valor **inventado** en `06` y se pudo retirar la falsa
  especificación porque `02` podía decidir el verdadero. Aquí hay **valores reales en producción**
  sin decisión que determine si deben conservarse. Eso exige una decisión posterior, no una
  corrección editorial — y por eso el cierre clasifica en vez de resolver.
- **Regla que consolida** (con DDL-055 y DDL-058): hay que seguir la cadena hasta ver dónde
  termina. **Si termina en `tokens.css`, la cadena no está cerrada.**
- **Corrección derivada**: la nota de gobernanza de DDL-036 afirmaba que su contenido migró a `06`
  en "radios, sombras y densidad". No decidió ninguna de las tres; se corrige en esa entrada.
- **Documentos afectados**: `06` § 2.1, § 4, § 7, § 9, § 13; nota de DDL-036 en este Log.

---

**Nota de procedencia** (DDL-024): OD-22 y OD-23 son el primer caso en que la regla de conflictos
se ejerce sobre el sistema ya congelado. Detectaron un valor inventado durante la redacción de un
documento normativo que estaba en camino de convertirse en especificación oficial sin que nadie lo
hubiera decidido. El mecanismo hizo lo que se diseñó para hacer.

---

## Open Decisions

**Cuatro: OD-26, OD-27, OD-28, OD-29.** OD-25 se cerró con DDL-059, OD-24 con DDL-060, OD-30 con
DDL-061 y OD-31 con DDL-062 + DDL-063, todas el 2026-08-09; OD-30 y OD-31 nacieron del recálculo
de OD-24. Las cuatro que quedan son de **trazabilidad**, no de conformidad.
Las veintiuna Open Decisions del reset quedan cerradas el 2026-08-08.
OD-22 y OD-23 se abrieron y cerraron ese mismo día al preparar la Fase 4. Las seis siguientes
salen de la **auditoría de trazabilidad de `01`–`08`** (2026-08-09), que OD-22 motivó: si un valor
sin procedencia había llegado a `06`, no había razón para suponerlo único. No lo era.

> **Regla de trabajo para todas ellas** (DDL-024, DDL-055): la auditoría identifica, la OD decide,
> el documento normativo consume, el código implementa. **No se cierra ninguna por continuidad ni
> por lo que diga el código.** Que un valor exista en `tokens.css` demuestra implementación, no
> decisión. Ninguna toca código.

### Hallazgo estructural de la auditoría — tres formas de contaminación de autoridad

Es el resultado más importante de la pasada, por encima de cualquier anomalía concreta: el fallo
de OD-22 no era un caso aislado sino **una de tres formas** en que la autoridad fluye al revés.
Se registra aquí porque condiciona cómo se cierran OD-24…OD-29 y cómo se redacta lo que venga:

| Forma | Mecanismo | Dónde apareció |
|---|---|---|
| **Redacción → normativa** | "Hay que rellenar esta celda o esta regla, ponemos X" | OD-22 (`#0F1720`), D3, D4, la mitad nueva de D5 |
| **Código → documento** | "El código tiene este valor, luego es normativa" | C1–C4, D7 |
| **Documento → documento** | "Este documento dice X, luego el anterior lo decidió" | D6, la mitad heredada de D5 |

La tercera es la más difícil de ver, porque la cadena de citas **parece** correcta: cada documento
cita al anterior y ninguno miente. Lo que falta está al principio de la cadena, no en ella.

### OD-30 — `#2F6FDD` solo cumple sobre Surface, y no se declara para qué uso (cerrada)

**Tipo**: valor por debajo de umbral en parte de sus superficies. **Cerrada por DDL-061** ·
**Fecha**: 2026-08-09. **Origen**: recálculo completo de OD-24 (DDL-040: hallazgo fuera de alcance,
se registra y no se arregla dentro).

> **Conclusión**: **no hay defecto de implementación, no hay cambio de token, no hay nuevo
> requisito de contraste.** Se corrige únicamente la autoridad semántica del token.

`02` § 3.2 declara `#2F6FDD` con "4.73:1 sobre blanco ✓". El dato es correcto, pero **solo
describe una de sus dos lecturas posibles**, y ni `02` ni `06` dicen cuál es:

- **Como relleno de botón con texto blanco**: 4.73:1 es el par blanco/`#2F6FDD` — **cumple**.
- **Como color de texto o enlace en estado hover**: sobre Canvas da **4.44** y sobre Subtle
  **4.31** — **por debajo de 4.5**. Solo cumple sobre Surface.

`06` § 2.1 lo etiqueta "Hover de acción primaria", que no desambigua: la acción primaria puede ser
un botón o un enlace. **Si existe hover de enlace en `#2F6FDD` sobre el fondo de página, hay un
incumplimiento real en producción**; si es solo relleno de botón, no lo hay y lo que falta es
declararlo.

**Qué debe decidir**: primero, cuál es el uso real (comprobación de código, no de documento);
después, si se restringe el token a relleno, se oscurece para cumplir en las tres superficies, o
se separan dos tokens por uso. **No se decide el valor antes de saber el uso.**

#### Investigación de uso real (2026-08-09)

Se siguió la cadena hasta el final, como exige DDL-058: *normativa → uso declarado → implementación
→ fondo efectivo*. `--color-primary-400` tiene **cuatro consumidores** en `src/` (excluidos los
artefactos de `obj/`), y **ninguno lo usa como texto**:

| Consumidor | Propiedad | Naturaleza | Umbral que aplica |
|---|---|---|---|
| `ZonaSoltarArchivo.razor.css:15` | `border-color` en el estado activo de arrastre | Límite de control | 3:1 |
| `ZonaSoltarArchivo.razor.css:36` | `color` de `.zona-soltar-archivo-icono` | **Icono decorativo** (`02` § 7: `aria-hidden`, acompañado de texto visible) | Ninguno |
| `list-page.css:571` | `box-shadow: inset 2px 0 0` — barra de acento de la fila con foco de teclado | Indicador de foco | 3:1 |
| `list-page.css:576` | `box-shadow: 0 0 0 1px` — mismo indicador en tarjeta | Indicador de foco | 3:1 |

Contraste contra los fondos **efectivos**, no los hipotéticos:

| Caso real | Ratio | |
|---|---|---|
| Borde de zona activa contra su relleno `--color-primary-50` | 4.31 | ✓ 3:1 |
| Borde de zona activa contra Canvas / Surface (exterior) | 4.44 / 4.73 | ✓ 3:1 |
| Barra de foco de fila contra el fondo de fila enfocada | 4.31 | ✓ 3:1 |
| Anillo de foco de tarjeta contra Surface / Canvas | 4.73 / 4.44 | ✓ 3:1 |

**No hay incumplimiento en producción.** El umbral de 4.5 que hacía saltar la alarma **no aplica a
ninguno de los cuatro usos**: los tres no decorativos son elementos no textuales, sujetos a 3:1, y
lo superan con margen.

**Pero el hallazgo real es otro, y no es menor**: `02` § 3.2 lo llama "Hover / variante" y `06`
§ 2.1 "Hover de acción primaria". **Ningún hover de botón usa este token.** Sus usos reales son
estado activo de zona de soltar, icono decorativo e indicador de foco de teclado. La etiqueta
documental no describe lo que hace — y, peor, **invita al uso que sí incumpliría**: quien lea
"hover de acción primaria" con "4.73:1 sobre blanco ✓" al lado puede aplicarlo como color de texto
sobre Canvas, donde da 4.44. Es exactamente el riesgo que DDL-060 describe.

### OD-31 — Ningún borde declarado alcanza el 3:1 que exige `02` § 8 (cerrada)

**Tipo**: conflicto entre una regla de `02` y los valores que el propio `02` declara.
**Cerrada en dos mitades**: la normativa por **DDL-062** (qué borde está sujeto al 3:1) y la de
token por **DDL-063** (con qué valor se satisface) · **Fecha**: 2026-08-09.

> **La única OD de esta serie con un incumplimiento real en producción**, y la única que terminó
> cambiando código. Las demás corrigieron autoridad; esta corrigió la interfaz. **Origen**: recálculo completo de OD-24 (DDL-040).

`02` § 8 fija **3:1** para "componentes de interfaz y **bordes significativos**". Los cuatro
tokens de borde del sistema quedan muy por debajo contra cualquier superficie de su tema: en claro
1.13–1.48; en oscuro 1.17–2.04. **Ninguna combinación de las catorce llega a 3:1.**

**Por qué no es automáticamente un defecto**: el umbral de 3:1 aplica al límite visual que
identifica un **control** —el borde de un campo de formulario, por ejemplo—, no a un separador o
al contorno de una tarjeta. Un borde decorativo no tiene que alcanzarlo. El problema es que **`02`
§ 8 no distingue**: dice "bordes significativos" sin definir cuáles lo son, y con los valores
actuales ningún borde podría serlo.

**Qué debe decidir**: si se acota la redacción de `02` § 8 a los bordes que sí identifican un
control, y en ese caso **qué token usan esos bordes**, porque ninguno de los cuatro actuales
sirve. Es la única de las ODs abiertas que puede terminar exigiendo un token nuevo.

> **Mitad normativa resuelta por DDL-062** (2026-08-09): el 3:1 aplica al borde que identifica un
> control y a los indicadores de estado; no a separadores ni contornos de agrupación.
> **Sigue abierta la mitad del token**: qué valor usan los bordes de control. `--color-border`
> (1.18:1 implementado) y `--color-border-strong` (1.48:1 declarado) **no sirven ninguno de los
> dos**. Requiere un valor nuevo, y por tanto una decisión propia — no se elige aquí.

#### Investigación del token (2026-08-09)

**Fondos adyacentes reales de los cinco controles.** El interior del control es
`--color-surface`; el exterior puede ser Surface, Canvas o Subtle según viva en una tarjeta, en la
página o en una isla. El borde debe cumplir contra **todos**, no solo contra blanco.

| Tema | Fondo más restrictivo | Condición sobre el borde |
|---|---|---|
| Claro | Subtle `#F1F5F7` | luminancia ≤ 0.2691 |
| Oscuro | Elevated `#202B36` | luminancia ≥ 0.1690 |

**Candidatos dentro de la paleta existente**, evaluados por su **peor** caso, no por el mejor:

| Candidato | Claro (peor) | Oscuro (peor) | |
|---|---|---|---|
| `--color-border` declarado `#E2E8EC` / `#293644` | 1.13 | 1.17 | ✗ |
| `--color-border-strong` `#CBD5E1` / `#3A4A5C` | 1.35 | 1.58 | ✗ |
| `neutral-300` `#D5DCE5` | 1.26 | — | ✗ |
| `neutral-400` `#A9B4C2` | 1.92 | **6.84** | ✗ claro · ✓ oscuro |
| **`neutral-500` `#738196`** | **3.61** | **3.64** | **✓ en ambos** |
| `--color-text-muted` `#5F6E84` / `#8592A3` | 4.73 | 4.55 | ✓, pero peso de texto |

**No hace falta inventar un color.** `neutral-500` cumple el 3:1 en los dos temas con el **mismo
valor**, contra todos los fondos efectivos. Es además el escalón que DDL-029 retiró del **texto**
por dar 3.96:1 —insuficiente para cuerpo de 14 px— y que aquí encuentra un rol legítimo: como
elemento **no textual** su umbral es 3:1, no 4.5:1. El valor no se recicla por comodidad; se
aplica donde su contraste sí es el correcto.

`--color-text-muted` también cumpliría, pero un borde tan oscuro como el texto secundario daría a
cada campo un peso de caja que compite con la contención que pide DDL-014.

**Sobre el nombre**: `--color-border-strong` **no debe absorber este uso**. Su semántica en el
reset es estructural —DDL-028 lo asigna al borde reforzado que expresa el nivel **Elevated** sin
sombra—, que es una función distinta de identificar un control. Fundir ambas en un token obligaría
a que la expresión de profundidad y el límite de un control compartan valor para siempre.

**Nota de alcance**: este hallazgo estaba fuera del radar de toda la auditoría anterior. No lo
detectó la lectura de los documentos —`02` § 8 y los tokens de borde son coherentes leídos por
separado—, sino el recálculo cruzado que pediste antes de decidir la tabla de `06` § 11.

#### Investigación de uso real (2026-08-09)

El borde llega casi siempre por el token compuesto `--border-default`
(`1px solid var(--color-border)`), con **93 usos** en `src/`. Clasificados por lo que hace el borde:

| Clase | Consumidores | ¿Aplica el 3:1? |
|---|---|---|
| **Identifica un control** | `CampoTexto` · `CampoTextarea` · `CampoSelect` · `CampoBuscarSelect` · `SelectorEntidad` · `Boton` · `BotonCopiar` · `ZonaSoltarArchivo` (reposo) | **Sí** — es el límite visual que distingue el control de su fondo |
| **Separador o contorno estructural** | `Tarjeta` · `TarjetaMetrica` · `Modal` · `Drawer` · `SeccionColapsable` · `BarraAccionesLote` · `Pestanas` · páginas de Account | **No** — agrupan, no identifican un control |
| **Estado / foco** | `--border-focus` (`2px solid var(--color-primary-500)`) | Sí, y **cumple**: 6.27:1 |

**Hay un incumplimiento real, y está en la primera clase.** Un campo de texto sobre Surface cuyo
único límite visual es un borde de **1.18:1** —valor implementado `#E8EDF2`; 1.24:1 con el
`#E2E8EC` que declara la normativa— no ofrece la frontera que 1.4.11 exige para identificar un
control. Afecta a los cinco componentes de entrada del sistema, no a un caso aislado.

**Dos datos que acotan el alcance del arreglo**:
- **El indicador de foco no está afectado**: usa otro token y da 6.27:1. Lo que falla es el estado
  de reposo del control, no su foco.
- **`--color-border-strong` no existe en `tokens.css`**: es un token del reset todavía no
  implementado, con cero consumidores. Está disponible como destino sin romper nada — pero su
  valor declarado (`#CBD5E1`, 1.48:1) **tampoco alcanza 3:1**, así que no resuelve por sí solo.

**Diferencia con OD-30**: allí la alarma se disolvió al mirar el uso real. Aquí el uso real
**confirma** el problema y lo acota. Es la primera OD de esta serie que va a exigir un cambio de
valor o un token nuevo, no solo una corrección de autoridad.

### OD-24 — Ratios de contraste declarados sin trazabilidad (cerrada)

**Tipo**: mediciones normativas no reproducibles. **Cerrada por DDL-060** · **Fecha**: 2026-08-09.
Generó OD-30 y OD-31, que siguen abiertas. La matriz completa de § 3 se conserva aquí como
**evidencia de auditoría**, no como normativa.

Cuatro ratios declarados en `02` y `06` no reproducen al recalcularlos sobre los propios valores
que esos documentos fijan:

| Fuente | Par | Declarado | Recalculado |
|---|---|---|---|
| `06` § 11 | Texto principal / Surface claro | 15.98:1 | **16.81:1** |
| `02` § 5 · `06` § 11 | Texto principal / Surface oscuro | 11.40:1 | **13.49:1** |
| `02` § 5 · `06` § 11 | Texto secundario / Surface oscuro | ≈6.5:1 | **5.14:1** |
| `02` § 3.3 · `06` § 11 | Texto de sistema / blanco | 5.58:1 | **5.16:1** |

**Los cuatro siguen superando su umbral**: no hay defecto de accesibilidad. Pero superar el umbral
no hace correcta la cifra, y **A3 y A4 sobreestiman** el contraste real, que es la dirección
peligrosa: declaran un margen que no existe.

#### Investigación (2026-08-09)

**1 · Resolución hacia atrás.** Se buscó, entre **todos** los pares posibles de la paleta declarada
en `02`, `06` y `tokens.css`, alguno que produjera cada cifra: si un ratio correspondiera a un par
real mal etiquetado, sería un error de documentación y no un número sin origen.

| Cifra | Par que la produce |
|---|---|
| 11.40 · 5.58 | **Ninguno** en toda la paleta |
| 15.98 | Solo 16.02, que es la isla del tema **oscuro** contra una superficie de hover **clara ya retirada**. Par que nadie mediría: coincidencia aritmética |
| ≈6.5 | Solo 6.55, que es `primary-300` sobre isla oscura — **color de enlace**, no texto secundario |

Ninguna de las cuatro es una medición real mal declarada.

**2 · Verificación completa de la capa de decisión.** Se recalcularon **las nueve** mediciones que
declara este Log. **Ocho reproducen exactas**: 6.27 y 4.78 (DDL-025); 3.48, 1.41, 2.14 y 2.43
(DDL-027); 3.96 y 5.18 (DDL-029). **Una no**: `#0C7792` = **5.58 declarado, 5.16 real**, en
DDL-027 — la misma entrada que acierta las otras cuatro. El protocolo de medición del banco
funcionó; se le escapó un valor.

> **Corrección de esta misma entrada**: su primera redacción afirmaba que "los cuatro que fallan no
> tienen DDL detrás". **Es falso para 5.58**, que sí está declarado en DDL-027. La afirmación se
> corrige aquí en vez de reescribirse en silencio (DDL-024).

#### Las cuatro no son un solo problema

| Clase | Cifras | Naturaleza |
|---|---|---|
| **Cifra errónea dentro de una decisión válida** | 5.58 (DDL-027 · `02` § 3.3 · `06` § 11) | `#0C7792` da 5.16, que **sigue superando 4.5:1**. La decisión de DDL-027 no queda invalidada — solo su cifra de apoyo. Enmendar evidencia dentro de un DDL, no decidir de nuevo |
| **Sin respaldo en ninguna capa** | 15.98 · 11.40 · ≈6.5 | Ni en el Log, ni derivables de ningún par, ni presentes en el historial antes del commit del reset. Contaminación **redacción → normativa** (DDL-058, forma 3) |

**Dirección del error, que reordena la prioridad**: 15.98 (real 16.81) y 11.40 (real 13.49)
**subestiman** — declaran menos margen del que hay, dirección inofensiva. ≈6.5 (real 5.14) y 5.58
(real 5.16) **sobreestiman**: afirman un margen que no existe, y una de las dos está en un DDL.

**Hallazgo colateral**: `06` § 11 titula su tabla "Pares medidos en el **banco visual**". La
investigación de OD-22 ya estableció que **no existe artefacto del banco versionado** — su único
registro es este Log. Una tabla de mediciones no puede atribuirse a una fuente que no existe como
documento; o sus cifras vienen del Log, o se recalculan.

#### 3 · Recálculo del conjunto completo (2026-08-09)

Antes de decidir qué filas merece tener la tabla, se calcularon **todos** los pares de primer
plano y superficie que el sistema declara, en ambos temas y contra su umbral (`02` § 8).

**Tema claro** — superficies Canvas `#F6F8FA` · Surface `#FFFFFF` · Subtle `#F1F5F7`:

| Primer plano | Umbral | Canvas | Surface | Subtle |
|---|---|---|---|---|
| Texto principal `#161E27` | 4.5 | 15.79 ✓ | 16.81 ✓ | 15.33 ✓ |
| Texto secundario `#5F6E84` | 4.5 | 4.87 ✓ | 5.19 ✓ | 4.73 ✓ |
| Texto de sistema `#0C7792` | 4.5 | 4.85 ✓ | 5.16 ✓ | 4.70 ✓ |
| Acción primaria `#235BC2` | 4.5 | 5.89 ✓ | 6.27 ✓ | 5.72 ✓ |
| **Hover primario `#2F6FDD`** | 4.5 | **4.44 ✗** | 4.73 ✓ | **4.31 ✗** |
| Indicador de sistema `#0E96B4` | 3.0 | 3.27 ✓ | 3.48 ✓ | 3.17 ✓ |
| **Borde normal `#E2E8EC`** | 3.0 | **1.16 ✗** | **1.24 ✗** | **1.13 ✗** |
| **Borde reforzado `#CBD5E1`** | 3.0 | **1.39 ✗** | **1.48 ✗** | **1.35 ✗** |

**Tema oscuro** — Canvas `#0E141B` · Surface `#17212C` · Subtle `#131B24` · Elevated `#202B36`:

| Primer plano | Umbral | Canvas | Surface | Subtle | Elevated |
|---|---|---|---|---|---|
| Texto principal `#E7EAEE` | 4.5 | 15.34 ✓ | 13.49 ✓ | 14.38 ✓ | 11.92 ✓ |
| Texto secundario `#8592A3` | 4.5 | 5.85 ✓ | 5.14 ✓ | 5.49 ✓ | 4.55 ✓ |
| Texto de sistema `#2BD4F0` | 4.5 | 10.38 ✓ | 9.13 ✓ | 9.73 ✓ | 8.07 ✓ |
| Enlace `#5CA2F4` | 4.5 | 6.99 ✓ | 6.15 ✓ | 6.55 ✓ | 5.43 ✓ |
| **Borde normal `#293644`** | 3.0 | **1.50 ✗** | **1.32 ✗** | **1.41 ✗** | **1.17 ✗** |
| **Borde reforzado `#3A4A5C`** | 3.0 | **2.04 ✗** | **1.79 ✗** | **1.91 ✗** | **1.58 ✗** |

**Lectura**: los textos cumplen en todas las combinaciones de ambos temas, algunos con margen
fino —secundario sobre Subtle claro 4.73 y sobre Elevated oscuro 4.55—, pero cumplen. Los dos
problemas reales **no estaban en ninguna de las cuatro filas de `06` § 11**, que es exactamente
lo que una tabla de cuatro pares elegidos a mano no puede detectar. Se registran como **OD-30** y
**OD-31**: son hallazgos ajenos al alcance de OD-24 y **no se arreglan dentro de ella** (DDL-040).

**Qué debe decidir**: si `02`/`06` conservan ratios; si se sustituyen por mediciones
reproducibles; **qué ratios son normativos y cuáles son solo evidencia**; y qué impide que un
número calculado durante la redacción vuelva a presentarse como "medido en el banco".

### OD-25 — Autoridad de los valores visuales copiados del código (cerrada)

**Tipo**: contaminación código → documento (DDL-058, forma 1). **Cerrada por DDL-059** ·
**Fecha**: 2026-08-09.

Cuatro bloques de `06` declaran valores verificados como **copia literal de `tokens.css`**:

| Bloque | Valores | Fuente declarada | Problema |
|---|---|---|---|
| § 2.1 | `primary-50/100/200/600/700` | DDL-025 | DDL-025 ratificó **solo** `#235BC2` |
| § 4 | 5 radios (6/10/12/14/9999 px) | DDL-054 | DDL-054 **reclasifica** radios como no-motion; no fija valores |
| § 7 | `space-10/12/16/24` | — | ninguna |
| § 9 | `260px`, `64px`, 4 breakpoints | — | ninguna |

**La pregunta no es si los valores son buenos**, sino cuáles son normativos y cuáles son
implementación. **No se asume que deban convertirse en decisión**: puede que la conclusión
correcta sea que `06` no debe normarlos todavía y pertenecen al ámbito de implementación.

Dos son además **sobreatribución**: citan una decisión que cubre menos de lo que se le atribuye.
Es un fallo distinto de "sin fuente" y más difícil de detectar, porque la cita existe.

#### Investigación (2026-08-09)

| Comprobación | Resultado |
|---|---|
| `02` como fuente de la rampa primaria | **Cero apariciones** de `primary` o de los cinco hexes en `02`. `02` § 3.2 declara **tres** valores **por rol** (acción, hover, enlace sobre oscuro), no una rampa de ocho escalones |
| DDL sobre valores de radio, escala de espaciado, breakpoints o dimensiones del shell | **Ninguna.** Lo más cercano es DDL-011 ("no se adopta ninguna escala de librería como identidad"), que **restringe** sin fijar valores |
| Antigüedad en el código | `--radius-card-sm`, `--space-24` y `--sidebar-width` datan del commit fundacional (`41a7453`, "Versión base Hydra"). `--color-primary-200` entró después, en una realineación de tokens |
| Regla de aplicación del espaciado (`06` § 7) | **Sí trazable**: cita `01` § 5.6 (densidad). Se separa del problema: la *regla* tiene fuente, la *escala de valores* no |

**Hallazgo adicional, dentro del propio Log**: la nota de gobernanza de **DDL-036** afirma que su
contenido migró a `06` "(radios, sombras y densidad)". El cuerpo de DDL-036 **no decide radios ni
sombras** — son once reglas de presentación (verbos, badges, microcopy, ranuras, iconografía,
anillo), ninguna dimensional; y la densidad la decide DDL-041, no DDL-036. Es una sobreatribución
**en la capa de máxima autoridad**, y es lo que hace que los radios parezcan respaldados desde dos
sitios (DDL-036 y DDL-054) cuando no lo están desde ninguno.

**Cierre**: **DDL-059**. Ninguno de los cuatro bloques se eleva a normativa por continuidad con el
código. La rampa queda **parcialmente ratificada** —tres escalones con autoridad real, cinco sin
ella— y los otros tres bloques quedan como **valores existentes no ratificados**, documentados
como estado vigente sin presentarse como especificación. `06` § 4 conserva la cita a DDL-054 pero
reformulada: decide **dónde viven** los radios, no cuánto miden. La nota de DDL-036 se corrige en
su propia entrada.

### OD-26 — `07` introduce restricciones sin autoridad identificable

**Tipo**: redacción → normativa, en motion. **Abierta** · **Fecha**: 2026-08-09.

- `07` § 4: "**máximo dos focos de movimiento simultáneos por pantalla**". DDL-016 fija los tres
  tiers y el presupuesto de Tier C (1–2 usos por pantalla); **este límite no está en ninguna DDL**.
  Primera aparición en el historial: el commit del reset.
- `07` § 7: dos rechazos permanentes —paralaje y desplazamientos decorativos; animaciones de
  entrada por elemento en listas largas— con la columna "Decisión" en **"—"**. El documento admite
  por sí mismo que no hay decisión detrás, y sin embargo `07` § 7 existe *precisamente* para que
  ningún brief futuro los reintroduzca: son prohibiciones permanentes sin decisión permanente.

**A determinar también**: si ambos comparten una decisión superior sobre presupuesto de movimiento
que habría que declarar una sola vez, en vez de dos reglas sueltas.

### OD-27 — "Umbral alto" es una condición de ejecución sin definir

**Tipo**: término normativo indefinido con efecto operativo. **Abierta** · **Fecha**: 2026-08-09.
**Prioridad alta.**

"Umbral alto" se invoca tres veces —`04` § 8.2, `04` § 8.3, `05` § 4.2— como si estuviera
definido. **No lo está en ningún documento ni en ninguna DDL** (verificado: cero apariciones en
este Log).

No es vaguedad de redacción. La cadena es:

```
"umbral alto"  →  condición de acción  →  acción en lote  →  IA sobre superficie operativa
```

`04` § 8.3 condiciona las confirmaciones en lote de propuestas de IA a superarlo. Sin definición,
**la implementación tendría que decidir por su cuenta qué significa "alto"** — exactamente en el
punto donde `01` § 5.4 y `05` § 4.4 ponen el límite duro de la confirmación humana.

### OD-28 — Límites numéricos de interacción sin fuente

**Tipo**: redacción → normativa. **Abierta** · **Fecha**: 2026-08-09.

Tres límites concretos cuya primera aparición en todo el historial es el commit del reset, sin
antecedente en este Log, en los documentos archivados ni en el código:

| Regla | Dónde | Comprobado |
|---|---|---|
| "Nunca más de **tres** avisos visibles" | `04` § 7.1, repetido en `08` § 4.4 | Sin fuente en Log, archivo ni código — `AnfitrionToasts` no implementa límite alguno |
| Matriz de composición de superficies + "**dos** niveles de overlay es el máximo" | `05` § 7 | Sin DDL. Es **estructura normativa nueva**, y `05` es justamente donde se decidió que viven los patrones estructurales |
| "Ninguna situación que requiera atención está a más de **un clic**" | `03` § 4.1 | Sin fuente. Se presenta junto a una regla heredada (ver OD-29), lo que le presta una continuidad que no tiene |

El caso de la matriz de `05` § 7 es el más serio de los tres: no es un número aislado sino un
sistema de reglas de composición del que se derivan cuatro consecuencias explícitas.

### OD-29 — Reglas heredadas del histórico, normadas sin ratificación

**Tipo**: documento → documento, con raíz en documentación archivada sin autoridad. **Abierta** ·
**Fecha**: 2026-08-09.

Tres reglas que `03`, `04` y `08` presentan como normativa y que la auditoría trazó hasta
`docs/archive/design/` — histórico explícitamente **sin autoridad** (DDL-056):

| Regla | Ruta de contaminación |
|---|---|
| "≤3 clics desde el Dashboard" | `UX_PATTERNS.md` archivado (§ 139) → `03` § 4.1, que la marca como "histórica" y la conserva |
| Filtros persisten en URL / orden no | `UX_PATTERNS.md`, histórico archivado sin autoridad (§§ 60, 65) → `03` § 5.2 → **`04` § 3.2 y § 3.3 citan a `03`** como si `03` lo hubiera decidido |
| `AnilloCumplimiento`: "umbral propio, distinto del semáforo" | `DESIGN_SYSTEM.md`, histórico archivado sin autoridad (§ 189), que **describía el código** (100 % Éxito, ≥50 % Advertencia, resto Peligro) → `08` § 4.4 |

**Qué debe decidir**: cuáles de estas reglas se **ratifican** como decisión propia —con DDL— y
cuáles se retiran. Conservar una regla porque estaba antes es precisamente lo que DDL-055
descarta: la autoridad viene de la posición en la cadena, no de la antigüedad.

**Nota sobre la dirección de referencia** (D6): el problema no se arregla añadiendo una cita. Hay
que determinar **cuál de los dos documentos debe ser autoridad** sobre la persistencia en URL —
`03` (dónde vive el estado) o `04` (cómo se comporta la lista)— antes de decidir el contenido.

### OD-23 — `02` no declara el texto principal en modo claro (cerrada)

**Tipo**: laguna normativa en `02`. **Cerrada por DDL-057** · **Fecha**: 2026-08-08.

`02` § 3–5 declara valor para todos los roles cromáticos del tema claro — azul, cian, semáforo,
cobre retirado, los cuatro niveles de superficie, ambos bordes — y para el texto principal y
secundario del tema **oscuro** (§ 5). El texto principal **claro** es la única celda que falta.

**Por qué importa**: esa ausencia es la causa mecánica de OD-22. `06` necesitaba el valor, `02`
no lo daba, y se rellenó con un valor inventado. Cerrar OD-22 sin cerrar esto deja el hueco que
lo produjo abierto para el siguiente documento que lo necesite.

**La pregunta**: ¿cuál es el valor normativo del texto principal en tema claro? La decisión
pertenece a `02`, no a `06`. Reglas que la acotan:

- **No se resuelve escribiendo el valor que hay en el código.** Sería repetir el error de OD-22
  con el signo cambiado: el código no es autoridad, tampoco cuando coincide con lo esperado.
- **No se resuelve desde `06`.** `06` deriva de `02`, no al revés.
- `#161E27` es la continuidad de hecho y da 16.81:1 sobre Surface y 15.79:1 sobre Canvas, pero
  eso lo hace *admisible*, no *decidido*.

**Cierre**: **DDL-057** declara `#161E27` en `02` § 4.1 por continuidad de identidad y
suficiencia de contraste, dejando explícito que el histórico y el código son evidencia, no
autoridad. `06` § 2.5 pasa de documentar continuidad a consumir una especificación con fuente.

**Detalle de ubicación**: la decisión se declara en `02` § 4.1, no en § 5 como se planteó al
proponerla. § 5 es *Modo oscuro*: alojar ahí el valor del tema claro habría vuelto a separar el
valor de su tema. § 4.1 ya emparejaba claro/oscuro para superficies y bordes, y es donde el
texto principal queda junto a los fondos contra los que se mide.

### OD-22 — `--color-text` en modo claro: `#0F1720` vs. `#161E27` (cerrada)

**Tipo**: conflicto de especificación entre dos afirmaciones de `06`. **No** es una divergencia
de implementación: no se resuelve mirando el código, porque el código no tiene autoridad aquí.

**Estado**: **Cerrada** · **Fecha**: 2026-08-08 (abierta y cerrada el mismo día).

Los hechos, sin interpretarlos:

| # | Fuente | Afirma |
|---|---|---|
| 1 | `06` § 2.5, tabla de neutros y texto | `--color-text` (claro) = `#0F1720` |
| 2 | `06` § tabla "Lo que cambia" | enumera los cambios de token uno a uno y **no** incluye `--color-text`; el único cambio cromático de texto que contempla es `--color-text-muted` |
| 3 | `06` § tabla "Impacto medido en el código actual" | mide 4 cambios; `--color-text` no aparece |
| 4 | Implementación (`tokens.css:66` → `tokens.css:47`) | `--color-text` resuelve `#161E27` vía `--color-neutral-900` |
| 5 | Histórico archivado (`docs/archive/design/DESIGN_SYSTEM.md`) | `#161E27` como texto principal — sin autoridad, solo fija la procedencia del valor actual |
| 6 | `02` | no declara ningún valor para el texto principal en claro |
| 7 | Banco visual (DDL-025…DDL-030) | ninguna decisión fija `--color-text`: DDL-028 fija canvas, surface y bordes; DDL-029 fija solo el muted; DDL-026 fija la identidad oscura |

**La pregunta abierta no es "¿qué color queremos?"**, sino: *¿cuál es la procedencia de una
especificación que aparece en `06` sin trazabilidad?* Mientras no esté demostrada, `#0F1720` no
es una decisión válida y no puede implementarse. El código actual (`#161E27`) **tampoco es prueba
en contra**: no tiene autoridad sobre esta capa.

#### Investigación de procedencia (2026-08-08)

| # | Comprobación | Resultado |
|---|---|---|
| 1 | Este Log: `#0F1720` y `--color-text` | **Cero apariciones** fuera de esta entrada. Ninguna DDL fija un valor para `--color-text` en claro |
| 2 | `02` § 3–5: valor normativo del texto principal | `02` **no lo declara**. Es la única laguna de su paleta: declara azul, cian, semáforo, cobre retirado, los cuatro niveles de superficie en ambos temas, ambos bordes, y el texto principal **oscuro** `#E7EAEE` y secundario oscuro `#8592A3` (§ 5), pero deja sin valor el texto principal **claro** |
| 3 | Banco visual / criterios de contraste | No existe artefacto del banco versionado: su registro es este Log (DDL-025…DDL-037), y no contiene el valor. **Además el contraste no lo explica**: `#161E27` ya daba 16.81:1 sobre Surface y 15.79:1 sobre Canvas; `#0F1720` da 18.05:1. Ambos pasan con enorme margen, así que ninguna medición del banco pudo motivar el cambio |
| 4 | Origen del `#161E27` actual | Introducido en `6e05ec0` ("Identidad visual propia y jerarquía del Dashboard"), muy anterior al reset |
| 5 | Git: introducción de `#0F1720` | **Primera aparición en todo el historial del repositorio** en el commit del reset (`bad7d7c` → `b7ac8db`). Solo dos commits tocan `06` y ambos son el reset: el valor **nació con el documento**, no lo heredó |

**Traza fila a fila de la tabla de `06` § 2.5** — las otras tres celdas sí tienen fuente aguas
arriba, lo que aísla la anomalía:

| Celda de `06` § 2.5 | Fuente |
|---|---|
| `--color-text` oscuro `#E7EAEE` | `02` § 5 |
| `--color-text-muted` claro `#5F6E84` | DDL-029 · `02` § 8 |
| `--color-text-muted` oscuro `#8592A3` | `02` § 5 |
| `--color-border` / `-strong` | DDL-028 · `02` § 4.1 |
| **`--color-text` claro `#0F1720`** | **ninguna** |

**Conclusión de la investigación**: no existe ninguna fuente que respalde `#0F1720`. Es una
derivación editorial no trazada, aparecida al redactar `06` para rellenar la única fila que `02`
había dejado sin valor. No se convierte en decisión.

**Laguna descubierta de paso, que OD-22 no cierra**: `02` no declara el texto principal en modo
claro. Se registra aparte como **OD-23** — decidir el valor normativo pertenece a `02`, y
resolverlo dentro de `06` sería repetir el mecanismo que produjo `#0F1720`.

#### Veredicto (2026-08-08)

> `#0F1720` fue una **derivación editorial sin autoridad** y se elimina. `#161E27` permanece
> como valor **no modificado por el reset**, pero la autoridad normativa definitiva de ese valor
> debe quedar establecida en `02`. La ausencia del texto principal claro en `02` es una nueva
> laguna normativa (OD-23) y no debe resolverse dentro de `06`.

Dos precisiones que el cierre **no** afirma, para que no se lean por implicación:

- **`#161E27` no queda "decidido" por el código ni por el documento histórico.** Ninguno de los
  dos tiene autoridad. Lo que respalda su permanencia es normativa: la tabla "Lo que cambia" de
  `06` § 12 no enumera `--color-text`, luego `06` afirma que no cambia.
- **Retirar `#0F1720` no es sustituirlo por otra decisión.** `06` § 2.5 pasa a documentar
  continuidad marcada como "sin cambio en este reset", no una especificación nueva.

**Ejecutado al cerrar**: `06` § 2.5 retira `#0F1720` y remite el valor a `02`; `06` § 12 añade
`--color-text` a la fila "Sin cambios". Sin DDL nuevo por este cierre: no se decidió nada, se
retiró algo que nunca se decidió. El valor se decide aparte, en DDL-057.

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

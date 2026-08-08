# 01 — Product Experience

**Estado**: Normativo · **Implementado hasta**: parcialmente. Dos superficies del producto ya
encarnan lo que aquí se define — **Centro 360** (`/centros`, `/empresas`) y el **Communication
Workspace** (`/comunicaciones`) —; el resto de la aplicación sigue en el paradigma anterior y se
migrará por fases. Ninguna afirmación de este documento debe leerse como descripción del estado
actual del software.

**Autoridad**: este documento define **qué debe conseguir** la experiencia de Hydra. No define
cómo se ve (`02`), cómo se organiza (`03`), cómo se comporta (`04`/`05`), ni con qué piezas se
construye (`06`/`07`/`08`). Las decisiones que lo respaldan viven en
[`DESIGN_DECISION_LOG.md`](DESIGN_DECISION_LOG.md); si este documento y el Log divergen sobre
*qué se decidió*, manda el Log.

**Relación con `PROJECT.md`**: `PROJECT.md` sigue gobernando qué es el producto como negocio, a
quién sirve y sus principios de decisión (YAGNI, consistencia de patrones). Este documento
gobierna la **experiencia**, y sustituye la parte experiencial que `PROJECT.md` describía de
forma implícita.

---

## 1. La tesis

Hydra es una plataforma operativa premium para la gestión de Coordinación de Actividades
Empresariales. Su trabajo es **absorber la complejidad** —portales ajenos, sincronizaciones,
correo, WhatsApp, validaciones, vencimientos— y devolverla convertida en **estado, atención y
acción**.

El gestor no opera datos: **supervisa un sistema que trabaja con él**.

La vara de medir, heredada de la auditoría UX y todavía vigente: *¿esto hace el trabajo del
Gestor CAE más rápido y fiable que Excel más operar directamente los portales?* Una pantalla que
no supera esa prueba no está terminada, por bien que se vea.

## 2. Qué significa "Premium" (DDL-001)

Premium es, por este orden:

1. **Operacional** — reducción de trabajo, claridad, precisión, confianza, baja carga cognitiva,
   automatización, inteligencia contextual. Este es el núcleo.
2. **De oficio** — consistencia, tipografía, espaciado, jerarquía, accesibilidad. Esta es la
   expresión del núcleo.
3. **Estético** — superficies, profundidad, movimiento. Esto es condimento.

**Test de revisión, aplicable a cualquier propuesta visual**: *¿esto reduce trabajo, lo explica,
o lo decora?* Solo la tercera respuesta necesita justificarse.

Premium **no** significa gradientes, glow, glassmorphism, animación constante, tarjetas gigantes
ni efectos por decoración. Un producto que parece una demo de escaparate transmite lo contrario
de lo que Hydra necesita transmitir.

## 3. El modelo mental (DDL-003)

```
Contexto → Estado → Atención → Acción → Workflow → Automatización → IA
```

Toda superficie responde, en este orden:

1. **¿Qué está pasando?** — el contexto operativo y su estado.
2. **¿Qué requiere mi atención?** — lo que se sale de lo normal, priorizado.
3. **¿Qué puedo hacer?** — las acciones disponibles sobre eso, en el sitio donde aparece.
4. **¿Qué datos necesito consultar o modificar?** — la profundidad, cuando hace falta.

El orden importa: una pantalla que empieza por el punto 4 obliga al usuario a construir en su
cabeza los tres anteriores. Eso es exactamente el trabajo que Hydra existe para ahorrar.

Este modelo no es una aspiración: **está validado por el propio producto**. Las dos superficies
mejor resueltas de Hydra son las dos que lo siguen.

## 4. El CRUD es la capa profunda, no el paradigma (DDL-002)

Las listas, las tablas, la selección múltiple, las importaciones y las exportaciones **siguen
siendo capacidades fundamentales** y no deben degradarse: en este sector, "me lo llevo a Excel"
es la lingua franca, y operar en lote es lo que hace viable una cartera grande.

Lo que cambia es su rango. "Entidad → lista → tabla → crear/editar → drawer" deja de ser el
modelo mental con el que se diseña una pantalla nueva. El CRUD es adonde se llega cuando ya se
sabe qué hay que hacer, no el sitio por donde se empieza.

## 5. Principios de producto

### 5.1 Context-first
La experiencia se organiza alrededor del **contexto operativo** (un centro, una conversación, un
día de trabajo), no alrededor del CRUD de cada entidad. La pregunta de diseño es "¿qué está
mirando esta persona y qué necesita decidir?", no "¿qué campos tiene esta tabla?".

### 5.2 Workspace-first
Las entidades sobre las que se **opera de verdad** se convierten en espacios de trabajo, no en
filas. Centro 360 es el patrón maestro de esa idea (DDL-005). Qué entidades merecen ese
tratamiento es una decisión de producto abierta (OD-07): no todas lo merecen, y forzarlo donde
no hay operación real es complejidad especulativa.

### 5.3 Automation-first
Las integraciones y los conectores no son funciones que el usuario ejecuta una por una: son
trabajo que el sistema hace y de lo que **rinde cuentas**. Sincronizaciones, portales, correo,
documentos y validaciones deben sentirse como algo que ocurre, con su estado visible, no como
botones que hay que acordarse de pulsar.

Corolario: si Hydra hace algo, tiene que **decir que lo hizo, cuándo y con qué resultado**. Una
automatización silenciosa no genera confianza; genera sospecha.

### 5.4 AI-assisted, no AI-gimmick
La IA es contextual y accionable: propone en el momento y el lugar donde la decisión ocurre,
con su nivel de confianza a la vista. **La IA propone; la persona confirma.** Nunca ejecuta sin
confirmación. "Automáticamente" en la interfaz significa *"con todo preparado, a un clic"*, no
*"sin supervisión"*.

No queremos un chatbot genérico. Una caja de "pregúntame lo que quieras" traslada al usuario el
trabajo de saber qué preguntar; el objetivo es el contrario.

### 5.5 La trazabilidad es una restricción, no una función
Hydra gestiona cumplimiento normativo con consecuencias reales: acceso físico de trabajadores a
centros y responsabilidad legal de las empresas. Por eso hay un límite duro a "ocultar la
complejidad":

> **La automatización puede ahorrar trabajo, pero nunca puede ocultar quién decidió qué.**

Quién aprobó, cuándo, con qué dato y si lo hizo una persona o el sistema debe poder responderse
siempre. Este principio tiene precedencia sobre cualquier consideración de fluidez o de
simplicidad visual, y es la razón por la que la marca de procedencia del sistema es obligatoria
allá donde un dato no lo introdujo un humano (DDL-032).

### 5.6 Densidad alta, carga cognitiva baja
Hydra es densa por naturaleza: cientos de trabajadores, miles de documentos, decenas de centros.
La respuesta correcta a esa densidad no es esconderla en más pantallas, sino **organizarla**:
jerarquía, columnas estables, estado codificado en la forma además del número, y detalle bajo
demanda en vez de detalle permanente.

El aire no es un valor en sí mismo. En superficies operativas, cuántas filas caben por pantalla
es una medida de calidad; en superficies de lectura o vacías, el aire sí lo es.

## 6. Lo que Hydra no debe ser

| No es | Por qué |
|---|---|
| **"Excel, pero bonito"** | Representar la complejidad no es gestionarla. Si el usuario sigue teniendo que cruzar datos a mano, el producto no ha hecho su trabajo. |
| **Una landing futurista** | Los efectos decorativos compiten con la información y erosionan la confianza en una herramienta de cumplimiento. |
| **Un dashboard de métricas de marketing** | El día del gestor no empieza con "engagement": empieza con qué se le vence hoy y a quién no dejan entrar mañana. |
| **Un chatbot con acceso a los datos** | La IA debe observar y proponer antes que conversar. |
| **Un Slack interno** | Introducir la metáfora de canales/hilos/DM añade un modelo mental ajeno al dominio. Las comunicaciones se organizan por conversación y contexto, no por canal. |

## 7. Identidad verbal

Hydra habla como **precisa, calmada, segura y accionable**. Enuncia hechos y ofrece el siguiente
paso.

- ✅ "Cumplimiento actualizado. 42 documentos verificados, 3 requieren revisión."
- ❌ "¡Excelente! Todo está increíble."

Reglas heredadas de `UX_PATTERNS.md` que siguen vigentes y se consolidarán en `04`: todo en
español, tono directo y humano, nunca jerga técnica de cara al usuario; el mensaje explica qué
pasó y, si aplica, qué puede hacer la persona ahora.

Un caso particular que este documento fija: **el sistema no se atribuye certezas que no tiene**.
Si la IA leyó un dato, se dice que lo leyó y con qué confianza; no se presenta como un hecho
verificado.

## 8. Cómo se decide

Ante una propuesta —de una pantalla, una función o un efecto— se responde en este orden:

1. ¿Supera la vara? (*¿más rápido y fiable que Excel más los portales?*)
2. ¿En qué punto del modelo mental encaja (§ 3)? Si no encaja en ninguno, probablemente sobra.
3. ¿Reduce trabajo, lo explica, o lo decora? (§ 2)
4. ¿Respeta la trazabilidad (§ 5.5) y la confirmación humana (§ 5.4)?
5. ¿Existe ya un patrón para esto en `04`/`05`? Si existe, se usa; si se cambia, se cambia el
   documento en el mismo movimiento.
6. YAGNI: ¿hay un consumidor real hoy, o se está construyendo para un caso hipotético?

## 9. Qué gobierna este documento

| Pregunta | Documento |
|---|---|
| Qué debe conseguir la experiencia | **01** (este) |
| Cómo debe sentirse y reconocerse | `02_BRAND_AND_VISUAL_IDENTITY.md` |
| Cómo se organiza la información y la navegación | `03_INFORMATION_ARCHITECTURE.md` |
| Cómo se comporta cada interacción | `04_UX_PATTERNS.md` |
| Cómo se estructura un espacio de trabajo | `05_WORKSPACE_PATTERNS.md` |
| Con qué tokens se construye | `06_DESIGN_SYSTEM.md` |
| Cómo se mueve | `07_MOTION_SYSTEM.md` |
| Qué componentes existen | `08_COMPONENT_CATALOG.md` |
| Cómo se especifica una superficie concreta | `docs/blueprints/` |
| Por qué existe una regla y qué reemplaza | `DESIGN_DECISION_LOG.md` |

## 10. Decisiones que respaldan este documento

| Decisión | Qué aporta |
|---|---|
| DDL-001 | Definición de Premium y su test de revisión (§ 2) |
| DDL-002 | El CRUD como capa profunda (§ 4) |
| DDL-003 | El modelo mental (§ 3) |
| DDL-004 | Los cuatro arquetipos de superficie — se especifican en `03`/`05` |
| DDL-005 | Centro 360 como patrón maestro (§ 5.2) |
| DDL-007 | Action Center como patrón de plataforma (§ 5.4) |
| DDL-008 | Personalidad de marca: Precision · Calm · Trust · Intelligence |
| DDL-032 | La procedencia del sistema se marca siempre (§ 5.5) |

## 11. Alcance ya decidido, pendiente de construir

No quedan preguntas abiertas sobre este documento. Lo que sigue está **decidido** y espera fase
de ejecución:

| Decisión | Qué añade a este documento |
|---|---|
| DDL-042 | **Trabajador** se promueve a Entity Workspace, junto a Centro y Empresa (§ 5.2). El resto de entidades se consultan con Context Panel |
| DDL-046 | El **Operational Home** no se fusiona con la Bandeja: es la superficie de entrada que **consume** esa cola y la presenta priorizada (§ 3) |
| DDL-049 | **Alias de Cliente** para razones sociales largas — solo de presentación; informes, exportaciones y documentos con valor legal muestran siempre la razón social completa (§ 5.5) |
| DDL-050 | **Resumen de ausencia** ("Hydra trabajó mientras no estabas"), sin aviso interruptor por ahora. Completa § 5.3, que hoy no responde *"esto llegó y no lo has visto"*. Requiere un modelo de "visto" por usuario y consume la misma cola que la Bandeja |
| DDL-041 | **Una sola densidad**, compacta, para toda la plataforma (§ 5.6) |

## 12. Preguntas abiertas

Ninguna.

# INNOVACIÓN PARA PRÓXIMOS MVPs — Hydra (CAE Manager)

**Tipo**: Backlog vivo de innovación — insumo para `PRODUCT_STRATEGY.md` y `ROADMAP_BUSINESS.md`. Se actualiza al planificar cada MVP (no es un snapshot: táchese, muévase y re-priorícese aquí mismo).
**Creado**: 2026-08-13, a partir de `MATURITY_REVIEW_2026-08-13.md` y `MACRO_PLAN_2026-08-13.md`.
**Propósito**: que Hydra no tenga nada que envidiar a las mejores plataformas de cada categoría — tomando **el mecanismo** que las hace buenas (no la feature copiada), traducido al dominio CAE. Cada ítem nombra su referente, el porqué en CAE, el corte MVP mínimo honesto, esfuerzo (S/M/L/XL, misma escala del macro plan) y dependencias.
**Regla de entrada**: este backlog es un **menú, no una cola**. Ningún ítem entra a un MVP sin la señal externa del macro plan § 0 (cliente que lo pide, piloto que lo necesita, o demo que lo exige). La columna "Ola" es la hipótesis de secuencia, no un compromiso.

---

## Cómo leer cada ítem

> **[Referente] → mecanismo que se toma prestado → traducción CAE → corte MVP → esfuerzo → dependencias**

Las olas al final (§ H) agrupan la hipótesis de secuencia: **MVP-2** (durante/tras el piloto, meses 1-3), **MVP-3** (meses 3-6, con primer cliente renovado), **MVP-4** (meses 6-12, con ≥3 tenants de pago).

---

## A. Comunicaciones de nivel Zendesk / Front / Intercom

El módulo ya tiene lo difícil (ingesta Graph real, WhatsApp, timeline unificado, macros, matching engine, outbound con rastro). Lo que falta es la capa que convierte una bandeja en una **operación de equipo medible** — exactamente lo que Zendesk/Front venden.

### A1. SLA de conversación con temporizadores visibles — *Zendesk*
- **Mecanismo**: toda conversación tiene un contrato de tiempo (primera respuesta, resolución) y el sistema lo hace visible y ordenable antes de que se incumpla.
- **Traducción CAE**: el SLA no es genérico — se hereda de la urgencia real: días hasta el vencimiento del documento reclamado, ventana de validación de la visita asociada (las Gestiones urgentes de Fase 90 ya calculan esto; falta convertirlo en temporizador de conversación).
- **Corte MVP**: badge de tiempo restante en cada fila de la bandeja + orden por SLA + un umbral configurable por tenant. Sin informes todavía.
- **Esfuerzo**: M · **Dependencias**: ninguna técnica; datos ya existentes.

### A2. Detección de colisión — *Front*
- **Mecanismo**: si dos personas miran/responden la misma conversación, ambas lo ven en tiempo real ("Ana está escribiendo aquí").
- **Traducción CAE**: en cuanto haya 2+ gestores por tenant (el escenario BPO), responder dos veces al mismo reclamado es el error de imagen más frecuente. Blazor Server con circuitos ya tiene el canal de tiempo real gratis — es de las pocas ventajas estructurales de la decisión de render: úsese.
- **Corte MVP**: indicador de presencia ("X está viendo esto") + aviso al pulsar Responder si otro tiene un borrador abierto.
- **Esfuerzo**: M · **Dependencias**: ninguna.

### A3. Posponer conversación (snooze) con despertar inteligente — *Front / Superhuman*
- **Mecanismo**: sacar de la bandeja hasta una fecha o hasta que pase algo.
- **Traducción CAE**: "hasta que responda", "hasta 7 días antes del vencimiento", "hasta que llegue el documento" — los despertadores naturales del dominio ya existen como eventos; el snooze genérico por fecha es la versión pobre, el snooze por evento de dominio es la versión que nadie más tiene.
- **Corte MVP**: posponer por fecha + "hasta que el remitente responda". Los despertares por evento documental, en segunda pasada.
- **Esfuerzo**: M · **Dependencias**: A1 opcional (reordenar al despertar).

### A4. Asignación automática por cartera — *Zendesk*
- **Mecanismo**: routing de conversaciones entrantes al agente correcto sin triaje manual.
- **Traducción CAE**: el matching engine ya vincula conversación→cliente; la cartera gestor→clientes ya existe (`AlcanceDatosService`). Unir ambos: lo que entra de un cliente va a su gestor, con desbordamiento configurable (vacaciones, sobrecarga).
- **Corte MVP**: asignación directa por cartera + reasignación manual visible en timeline (ya existe el evento). Round-robin y balanceo de carga, después.
- **Esfuerzo**: S-M · **Dependencias**: ninguna.

### A5. Agente IA de primer nivel para reclamados — *Intercom Fin*
- **Mecanismo**: la IA resuelve sola las conversaciones repetitivas de bajo riesgo y escala el resto con contexto.
- **Traducción CAE**: el 80% de lo que pregunta un subcontratista reclamado es repetitivo y objetivo: "¿qué documento me pedís?", "¿en qué formato?", "¿hasta cuándo tengo?", "¿me lo validasteis ya?". Todas las respuestas están en datos del sistema. El agente responde SOLO sobre el expediente del propio reclamado (aislamiento natural), nunca decide validaciones, y todo queda en el timeline.
- **Corte MVP**: respuesta automática solo a la pregunta "estado de mi documentación" con la lista de faltantes/validados, firmada como automática, con opt-out. Conversacional completo, después — y con la suite de regresión de prompts del macro plan § 6.6 como prerequisito.
- **Esfuerzo**: L · **Dependencias**: macro plan 6.2 (cadencia de reclamación) y 6.6 (gobernanza IA); DPA IA firmado (H0.4).
- **Nota de honestidad**: este ítem es el "Fin" de Hydra y el más vendible de la sección — y también el más peligroso si se lanza sin la gobernanza. El orden importa más que la prisa.

### A6. Notas internas y @menciones en conversación — *Front*
- **Mecanismo**: hablar del caso dentro del caso, sin que el externo lo vea, con notificación al mencionado.
- **Traducción CAE**: "@Laura este TC2 no me cuadra, ¿lo ves?" — hoy esa conversación pasa por WhatsApp personal y se pierde del expediente. En BPO con auditoría, que el contexto interno viva en el timeline es además argumento de compliance.
- **Corte MVP**: nota interna en el timeline (visualmente inconfundible con un mensaje saliente) + mención que genera notificación in-app.
- **Esfuerzo**: M · **Dependencias**: deep-links (macro plan 2.6) para que la notificación aterrice bien.

### A7. CSAT de cierre — *Zendesk*
- **Mecanismo**: micro-encuesta al resolver, agregada por agente/equipo/periodo.
- **Traducción CAE**: dos audiencias distintas — el cliente (¿el gestor te resolvió?) y el reclamado (¿el proceso fue claro?). La segunda es la innovadora: nadie mide la experiencia del subcontratista, y un proceso de reclamación menos hostil es más tasa de respuesta documental (medible con el rastro de #190).
- **Corte MVP**: un clic (😊/😐/😞) en el correo de cierre de reclamación, agregado en el Dashboard BPO.
- **Esfuerzo**: S-M · **Dependencias**: ninguna.

---

## B. Voz del cliente y salud de cuenta — *Medallia / Qualtrics / Gainsight*

Medallia vende una idea: **la experiencia se mide donde ocurre, y las señales disparan acción, no informes**. Para un SaaS unipersonal, la versión honesta no es un módulo XM — es un sistema de alerta temprana de churn y fricción.

### B1. Señales de experiencia en el momento — *Medallia*
- **Mecanismo**: micro-feedback contextual en el punto de fricción, no encuesta anual.
- **Traducción CAE**: tres puntos de captura de máximo valor: al terminar el onboarding de importación ("¿qué faltó?"), al cerrar la primera reclamación completa, y al usar por primera vez cada módulo (una sola vez, jamás recurrente). Cada respuesta con texto va a una bandeja del operador con contexto (tenant, pantalla, momento).
- **Corte MVP**: los tres puntos anteriores, almacenados y visibles al operador. Nada de dashboards de "sentiment".
- **Esfuerzo**: S-M · **Dependencias**: ninguna.

### B2. Customer Health Score por tenant — *Gainsight*
- **Mecanismo**: una puntuación compuesta por cuenta que predice renovación/churn y ordena la atención proactiva.
- **Traducción CAE**: componer con datos que ya existen — frecuencia de login por rol, % del ciclo documental hecho dentro de Hydra vs. fuera, tendencia del % de cumplimiento de su cartera, adopción de módulos, incidencias abiertas. Con 1-5 tenants es una tabla que el operador mira los lunes; el valor es el hábito, no el algoritmo.
- **Corte MVP**: tabla interna (solo Administrador de plataforma) con 5 componentes explicables y tendencia 30 días. Sin ML, sin colores mágicos: cada score dice de qué se compone.
- **Esfuerzo**: M · **Dependencias**: telemetría de uso definida (macro plan 1.1).

### B3. NPS con cadencia y circuito cerrado — *Medallia*
- **Mecanismo**: NPS trimestral + **cerrar el bucle**: cada detractor recibe seguimiento personal, cada promotor una petición de referencia/testimonio.
- **Traducción CAE**: con pocos tenants, el valor no es el número — es el ritual del bucle cerrado y el banco de testimonios para venta (que hoy es cero).
- **Corte MVP**: envío trimestral por correo (la infra de correo existe), registro de respuesta por tenant, y una tarea manual de seguimiento por respuesta.
- **Esfuerzo**: S · **Dependencias**: ninguna.

### B4. Roadmap visible y portal de peticiones — *Canny / Linear*
- **Mecanismo**: los clientes ven qué se está construyendo, votan, y se enteran cuando su petición sale ("lo pediste tú" en las release notes).
- **Traducción CAE**: convierte la mayor debilidad (proveedor pequeño) en fortaleza percibida (velocidad de respuesta al feedback — 20 fases en 12 días es un superpoder si el cliente lo ve). También disciplina interna: el portal ES la regla de la señal externa hecha producto.
- **Corte MVP**: página pública simple con "En curso / Siguiente / Hecho" mantenida a mano + correo de release notes mensual a los tenants.
- **Esfuerzo**: S · **Dependencias**: ninguna.

---

## C. Cumplimiento como producto vendible — *Vanta / Drata / DocuSign*

Vanta convirtió "estar en regla" de coste a **argumento de venta con página propia**. Hydra custodia datos de salud y su ingeniería de aislamiento es de primera — pero hoy nada de eso es *visible* para un comprador. Esta sección convierte madurez real en material de venta.

### C1. Trust Center público — *Vanta*
- **Mecanismo**: una página que responde el cuestionario de seguridad antes de que lo pregunten: arquitectura de aislamiento, cifrado, subencargados, backups, RGPD, uptime.
- **Traducción CAE**: el material fuente ya existe y es inusualmente bueno (`MULTITENANCY.md`, `RGPD-TRATAMIENTO-DATOS.md`, runbooks, RLS, pentest de H1.3). Es curación, no creación. Para vender a consultoras de PRL —que viven de la conformidad— es probablemente el contenido de marketing de mayor conversión posible.
- **Corte MVP**: página estática pública + PDF descargable del resumen de medidas + lista de subencargados viva (obligación del DPA, doble uso).
- **Esfuerzo**: S-M · **Dependencias**: H0.4 (legal revisado) y H1.3 (pentest, para poder citarlo).

### C2. Firma electrónica integrada — *DocuSign*
- **Mecanismo**: el documento que exige firma se firma dentro del flujo, con evidencia.
- **Traducción CAE**: dos usos de valor muy distinto: (a) el DPA/contrato del tenant en el alta — cierra el circuito legal sin salir del producto; (b) documentos CAE que requieren firma del trabajador (entrega de EPIs, información de riesgos) — hoy se imprimen, firman y escanean. La verificación de firma PDF ya existe en el sistema (`VerificadorFirmaPdfService`); esto es el camino de ida.
- **Corte MVP**: (a) primero con proveedor externo embebido (Signaturit/Docuseal — europeo, eIDAS); (b) cuando un cliente lo pida.
- **Esfuerzo**: M (a) / L (b) · **Dependencias**: H0.4.

### C3. Score de homologación por subcontrata — *el "credit score" CAE*
- **Mecanismo** (sin referente directo — esto es diferenciación pura): cada subcontrata/empresa tiene una puntuación viva de cumplimiento documental — % en regla, velocidad media de respuesta a reclamaciones, historial de caducidades, incidencias — visible para el cliente y comparable.
- **Por qué nadie lo tiene**: las plataformas CAE incumbentes muestran estado binario (apto/no apto); un score con tendencia convierte el dato muerto en decisión de compra ("¿a qué subcontrata llamo para esta obra?").
- **Corte MVP**: score explicable de 3 componentes en la ficha de Empresa/Subcontrata + orden por score en las listas. Compartible fuera, en segunda fase (con consentimiento y letra pequeña — decisión legal previa).
- **Esfuerzo**: M · **Dependencias**: rastro de reclamaciones (existe desde #190).

### C4. Acreditación verificable por QR en obra — *el cierre del bucle físico*
- **Mecanismo** (innovación CAE real): cada trabajador con documentación en regla tiene un QR (en el móvil o impreso) que cualquiera —el jefe de obra del cliente final— escanea y ve verde/rojo **en tiempo real contra Hydra**, sin app ni cuenta.
- **Por qué importa**: el CAE existe para que en la obra solo entre gente en regla, y sin embargo el último metro (la puerta de la obra) se resuelve con listados impresos desactualizados. Verificación pública de solo-lectura, sin datos personales más allá de nombre y estado, con token firmado y caducidad.
- **Corte MVP**: QR por trabajador desde su ficha + página pública de verificación (estado agregado, sin detalle documental). El modo portería con listas y registro de accesos, en ola posterior.
- **Esfuerzo**: M (MVP) · **Dependencias**: revisión RGPD del alcance del dato expuesto (H0.4 lo cubre de paso).

---

## D. Productividad de élite — *Linear / Superhuman* (continúa el macro plan § 7)

### D1. Modo triaje — *Superhuman inbox zero*
- **Mecanismo**: procesar la cola de uno en uno a pantalla completa, con 4 decisiones de teclado (resolver/posponer/delegar/escalar), sin volver a la lista entre elementos.
- **Traducción CAE**: la bandeja priorizada de Fase 88 + los atajos existentes ya son el 70%; falta el flujo "siguiente automático" y las 4 decisiones fijas. Es el modo con el que un gestor BPO despacha la mañana en 40 minutos.
- **Corte MVP**: sobre la bandeja priorizada, tecla para entrar en modo triaje, avance automático al decidir.
- **Esfuerzo**: M · **Dependencias**: A3 (posponer) para que "posponer" sea una de las 4 decisiones.

### D2. Objetivos de cartera semanales — *Linear cycles*
- **Mecanismo**: el trabajo se agrupa en ciclos cortos con objetivo visible y cierre explícito (no un backlog infinito).
- **Traducción CAE**: "esta semana: bajar los vencidos de la cartera X de 34 a 10" — el ciclo se define solo con datos existentes (vencimientos próximos por cartera) y su cierre alimenta el informe BPO al cliente. La gamificación v1 existente encuentra aquí su sustancia: celebrar el cierre de ciclo real, no puntos abstractos.
- **Corte MVP**: objetivo semanal auto-propuesto por cartera + barra de progreso + resumen de cierre.
- **Esfuerzo**: M · **Dependencias**: ninguna.

### D3. Command palette universal — *Linear/Raycast*
- **Mecanismo**: TODO se puede hacer desde ⌘K — no solo navegar y crear: actuar sobre la selección actual ("validar", "reclamar", "asignar a...").
- **Traducción CAE**: el palette con comandos de creación ya existe; extenderlo a acciones contextuales sobre la entidad/selección visible es lo que lo convierte de lanzador en sistema operativo.
- **Corte MVP**: 10 acciones contextuales más frecuentes, medida de uso para decidir las siguientes.
- **Esfuerzo**: M · **Dependencias**: búsqueda con operadores (macro plan § 7.9) es sinérgica pero independiente.

---

## E. Inteligencia sobre los datos — *Gong / HubSpot / Crunchbase*

### E1. Cliente 360 con línea de vida completa — *HubSpot*
- **Mecanismo**: una vista por cuenta donde TODA interacción (correo, WhatsApp, reclamación, visita, incidencia, cambio documental, factura) es una sola línea temporal filtrable.
- **Traducción CAE**: Centro 360 ya demostró el patrón; el timeline unificado de Comunicaciones ya existe. Cliente 360 es la generalización natural y la pantalla que se abre antes de cada llamada — la preparación de reunión de 10 minutos pasa a 30 segundos.
- **Corte MVP**: pestaña "Actividad" en la ficha de Cliente agregando los 4 flujos con más rastro (comunicaciones, reclamaciones, documentos, visitas).
- **Esfuerzo**: M · **Dependencias**: ninguna (todos los eventos existen).

### E2. Benchmarks anónimos entre tenants — *el efecto red de datos*
- **Mecanismo** (Crunchbase/Glassdoor): cada cliente aporta datos y recibe contexto que solo la agregación puede dar.
- **Traducción CAE**: "tu tasa de respuesta a reclamaciones es 62% — la mediana de la plataforma es 71%"; "las subcontratas de electricidad tardan una mediana de 9 días en entregar el certificado X". Con pocos tenants los números son débiles (mostrarlos solo con n suficiente y decirlo) — pero el diseño del agregado anónimo se decide ahora, porque el DPA debe declararlo desde la primera firma (uso de datos agregados/anonimizados: cláusula estándar, gratis hoy, carísima de añadir después).
- **Corte MVP**: la cláusula en el DPA (H0.4) + 2 métricas agregadas cuando haya ≥5 tenants.
- **Esfuerzo**: S (cláusula) + M (métricas) · **Dependencias**: H0.4, y el umbral de n.

### E3. Predicción de carga y riesgo estacional — *forecasting operativo*
- **Mecanismo**: anticipar picos (renovaciones masivas de enero, revisiones médicas anuales) y proponer adelantar trabajo.
- **Traducción CAE**: los vencimientos son conocidos a un año vista — no hace falta ML, hace falta la vista "muro de los próximos 90 días" por cartera con un botón "adelantar la reclamación de todo esto" (enlaza con macro plan 6.2). La versión con IA (estimar probabilidad de respuesta a tiempo por proveedor según historial) llega después con el rastro acumulado.
- **Corte MVP**: vista de carga futura 90 días + acción en lote de adelanto.
- **Esfuerzo**: M · **Dependencias**: macro plan 6.2 para el disparo en lote.

---

## F. Plataforma y ecosistema — *Stripe / Zapier*

### F1. Documentación de API de nivel Stripe — *Stripe*
- **Mecanismo**: la documentación ES el producto para el integrador: ejemplos ejecutables, errores explicados, changelog, entorno de pruebas.
- **Traducción CAE**: cuando la API tenga escrituras (macro plan 3.1), la diferencia entre "tenemos API" y "da gusto integrar con Hydra" es esta capa. Los competidores CAE tienen APIs notoriamente hostiles — es una vara baja de superar con efecto real en decisiones de compra técnica.
- **Corte MVP**: portal generado desde el OpenAPI existente (Scalar/Redoc) + guía "primera integración en 15 minutos" + tenant sandbox.
- **Esfuerzo**: M · **Dependencias**: macro plan 3.1.

### F2. Automatización no-code por tenant — *Zapier interno*
- **Mecanismo**: "cuando pase X, haz Y" configurable por el usuario sin desarrollador.
- **Traducción CAE**: los disparadores ya existen como eventos de dominio (documento vencido/validado, reclamación sin respuesta N días, visita creada) y las acciones también (enviar plantilla, crear gestión, notificar, etiquetar). Un motor de reglas por tenant sobre ese vocabulario cubre el 80% de las peticiones de personalización **sin tocar código por cliente** — la trampa mortal del B2B pequeño es prometer personalización a código; esto es la vacuna.
- **Corte MVP**: 5 disparadores × 4 acciones, editor de lista (no canvas visual), simulación antes de activar, log de ejecuciones.
- **Esfuerzo**: L · **Dependencias**: inventario de eventos estable; webhooks salientes (macro plan 3.1) comparten motor.

### F3. Plantillas de arranque por sector — *Notion templates*
- **Mecanismo**: empezar desde un preconfigurado del sector, no desde cero.
- **Traducción CAE**: catálogos de tipos de documento y exigencias por sector (construcción, industria, logística, limpieza) listos al crear tenant — el conocimiento ya está en los seeders de demo; convertirlo en catálogo elegible reduce el time-to-value del alta de días a minutos y alimenta la demo ("mira, tu sector ya está modelado").
- **Corte MVP**: 2 sectores (construcción + el del piloto) elegibles en el alta de tenant.
- **Esfuerzo**: M · **Dependencias**: ninguna.

---

## G. Innovación CAE sin referente (donde Hydra puede definir la categoría)

### G1. Pasaporte CAE del trabajador — *portabilidad con consentimiento*
- **Idea**: la documentación de un trabajador (formaciones, aptitudes) es SUYA y hoy se re-sube N veces en N plataformas. Un perfil portable con consentimiento del trabajador, que un nuevo empleador/cliente puede recibir con un enlace.
- **Por qué es grande**: efecto red real (cada trabajador portado trae a su siguiente empresa) y alineado con RGPD (portabilidad es un derecho, art. 20 — convertir una obligación en feature).
- **Por qué esperar**: exige masa (≥ decenas de empresas), identidad del trabajador como actor (hoy no tiene login) y diseño legal fino. **Ola MVP-4, pero decidir ahora que ninguna decisión de modelo de datos lo imposibilite** (el trabajador ya es entidad propia con documentos propios — está bien encaminado).
- **Esfuerzo**: XL · **Dependencias**: masa crítica; asesoría legal.

### G2. Grafo de la cadena de subcontratación
- **Idea**: la pirámide real de una obra (cliente → contratas → subcontratas → autónomos) como grafo navegable con el cumplimiento agregado hacia arriba: el semáforo de la obra es el peor semáforo de su cadena.
- **Por qué**: ADR-005 (Subcontratas Supervisadas) ya modela el eslabón; la visualización de la cadena completa es lo que un responsable de PRL del cliente final no tiene en ninguna plataforma incumbente (todas muestran listas planas).
- **Corte MVP**: vista de árbol (no grafo libre) por Centro con agregado de cumplimiento por rama.
- **Esfuerzo**: M-L · **Dependencias**: ADR-005 en uso real.

### G3. Modo obra (móvil, degradado y por QR)
- **Idea**: la verificación en la puerta de la obra (C4) ampliada a modo portería: lista del día, escaneo continuo, registro de accesos — pensado para un móvil con mala cobertura (caché de corta duración firmada).
- **Por qué esperar**: Blazor Server sin conexión es su peor escenario; el corte C4 (QR + página pública) da el 80% del valor sin pelear contra la arquitectura. Solo si el piloto lo valida como decisivo.
- **Esfuerzo**: L-XL · **Dependencias**: C4 desplegado y con uso medido.

### G4. Radar normativo con IA
- **Idea**: vigilancia de cambios normativos (convenios, RD de subcontratación, criterios ITSS) con resumen IA de impacto sobre los tipos de documento del tenant ("el convenio del metal actualizó la formación mínima: 47 trabajadores de 3 clientes quedarían fuera de plazo").
- **Por qué es diferencial**: las incumbentes informan del cambio; cruzarlo automáticamente contra TU censo es la diferencia entre newsletter y producto. Además reutiliza exactamente el músculo IA ya construido (extracción + cruce con censo).
- **Corte MVP**: fuente manual (el operador registra el cambio normativo y qué exige), el cruce contra censo automático. La detección automática de la fuente, después.
- **Esfuerzo**: M (corte) · **Dependencias**: catálogo de exigencias por tipo de documento (existe: `TipoDocumento`/exigencias por centro).

---

## H. Hipótesis de olas (revisar contra el piloto, no ejecutar a ciegas)

### MVP-2 (meses 1-3 — durante el piloto; sesgo: retención del piloto y material de venta)
| Ítem | Por qué ahora | Esfuerzo |
|---|---|---|
| C1 Trust Center | Convierte la madurez ya pagada en conversión de ventas; casi solo curación | S-M |
| A4 Asignación por cartera | El escenario BPO multi-gestor del piloto lo pide solo | S-M |
| A1 SLA visibles | Da al Dashboard BPO su métrica operativa reina | M |
| B1 Señales de experiencia | El piloto es la única fuente de verdad — instrumentarlo antes de que empiece | S-M |
| B4 Roadmap visible | Disciplina interna + percepción de velocidad; coste trivial | S |
| E1 Cliente 360 | La pantalla de preparación de llamada; todo el rastro ya existe | M |
| E2 (solo la cláusula DPA) | Gratis ahora, carísima después | S |

### MVP-3 (meses 3-6 — con primer cliente renovando; sesgo: diferenciación visible)
| Ítem | Por qué | Esfuerzo |
|---|---|---|
| C3 Score de homologación | Primera feature "sin referente" vendible; rastro ya acumulado | M |
| C4 QR de acreditación | El cierre del bucle físico; demo devastadora | M |
| A2 Colisión + A6 Notas internas | El equipo BPO crece; la bandeja pasa de personal a compartida | M+M |
| D1 Modo triaje + A3 Snooze | La mañana del gestor en 40 minutos — retención del usuario diario | M+M |
| A7 CSAT reclamados | Alimenta C3 y mide el efecto de 6.2 (reclamación autónoma) | S-M |
| G4 Radar normativo (corte manual) | Diferenciación IA barata sobre músculo existente | M |
| F3 Plantillas por sector | Acorta el alta del segundo y tercer tenant | M |

### MVP-4 (meses 6-12 — con ≥3 tenants; sesgo: plataforma y efecto red)
| Ítem | Por qué | Esfuerzo |
|---|---|---|
| A5 Agente IA de reclamados | El "Fin" de Hydra — tras la gobernanza IA y con corpus real | L |
| F2 Automatización no-code | La vacuna contra la personalización a código, con ≥3 tenants pidiendo cosas distintas | L |
| F1 Docs API nivel Stripe | Acompaña a la API de escrituras del macro plan 3.1 | M |
| G2 Grafo de subcontratación | Con ADR-005 rodado en datos reales | M-L |
| E2 Benchmarks (métricas) | Con n suficiente para no mentir | M |
| B2 Health score | Con suficientes tenants para que ordene algo | M |
| C2 Firma electrónica (b) | Cuando un cliente pida la firma de EPIs/riesgos | L |
| G1 Pasaporte CAE / G3 Modo obra | Las apuestas de categoría — solo con masa y señal | XL |

---

## Reglas de mantenimiento de este archivo

1. Al planificar cada MVP: mover ítems entre olas con una línea de motivo fechada, no en silencio.
2. Un ítem que dos MVPs seguidos no encuentre señal externa se marca **dormido** (no se borra: el mecanismo referente sigue siendo bueno).
3. Todo ítem que entre en un MVP se lleva su corte mínimo tal cual está escrito aquí — si el corte crece durante la implementación, es una decisión nueva que se registra, no una deriva.
4. Ningún ítem de IA de cara a cliente (A5, G4, y los del macro plan § 6) se activa antes que la gobernanza del macro plan § 6.6 y el DPA de subencargados. Sin excepciones — es la línea que separa "diferenciación" de "incidente".

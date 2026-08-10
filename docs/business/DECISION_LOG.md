# DECISION_LOG — Registro de decisiones de negocio de Hydra

**Tipo**: Estratégico (registro transversal — no es un documento temático como los demás, es el historial de decisiones que los alimenta a todos)
**Estado**: In Progress — primera decisión registrada.
**Propósito**: Ser el equivalente de negocio de los ADR técnicos (`ADR-001-multitenant.md`, `ADR-002-single-tenant.md`, `ADR-003-saas-multitenant.md`): un registro cronológico e inmutable de decisiones de negocio ya tomadas, con su motivo, las alternativas descartadas y su impacto. Cuando dentro de un año alguien dude por qué una decisión comercial se tomó de una manera y no de otra, este documento es la respuesta — igual que los ADR lo son hoy para la arquitectura técnica.

## Qué pertenece aquí

- Cada decisión de negocio, en el momento en que el propietario del producto la confirma — no antes, no como propuesta en discusión.
- Fecha, decisión tomada, motivo, alternativas descartadas, impacto (qué documentos de `docs/business/` u otros quedan afectados o pasan a `Approved`).
- Decisiones sobre modelo de propiedad de datos, modelo de delegación entre consultora y empresa contratista, pricing, estrategia de mercado, o cualquier otra que afecte a más de un documento de esta carpeta.

## Qué NO pertenece aquí

- El desarrollo completo de una decisión: eso vive en el documento temático correspondiente (pricing en `PRICING.md`, propiedad de datos en `DATA_OWNERSHIP.md`, modelo comercial en `BUSINESS_MODEL.md`...). Este registro apunta a esos documentos y resume el "por qué"; no los sustituye ni duplica su contenido.
- Decisiones técnicas o de arquitectura — esas usan los ADR (`ADR-001`, `ADR-002`, `ADR-003`) en la raíz del repositorio.
- Decisiones hipotéticas, en discusión o sin confirmar — solo entran aquí decisiones ya tomadas por el propietario del producto.
- Decisiones de cumplimiento normativo (RGPD/LOPDGDD, DPA, términos de uso) tomadas unilateralmente — esas requieren además revisión legal, regla ya establecida en `CLAUDE.md`; si además son decisiones de negocio, se registran aquí una vez confirmadas por ambas vías.

## Formato de cada entrada

Cada entrada nueva se añade al final del documento (orden cronológico), con esta estructura fija:

```
## AAAA-MM-DD — <Título corto de la decisión>

**Decisión**: qué se decidió, en una o dos frases.
**Motivo**: por qué se decidió así.
**Alternativas descartadas**: qué otras opciones se consideraron y por qué no se eligieron.
**Impacto**: qué documentos de `docs/business/` (u otros) quedan afectados, actualizados o pasan a `Approved`.
**Estado**: Vigente | Revisada por <fecha, entrada que la sustituye> | Descartada
```

Una entrada nunca se edita para cambiar lo que se decidió en su momento — si una decisión posterior la sustituye, se añade una entrada nueva y se marca la anterior como "Revisada por" esa entrada, igual que un ADR superseded se conserva íntegro y no se reescribe.

## Entradas

## 2026-07-25 — Resolución de tres colisiones de nombre en el lenguaje de negocio

**Decisión**: Se resuelven las tres colisiones de nombre detectadas al construir `GLOSSARY.md` (renombrado en el mismo cambio a `UBIQUITOUS_LANGUAGE.md`):
1. El concepto de negocio de "una consultora operando en nombre de una empresa gestionada" se nombra **Delegated Workspace** — nunca "Workspace" a secas.
2. Se descarta "Cliente Final"; la familia de términos pasa a ser **Cliente** (cualquier organización que contrata Hydra) / **Cliente Directo** (gestiona su propia operación) / **Cliente Delegante** (delega en una Consultora).
3. Se descarta "Coordinador CAE" como rol de autorización nuevo; el concepto se resuelve reconociendo dos ejes distintos — **cargo organizativo** de negocio (Draft: Director Consultora, Responsable de Operaciones, Líder de Equipo, Gestor CAE) frente a **rol de autorización** del sistema (ya implementado: Administrador, Supervisor, Ejecutivo CAE, Consulta) — sin renombrar ningún rol ya implementado en código.

**Motivo**: Los tres términos originales colisionaban con conceptos ya existentes en el repositorio (el "Workspace" técnico de `PLAN-CONTEXT-WORKSPACE.md`, el "Cliente" de dominio de `PROJECT.md`, y el rol de autorización "Ejecutivo CAE"). Introducirlos sin resolver la colisión habría reproducido en la documentación de negocio el mismo problema que esta carpeta existe para evitar: dos documentos usando nombres distintos para lo mismo, o el mismo nombre para dos cosas distintas.

**Alternativas descartadas**:
- Para el Workspace de negocio: "Operating Workspace", "Business Workspace", "Organization Workspace", "Client Workspace" — descartadas por ser menos precisas que "Delegated Workspace" para el caso de uso real (una consultora actuando en nombre de un cliente).
- Mantener "Cliente Final" como término aparte de "Cliente" — descartado por riesgo de confusión adicional con "usuario final" sin aportar un matiz que "Cliente Delegante"/"Cliente Directo" no cubran ya.
- Renombrar el rol de autorización implementado "Ejecutivo CAE" a "Gestor CAE" o "Coordinador CAE" — descartado: es un cambio de código (ASP.NET Core Identity), no una decisión de vocabulario de negocio, y no lo pedía la tarea. "Gestor CAE" queda como cargo organizativo de negocio, en un eje distinto del rol de autorización.

**Impacto**: `docs/business/GLOSSARY.md` renombrado a `docs/business/UBIQUITOUS_LANGUAGE.md` (mismo propósito, encuadre DDD). Términos **Cliente**, **Cliente Directo**, **Cliente Delegante** y **Delegated Workspace** pasan a `Approved` en ese documento. Se añade la distinción cargo organizativo / rol de autorización, con una lista `Draft` de cargos pendiente de confirmar en `BUSINESS_ARCHITECTURE.md`. Ningún cambio de código ni de roles ya implementados.

**Estado**: Vigente

## 2026-08-05 — Estado del Documento derivado + Acreditación por plataforma destino (alcance MVP1, manual)

**Decisión**:
1. El estado del Documento en Hydra sigue siendo **derivado** (fecha de emisión + umbrales del tenant, `DATABASE.md`), sin workflow interno de aprobación.
2. La acreditación de cada documento en cada plataforma Inbound destino (Dokify, Nalanda, Twind…) se modela como **entidad satélite separada por documento×plataforma** — un mismo documento puede estar vigente en Hydra, aceptado en Dokify y pendiente en Nalanda a la vez.
3. **Alcance MVP1: registro manual** — el gestor consulta la plataforma y anota el estado una vez por renovación; los rechazos exigen causa tipificada + motivo literal de la plataforma, y su historial sobrevive a las renovaciones. Los conectores de la Fase 2 "Orquestador" (`PRODUCT_STRATEGY.md`) sincronizarán la misma entidad sin cambio de modelo.
4. En el catálogo de proveedores (`ProveedorIntegracion`, diseño de `ARQUITECTURA-INTEGRACIONES.md`), **CTAIMACAE (legacy), Twind y e-coordina son tres proveedores separados** unidos por grupo empresarial; las migraciones de un cliente entre plataformas se registran mediante la acción "Migrar a…" (conservando credenciales cuando solo cambia el enlace) para tener inteligencia interna de qué clientes migran a qué plataformas.

**Motivo**: El trabajo diario Outbound termina en las plataformas de las titulares; sin la visión del estado por plataforma, Hydra no puede ser la única pantalla desde la que el gestor garantiza que todo está al día (problema nº 2 del top-10 de `docs/ux-audit/ROADMAP-UX.md`). La causa tipificada del rechazo separa "falló Hydra" de "el documento vino mal de origen", y el registro de migraciones alimenta la priorización de conectores con datos propios.

**Alternativas descartadas**: workflow interno aprobar/rechazar sobre el Documento (duplicaría la validación que ya hace la titular); campo de estado editable en el Documento (rompería la regla central de estado calculado); crear un catálogo de plataformas nuevo y paralelo (ya existe el diseño `ProveedorIntegracion`); fusionar CTAIMACAE/Twind/e-coordina en una sola entrada (hay empresas operando hoy en solo una de las tres).

**Impacto**: `docs/ux-audit/PLAN-EJECUCION-UX.md` (nuevo — plan de ejecución con el bloque Acreditación (a)-(h) y la semilla de dominios verificada); `docs/ux-audit/ROADMAP-UX.md` (la cadena 1 del Horizonte 2 pasa a alcance MVP1); término **Acreditación** pendiente de alta en `UBIQUITOUS_LANGUAGE.md` al implementar (nunca "Incidencia" para lo documental — colisión registrada en `docs/business/inbound/INBOUND_DOMAIN_GLOSSARY.md`); `ARQUITECTURA-INTEGRACIONES.md` no cambia.

**Estado**: Vigente

## 2026-08-05 — Centro 360: rediseño de Asignaciones/Centros como panel operativo único, retirada de Evaluaciones

**Decisión**:
1. `/asignaciones` deja de ser una página independiente. Se convierte en un acordeón por Centro dentro de `/centros` (contraído por defecto, carga perezosa), con el drawer de alta N×M (matriz + preflight) y la baja en lote conservados como acciones del acordeón, más un export plano de asignaciones activas.
2. Cada Centro muestra si tiene una visita programada sin cambiar de pantalla; el estado de cada documento gana un modificador visual cuando es válido hoy pero caduca dentro de la ventana de la próxima visita del centro (sigue siendo estado derivado, solo cambia la fecha de referencia).
3. La documentación requerida de un Centro pasa a ser configurable en ambos sentidos (incluir tipos adicionales y excluir tipos globalmente obligatorios) — hoy `TipoDocumentoCentro` solo permite restringir, no excluir.
4. **El módulo Evaluaciones se retira.** La puntuación de un centro/trabajador pasa a calcularse automáticamente como % de documentación requerida al día — nunca fue una puntuación manual con sentido de uso real.
5. `CanalGestionDocumental` pasa de 1:1 con el Centro a **N accesos por Centro** con etiqueta de propósito libre (ej. distinguir credenciales de trabajadores extranjeros de la gestión general, aunque compartan URL).

**Motivo**: La operación diaria del Gestor CAE ocurre por Centro, no por lista plana de asignaciones — agrupar ahí reduce el ruido visual reportado con datos reales (298 asignaciones, 48 centros) y responde en una sola pantalla las preguntas que hoy exigen saltar entre `/asignaciones`, `/visitas` y `/centros`. La puntuación manual de Evaluaciones nunca reflejó nada objetivo; sustituirla por un cálculo derivado de documentación requerida es coherente con la regla ya vigente de que lo calculado nunca se edita a mano. La restricción-only de `TipoDocumentoCentro` no cubre el caso real de plataformas Inbound que piden menos documentación de la estándar. Un único canal por Centro no cubre credenciales distintas para el mismo link.

**Alternativas descartadas**: mantener `/asignaciones` como página aparte y solo añadir el badge de visita en `/centros` (no resuelve el ruido visual de la lista plana); conservar Evaluaciones como juicio de campo manual en paralelo al % calculado (dos números que responden la misma pregunta con distinta fuente confunden más de lo que ayudan — si en el futuro hace falta un juicio de campo real, es una decisión nueva y separada); invertir directamente la semántica de `TipoDocumentoCentro` sin tabla de exclusión explícita (se decide en la sesión de implementación, con test que cubra ambos sentidos).

**Impacto**: `docs/ux-audit/PLAN-EJECUCION-UX.md` § Parte 0 "Centro 360" (nueva, prioridad 1 del plan, por delante de los quick wins sueltos); `docs/ux-audit/ROADMAP-UX.md` (nota de repriorización); pendiente al implementar: alta de un badge "vigente con riesgo en ventana de visita" en `DESIGN_SYSTEM.md`/`UX_PATTERNS.md`; baja formal de la ruta/entidad `Evaluacion` (no solo ocultar el menú).

**Estado**: Vigente

## 2026-08-07 — Definición formal de "CAE Outbound"

**Decisión**: Se confirma la definición de **CAE Outbound** (sinónimos: CAE Saliente, CAE Externo) como concepto de negocio de Hydra: la delegación integral de las tareas de gestión, carga, actualización y seguimiento de la documentación preventiva de una empresa hacia las plataformas digitales de sus clientes — la empresa actúa como Contratista/Subcontrata, rol opuesto a **CAE Inbound** (Titular/Principal). Aportada por el propietario del producto con marco legal (Art. 24 Ley 31/1995, RD 171/2004, Art. 19 LPRL), funciones del servicio y beneficios declarados.

**Motivo**: `docs/business/inbound/RESEARCH_BACKLOG.md` dejaba abierta desde su creación la pregunta de qué significa "Outbound" en Hydra, distinto del "Inbound" investigado en esa carpeta (plataformas externas del mercado). `docs/business/inbound/OUTBOUND_USAGE_ANALYSIS.md` ya había usado un sentido operativo de "Outbound" sin definición formal; esta decisión lo formaliza como término de negocio.

**Alternativas descartadas**: ninguna — es la primera definición aportada para este término, sin alternativas en discusión.

**Impacto**: Nuevo documento `docs/business/OUTBOUND_CAE.md` (Approved) con la definición completa. Término **CAE Outbound** pasa a `Approved` en `docs/business/UBIQUITOUS_LANGUAGE.md`. Pregunta abierta 1 de `docs/business/inbound/RESEARCH_BACKLOG.md` queda respondida (referencia añadida). `docs/business/inbound/README.md` actualizado con nota de desambiguación entre este uso de "Outbound"/"Inbound" (rol Titular vs. Contratista) y el uso de "Inbound" de esa carpeta (plataformas externas a consumir/sincronizar).

**Estado**: Vigente

## 2026-08-08 — Confirmación del término "Acreditación" (Draft → Approved)

**Decisión**: Se confirma **Acreditación** como término de negocio Approved: el estado de un Documento frente a un acceso de plataforma Inbound destino concreto (Dokify, Nalanda, Twind…) — se refiere a la subida de los documentos Outbound de Hydra a las plataformas Inbound de las titulares, mediante las integraciones existentes o futuras (Dokify, etc.). Un mismo documento puede estar vigente en Hydra, aceptado en una plataforma y pendiente en otra a la vez.

**Motivo**: El término se dio de alta como `Draft` en `UBIQUITOUS_LANGUAGE.md` el 2026-08-08 al construir la entidad `AcreditacionDocumentoPlataforma` (Lote 2-C, `PLAN-EJECUCION-UX.md` § Parte 2 (b)), porque el propio término no tenía todavía la confirmación explícita de negocio que exige "Reglas de este documento" — solo el bloque que lo usa estaba autorizado (entrada del 2026-08-05 de este mismo registro). El propietario confirma en conversación el mismo alcance con el que se implementó.

**Alternativas descartadas**: ninguna — es una confirmación del término tal como ya estaba definido, no una redefinición.

**Impacto**: Término **Acreditación** pasa a `Approved` en `docs/business/UBIQUITOUS_LANGUAGE.md`.

**Estado**: Vigente

## 2026-08-10 — Aprobación de ADR-005: nivel de servicio de Subcontrata y supervisión externa

**Decisión**: El propietario del producto aprueba explícitamente y en su totalidad `ADR-005-subcontratas-supervisadas.md` (que pasa a Aceptado): (1) la entidad existente `Subcontrata` gana un **nivel de servicio** — `Gestionada` (semántica actual: se sube y valida su documentación) / `Supervisada` (solo se audita su cumplimiento en las plataformas Inbound del titular, sin cargar nada) — cambiable como operación de negocio sin migración ni cambio de entidad; (2) la supervisión se registra mediante **Verificaciones Externas** manuales (fecha, resultado, válido-hasta, evidencia, verificador) contra la lista de tipos exigidos por cada Centro (`TipoDocumentoCentro`), con estado calculado y nunca almacenado; (3) la Subcontrata vive en el tenant del Cliente Directo/Delegante que la contrata — si algún día contrata Hydra, tenant propio + `DelegacionTenant`, sin migrar historial.

Además, el propietario deja posicionamiento (dirección, no diseño cerrado) sobre la futura bandeja unificada del gestor: la agregación multi-cliente se hará como proyección transversal de lectura sobre tenants delegados autorizados (patrón Visión de cartera, aislamiento ADR-004 intacto); el canal (Email/WhatsApp) evolucionará a atributo del mensaje, no de la conversación; y el selector "Responder como" se especificará solo tras validar `SendAs`/`SendOnBehalf`/buzones compartidos contra Microsoft Graph.

**Motivo**: Dos escenarios de negocio reales (Cliente Directo con subcontratas auditadas; Consultora BPO que reporta cumplimiento de subcontratas de sus Clientes Delegantes) necesitan distinguir "gestiono sus documentos" de "solo vigilo su cumplimiento externo" — hoy una subcontrata no gestionada aparece indistinguible de una gestionada con todo caducado. El registro manual aporta valor desde el MVP sin esperar a los conectores de la Plataforma de Integraciones, que alimentarán el mismo agregado.

**Alternativas descartadas**: entidad nueva separada para subcontratas supervisadas (rompería el paso Supervisada→Gestionada sin migración, decidido como cambio de servicio, no de entidad); nivel de servicio por relación Subcontrata–Cliente (YAGNI — se revisará si un caso real lo exige); vínculo N:M explícito Subcontrata–Centro (el despliegue ya se deriva de las Asignaciones de sus trabajadores; para supervisar basta el `CentroId` de cada verificación); segundo catálogo de requisitos documentales (se reutiliza `TipoDocumentoCentro`).

**Impacto**: `ADR-005-subcontratas-supervisadas.md` pasa a **Aceptado**. Término **Nivel de servicio de Subcontrata** (Gestionada/Supervisada) pasa a `Approved` en `docs/business/UBIQUITOUS_LANGUAGE.md`. **Métricas BPO** permanece `Draft` (su fasado quedó registrado, pero los KPIs y el diseño no están aprobados). Arranca la implementación: migración de esquema, commands/queries, siembra de datos de prueba (requerimiento global nº 1) y capa de presentación.

**Estado**: Vigente

## 2026-08-10 — CIF/NIF obligatorio en Empresas y Subcontratas (ancla de identidad entre tenants)

**Decisión**: El CIF/NIF pasa a ser **obligatorio** en las entidades `Empresa` y `Subcontrata`. Es el ancla de identidad que permitirá reconocer a la misma empresa/subcontrata a través de tenants independientes (sugerencias de reutilización en la vista transversal de cartera, indicadores agregados para dirección, y el futuro enlace de compartición cuando una subcontrata se convierta en Cliente de Hydra — dirección registrada en `ADR-005-subcontratas-supervisadas.md` § 2.4). Cada tenant conserva su propia fila (el aislamiento de ADR-004 no cambia); el CIF solo ancla el *matching*, nunca comparte datos por sí mismo.

**Motivo**: Sin identificador fiscal fiable, el matching entre tenants es imposible o inventa coincidencias por razón social. El propietario del producto confirma que los dos escenarios reales que motivan ADR-005 (subcontrata que sirve a varios clientes de la consultora; subcontrata que acaba contratando Hydra) dependen de esta ancla.

**Alternativas descartadas**: mantener el CIF opcional y hacer matching solo cuando esté informado (deja el efecto red a merced de la calidad de datos); usar razón social como ancla (ambigua y mutable); un identificador interno de plataforma (no existe hasta que ambas partes están en Hydra — el CIF ya existe antes).

**Impacto**: Supersede la opcionalidad del CIF decidida en Issue #5 (`Subcontrata.Cif`/`Empresa.Cif` opcionales). Implementación como cambio propio, separado de la fase de Subcontratas Supervisadas: obligatorio en dominio para alta y edición (una fila antigua sin CIF exige informarlo al editarla), columna aún anulable para filas históricas hasta sanearlas, plantillas de importación actualizadas para exigir el campo, y siembra de datos de prueba con CIF siempre informado. La unicidad por `(TenantId, Cif)` se evalúa en esa implementación.

**Estado**: Vigente

## 2026-08-10 — Cierre de las dos cuestiones abiertas de `ADR-004`: autoservicio de delegación y no-hibridación de tenants

**Decisión**:
1. **Autoservicio de delegación** (cierra `ADR-004` § 12, punto 2). La **vinculación** entre un Cliente Delegante y una Consultora que ya existen como tenants es un proceso de autoservicio descentralizado entre ambas partes, **sin intervención del Administrador de plataforma**. Dos flujos simétricos: el Cliente Delegante emite una invitación de delegación (con alcance y vigencia) que la Consultora acepta; o la Consultora envía una solicitud de vinculación que el Cliente Delegante aprueba. **Autoridad única**: solo un usuario con rol `Administrador` en el tenant del **Cliente Delegante** aprueba, modifica o revoca la `DelegacionTenant`; la revocación es unilateral e inmediata y no toca los datos de origen. El alta de un Cliente Delegante que **todavía no existe** en Hydra sigue siendo de la plataforma (`CrearClienteDeleganteCommand` crea el tenant, no solo el vínculo).
2. **No existen tenants híbridos** (cierra `ADR-004` § 12, punto 6, y su reserva sobre un futuro tenant #1 con datos reales). Un tenant es Cliente Directo/Delegante **o** Consultora, nunca ambos. Una organización con los dos papeles se modela como dos tenants unidos por una `DelegacionTenant`; un tenant que hoy acumulase ambos se escinde mediante migración de datos puntual. **No se admiten excepciones por tenant en el código** (`if (tenantId == 1)`, banderas de "híbrido legacy") en dominio, filtros ni autorización.

**Motivo**: (1) El Administrador de plataforma es un rol de infraestructura SaaS, no un actor del negocio CAE — exigir su intervención en cada vinculación crea un cuello de botella comercial y contradice el principio ya cerrado de que Hydra aplica autorizaciones pero no las arbitra (`ADR-004` § 11.3). La autoridad exclusiva del Cliente Delegante refleja que el dueño de los datos es el único con potestad para delegar su gestión. (2) Las excepciones por tenant en el motor ensucian la capa de dominio, debilitan la frontera de aislamiento (filtro global de `TenantId` + interceptor de sellado + RLS de PostgreSQL por tabla) y son deuda técnica permanente; formalizan como invariante lo que `ADR-004` § 5.1 ya elegía (la Consultora es un tenant sin datos operativos propios).

**Alternativas descartadas**:
- Mantener el alta solo-plataforma como modelo definitivo (era la v1 mínima del hallazgo P0-7, aceptada como provisional) — descartada por el cuello de botella operativo.
- Permitir que la Consultora se autovincule a un cliente sin aprobación de éste — descartada: rompe la soberanía del dueño de los datos.
- Soportar tenants híbridos en el motor con banderas o excepciones por Id — descartada por deuda técnica y riesgo sobre el aislamiento.

**Impacto**: `ADR-004-delegacion-consultoras-cae.md` — puntos 2 y 6 de § 12 marcados como cerrados; nueva regla cerrada de producto § 11.5 ("No existen tenants híbridos"). **Nada de esto está implementado**: el flujo de invitación/solicitud, su notificación y la pantalla de aprobación son trabajo nuevo sobre `/delegaciones`, y hasta entonces el alta sigue siendo solo-plataforma. Las implicaciones comerciales del autoservicio (¿venta autoservicio o gestionada?) se coordinan con `docs/business/BUSINESS_ARCHITECTURE.md` al implementarlo. No afecta a `ADR-004` § 13/§ 14 (hipótesis pendientes de revisión legal, con quién se firma el DPA), que siguen abiertas.

**Estado**: Vigente

## Documentos relacionados

- Todos los documentos de `docs/business/` — cualquiera puede generar una entrada aquí cuando su contenido pasa de `Draft`/`In Progress` a `Approved`.
- `ADR-001-multitenant.md`, `ADR-002-single-tenant.md`, `ADR-003-saas-multitenant.md` — el equivalente técnico de este registro, mismo espíritu (cronológico, inmutable, con alternativas descartadas).

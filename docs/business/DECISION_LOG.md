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

## Documentos relacionados

- Todos los documentos de `docs/business/` — cualquiera puede generar una entrada aquí cuando su contenido pasa de `Draft`/`In Progress` a `Approved`.
- `ADR-001-multitenant.md`, `ADR-002-single-tenant.md`, `ADR-003-saas-multitenant.md` — el equivalente técnico de este registro, mismo espíritu (cronológico, inmutable, con alternativas descartadas).

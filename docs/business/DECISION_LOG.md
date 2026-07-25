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

## Documentos relacionados

- Todos los documentos de `docs/business/` — cualquiera puede generar una entrada aquí cuando su contenido pasa de `Draft`/`In Progress` a `Approved`.
- `ADR-001-multitenant.md`, `ADR-002-single-tenant.md`, `ADR-003-saas-multitenant.md` — el equivalente técnico de este registro, mismo espíritu (cronológico, inmutable, con alternativas descartadas).

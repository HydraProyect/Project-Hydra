# DECISION_LOG — Registro de decisiones de negocio de Hydra

**Tipo**: Estratégico (registro transversal — no es un documento temático como los demás, es el historial de decisiones que los alimenta a todos)
**Estado**: Draft — estructura creada, sin decisiones registradas todavía.
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

_Sin decisiones registradas todavía. La primera entrada se añade cuando el propietario del producto confirme la primera decisión formal de negocio._

## Documentos relacionados

- Todos los documentos de `docs/business/` — cualquiera puede generar una entrada aquí cuando su contenido pasa de `Draft`/`In Progress` a `Approved`.
- `ADR-001-multitenant.md`, `ADR-002-single-tenant.md`, `ADR-003-saas-multitenant.md` — el equivalente técnico de este registro, mismo espíritu (cronológico, inmutable, con alternativas descartadas).

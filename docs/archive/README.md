# docs/archive — informes históricos

> ## ESTADO: HISTÓRICO · NO NORMATIVO
>
> **Todo el contenido de esta carpeta es registro histórico y no constituye autoridad sobre el
> sistema vigente.** Se conserva por trazabilidad: muestra qué se pensó, qué problemas
> aparecieron y por qué una decisión cambió.
>
> **Un documento de esta carpeta no puede utilizarse como fuente para una decisión de diseño o
> de implementación.** Si una regla necesaria no está en la cadena normativa vigente, no se
> recupera de aquí: se registra una decisión nueva. Ver `docs/README.md` § 3.

Snapshots fechados que ya cumplieron su propósito — no son fuente de verdad de nada vigente y no se reescriben (mismo criterio que el `MATURITY_REVIEW.md`/`DECISION_LOG.md` del repositorio local de negocio, ver `CLAUDE.md`: un informe nuevo se añade aparte, no se edita uno viejo para que "siga estando al día"). Si buscas el estado actual de algo, ve al documento normativo correspondiente (`docs/MULTITENANCY.md`, `ARCHITECTURE.md`, `DATABASE.md`, los ADR en la raíz...), no aquí.

| Documento | Qué es | Por qué es histórico |
|---|---|---|
| `RUNBOOK-MIGRACION-POSTGRESQL.md` | Procedimiento del corte de SQLite a PostgreSQL. | El propio documento se declara histórico: el corte ya se ejecutó (2026-08-01) y la rama SQLite se retiró del código. |

**Auditorías técnicas y hallazgos de aislamiento multi-tenant (2026-08-13, ya no aquí)**: `INFORME-AUDITORIA-TECNICA.md`, `INFORME-AUDITORIA-2.md`, `FIX-LOG.md` e `INFORME-MULTITENANT.md` salieron del repositorio público — contienen exploits reproducibles y detalle de explotación para hallazgos que, a esa fecha, no estaban todos cerrados (`FIX-LOG.md` mismo lista qué seguía abierto). Ver `CLAUDE.md` § "Qué no entra en este repositorio". Viven ahora en `seguridad/` del repositorio local de negocio (`C:\Users\chris\Project-Hydra-Negocio`), con su historial preservado — consúltalos desde ahí, nunca los reconstruyas aquí a partir de memoria.

Para el informe de madurez del comité de revisión (2026-08-01), ver `MATURITY_REVIEW.md` en el repositorio local de negocio — mismo criterio de "snapshot que no se reescribe", ya no vive en este repositorio (ver `CLAUDE.md`).

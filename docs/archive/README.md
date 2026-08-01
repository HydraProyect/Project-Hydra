# docs/archive — informes históricos

Snapshots fechados que ya cumplieron su propósito — no son fuente de verdad de nada vigente y no se reescriben (mismo criterio que `docs/business/MATURITY_REVIEW.md` y `DECISION_LOG.md`: un informe nuevo se añade aparte, no se edita uno viejo para que "siga estando al día"). Si buscas el estado actual de algo, ve al documento normativo correspondiente (`docs/MULTITENANCY.md`, `ARCHITECTURE.md`, `DATABASE.md`, los ADR en la raíz...), no aquí.

| Documento | Qué es | Por qué es histórico |
|---|---|---|
| `INFORME-AUDITORIA-TECNICA.md` | Auditoría técnica estática, primera ronda (2026-07-30). | Todos sus hallazgos están cerrados — ver `FIX-LOG.md` y la segunda ronda. |
| `INFORME-AUDITORIA-2.md` | Auditoría técnica, segunda ronda — verifica los arreglos de la primera. | Igual: hallazgos cerrados, snapshot de esa fecha. |
| `FIX-LOG.md` | Registro de los hallazgos de aislamiento multi-tenant cerrados tras la primera auditoría. | Los seis hallazgos que enumera están todos "✅ cerrado". |
| `INFORME-MULTITENANT.md` | Análisis y propuesta original de la arquitectura multi-tenant (Fases 1-2, antes de `ADR-003`). | Superseded por `docs/MULTITENANCY.md` (normativo) y `ADR-003-saas-multitenant.md` (decisión vigente). |
| `RUNBOOK-MIGRACION-POSTGRESQL.md` | Procedimiento del corte de SQLite a PostgreSQL. | El propio documento se declara histórico: el corte ya se ejecutó (2026-08-01) y la rama SQLite se retiró del código. |

Para el informe de madurez del comité de revisión (2026-08-01), ver `docs/business/MATURITY_REVIEW.md` — vive en `docs/business/` por ser un informe de negocio, no de arquitectura, pero sigue el mismo criterio de "snapshot que no se reescribe".

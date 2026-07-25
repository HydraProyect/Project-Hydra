# ROADMAP_BUSINESS — Roadmap comercial de Hydra

**Tipo**: Operativo
**Estado**: Placeholder — sin contenido desarrollado todavía.
**Propósito**: Calendario de hitos comerciales de Hydra (lanzamiento comercial, primeros tenants de pago, apertura de canal partner, expansión de segmento) — distinto de `ROADMAP.md`, que ordena las fases de **construcción técnica** del producto por dependencia de dominio.

## Qué pertenece aquí

- Hitos comerciales con fecha objetivo y sus dependencias entre sí (ej. "apertura de canal partner con consultoras" depende de tener referencias de al menos un tenant de consultora en producción).
- Relación explícita con las condiciones técnicas bloqueantes ya fijadas en `ADR-003-saas-multitenant.md` § "Condiciones de salida a producción SaaS" (aislamiento auditado, migración a PostgreSQL, DPA/Términos de Uso, sin self-signup ni billing antes de eso) — este documento las hereda como dependencia de calendario, no las redefine ni las adelanta.
- Secuencia de apertura de segmentos de `ICP.md` (ej. contratistas directas primero, consultoras después, o al revés).
- Hitos de `GO_TO_MARKET.md` y `PRODUCT_STRATEGY.md` situados en el tiempo.

## Qué NO pertenece aquí

- Fases de construcción técnica del producto (Fase 0, Fase 1, Fase 2...) → `ROADMAP.md`.
- Las condiciones técnicas bloqueantes en sí (viven en `ADR-003-saas-multitenant.md`; este documento solo las referencia como dependencia de fecha).
- La estrategia en sí (canales, mensajes) → `GO_TO_MARKET.md`.

## Documentos relacionados

- `ROADMAP.md` — roadmap técnico de construcción del producto, con el que este roadmap comercial debe mantenerse coherente en secuencia (no se anuncia ni se vende lo que el roadmap técnico no ha entregado).
- `ADR-003-saas-multitenant.md` § "Condiciones de salida a producción SaaS" — bloqueantes técnicos heredados como dependencia.
- `GO_TO_MARKET.md`, `PRODUCT_STRATEGY.md` — estrategias que este calendario secuencia.

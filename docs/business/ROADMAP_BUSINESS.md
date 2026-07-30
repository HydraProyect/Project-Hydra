# ROADMAP_BUSINESS — Roadmap comercial de Hydra

**Tipo**: Operativo
**Estado**: Draft — sin contenido desarrollado todavía.
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

## Primeros hitos comerciales con fecha (Draft, 2026-07)

- **Julio 2026**: presentación de propuesta formal de Cliente Fundador a GESEME (reunión con
  Directora de CAE, responsable de aprobación de presupuesto; escalado informativo a Dirección
  de Operaciones).
- **+5-7 días laborables tras el envío**: seguimiento si no hay respuesta.
- **Estimado 2-4 semanas tras la reunión**: cierre contractual formal, condicionado a la
  constitución de la estructura societaria/autónomo del proveedor.
- **Día 90 desde la puesta en marcha**: checkpoint de la garantía de implantación pactada en
  `PRICING.md` (gestión del ciclo documental mensual completo de al menos 50 clientes).

*Draft — calendario de la primera venta, no un roadmap comercial completo del negocio.*

## Documentos relacionados

- `ROADMAP.md` — roadmap técnico de construcción del producto, con el que este roadmap comercial debe mantenerse coherente en secuencia (no se anuncia ni se vende lo que el roadmap técnico no ha entregado).
- `ADR-003-saas-multitenant.md` § "Condiciones de salida a producción SaaS" — bloqueantes técnicos heredados como dependencia.
- `GO_TO_MARKET.md`, `PRODUCT_STRATEGY.md` — estrategias que este calendario secuencia.

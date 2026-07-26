# UNIT_ECONOMICS — Economía unitaria de Hydra

**Tipo**: Operativo
**Estado**: Draft — sin contenido desarrollado todavía.
**Propósito**: Medir si el modelo de negocio y las tarifas de `PRICING.md` son sostenibles: coste de adquisición de cliente, valor de vida del cliente, abandono y coste de servir cada tenant.

## Qué pertenece aquí

- CAC (coste de adquisición de cliente) por canal.
- LTV (valor de vida del cliente) y su relación con el CAC.
- Tasa de abandono (churn) esperada u observada, por segmento (consultora vs. contratista directa).
- Coste de servir un tenant: infraestructura (hosting, almacenamiento, PostgreSQL en producción, ver condiciones de `ADR-003`), soporte, servicios profesionales prestados.
- Margen bruto por plan y por segmento de cliente.
- Supuestos de crecimiento usados para proyectar estas métricas.

## Qué NO pertenece aquí

- Las tarifas en sí (entrada del cálculo, no el resultado) → `PRICING.md`.
- Estrategia de captación de clientes → `GO_TO_MARKET.md`.
- Costes de infraestructura técnica en detalle de implementación → documentación técnica (`ARCHITECTURE.md`, `DEPLOY.md`).

## Documentos relacionados

- `PRICING.md` — tarifas sobre las que se calculan estas métricas.
- `BUSINESS_MODEL.md` — modelo de negocio que estas métricas validan o cuestionan.
- `GO_TO_MARKET.md` — estrategia de adquisición que determina el CAC.

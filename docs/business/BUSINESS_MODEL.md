# BUSINESS_MODEL — Modelo de negocio de Hydra

**Tipo**: Estratégico
**Estado**: Draft — sin contenido desarrollado todavía.
**Propósito**: Definir cómo Hydra genera ingresos — qué se vende, a quién y bajo qué lógica comercial general — como base de la que dependen `PRICING.md`, `UNIT_ECONOMICS.md` y el resto de documentos operativos de esta carpeta.

## Qué pertenece aquí

- El o los modelos de ingresos considerados (suscripción por tenant, por usuario, por módulo, transaccional, híbrido...).
- Qué compra exactamente el cliente: producto core (gestión CAE), módulos adicionales, integraciones, servicios profesionales.
- Cómo se traduce comercialmente la existencia de dos perfiles de comprador — consultora de PRL que gestiona la CAE de varias empresas contratistas, y empresa contratista que gestiona la suya propia (ya identificados técnicamente en `docs/MULTITENANCY.md` § 2 como "Escenarios de negocio", y en `PROJECT.md` § "A quién sirve").
- Lógica de expansión de cuenta (upsell, cross-sell, crecimiento dentro de un tenant existente).
- Si la futura Plataforma de Integraciones (`ARQUITECTURA-INTEGRACIONES.md`) se monetiza de forma independiente (marketplace, comisión por conector) — a nivel de modelo, no de arquitectura.

## Qué NO pertenece aquí

- Tarifas y planes concretos → `PRICING.md`.
- Estructura organizativa, canales de venta y segmentación comercial → `BUSINESS_ARCHITECTURE.md`.
- Métricas de rentabilidad (CAC, LTV, churn) → `UNIT_ECONOMICS.md`.
- El modelo técnico de aislamiento multi-tenant que hace posible vender a distintos tenants → `docs/MULTITENANCY.md`.

## Documentos relacionados

- `ICP.md` — a quién se dirige este modelo de negocio.
- `PRICING.md` — implementación concreta del modelo.
- `BUSINESS_ARCHITECTURE.md` — cómo se organiza comercialmente.
- `docs/MULTITENANCY.md` § 2 — escenarios de negocio ya nombrados desde el ángulo técnico de aislamiento.
- `PROJECT.md` § "A quién sirve" — visión de producto de los dos perfiles de comprador.

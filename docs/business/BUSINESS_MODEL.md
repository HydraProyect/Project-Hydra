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

## Hipótesis de segmento de entrada y métrica de valor (Draft, 2026-07)

- **Segmento de entrada validado con tracción real**: SPA/Consultora de PRL (no contratista
  directa), con un Cliente Fundador real (GESEME) en proceso de contratación. Rompe la hipótesis
  de secuencia de segmentos anterior, que estaba sin datos — se confirma "consultoras primero".
- **Métrica de valor candidata para el pricing**: trabajadores activos monitorizados y/o
  clientes gestionados, en sustitución de una tarifa plana sin límite superior (descartada
  explícitamente por riesgo de coste no acotado frente al crecimiento del cliente).
- **Hipótesis de reencuadre competitivo**: las plataformas Inbound (CTAIMA, Nalanda, Dokify,
  6conecta, Twind...) no son solo comparables de integración futura — son simultáneamente
  clientes potenciales de integración, competidores potenciales de producto propio, y
  adversarios técnicos frente a cualquier automatización de subida (Fase 2 de
  `PRODUCT_STRATEGY.md`). Relación de coopetición, no solo de integración técnica.

*Draft — pendiente de desarrollo completo y confirmación explícita antes de Approved.*

## Nota de escenarios de salida (hipótesis fechada 2026-08-02)

Insumo de `BENCHMARK_PRECIOS_CAE.md` § 1-bis. La adquisición de Hydra por un grupo consolidador del sector (Once For All ya adquirió Nalanda y Dokify) es un escenario de salida plausible **por consecuencia** de ganar el segmento consultora de forma independiente — explícitamente descartado como *driver* de diseño de producto: diseñar para ese comprador arriesga un comprador único, distorsiona el roadmap, y el escenario simétrico más probable frente al éxito de Hydra es la copia por un incumbente, no la compra.

## Precedente de monetización (Draft, 2026-08-02)

El mercado ya acepta pagar por la relación cliente↔documentación ("visibilidad", ver `BENCHMARK_PRECIOS_CAE.md` § 1 y § 6) como unidad de cobro independiente de la licencia base. Relevante para elegir la unidad de facturación del modelo Hydra en la banda consultora — desarrollo concreto en `PRICING.md`, aquí solo se registra el precedente de mercado.

## Documentos relacionados

- `ICP.md` — a quién se dirige este modelo de negocio.
- `PRICING.md` — implementación concreta del modelo.
- `BUSINESS_ARCHITECTURE.md` — cómo se organiza comercialmente.
- `docs/MULTITENANCY.md` § 2 — escenarios de negocio ya nombrados desde el ángulo técnico de aislamiento.
- `PROJECT.md` § "A quién sirve" — visión de producto de los dos perfiles de comprador.
- `BENCHMARK_PRECIOS_CAE.md` — fuente del precedente de monetización y del escenario de salida de arriba.

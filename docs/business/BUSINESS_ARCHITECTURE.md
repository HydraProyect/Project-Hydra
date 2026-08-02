# BUSINESS_ARCHITECTURE — Arquitectura comercial de Hydra

**Tipo**: Estratégico
**Estado**: Draft — sin contenido desarrollado todavía.
**Propósito**: Describir cómo se organiza comercialmente el negocio — segmentos de cliente, canales de venta y relación entre los distintos tipos de comprador — igual que `ARCHITECTURE.md` describe cómo se organiza el sistema técnico. Es la contraparte de negocio de ese documento, no una versión resumida.

## Qué pertenece aquí

- Segmentación de mercado y cómo se relacionan comercialmente los segmentos entre sí — en particular, consultora de PRL actuando como gestor/revendedor frente a empresa contratista como cliente directo (ver los "Escenarios de negocio" ya nombrados en `docs/MULTITENANCY.md` § 2, aquí desde el ángulo comercial, no de aislamiento de datos).
- Canales de venta: directo, partners, canal indirecto a través de consultoras.
- Estructura de cuentas desde el ángulo comercial: qué significa comercialmente que un tenant sea una consultora con varias empresas gestionadas dentro, frente a un tenant de una sola empresa.
- Cómo encaja la futura Plataforma de Integraciones (`ARQUITECTURA-INTEGRACIONES.md`) como oportunidad de canal o de marketplace, sin entrar en su diseño técnico.

## Qué NO pertenece aquí

- Arquitectura técnica del sistema (capas, patrones, stack) → `ARCHITECTURE.md`.
- Modelo de aislamiento multi-tenant → `docs/MULTITENANCY.md`.
- Tarifas concretas → `PRICING.md`.
- Por qué genera ingresos cada segmento (eso es `BUSINESS_MODEL.md`; este documento asume el modelo de negocio como dado y describe cómo se organiza).

## Documentos relacionados

- `BUSINESS_MODEL.md` — el modelo de ingresos que esta arquitectura comercial soporta.
- `docs/MULTITENANCY.md` § 2 — escenarios de negocio desde el ángulo técnico de aislamiento.
- `ARQUITECTURA-INTEGRACIONES.md` — diseño técnico de la Plataforma de Integraciones, backlog, no implementado.
- `ADR-004-delegacion-consultoras-cae.md` — diseño técnico completo del **Delegated Workspace** (modelo de dominio, resolución de sesión, reporting transversal entre tenants, jerarquía Director Consultora→Coordinador→Gestor). Implementado (incluida el alta de delegaciones, `ADR-004` § 12.2, P0-7). Cuando este documento se desarrolle, la relación comercial Consultora↔Cliente Delegante (§ "Qué pertenece aquí") debe ser consistente con lo ya diseñado ahí — en particular, quién más allá del Administrador de plataforma puede autorizar/revocar una delegación (`ADR-004` § 12.2, todavía abierto para autoservicio) es tanto una decisión comercial como técnica.

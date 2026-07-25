# PRICING — Planes y tarifas de Hydra

**Tipo**: Operativo
**Estado**: Placeholder — sin contenido desarrollado todavía. No hay planes ni precios confirmados.
**Propósito**: Documentar los planes comerciales concretos de Hydra y sus tarifas — la traducción operativa de `BUSINESS_MODEL.md` en algo que un cliente puede comprar. Es la fuente oficial para cualquier límite de plan que la plataforma técnica necesite consultar (ver el esbozo de `Licensing` en `docs/PLATFORM.md` § 4).

## Qué pertenece aquí

- Nombres y niveles de plan (el esbozo técnico de `docs/PLATFORM.md` § 4 menciona, sin decidir, Starter/Professional/Enterprise).
- Tarifas por plan, periodicidad de facturación (mensual/anual), moneda.
- Qué capacidades y límites incluye cada plan (nº de usuarios, nº de Centros, nº de conexiones de integración activas, módulos habilitados) — la cara de negocio de los `FeatureFlag`/`TenantFeatureFlag` esbozados en `docs/PLATFORM.md` § 4.
- Política de descuentos, upgrades/downgrades de plan, periodo de prueba.

## Qué NO pertenece aquí

- Por qué existe un modelo de suscripción por planes → `BUSINESS_MODEL.md`.
- La implementación técnica de licencias y feature flags (`FeatureFlag`, `TenantFeatureFlag`, `IFeatureFlagService`) → `docs/PLATFORM.md` § 4.
- Si los precios resultantes son rentables → `UNIT_ECONOMICS.md`.
- Servicios vendidos aparte de la licencia (onboarding, migración, soporte premium) → `PROFESSIONAL_SERVICES.md`.

## Documentos relacionados

- `BUSINESS_MODEL.md` — modelo de negocio del que este documento deriva.
- `UNIT_ECONOMICS.md` — si estas tarifas son sostenibles.
- `PROFESSIONAL_SERVICES.md` — qué se vende además de estos planes.
- `docs/PLATFORM.md` § 4 "Licensing" — implementación técnica de planes/límites/feature flags (documento técnico, referencia esta página en lugar de fijar cifras).

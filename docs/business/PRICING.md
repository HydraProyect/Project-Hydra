# PRICING — Planes y tarifas de Hydra

**Tipo**: Operativo
**Estado**: Draft — sin contenido desarrollado todavía. No hay planes ni precios confirmados.
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

## Programa de Cliente Fundador — condiciones confirmadas (2026-07)

Confirmadas explícitamente por el propietario del producto para la oferta al primer cliente
(GESEME). Candidatas a entrada en `DECISION_LOG.md` cuando el contrato quede firmado.

| Concepto | Condición |
|---|---|
| Modalidad anual | 4.500 €/año prepagado (≈375 €/mes). Incluye garantía de implantación a 90 días. |
| Modalidad mensual | 450 €/mes, sin permanencia. |
| Volumen incluido | Hasta 50 clientes gestionados o 600 trabajadores activos (lo que se alcance primero). |
| Ampliación de volumen | +100 €/mes por cada bloque adicional de 20 clientes o 200 trabajadores activos. |
| Blindaje de tarifa | 24 meses garantizados por contrato. |
| Tarifa comercial de referencia | 1.200 €/mes para volumen equivalente (**hipótesis débil** — sin contrastar, ver nota de mercado abajo). |
| Coste unitario resultante (anual) | ≈7,50 €/mes por cliente gestionado · ≈0,63 €/mes por trabajador activo. |

**Regla de diseño adoptada**: ninguna tarifa se ofrece "sin límite" de volumen — siempre en
banda con escalones, para que el coste de servir no quede desacoplado del crecimiento del
cliente.

**Dato de mercado verificado (2026-07)**: GESEME paga hoy a Twind 100 € de despliegue +
300 €/mes. Primer precio de mercado real obtenido por esta vía; pendiente de más comparables
antes de ajustar la tarifa comercial de referencia de la tabla anterior.

*Draft — condiciones ofertadas, pendientes de firma y de confirmación formal.*

## Anclas de mercado (Draft, 2026-08-02)

Insumo de `BENCHMARK_PRECIOS_CAE.md` § 6, no decisión de pricing:

- **La "visibilidad" como unidad de cobro** es la unidad que el mercado ya acepta pagar por cada relación cliente↔documentación (~180–190 €/año en la estructura de packs y ampliaciones de Nalanda). Es trasladable como referencia de disposición a pagar a la banda de 50 clientes gestionados de Hydra — hipótesis a contrastar, no una unidad de facturación decidida.
- **Coste agregado del statu quo**: un contratista con varios titulares paga hoy cientos a miles de €/año por plataforma que le impone cada cliente (detalle completo en `BENCHMARK_PRECIOS_CAE.md` § 6.3) — argumento de valor directo para justificar el precio de Hydra frente al coste total que ya soporta el mercado, no frente a una alternativa gratuita.
- **Trade-off abierto: transparencia vs. opacidad**. Ningún competidor publica precio por encima de ~50 contratas gestionadas (`BENCHMARK_PRECIOS_CAE.md`, "opacidad de precios en banda consultora"). Dos caminos: publicar precios en la banda consultora como diferenciador (patrón UCAE en banda baja), o negociar a medida como práctica incumbente (más margen capturable, menos fricción de entrada). Pregunta abierta, no resuelta aquí.
- **Add-on conector Twind — no tarifable todavía**: su coste depende de los niveles de acceso API EXTRA/ADVANTAGE de CTAIMA, no publicados, y de si la clave API se contrata por organización o por cliente final. **Bloqueante identificado**: hasta que la plantilla 2 de `PLANTILLAS_SOLICITUD_PRECIOS.md` obtenga respuesta de CTAIMA, este add-on no puede llevar cifra.

## Documentos relacionados

- `BUSINESS_MODEL.md` — modelo de negocio del que este documento deriva.
- `UNIT_ECONOMICS.md` — si estas tarifas son sostenibles.
- `PROFESSIONAL_SERVICES.md` — qué se vende además de estos planes.
- `docs/PLATFORM.md` § 4 "Licensing" — implementación técnica de planes/límites/feature flags (documento técnico, referencia esta página en lugar de fijar cifras).
- `BENCHMARK_PRECIOS_CAE.md` — fuente de las anclas de mercado de arriba.
- `ARQUITECTURA-INTEGRACIONES.md` — restricción de niveles de acceso de la API de Twind que bloquea la tarifa del add-on conector.

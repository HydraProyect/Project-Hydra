# ICP — Perfil de Cliente Ideal (Ideal Customer Profile) de Hydra

**Tipo**: Estratégico
**Estado**: Draft — sin contenido desarrollado todavía.
**Propósito**: Formalizar, desde el ángulo comercial, quién compra Hydra — desarrollando con criterios de cualificación los dos perfiles de comprador ya nombrados en la documentación de producto y técnica (`PROJECT.md` § "A quién sirve", `docs/MULTITENANCY.md` § 2 "Escenarios de negocio"): consultoras de PRL y empresas contratistas.

## Qué pertenece aquí

- Segmentos de cliente objetivo con criterios concretos: tamaño de organización, sector, volumen de trabajadores/centros gestionados, madurez digital.
- Dolor específico de cada segmento que Hydra resuelve (ver el diagnóstico ya hecho en `PROJECT.md` § "El problema que resolvemos", ahí desde el ángulo de producto).
- Criterios de cualificación de lead (qué hace que un prospecto merezca esfuerzo comercial).
- Anti-ICP: perfiles a los que conscientemente no se vende o no se prioriza.
- Diferencias de necesidad entre el perfil "consultora PRL gestionando varias empresas" y "empresa contratista gestionando la suya propia".

## Qué NO pertenece aquí

- Cómo se llega comercialmente a estos perfiles → `GO_TO_MARKET.md`.
- Cuánto se les cobra → `PRICING.md`.
- El modelado técnico de estos dos escenarios como tenants aislados (ya cubierto en detalle en `docs/MULTITENANCY.md` § 2, desde el ángulo de aislamiento de datos, no de cualificación comercial).

## Afinado de perfil de entrada (Draft, 2026-07)

- **Perfil validado con tracción real**: SPA de banda "Tier 2" (orden de magnitud 10-25 M€ de
  facturación, tomando como referencia al Cliente Fundador GESEME), no contratista directa.
- **Señal de cualificación de lead identificada**: dolor de **capacidad no absorbida** — SPA que
  ha rechazado o no ha podido aceptar un cliente de gran volumen por falta de capacidad de
  gestión manual, sin recurrir a ampliar plantilla.
- **Segmento de posicionamiento diferenciado**: SPAs que compiten por calidad de trato humano y
  rechazan explícitamente sustituir gestores por IA. Para este segmento, el mensaje de encaje es
  "multiplicador de capacidad del gestor", no "automatización que reemplaza personas" — ver
  principio de diseño en `PRODUCT_STRATEGY.md`.

*Draft — un solo caso validado; hipótesis pendiente de confirmarse con más clientes antes de
generalizarse como criterio de cualificación.*

## Documentos relacionados

- `PROJECT.md` § "A quién sirve" y § "El problema que resolvemos" — descripción de producto de estos mismos perfiles.
- `docs/MULTITENANCY.md` § 2 "Escenarios de negocio" — el mismo par de perfiles desde el ángulo de aislamiento técnico por tenant.
- `BUSINESS_MODEL.md` — cómo se monetiza cada perfil.
- `GO_TO_MARKET.md` — cómo se les capta.

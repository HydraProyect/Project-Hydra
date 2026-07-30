# COMPETITOR_ANALYSIS — Análisis de competidores de Hydra

**Tipo**: Operativo
**Estado**: Draft — sin contenido desarrollado todavía.
**Propósito**: Mapear a los competidores directos e indirectos de Hydra en el mercado de gestión de Coordinación de Actividades Empresariales (CAE), y documentar cómo se posiciona Hydra frente a ellos.

## Qué pertenece aquí

- Listado de competidores directos (otros gestores CAE) e indirectos (hojas de cálculo, procesos manuales, suites genéricas de PRL).
- Comparativa de funcionalidades, precios y experiencia de usuario.
- Ventajas competitivas de Hydra y debilidades frente a cada competidor.
- Amenazas de mercado (nuevos entrantes, cambios normativos que afecten al sector CAE).
- Nota de deslinde: plataformas como CTAIMA, Dokify o 6Coordina aparecen en `ARQUITECTURA-INTEGRACIONES.md` como **proveedores con los que Hydra se integra** (portales externos que los clientes ya usan). Si alguna de ellas compite también como producto propio de gestión CAE, ese análisis competitivo va aquí, no en el documento de integraciones — son ángulos distintos sobre el mismo nombre.

## Qué NO pertenece aquí

- Diseño técnico de integración con proveedores externos → `ARQUITECTURA-INTEGRACIONES.md`.
- A quién vendemos → `ICP.md`.
- Cómo se comunica la ventaja competitiva al mercado → `GO_TO_MARKET.md`.

## Primeros datos de mercado y reencuadre de categoría (Draft, 2026-07)

- **Precio de mercado verificado**: Twind — 100 € de despliegue + 300 €/mes (dato de cliente:
  GESEME). Primer precio real obtenido; pendiente de ampliar con más comparables (Nalanda,
  Dokify, CTAIMA, 6conecta, otros) antes de tratarlo como benchmark consolidado.
- **Reencuadre de categoría (hipótesis)**: en su Fase 1 (ver `PRODUCT_STRATEGY.md`), Hydra no
  compite frontalmente con las plataformas Inbound — son capas complementarias. Las Inbound
  resuelven la relación documental con una planta/cliente concreto; Hydra resuelve la operación
  interna del SPA (orden, vigencias, ausencia de Excel/correo/carpetas). La comparación
  económica relevante en Fase 1 es frente al coste de gestión manual, no frente a estas
  plataformas — ver `UNIT_ECONOMICS.md` y la comparativa de `PRICING.md`.
- **Pendiente de investigación**: alcance exacto del contrato de GESEME con Twind (nº de
  centros/clientes cubiertos, módulos incluidos) — insumo directo para calibrar la tarifa
  comercial de referencia en `PRICING.md`.

*Draft — no sustituye el desarrollo completo pendiente de este documento (competidores directos
e indirectos, ventajas y debilidades frente a cada uno).*

## Documentos relacionados

- `GO_TO_MARKET.md` — cómo se traduce este análisis en posicionamiento de mercado.
- `ICP.md` — perfil de cliente frente al que se compara la competencia.
- `ARQUITECTURA-INTEGRACIONES.md` — mismos nombres de proveedor, ángulo de integración técnica en vez de competencia comercial.

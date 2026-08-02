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

## Ampliación de benchmark de mercado (Draft, 2026-08-02)

Insumo de la sesión de benchmark de precios — detalle completo en `BENCHMARK_PRECIOS_CAE.md`, este apartado solo referencia sus tablas y añade la lectura competitiva.

- **Precios de mercado**: ver `BENCHMARK_PRECIOS_CAE.md` § 1-3 (plataformas inbound al contratista y al titular, rangos orientativos) — no se copian aquí, se referencian.
- **Corrección del mapa competitivo**: **e-coordina deja de ser competidor independiente** — Twind (CTAIMA Group) unifica CTAIMACAE y e-coordina en un único entorno (verificado en help.ctaima.com, 2026-08-02). Cualquier análisis futuro de e-coordina como entidad aparte queda desactualizado.
- **Consolidación del mercado**: el mercado converge en dos polos — **CTAIMA Group** (Twind, fuera de la red Konvergia, con API REST propia documentada) y **Once For All** (Nalanda + Dokify + gobernanza de Konvergia vía CEO común con Dokify). La estructura societaria está verificada [V]; la lectura de "dos polos" como marco competitivo es hipótesis, no dato.
- **Konvergia** (`BENCHMARK_PRECIOS_CAE.md` § 1-bis): no es un competidor directo — es infraestructura de transporte documental entre plataformas CAE asociadas, sin API pública, con acceso solo por adhesión de socios. Complementaria en producto (mueve documentos, no valida) y adversaria en narrativa (erosiona el argumento de fricción multi-plataforma que sostiene parte del posicionamiento de Hydra). Postura de Hydra frente a esta red: pregunta abierta, se desarrolla en `PRODUCT_STRATEGY.md`.
- **Debilidades explotables de incumbentes** [V]: quejas públicas recurrentes en Trustpilot/GoWork/comparadoras (renovaciones automáticas opacas, cobro por elementos ya dados de baja, subidas de cuota sin preaviso); la interoperabilidad (Konvergia) se vende como capa premium, no básica (nivel Max de Nalanda, 780 €/año); mecánica de "descuento" de anclaje (~50% de precio de lista tachado) en todos los packs de Nalanda — teatro de precio, no descuento real.
- **Opacidad de precios en banda consultora**: verificado que ningún competidor publica precio por encima de ~50 contratas gestionadas — toda esa banda se negocia a medida. Ver el trade-off de transparencia que esto abre para `PRICING.md`.
- **Pendiente de investigación**: tarifa propia del panel unificado de Konvergia (konvergia.com, no publicada en la página de Nalanda); adjudicaciones públicas con importe real (método en `BENCHMARK_PRECIOS_CAE.md` § 5); catálogo de API de Twind (hoy solo público el catálogo 1.0 legacy en developers.ctaima.com).

Nota de deslinde ya vigente en este documento (ver "Qué pertenece aquí" arriba): CTAIMA mantiene doble rol — competidor aquí, proveedor de integración en `ARQUITECTURA-INTEGRACIONES.md`. Esta ampliación lo refuerza: el análisis competitivo de Twind va aquí; el diseño del conector va allí.

## Documentos relacionados

- `GO_TO_MARKET.md` — cómo se traduce este análisis en posicionamiento de mercado.
- `ICP.md` — perfil de cliente frente al que se compara la competencia.
- `ARQUITECTURA-INTEGRACIONES.md` — mismos nombres de proveedor, ángulo de integración técnica en vez de competencia comercial.
- `BENCHMARK_PRECIOS_CAE.md` — fuente de datos de precios con nivel de fiabilidad, referenciada en la ampliación de arriba.
- `PRODUCT_STRATEGY.md` — postura frente a Konvergia, pregunta abierta trasladada allí.

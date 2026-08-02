# GO_TO_MARKET — Estrategia de salida al mercado de Hydra

**Tipo**: Estratégico
**Estado**: Draft — sin contenido desarrollado todavía.
**Propósito**: Definir cómo Hydra llega a sus clientes objetivo — canales de adquisición, estrategia de lanzamiento y mensajes de posicionamiento — una vez definido a quién se dirige (`ICP.md`) y qué se le vende (`BUSINESS_MODEL.md`, `PRICING.md`).

## Qué pertenece aquí

- Canales de adquisición: venta directa, partners, boca a boca dentro del sector PRL, marketplace de integraciones como canal indirecto.
- Estrategia de lanzamiento por segmento (consultoras primero vs. contratistas directas primero, o simultáneo).
- Mensajes de posicionamiento y propuesta de valor por segmento.
- Plan de expansión geográfica o sectorial más allá del alcance inicial.
- Rol de las consultoras de PRL como canal de distribución además de como cliente (revenden o recomiendan Hydra a sus propias empresas contratistas gestionadas).

## Qué NO pertenece aquí

- Definición detallada de quién es el cliente ideal → `ICP.md`.
- Tarifas → `PRICING.md`.
- Hitos comerciales con fecha concreta → `ROADMAP_BUSINESS.md`.
- Análisis de competidores → `COMPETITOR_ANALYSIS.md` (se usa como insumo, no se desarrolla aquí).

## Guion de objeciones validado en campo (Draft, 2026-07)

Primer guion de objeciones comerciales, surgido de la preparación y ejecución de la reunión de
propuesta al Cliente Fundador (GESEME). Candidato a reutilizarse y refinarse con los siguientes
prospectos:

- **"Queremos comprarlo / desarrollo interno"**: se responde mostrando el coste real de
  mantener software internamente (ver comparativa en `PRICING.md`/`UNIT_ECONOMICS.md`), no
  negando la petición. La objeción de fondo suele ser miedo a la dependencia de un proveedor
  unipersonal — se resuelve con garantías de continuidad (`DATA_OWNERSHIP.md`), no con venta del
  código.
- **"¿Y si el fundador se va de su empleo actual?"**: se responde separando la continuidad del
  servicio (estructura empresarial) de la relación laboral personal del fundador.
- **"Es caro / ¿por qué no gratis?"**: se responde mostrando la tarifa comercial de referencia
  frente a la tarifa de Cliente Fundador como intercambio (caso de éxito y referencia), no como
  regalo.
- **"Queremos exclusividad territorial"**: se descarta explícitamente por poner en riesgo la
  viabilidad del proveedor; se ofrece en su lugar prioridad de roadmap y acceso anticipado a
  módulos.
- **Caso de cliente de gran volumen no absorbido por falta de capacidad**: se usa como argumento
  de capacidad de crecimiento (ver `ICP.md`), nunca como garantía contractual — su cumplimiento
  depende de decisiones comerciales internas del cliente, ajenas al control del proveedor.

*Draft — guion de trabajo, no política de venta formalmente aprobada.*

## Munición de posicionamiento (Draft, 2026-08-02)

Insumo de `BENCHMARK_PRECIOS_CAE.md` § 1 y `COMPETITOR_ANALYSIS.md`:

- **Resentimiento del contratista contra el modelo incumbente**: las quejas públicas más repetidas del sector (renovaciones automáticas opacas, cobro por elementos ya dados de baja, subidas de cuota sin preaviso) son munición de mensaje directamente citable — fuentes públicas en `BENCHMARK_PRECIOS_CAE.md` § 1. "Quién paga" (consultora vs. repercutir a contratas) es, además de una decisión de pricing, una decisión de marca: alinearse con el lado que sufre el modelo actual es un mensaje de posicionamiento en sí mismo.
- **Caducidad del pitch "hoy es Excel"**: el dolor de la gestión manual tiene fecha de caducidad — Konvergia (interoperabilidad entre plataformas) y la IA/OCR que los incumbentes ya están incorporando erosionan progresivamente ese argumento. Implicación de mensaje: el argumento de venta de 2026 es el dolor actual (fricción, opacidad, coste agregado multi-plataforma), pero la ventaja defendible a 5 años tiene que ser otra — el modelo de operación delegada de consultora, que ningún incumbente cubre hoy porque colisiona con su propio pricing por "visibilidad" (cada cliente-titular es una unidad de cobro, no un caso a resolver con eficiencia operativa).

## Documentos relacionados

- `ICP.md` — a quién se dirige esta estrategia.
- `COMPETITOR_ANALYSIS.md` — panorama competitivo que informa el posicionamiento.
- `ROADMAP_BUSINESS.md` — calendario de ejecución de esta estrategia.
- `BUSINESS_ARCHITECTURE.md` — canales de venta como parte de la arquitectura comercial.
- `BENCHMARK_PRECIOS_CAE.md` — fuente de la munición de posicionamiento de arriba.

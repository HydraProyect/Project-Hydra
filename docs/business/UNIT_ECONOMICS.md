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

## Estructura de costes y margen — banda de 50 clientes (Draft, 2026-07)

**Modelo de volumen documental estimado** (pendiente de validar con medición real del piloto):
~400-1.000 documentos/mes a procesar en Fase 1 de `PRODUCT_STRATEGY.md`; ~3.000-11.000
subidas/mes equivalentes si se activara la Fase 2 (dimensiona el problema que resuelve el
producto, no un compromiso de construir conectores).

**Coste de servir** (escala con el nº de tenants — banda de 50 clientes):

| Partida | €/mes |
|---|---|
| Hosting UE (VPS + PostgreSQL + backups + S3) | 40-60 |
| OCR/clasificación IA en producción (pago por uso) | 5-15 |
| Email transaccional, dominio, monitorización | 10-15 |
| **Subtotal** | **~55-90** |

**Coste de estructura** (fijo, amortizado entre todos los clientes presentes y futuros — no
escala por cliente):

| Partida | €/mes |
|---|---|
| Herramientas de desarrollo (Claude Code Max) | 100 |
| Electricidad marginal del desarrollo | ~10 |
| Amortización de equipo | ~40 |
| **Subtotal** | **~150** |

**Margen resultante**: sobre la tarifa de Cliente Fundador (450 €/mes), margen neto ≈50%
(~210-245 €/mes). Decisión adoptada: no contratar una segunda suscripción de modelo de IA
(GPT) en paralelo a las herramientas de desarrollo — evaluado como duplicación de coste sin
caso de uso definido; cualquier necesidad puntual de comparación de modelos se cubre por API
de pago por uso.

*Draft — cifras estimadas, a sustituir por medición real del primer piloto (GESEME).*

## Coste de servir: conector Twind (Draft, 2026-08-02)

Insumo de `ARQUITECTURA-INTEGRACIONES.md` (niveles de acceso API de CTAIMA/Twind) y `BENCHMARK_PRECIOS_CAE.md`. El futuro add-on de integración con Twind arrastra un coste externo por tenant, o compartido entre tenants, en forma de suscripción a un nivel de la API de CTAIMA (STANDARD/EXTRA/ADVANTAGE). El límite del nivel STANDARD (1.000 peticiones/semana) es insuficiente para una sola consultora de 50 clientes: solo el sondeo diario de vencimientos documentales ya lo agotaría con margen estrecho, antes de contar cualquier sincronización adicional. Estimar el volumen de peticiones/tenant esperado es un insumo directo para dimensionar si el margen del add-on aguanta el coste del nivel EXTRA/ADVANTAGE una vez esos precios se conozcan (bloqueado en `PRICING.md`, pendiente de respuesta de CTAIMA vía `PLANTILLAS_SOLICITUD_PRECIOS.md`).

## Documentos relacionados

- `PRICING.md` — tarifas sobre las que se calculan estas métricas.
- `BUSINESS_MODEL.md` — modelo de negocio que estas métricas validan o cuestionan.
- `GO_TO_MARKET.md` — estrategia de adquisición que determina el CAC.
- `ARQUITECTURA-INTEGRACIONES.md` — límites de nivel de acceso de la API de Twind que originan el coste de servir del add-on de integración.

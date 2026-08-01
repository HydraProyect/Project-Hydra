# docs/business — Documentación de negocio de Hydra

**Tipo**: Índice
**Estado**: Draft — estructura creada (2026-07-25), contenido pendiente de desarrollo. Ningún documento de esta carpeta tiene todavía decisiones de negocio confirmadas.

## Por qué existe esta carpeta

Hydra tiene, desde `ADR-003-saas-multitenant.md`, un objetivo comercial explícito: ser una plataforma SaaS vendible a consultoras de PRL y empresas contratistas. Esa vía comercial genera preguntas que **no son de arquitectura ni de dominio técnico** — a quién vendemos, cuánto cobramos, cómo nos diferenciamos, qué servicios adicionales ofrecemos, quién es dueño de los datos en el contrato — y que hasta ahora no tenían un lugar propio en el repositorio. Sin esta carpeta, esas decisiones tienden a colarse como notas sueltas dentro de documentos técnicos (ver "Qué no pertenece aquí" en `docs/PLATFORM.md` § 4, o el aviso de `ARQUITECTURA-INTEGRACIONES.md` sobre presupuesto/pricing), donde son difíciles de encontrar y quedan mezcladas con decisiones de otra naturaleza.

`docs/business/` es la **fuente oficial** para toda decisión comercial: pricing, planes, servicios profesionales, unit economics, go-to-market, competencia, ICP, estrategia y roadmap comercial. Cualquier decisión de arquitectura que dependa del modelo de negocio (por ejemplo, límites de plan en `docs/PLATFORM.md` § 4 "Licensing") debe referenciar el documento correspondiente aquí en vez de definir la cifra o la regla de negocio in situ.

## Diferencia entre documentación técnica y documentación de negocio

| | Documentación técnica (raíz del repo, `docs/*.md` fuera de `business/`) | Documentación de negocio (`docs/business/`) |
|---|---|---|
| Responde a | ¿Qué construimos y cómo? | ¿A quién vendemos, qué vendemos y por qué? |
| Ejemplos | `ARCHITECTURE.md`, `DOMAIN.md`, ADRs, `docs/MULTITENANCY.md`, `docs/PLATFORM.md` | `PRICING.md`, `ICP.md`, `GO_TO_MARKET.md` |
| Cambia por | Decisiones de ingeniería y de dominio | Decisiones comerciales, de mercado y de producto a nivel estratégico |
| Autoridad | Equipo técnico / arquitectura | Propietario del producto / negocio |

Una decisión técnica puede *depender* de una decisión de negocio (ej. "el plan Starter permite hasta 5 Centros" depende de `PRICING.md`), pero el documento técnico no debe contener la cifra ni la regla comercial — solo una referencia. Ver `CLAUDE.md` § "Disciplina de decisión para cambios de arquitectura": Dominio → Arquitectura → Plataforma → Implementación. La documentación de negocio precede y alimenta ese orden desde fuera; no es un peldaño más de la misma escalera técnica.

## Convenciones y glosario

Antes de escribir o editar cualquier documento de esta carpeta, dos referencias obligatorias:

- **`DOCUMENT_STANDARDS.md`** — la guía editorial: plantilla de cada documento, vocabulario de estado (`Draft`/`In Progress`/`Approved`/`Deprecated`), cómo registrar decisiones, qué es normativo frente a exploratorio, cómo referenciar ADR y documentos técnicos, y convenciones de tablas/diagramas/glosario. Es el único lugar donde se define esa plantilla — este índice no la repite.
- **`UBIQUITOUS_LANGUAGE.md`** — el lenguaje ubicuo oficial de negocio (Tenant, Cliente Directo, Cliente Delegante, Delegated Workspace, Plan, Add-on...). Ningún documento redefine un término que ya tenga entrada allí.

## Orden de lectura recomendado

El modelo de negocio es la raíz: el ICP nace de él (a quién le sirve ese modelo), no al revés.

1. **`BUSINESS_MODEL.md`** — cómo genera ingresos Hydra: qué se vende, a quién y bajo qué lógica. La raíz de la que derivan el resto de documentos.
2. **`ICP.md`** — a quién se dirige ese modelo de negocio, con criterios de cualificación concretos.
3. **`PRODUCT_STRATEGY.md`** — hacia dónde evoluciona el producto para sostener ese modelo frente al ICP identificado.
4. **`BUSINESS_ARCHITECTURE.md`** — cómo se organiza comercialmente lo anterior (segmentos, canales de venta, relación consultora ↔ contratista).
5. **`PRICING.md`** — traducción del modelo de negocio en planes y tarifas concretas.
6. **`UNIT_ECONOMICS.md`** — si los números del pricing funcionan (CAC, LTV, márgenes).
7. **`PROFESSIONAL_SERVICES.md`** — qué se vende además de la licencia (onboarding, migración, soporte premium).
8. **`DATA_OWNERSHIP.md`** — de quién son los datos de cada tenant y qué implica contractualmente.
9. **`GO_TO_MARKET.md`** — cómo se lleva todo lo anterior al mercado.
10. **`COMPETITOR_ANALYSIS.md`** — frente a quién competimos y con qué ventaja.
11. **`ROADMAP_BUSINESS.md`** — cuándo, en qué orden y con qué hitos comerciales.

## Documentos estratégicos vs. operativos

**Estratégicos** (cambian poco, son la base de todo lo demás — cualquier cambio aquí obliga a revisar los documentos operativos que dependen de él):

- `ICP.md`
- `BUSINESS_MODEL.md`
- `BUSINESS_ARCHITECTURE.md`
- `DATA_OWNERSHIP.md`
- `GO_TO_MARKET.md`
- `PRODUCT_STRATEGY.md`

**Operativos** (se revisan y actualizan con más frecuencia, aplican la estrategia a decisiones concretas y datables):

- `PRICING.md`
- `PROFESSIONAL_SERVICES.md`
- `UNIT_ECONOMICS.md`
- `COMPETITOR_ANALYSIS.md`
- `ROADMAP_BUSINESS.md`

`DECISION_LOG.md` y `MATURITY_REVIEW.md` no entran en esta clasificación — no son documentos temáticos, son registros transversales (ver siguientes secciones).

## Registro de decisiones

`DECISION_LOG.md` no se lee de forma lineal como el resto: es el equivalente de negocio de los ADR técnicos (`ADR-001`, `ADR-002`, `ADR-003`) — un historial cronológico e inmutable de decisiones ya tomadas (fecha, decisión, motivo, alternativas descartadas, impacto), no un documento que se reescribe. Se consulta cuando hace falta saber *por qué* una decisión comercial se tomó de una manera y no de otra, y se amplía cada vez que el propietario del producto confirma una decisión de negocio nueva. Cualquier documento temático de esta carpeta puede generar una entrada ahí al pasar de `Draft`/`In Progress` a `Approved`.

## Informes de evaluación

`MATURITY_REVIEW.md` es un informe de madurez del producto (snapshot fechado, comité de revisión técnica externo al contenido de negocio). Como `DECISION_LOG.md`, no es un documento temático: no define decisiones, las **alimenta** — su ranking de prioridades P0-P3 es insumo para `ROADMAP_BUSINESS.md` y `PRODUCT_STRATEGY.md`, y cualquier decisión que derive de él se registra en `DECISION_LOG.md`. Un informe futuro se añade como snapshot nuevo fechado, no editando el existente.

## Reglas de esta carpeta

- Esta carpeta es la fuente oficial de toda decisión comercial. Ningún otro documento del repositorio debe definir pricing, planes, unit economics, ICP, competencia o roadmap comercial — debe referenciar el documento correspondiente aquí.
- No se duplica contenido entre documentos de esta carpeta ni se copia contenido de documentos técnicos existentes. Si un documento técnico ya contiene información de negocio, se añade aquí (o allí) una referencia cruzada, nunca una copia.
- Todo documento temático nuevo sigue la plantilla de `DOCUMENT_STANDARDS.md` § 2, y todo término de negocio nuevo se da de alta en `UBIQUITOUS_LANGUAGE.md` antes de usarse en más de un documento.
- Ningún documento de negocio decide por sí solo cambios de arquitectura, dominio o cumplimiento normativo (RGPD/LOPDGDD, DPA, términos de uso) — esas decisiones siguen las reglas de `CLAUDE.md` y requieren confirmación explícita del propietario del producto y, cuando aplique, revisión legal.
- Ver `CLAUDE.md` para las reglas de trabajo generales del repositorio y `docs/PLATFORM.md` para dónde encaja "negocio" frente a "kernel de plataforma" y "módulo de dominio".

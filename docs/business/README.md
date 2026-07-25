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

## Vocabulario de estado

Cada documento declara su **Estado** con uno de estos cuatro valores — el mismo criterio que hace falta cuando esta carpeta pase de 12 a 40-50 documentos y ya no sea obvio cuál es la fuente de verdad vigente:

| Estado | Significa |
|---|---|
| **Draft** | En redacción. El contenido, si existe, no está confirmado y puede cambiar sustancialmente. Es el estado de partida de todo documento nuevo — hoy lo son los 11 documentos temáticos de esta carpeta. |
| **In Progress** | En desarrollo activo. Incompleto, pero ya usable como referencia parcial — algunas secciones están decididas, otras no. |
| **Approved** | Aprobado por el propietario del producto/negocio. Es la fuente de verdad vigente para ese tema; cualquier documento técnico que lo referencie puede asumir su contenido como decidido. |
| **Deprecated** | Superseded o ya no vigente. Se conserva como registro histórico, igual que `ADR-002-single-tenant.md` en el lado técnico — nunca se borra, se marca. |

Un documento solo pasa a `Approved` cuando el propietario del producto lo confirma explícitamente — igual que las decisiones de cumplimiento normativo de `CLAUDE.md`, no es una decisión que un cambio de código pueda dar por hecha.

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

`DECISION_LOG.md` no entra en esta clasificación — no es un documento temático, es un registro transversal (ver siguiente sección).

## Registro de decisiones

`DECISION_LOG.md` no se lee de forma lineal como el resto: es el equivalente de negocio de los ADR técnicos (`ADR-001`, `ADR-002`, `ADR-003`) — un historial cronológico e inmutable de decisiones ya tomadas (fecha, decisión, motivo, alternativas descartadas, impacto), no un documento que se reescribe. Se consulta cuando hace falta saber *por qué* una decisión comercial se tomó de una manera y no de otra, y se amplía cada vez que el propietario del producto confirma una decisión de negocio nueva. Cualquier documento temático de esta carpeta puede generar una entrada ahí al pasar de `Draft`/`In Progress` a `Approved`.

## Reglas de esta carpeta

- Esta carpeta es la fuente oficial de toda decisión comercial. Ningún otro documento del repositorio debe definir pricing, planes, unit economics, ICP, competencia o roadmap comercial — debe referenciar el documento correspondiente aquí.
- No se duplica contenido entre documentos de esta carpeta ni se copia contenido de documentos técnicos existentes. Si un documento técnico ya contiene información de negocio, se añade aquí (o allí) una referencia cruzada, nunca una copia.
- Ningún documento de negocio decide por sí solo cambios de arquitectura, dominio o cumplimiento normativo (RGPD/LOPDGDD, DPA, términos de uso) — esas decisiones siguen las reglas de `CLAUDE.md` y requieren confirmación explícita del propietario del producto y, cuando aplique, revisión legal.
- Ver `CLAUDE.md` para las reglas de trabajo generales del repositorio y `docs/PLATFORM.md` para dónde encaja "negocio" frente a "kernel de plataforma" y "módulo de dominio".

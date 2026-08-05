# docs/business/inbound — Investigación del ecosistema Inbound (CAE) español

**Tipo**: Índice
**Estado**: Draft — todo el contenido de esta carpeta es investigación de mercado sin confirmar, no decisiones de producto.

## Qué es esta carpeta y de dónde viene

Este material llegó como una serie de 18 documentos redactados fuera de este repositorio, cubriendo el mercado español de plataformas Inbound (CAE: Nalanda, CTAIMA, Dokify, Metacontratas...), un intento de modelo canónico para integrarlas, y un backlog de investigación abierto. **No son verdades confirmadas de Hydra** — son la lectura de quien los escribió sobre un mercado externo, con su propio sistema de niveles de confianza (Confirmado/Observado/Inferido/Hipótesis) que ya advierte de esto. Se han reorganizado en 7 documentos temáticos (§ "Documentos de esta carpeta") aplicando la plantilla de `docs/business/DOCUMENT_STANDARDS.md`, y — el motivo real de esta nota — **cada uno se ha cotejado contra lo que Hydra ya tiene decidido o construido**, para no dejar que investigación externa compita en autoridad con decisiones ya tomadas.

## Regla de lectura: qué gana si hay contradicción

Si algo en esta carpeta choca con un documento normativo existente, **gana el documento existente**, siempre:

| Si el tema es... | La fuente de verdad es... | No esta carpeta |
|---|---|---|
| Arquitectura de conectores/integraciones (cómo se sincroniza con Dokify/CTAIMA/etc.) | `ARQUITECTURA-INTEGRACIONES.md` — ya decidido y parcialmente construido (conectores reales de Microsoft 365 y WhatsApp) | `CANONICAL_MODEL_DRAFT.md` de esta carpeta es un diseño alternativo **anterior y no adoptado** — ver su nota de cabecera |
| Vocabulario de negocio (Cliente, Empresa, Trabajador, Documento...) | `docs/business/UBIQUITOUS_LANGUAGE.md` + `PROJECT.md` § "Glosario de dominio" | `INBOUND_DOMAIN_GLOSSARY.md` de esta carpeta usa otros nombres para conceptos de mercado — ver tabla de colisiones en ese documento |
| Modelo de dominio implementado (entidades, agregados) | `DOMAIN.md` (verificado contra código) | Esta carpeta describe el dominio de **plataformas externas**, no el de Hydra |
| Competidores y posicionamiento comercial de Hydra | `docs/business/COMPETITOR_ANALYSIS.md`, `docs/business/BENCHMARK_PRECIOS_CAE.md` (datos verificados: precios reales, estructura societaria) | `MARKET_CATALOG.md` de esta carpeta tiene datos con confianza mucho menor (mayoría "Inferido"/"Hipótesis") — tratar como pista de investigación, no como benchmark |
| Estrategia de producto de Hydra | `docs/business/PRODUCT_STRATEGY.md` | `MARKET_GAPS_AND_POSITIONING.md` de esta carpeta es *insumo* para esa estrategia, no la estrategia en sí |

## Qué aporta genuinamente (la parte que sí suma)

Descontando lo anterior, el valor real de esta investigación para Hydra es:

1. **Segmentación del mercado por sector** (`SECTOR_AND_TRENDS.md`) — cómo cambia la gestión CAE entre construcción, industria, energía, logística, sanidad, retail... Esto no existe en ningún documento actual de Hydra y es información de mercado genuinamente nueva, útil para `docs/business/ICP.md` y para priorizar qué sectores atacar primero.
2. **Catálogo de plataformas y matriz comparativa** (`MARKET_CATALOG.md`) — lista más amplia de plataformas del mercado (Tier 1/2/3) que la que hoy tiene `docs/business/COMPETITOR_ANALYSIS.md`. Útil como lista de candidatos a investigar, no como dato confirmado.
3. **Vocabulario y catálogo documental vistos desde fuera** (`INBOUND_DOMAIN_GLOSSARY.md`) — cómo nombran y clasifican documentos/requisitos/estados las plataformas de la competencia. Útil para diseñar mapeos de conectores futuros (`docs/INTEGRATION_GUIDELINES.md`) y para detectar qué términos de Hydra podrían confundir a un cliente que viene de Nalanda o CTAIMA.
4. **Huecos de mercado y tendencias** (`MARKET_GAPS_AND_POSITIONING.md`) — el ángulo de Hydra como capa de agregación sobre múltiples plataformas Inbound (frente al ángulo actual, Hydra como gestor CAE en sí mismo) es una idea de posicionamiento que no está explorada en `docs/business/PRODUCT_STRATEGY.md` — se deja como pista a evaluar, no como decisión.
5. **Entidades que Hydra todavía no modela**: `Maquinaria` y `Actividad` aparecen en varios de estos documentos como conceptos habituales del mercado (ver `INBOUND_DOMAIN_GLOSSARY.md` § "Titulares documentales"). `DOMAIN.md` confirma que ninguna de las dos existe hoy en el dominio de Hydra. Son candidatas reales a futuras funcionalidades Inbound, no decisiones — habría que confirmar demanda real antes de construir (regla YAGNI de `CLAUDE.md`).
6. **Backlog de investigación abierto** (`RESEARCH_BACKLOG.md`) — preguntas de mercado sin responder (arquitectura funcional real de cada plataforma, APIs disponibles, motores de configuración) que sí son relevantes cuando se priorice el primer conector externo real (ver `ARQUITECTURA-INTEGRACIONES.md` § 11, fila "Construir conectores/capacidades especulativas antes de tener un proveedor real priorizado").

## Sobre "Outbound"

El material recibido usa "Inbound" para referirse a las plataformas externas que Hydra debe consumir/sincronizar (Nalanda, CTAIMA, Dokify...). El usuario menciona que este material también debe dirigir las implementaciones de **Outbound** — ese término no está definido todavía en ningún documento de Hydra (ni técnico ni de negocio). Antes de escribir contenido bajo ese nombre hace falta una definición confirmada por el propietario del producto (qué es Outbound en Hydra, en qué se diferencia de Inbound) — se deja como pregunta abierta en `RESEARCH_BACKLOG.md` en vez de asumirla.

## Documentos de esta carpeta

| Documento | Contenido | Relación con Hydra |
|---|---|---|
| `MARKET_CATALOG.md` | Catálogo de plataformas del mercado español + matriz comparativa (Tier 1/2/3, autenticación, workflow, riesgo) | Complementa `docs/business/COMPETITOR_ANALYSIS.md` — confianza menor, tratar como lista de candidatos |
| `SECTOR_AND_TRENDS.md` | Diferencias de gestión CAE por sector económico + tendencias de mercado observadas | Nuevo, sin solapamiento con documentación existente |
| `INBOUND_DOMAIN_GLOSSARY.md` | Dominio funcional, glosario, catálogo documental y flujos de trabajo vistos desde plataformas externas | **Colisiona en nombres** con `UBIQUITOUS_LANGUAGE.md`/`DOMAIN.md` — ver tabla de colisiones al inicio del documento |
| `MARKET_GAPS_AND_POSITIONING.md` | Huecos del mercado y posicionamiento de Hydra como capa de agregación multi-plataforma | Insumo para `docs/business/PRODUCT_STRATEGY.md`, no lo sustituye |
| `CANONICAL_MODEL_DRAFT.md` | Propuesta de modelo canónico y contrato de conector para integrar plataformas Inbound | **Superseded en la práctica** por `ARQUITECTURA-INTEGRACIONES.md` (ya construida parcialmente) — se conserva por el razonamiento agnóstico de proveedor, no como diseño a implementar |
| `RESEARCH_BACKLOG.md` | Preguntas de investigación abiertas, decisiones de investigación tomadas, fuentes y metodología | Vivo — se actualiza cuando se investigue una plataforma nueva |

## Documentos relacionados

- `docs/business/README.md` — índice general de `docs/business/`.
- `docs/business/DOCUMENT_STANDARDS.md` — plantilla que siguen los documentos de esta carpeta.
- `docs/business/UBIQUITOUS_LANGUAGE.md` — lenguaje ubicuo oficial; ningún documento de esta carpeta lo redefine.
- `ARQUITECTURA-INTEGRACIONES.md`, `DOMAIN.md`, `docs/business/COMPETITOR_ANALYSIS.md`, `docs/business/PRODUCT_STRATEGY.md` — fuentes de verdad que priman sobre esta carpeta en sus respectivos temas.

# OUTBOUND_USAGE_ANALYSIS — Datos reales de uso para priorizar conectores Inbound

**Tipo**: Operativo
**Estado**: Draft — primer análisis de un export operativo bruto, sin depurar por el propietario del producto. Ver § "Limitaciones" antes de tomar cualquier decisión de roadmap a partir de esto.
**Propósito**: Registrar y ordenar el único dato de **uso real, propio** que existe hoy sobre qué plataformas Inbound externas maneja el equipo de gestión CAE (frente al resto de esta carpeta, que es investigación de mercado externa de terceros). Sirve como *insumo* para priorizar qué conectores Inbound construir después de `ARQUITECTURA-INTEGRACIONES.md` § 5.1 — no sustituye esa decisión.

## Qué pertenece aquí

- El listado y ranking, por volumen de aparición, de **qué plataformas** aparecen en un export operativo de enlaces recopilado por el equipo de gestión CAE al acceder a portales de Clientes Principales — sin identificar a qué Cliente Principal concreto corresponde cada enlace.
- El cruce de ese ranking con `MARKET_CATALOG.md` (tiers de investigación externa) y con `ARQUITECTURA-INTEGRACIONES.md` § 5.1 (única decisión de priorización de conectores ya tomada).
- Una recomendación de orden de investigación/priorización para candidatos a **segundo conector real**, condicionada a las preguntas P0 de `RESEARCH_BACKLOG.md` (API disponible, documentación técnica) que este documento no responde.

## Qué NO pertenece aquí

- **Nombres de Clientes Principales, ni ningún dato que permita inferir qué cliente concreto usa qué plataforma.** El export original incluía subdominios y rutas por cliente (p. ej. una URL distinta por cada empresa dentro de una misma plataforma multi-cliente); esta información es confidencial y se ha descartado por completo del análisis — solo se conserva el nombre de la plataforma y su dominio principal.
- La decisión de qué conector se construye primero → ya tomada en `ARQUITECTURA-INTEGRACIONES.md` § 5.1 (CTAIMA/Twind, por tener API REST documentada en `developers.ctaima.com`). Este documento la refuerza con datos de uso real, no la reabre.
- Precios o estructura societaria de cada plataforma → `docs/business/COMPETITOR_ANALYSIS.md`, `docs/business/BENCHMARK_PRECIOS_CAE.md`.
- Definición formal de "Outbound" como concepto de producto de Hydra → sigue siendo una pregunta abierta para el propietario del producto (`RESEARCH_BACKLOG.md` pregunta 1). Este documento aporta el dato empírico que faltaba para responderla, pero no la responde por su cuenta.

## Origen de los datos y tratamiento de confidencialidad

El propietario del producto aportó un export operativo de ~700 líneas: enlaces recopilados por el equipo a lo largo del tiempo al acceder a los portales de sus Clientes Principales para subir documentación de la propia empresa como contratista — el flujo que el propietario llama "CAE Outbound". No es una lista curada: tiene URLs repetidas (a veces docenas de veces), variantes `http`/`https`/`www`, subdominios y rutas específicas por Cliente Principal, y algunas líneas ajenas al análisis.

Reglas aplicadas antes de publicar este documento:

- **Direcciones de correo electrónico** (coordinadores CAE de cada cliente): descartadas del análisis. El propietario confirmó que corresponden a relaciones que se gestionan por correo, no a un portal web — no aportan nada a una priorización de conectores.
- **Encabezados de tabla sin contenido** ("URL", "PLATAFORMA", "Correo"...): descartados.
- **Cualquier subdominio, ruta o fragmento de URL que identifique a un Cliente Principal concreto**: descartado. Cuando una plataforma expone un entorno distinto por cliente (subdominio o ruta), este documento solo cuenta cuántas veces apareció esa plataforma en total — nunca lista ni el número ni el nombre de los clientes detrás.
- El resultado es exclusivamente: **qué plataformas aparecen, con qué frecuencia relativa, y el enlace a su web/portal principal** — nada más granular.

## Ranking de plataformas por volumen de menciones

| # | Plataforma | Web / portal principal | Menciones en el export | Tier en `MARKET_CATALOG.md` |
|---|---|---|---|---|
| 1 | **CTAIMA Group** (incluye Twind, la marca unificada, y el legado CTAIMACAE/e-coordina — ver `ARQUITECTURA-INTEGRACIONES.md` § 5.1) | https://welcometotwind.io | 213 | ★★★★★ Tier 1 |
| 2 | IEDOCE | https://www.gestion.iedoce.com | 31 | No catalogado |
| 3 | eGestiona | https://www.egestiona.com | 26 | No catalogado |
| 3 | Integra (ASEM Web Services) | https://integra.asemwebservices.es | 26 | No catalogado — posible extranet de un único Cliente Principal, no plataforma multi-cliente (ver Limitaciones) |
| 5 | Quioo | https://quioo.es | 25 | No catalogado |
| 6 | Dokify | https://dokify.net | 24 | ★★★★★ Tier 1 |
| 7 | OHS-Solutions ("Navegador") | https://www.ohs-solutions.com | 21 | No catalogado |
| 8 | UCAE | https://www.ucae.es | 20 | ★★★★☆ Tier 2 |
| 9 | Nalanda | https://www.nalandaglobal.com | 17 | ★★★★★ Tier 1 |
| 9 | CoordinaPlus | https://www.coordinaplus.net | 17 | ★★★★☆ Tier 2 |
| 11 | Metacontratas | https://www.metacontratas.com | 16 | ★★★★★ Tier 1 |
| 12 | 6Coordina | https://www.6conecta.com | 15 | No catalogado (nombrado en `ARQUITECTURA-INTEGRACIONES.md` § 3 y `CLAUDE.md` como candidato) |
| 13 | Validate | https://secure.validate.network | 13 | ★★★☆☆ Tier 3 |
| 14 | Koordinatu | https://gestion.koordinatu.com | 12 | No catalogado — verificar si es la misma familia de producto que CTAIMA Group (mismo patrón de entorno por cliente) |
| 14 | SmartOSH | https://smartosh.com | 12 | ★★★★☆ Tier 2 |
| 14 | EcoGestor | https://clientes.ecogestor.com | 12 | ★★★★☆ Tier 2 |
| 17 | BIA360 CAE | https://bia360cae.com | 10 | No catalogado |
| 17 | Prevengos | https://prevengos.com | 10 | No catalogado |
| 17 | Cualtis | https://cae.cualtisonline.com | 10 | No catalogado |
| 20 | Achilles (red internacional de homologación, no un CAE español) | https://www.achilles.com | 13* | No catalogado |
| 21 | Sgred | https://sgred.net | 9 | No catalogado |
| 22 | Ergosup / "Flexia" | https://flexia.ergosup.net | 9 | No catalogado |
| 23 | SGS Gestiona | https://sgs-gestiona.com | 5 | No catalogado |
| — | Resto (~60 plataformas/dominios con 1-4 menciones cada uno) | — | ~110 | Mayoría extranets propias de un único Cliente Principal o portales institucionales — no son candidatos a conector genérico (ver Limitaciones) |

\* Incluye el subdominio de autenticación asociado a Achilles; no se mezcla con la autenticación de Quioo, que usa el mismo proveedor de identidad (Azure AD B2C) pero es un producto distinto.

## Hallazgo principal: esto refuerza con fuerza la decisión ya tomada, no la cambia

**CTAIMA Group concentra el 30,8% (213 de 691) de todas las menciones del export** — casi el triple que la siguiente plataforma. Esto es coherente con, y refuerza fuertemente, la decisión ya verificada en `ARQUITECTURA-INTEGRACIONES.md` § 5.1: Twind (CTAIMA Group) como objetivo del primer conector real, apoyada allí en un dato distinto y ya ✅ confirmado (API REST documentada en `developers.ctaima.com`). Ahora hay dos líneas de evidencia independientes — mercado/API (§ 5.1) y uso operativo real propio (este documento) — apuntando al mismo proveedor. No cambia la decisión; la hace más sólida.

Dentro de CTAIMA Group, la marca legacy `e-coordina` concentra la mayor parte de las menciones, seguida del dominio legacy `ctaimacae.net`; el propio Twind ya suma un volumen relevante pese a ser la marca más nueva del grupo — coherente con la migración en curso descrita en `ARQUITECTURA-INTEGRACIONES.md` § 5.1.

## Plataformas con peso real que no estaban en `MARKET_CATALOG.md`

La investigación de mercado externa (`MARKET_CATALOG.md`) no incluye varias plataformas que este dato de uso real sitúa por delante de algunas del Tier 2/3: **IEDOCE** (31 menciones, más que Dokify), **eGestiona** (26), **Quioo** (25), **OHS-Solutions** (21), **Cualtis** (10), **Prevengos** (10), **BIA360 CAE**, **Sgred**, **Koordinatu**. Ninguna tiene ficha en `MARKET_CATALOG.md` ni dato verificado en `COMPETITOR_ANALYSIS.md`/`BENCHMARK_PRECIOS_CAE.md` — quedan como candidatas a investigación (`RESEARCH_BACKLOG.md` P0 "Integraciones disponibles"), no como conectores a construir sin más datos.

## Qué NO es candidato a conector, aunque aparezca en la lista

Alrededor del 16% de las menciones corresponden a dominios propios de un único Cliente Principal (extranets corporativas a medida, sin marca de plataforma CAE compartida) o a portales/formularios de organismos públicos. Un conector solo tiene sentido económico cuando cubre una plataforma que sirve a **muchos** Clientes Principales a la vez (así funciona la propuesta de valor de `IIntegrationProvider` en `ARQUITECTURA-INTEGRACIONES.md` § 1: un adaptador, muchas conexiones de tenant). Estas entradas se excluyen de cualquier priorización de conectores; identificar cuáles son exactamente no aporta nada a esa priorización y sí sería información confidencial (nombres de Clientes Principales).

## Recomendación de orden de investigación (no de construcción)

1. **CTAIMA/Twind** — ya decidido (`ARQUITECTURA-INTEGRACIONES.md` § 5.1), reforzado por este dato. Sin cambios.
2. **Candidatos a investigar para un eventual segundo conector**, por volumen de menciones observado y sujeto a que `RESEARCH_BACKLOG.md` P0 confirme que existe API/documentación técnica (condición que hoy solo está verificada para CTAIMA/Twind): eGestiona, IEDOCE, Quioo, Dokify (ya Tier 1 en `MARKET_CATALOG.md` y nombrado como ejemplo en `ARQUITECTURA-INTEGRACIONES.md` § 3), OHS-Solutions, UCAE, Nalanda, CoordinaPlus, Metacontratas, 6Coordina.
3. Ninguna decisión de construir un segundo conector se toma solo con este documento — falta la misma verificación técnica que ya se hizo para CTAIMA (§ 5.1: catálogo de API, niveles de acceso, coste) y falta que el propietario del producto confirme relevancia comercial real (número de contratos activos por plataforma, no solo menciones — ver Limitaciones).

## Sobre el término "Outbound"

`RESEARCH_BACKLOG.md` pregunta 1 dejó abierto qué significa "Outbound" en Hydra. Este export es la primera evidencia empírica del propietario del producto sobre a qué se refiere en la práctica: el flujo por el que el equipo de gestión CAE **sube documentación propia a los portales de sus Clientes Principales** (dirección contraria a "Inbound", que el resto de esta carpeta usa para "Hydra consume/sincroniza con plataformas externas"). Esto no cierra la pregunta abierta — sigue haciendo falta que el propietario del producto confirme si "Outbound" es un concepto de producto formal en Hydra (p. ej. un tipo de capacidad de integración de escritura, ver `EscrituraRemota` en `ARQUITECTURA-INTEGRACIONES.md` § 3.1) o simplemente el nombre operativo interno de este flujo de trabajo manual.

## Limitaciones

- El recuento mide **cuántas veces apareció un enlace a esa plataforma en el export**, no número de contratos, facturación, ni frecuencia de uso real — es un indicio direccional, no una métrica de negocio. Dos plataformas con 10 y 20 menciones respectivamente no implican necesariamente el doble de actividad real.
- El export mezcla portales con login compartido (una URL sirve a todos los clientes de esa plataforma) con portales que exponen un entorno distinto por cliente — esa distinción de estructura se conserva en la tabla solo quitando toda identificación de cliente, no aporta un recuento de "relaciones" por no poder confirmarse sin datos confidenciales.
- Ningún dato de este documento tiene nivel de confianza ✅ Confirmado en el sentido de `MARKET_CATALOG.md` — es 🟡 Observado (evidencia directa del propio export) para "esta plataforma se usa", pero 🔴 Hipótesis para cualquier lectura cuantitativa fina (ranking exacto, peso relativo).
- No se ha verificado si alguna de las plataformas "no catalogadas" (IEDOCE, eGestiona, Quioo, OHS-Solutions, Koordinatu...) tiene API pública o privada — es la misma pregunta P0 de `RESEARCH_BACKLOG.md` sin responder para CTAIMA/Twind hasta el benchmark del 2026-08-02.
- Los enlaces "Web / portal principal" de la tabla son el dominio raíz observado con más frecuencia en el export para cada plataforma — no se ha verificado contra la web corporativa oficial de cada proveedor en todos los casos.

## Documentos relacionados

- `ARQUITECTURA-INTEGRACIONES.md` § 5.1 — única decisión de priorización de conectores ya tomada (CTAIMA/Twind); este documento la refuerza, no la sustituye.
- `MARKET_CATALOG.md` — tiers de investigación de mercado externa, cruzados en la tabla de ranking.
- `RESEARCH_BACKLOG.md` — pregunta abierta 1 ("¿Qué es Outbound en Hydra?") y líneas P0 de investigación pendientes (API/documentación técnica) que condicionan cualquier segundo conector.
- `docs/business/COMPETITOR_ANALYSIS.md`, `docs/business/BENCHMARK_PRECIOS_CAE.md` — datos verificados de precio/estructura, sin cubrir las plataformas nuevas que este documento revela.

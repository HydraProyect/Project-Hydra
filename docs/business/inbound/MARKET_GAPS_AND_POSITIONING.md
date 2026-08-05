# MARKET_GAPS_AND_POSITIONING — Huecos de mercado y posicionamiento como capa de agregación

**Tipo**: Operativo
**Estado**: Draft — lectura de mercado no confirmada como estrategia de producto.
**Propósito**: Recoger las oportunidades detectadas en la investigación externa y el ángulo de posicionamiento "Hydra como capa sobre múltiples plataformas Inbound", como insumo para que el propietario del producto decida si incorporarlo a `docs/business/PRODUCT_STRATEGY.md`. No es, por sí mismo, una decisión de estrategia.

## Qué pertenece aquí

- Brechas (`GAP`) detectadas en el mercado Inbound español y su relación con las capacidades actuales de Hydra.
- El ángulo de posicionamiento de Hydra como capa de agregación multi-plataforma, distinto del posicionamiento actual (gestor CAE en sí mismo).

## Qué NO pertenece aquí

- Decisión de si Hydra adopta este posicionamiento → `docs/business/PRODUCT_STRATEGY.md` (pendiente).
- Competidores concretos y su comparación con Hydra → `docs/business/COMPETITOR_ANALYSIS.md`.
- Roadmap o priorización → `docs/business/ROADMAP_BUSINESS.md`.

## Resumen: siete problemas recurrentes del mercado

1. Fragmentación del mercado (una empresa opera en varias plataformas a la vez).
2. Duplicidad documental (mismo documento subido repetidamente).
3. Ausencia de interoperabilidad entre plataformas.
4. Experiencia de usuario inconsistente entre proveedores.
5. Escasa automatización de tareas repetitivas.
6. Configuración compleja en plataformas altamente personalizables.
7. Escasa explotación de la información (foco en almacenar, no en analizar).

## Brechas detectadas y su relación con Hydra hoy

| Brecha | Situación observada | Estado en Hydra |
|---|---|---|
| GAP-01 Fragmentación del ecosistema | Una empresa contratista opera en múltiples plataformas CAE simultáneamente, cada una con su propio entorno | Hydra hoy es un gestor CAE **interno** del SPA/contratista (ver `docs/business/COMPETITOR_ANALYSIS.md`: "Hydra resuelve la operación interna del SPA... no compite frontalmente con las plataformas Inbound"). No agrega, hoy, el estado de varias plataformas externas — es la brecha que este documento explora, sin decisión tomada. |
| GAP-02 Duplicidad documental | El mismo documento (seguro RC, formación, ITV) se sube repetidamente en cada plataforma | Sin solución hoy — sería relevante solo si Hydra actuara como fuente única reutilizada hacia varias plataformas externas, lo que exige los conectores de `ARQUITECTURA-INTEGRACIONES.md` (ninguno de CAE construido todavía, salvo M365/WhatsApp que son de Comunicaciones) |
| GAP-03 Ausencia de lenguaje común | Cada proveedor usa su propia terminología para el mismo concepto | Hydra ya resuelve esto **para sí misma** vía `UBIQUITOUS_LANGUAGE.md` / `PROJECT.md`; no resuelve la torre de Babel entre plataformas externas |
| GAP-04 Requisitos no estandarizados | Cada Cliente Principal define su propio catálogo de requisitos | `RequisitoDocumental` de Hydra ya es texto libre configurable por Centro — mismo principio ("sin catálogo fijo") que recomienda la investigación |
| GAP-05 Estados incompatibles | Terminología de estado distinta entre proveedores para el mismo concepto | No aplica a Hydra internamente (estado de `Documento` calculado y único); aplicaría solo si se normalizaran estados de plataformas externas via conector |
| GAP-06 Baja automatización | Subidas, comprobaciones, consultas y seguimiento de incidencias mayormente manuales | Hydra ya automatiza cálculo de estado/alertas (`CalculadoraEstadoDocumento`, `Alerta`) para su propio dominio |
| GAP-07 Escasa visibilidad global | Sin vista consolidada cuando una empresa opera con varios Clientes Principales | Mismo hueco que GAP-01 — relevante solo bajo el ángulo de agregación multi-plataforma |
| GAP-08 Gestión reactiva | Las plataformas reaccionan tras la incidencia, poca prevención anticipada | Parcialmente cubierto por alertas de vencimiento (`ParametroSistema`, umbrales ámbar/rojo) |
| GAP-09 Configuración compleja | Plataformas muy personalizables exigen configuración extensa | No es un problema hoy en Hydra (modelo simple de `TipoDocumento`/`RequisitoDocumental`); vigilar si se amplía el motor de requisitos (ver `INBOUND_DOMAIN_GLOSSARY.md`) |
| GAP-10 Explotación limitada de datos | Foco en almacenar documentación, poco análisis de tendencias | Coincide con la tendencia "Mayor importancia del dato" de `SECTOR_AND_TRENDS.md` — sin desarrollo en Hydra hoy, pista para `PRODUCT_STRATEGY.md` |

### Priorización de oportunidades (según la investigación original)

| Oportunidad | Impacto | Frecuencia observada |
|---|---|---|
| Reducir duplicidad documental | Muy Alto | Muy Alta |
| Visión consolidada multi-plataforma | Muy Alto | Muy Alta |
| Normalización terminológica | Alto | Muy Alta |
| Normalización de estados | Alto | Alta |
| Simplificación de configuraciones | Alto | Alta |
| Automatización de tareas repetitivas | Muy Alto | Alta |
| Mejor explotación de datos | Media | Alta |
| Gestión preventiva | Media | Media |

Nota: las dos oportunidades de mayor impacto (reducir duplicidad, visión consolidada) **solo tienen sentido bajo el posicionamiento de agregación multi-plataforma** (§ siguiente) — no son alcanzables desde el posicionamiento actual de Hydra como gestor CAE interno.

## El ángulo de posicionamiento: Hydra como capa sobre plataformas Inbound

La investigación original describe el paradigma dominante del mercado así:

```
Cliente Principal → selecciona una plataforma → las contratistas acceden a ella → toda la gestión ocurre dentro de ese ecosistema
```

Cuando una empresa trabaja con varios Clientes Principales, el proceso se repite tantas veces como plataformas existan. Propone un posicionamiento alternativo:

```
                 Hydra
                    │
      ┌─────────────┼─────────────┐
      ▼             ▼             ▼
 Plataforma A   Plataforma B   Plataforma C
      ▼             ▼             ▼
 Cliente A      Cliente B      Cliente C
```

Es decir: Hydra como capa transversal de normalización sobre múltiples plataformas externas, sin sustituir necesariamente la plataforma que ya usa cada Cliente Principal.

### Por qué esto es una pregunta abierta, no un hecho

`docs/business/COMPETITOR_ANALYSIS.md` (2026-07, Draft) ya describe el posicionamiento actual de Hydra en su Fase 1: **"Hydra no compite frontalmente con las plataformas Inbound — son capas complementarias. Las Inbound resuelven la relación documental con una planta/cliente concreto; Hydra resuelve la operación interna del SPA."** Esto ya es, de hecho, una forma de posicionamiento como capa — pero centrada en la operación *interna* del SPA/contratista (orden, vigencias, sustituir Excel/correo/carpetas), no en agregar o normalizar el estado de *varias plataformas externas a la vez* como propone este documento. La diferencia es real: uno no requiere ningún conector externo construido; el otro depende por completo de que existan conectores reales hacia Nalanda/CTAIMA/Dokify (ninguno construido hoy, ver `ARQUITECTURA-INTEGRACIONES.md` § 13).

**Esto no es una recomendación de cambiar el posicionamiento** — es señalar que la investigación aporta un ángulo (agregación multi-plataforma) que hoy no está explícitamente evaluado ni descartado en `PRODUCT_STRATEGY.md`, y que depende de una inversión de ingeniería (conectores reales) que todavía no se ha priorizado.

### Diferenciadores funcionales potenciales (si se adoptara este ángulo)

- Abstracción del proveedor: operar sobre un modelo funcional independiente de la plataforma externa.
- Modelo documental universal: catálogo común en vez del de cada proveedor.
- Consolidación de cumplimiento: visión agregada entre plataformas.
- Independencia sectorial: el modelo no depende del sector económico (ver `SECTOR_AND_TRENDS.md`).

### Riesgos si se adoptara

- Cada Cliente Principal configura su plataforma de forma distinta — la normalización es costosa y nunca completa (mismo reto que ya anticipa `ARQUITECTURA-INTEGRACIONES.md` § 3.1 con el modelo de capacidades por proveedor).
- Los proveedores existentes (Nalanda/Dokify vía Once For All, CTAIMA vía Twind) siguen ampliando funcionalidades y consolidando el mercado — ver `docs/business/COMPETITOR_ANALYSIS.md`.
- Depende enteramente de construir conectores reales, que hoy son diseño sin implementar (excepción: M365 y WhatsApp, que son de Comunicaciones, no de sincronización CAE).

## Documentos relacionados

- `docs/business/PRODUCT_STRATEGY.md` — donde se decidiría formalmente si este ángulo se adopta.
- `docs/business/COMPETITOR_ANALYSIS.md` — posicionamiento actual ya redactado (Fase 1, capas complementarias).
- `ARQUITECTURA-INTEGRACIONES.md` — qué haría falta construir para que la agregación multi-plataforma fuera viable.
- `SECTOR_AND_TRENDS.md` — tendencias de mercado que respaldan parte de este análisis.

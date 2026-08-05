# MARKET_CATALOG — Catálogo y matriz comparativa de plataformas Inbound (CAE) españolas

**Tipo**: Operativo
**Estado**: Draft — investigación externa, mayoría de datos con nivel de confianza "Inferido" u "Hipótesis" (ver § "Nivel de confianza"). No usar como benchmark comercial confirmado.
**Propósito**: Listado amplio de plataformas Inbound del mercado español y comparación funcional/operativa, como lista de candidatos a investigar e integrar. Complementa, no sustituye, a `docs/business/COMPETITOR_ANALYSIS.md` (que tiene datos verificados de precio y estructura societaria para un subconjunto de estas plataformas).

## Qué pertenece aquí

- Catálogo de plataformas Inbound identificadas en el mercado español, con su nivel de evidencia.
- Matriz comparativa funcional, de autenticación, de workflow documental y de riesgo operativo.
- Priorización de investigación futura sobre qué plataformas estudiar primero.

## Qué NO pertenece aquí

- Precios verificados o análisis competitivo confirmado de Hydra → `docs/business/COMPETITOR_ANALYSIS.md` y `docs/business/BENCHMARK_PRECIOS_CAE.md` (datos con fuente real: p. ej. Twind/CTAIMA Group, Once For All como grupo de Nalanda+Dokify, Konvergia como red de transporte documental sin API pública).
- Diseño técnico de un conector real → `ARQUITECTURA-INTEGRACIONES.md` y, cuando exista, `docs/INTEGRATION_GUIDELINES.md`.
- Decisiones de a qué plataforma integrar primero → pendiente de decisión de producto, no se fija aquí.

## Nivel de confianza

Metodología heredada de la investigación original — se conserva porque es la única forma honesta de leer las tablas siguientes.

| Nivel | Significado |
|---|---|
| ✅ Confirmado | Evidencia directa: documentación oficial, API pública, demo comercial, uso real. |
| 🟡 Observado | Observado directamente durante navegación o pruebas (DevTools, HTML, capturas). |
| 🟠 Inferido | Deducción razonable basada en varios indicios, sin confirmación oficial. |
| 🔴 Hipótesis | Sin evidencia suficiente; requiere validación antes de tratarse como dato técnico. |

**Nota importante**: salvo que se indique lo contrario, casi todo el catálogo está en 🟡/🟠. Para Twind (CTAIMA), Nalanda, Dokify y Konvergia, `docs/business/COMPETITOR_ANALYSIS.md` y `docs/business/BENCHMARK_PRECIOS_CAE.md` ya tienen datos ✅ verificados más recientes (2026-08-02) — priorizar esos sobre las fichas de abajo cuando entren en conflicto.

## Clasificación del mercado español

| Grupo | Características | Plataformas |
|---|---|---|
| 1. Grandes Redes CAE | Miles de contratistas, clientes Enterprise, ecosistemas propios, servicios de validación | Nalanda, CTAIMA, Dokify |
| 2. Plataformas Independientes | Especializadas en CAE, normalmente SaaS | Metacontratas, CoordinaPlus, UCAE, Validate, eGestiona |
| 3. Suites HSE | CAE como módulo del sistema preventivo | SmartOSH, EcoGestor, Sabentis, Unifikas, Ergasia |
| 4. Servicios de Prevención (SPA con portal CAE) | — | Quirón Prevención, Valora, Norprevención, Previntegral |
| 5. Plataformas Sectoriales | Especializadas por sector (construcción, industria, química, energía) | — |
| 6. Plataformas IA | Nuevos operadores, arquitectura moderna, IA/OCR declarado | PlayCAE, Arch, Opground, DocuPRL |

## Matriz de prioridad de investigación (según el material original)

| Nivel | Plataformas |
|---|---|
| ★★★★★ (Tier 1) | Nalanda, CTAIMA, Dokify, Metacontratas |
| ★★★★☆ (Tier 2) | UCAE, CoordinaPlus, SmartOSH, EcoGestor, Quirón |
| ★★★☆☆ (Tier 3) | Validate, Sabentis, Ergasia, Valora, Norprevención, Previntegral, Arch, Opground, DocuPRL |

## Matriz funcional consolidada

| Plataforma | Segmento | Mercado | Flexibilidad requisitos | Modelo documental | Integración esperada |
|---|---|---|---|---|---|
| Nalanda | Gran Red | Enterprise | Alta | Mixto | Compleja |
| CTAIMA | Enterprise | Enterprise | Muy Alta | Mixto | Media-Alta |
| Dokify | SaaS | Mid-Market | Alta | Automatizado | Media |
| Metacontratas | Independiente | Mid-Market | Muy Alta | Automatizado | Baja |
| CoordinaPlus | Independiente | Enterprise | Alta | Mixto | Media |
| UCAE | Independiente | PYME | Media | Tradicional | Baja |
| SmartOSH | HSE | Enterprise | Alta | ERP | Media |
| EcoGestor | HSE | Enterprise | Alta | ERP + BPO | Media |
| Quirón | SPA | Transversal | Media | Humano | Alta |
| Sabentis | HSE | Enterprise | Alta | ERP | Media |
| Validate | Documental | Mid-Market | Media | Mixto | Media |
| Ergasia | PRL | PYME | Media | Tradicional | Baja |
| PlayCAE | IA | Emerging | Alta | IA | Baja |
| Arch | IA | Emerging | Alta | IA | Baja |
| Opground | IA | Emerging | Alta | IA | Baja |
| DocuPRL | IA | PYME | Media | IA | Media |

## Matriz de autenticación (evidencia)

| Plataforma | Email/Password | SSO | MFA visible | Certificado | Evidencia |
|---|---|---|---|---|---|
| Nalanda | Sí | Parcial | No confirmada | No confirmado | 🟡 Observado |
| CTAIMA | Sí | Sí | No confirmada | No confirmado | 🟠 Inferido |
| Dokify | Sí | No confirmado | No confirmada | No confirmado | 🟡 Observado |
| Metacontratas | Sí | No confirmado | No observada | No | 🟡 Observado |
| UCAE | Sí | No | No observada | No | 🟡 Observado |
| SmartOSH | Sí | No confirmado | No confirmada | No | 🟠 Inferido |
| EcoGestor | Sí | No confirmado | No confirmada | No | 🟠 Inferido |
| Quirón | Sí | Sí | Probable | No confirmado | 🟠 Inferido |
| PlayCAE | Sí | No confirmado | No confirmada | No | 🟠 Inferido |

## Matriz de workflow documental

| Plataforma | Automático | OCR | IA | Validación humana | Observaciones |
|---|---|---|---|---|---|
| Nalanda | Parcial | Hipótesis | Hipótesis | Sí | Outsourcing relevante |
| CTAIMA | Parcial | Inferido | Inferido | Sí | Fuerte componente técnico |
| Dokify | Sí | Inferido | Inferido | Parcial | Flujo ágil |
| Metacontratas | Sí | Inferido | No conocido | Sí | SLA corto |
| UCAE | Parcial | No conocido | No | Sí | Soporte humano |
| SmartOSH | Parcial | No conocido | No conocido | Sí | Integrado en ERP |
| EcoGestor | Parcial | No conocido | No conocido | Sí | BPO documental |
| Quirón | No | No conocido | No | Sí | Red SPA |
| PlayCAE | Sí | Inferido | Inferido | No conocido | Declarado IA-first |

## Riesgo operativo (para quien construya un conector)

| Plataforma | Dependencia humana | Riesgo cambios UI | Riesgo bloqueo | Riesgo legal datos | Riesgo global |
|---|---|---|---|---|---|
| Nalanda | Alto | Alto | Alto | Medio | Alto |
| CTAIMA | Medio | Alto | Medio | Medio | Alto |
| Dokify | Medio | Medio | Medio | Medio | Medio |
| Metacontratas | Medio | Bajo | Bajo | Medio | Bajo |
| UCAE | Medio | Bajo | Bajo | Medio | Bajo |
| Quirón | Alto | Medio | Medio | Alto | Alto |
| PlayCAE | Bajo | Bajo | Bajo | Medio | Bajo |

## Priorización recomendada por el material original (no confirmada por Hydra)

| Fase | Plataformas | Motivo declarado |
|---|---|---|
| 1 — Validación de mercado | Metacontratas, UCAE, Ergasia | Menor fricción de investigación, alta probabilidad de éxito rápido |
| 2 — Cobertura comercial relevante | Dokify, CoordinaPlus, SmartOSH | — |
| 3 — Integraciones estratégicas | Nalanda, CTAIMA, Quirón | — |
| 4 — Innovación y alianzas | PlayCAE, Arch, Opground | — |

Esta priorización **contradice en parte** la ya verificada en `ARQUITECTURA-INTEGRACIONES.md` § 5.1, que identifica **Twind (CTAIMA Group)** como el objetivo del primer conector real por tener API REST documentada (`developers.ctaima.com`) — un dato ✅ verificado que pesa más que esta priorización 🟠/🔴. Cualquier decisión de priorización de conectores debe partir de `ARQUITECTURA-INTEGRACIONES.md` § 5.1, no de esta tabla.

## Documentos relacionados

- `docs/business/COMPETITOR_ANALYSIS.md` — datos verificados de precio y estructura societaria (Twind, Once For All, Konvergia).
- `docs/business/BENCHMARK_PRECIOS_CAE.md` — precios de mercado con fuente.
- `ARQUITECTURA-INTEGRACIONES.md` § 5.1 — estado real del primer conector objetivo (CTAIMA/Twind).
- `SECTOR_AND_TRENDS.md` — en qué sectores opera cada tipo de plataforma.
- `RESEARCH_BACKLOG.md` — líneas de investigación abiertas sobre estas plataformas.

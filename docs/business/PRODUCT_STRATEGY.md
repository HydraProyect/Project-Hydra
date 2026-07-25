# PRODUCT_STRATEGY — Estrategia de producto de Hydra

**Tipo**: Estratégico
**Estado**: Placeholder — sin contenido desarrollado todavía.
**Propósito**: Definir, desde el ángulo de negocio, hacia dónde evoluciona el producto Hydra a medio/largo plazo — qué módulos o capacidades futuras se priorizan y por qué, en función de la estrategia comercial y competitiva, no solo de la viabilidad técnica.

## Qué pertenece aquí

- Priorización de módulos de negocio futuros (el backlog conceptual sin priorizar que ya esboza `docs/PLATFORM.md` § 1: Incidents, Training, PPE, Billing, Analytics...) con justificación de negocio — por qué uno antes que otro.
- Criterios de decisión "construir vs. comprar vs. integrar" desde el ángulo comercial (ej. cuándo conviene más una integración vía `ARQUITECTURA-INTEGRACIONES.md` que construir un módulo propio).
- Relación entre la evolución del producto y la ventaja competitiva sostenida (ver `COMPETITOR_ANALYSIS.md`).
- Hitos de producto que condicionan la estrategia comercial (ej. qué debe existir en el producto antes de abrir un nuevo segmento de `ICP.md`).

## Qué NO pertenece aquí

- Diseño técnico de los módulos futuros — vive en `ARCHITECTURE.md`/`DOMAIN.md` cuando exista una decisión real de construirlos (regla YAGNI de `CLAUDE.md` y `docs/PLATFORM.md`).
- El backlog conceptual del kernel de plataforma en sí → `docs/PLATFORM.md` § 1 (este documento prioriza el backlog de negocio; `PLATFORM.md` describe el backlog conceptual, sin priorizar, desde el ángulo de arquitectura).
- Calendario concreto de ejecución → `ROADMAP_BUSINESS.md`.

## Documentos relacionados

- `docs/PLATFORM.md` § 1 — backlog conceptual de módulos de dominio futuros, sin priorizar.
- `COMPETITOR_ANALYSIS.md` — panorama frente al que se decide esta estrategia.
- `ROADMAP_BUSINESS.md` — calendario de ejecución de esta estrategia.
- `ARQUITECTURA-INTEGRACIONES.md` — alternativa de integración frente a construcción propia.

# PRODUCT_STRATEGY — Estrategia de producto de Hydra

**Tipo**: Estratégico
**Estado**: Draft — sin contenido desarrollado todavía.
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

## Secuencia de producto — Fase 1 / Fase 2 (Draft, 2026-07)

Hipótesis de secuencia surgida de la validación con el Cliente Fundador (GESEME):

- **Fase 1 — "Registro"**: core de gestión y orden documental. Alta y gestión de clientes,
  empresas, trabajadores, subcontratas, centros y vehículos; documentación de empresa, de
  trabajador y ciclo mensual recurrente con control de vigencias; visitas programadas; alertas
  de caducidad. Sin conectores ni agentes hacia plataformas Inbound de terceros.
- **Fase 2 — "Orquestador"**: conectores/agentes de subida automática hacia plataformas Inbound
  (CTAIMA, Nalanda, Dokify, etc.). Diferida deliberadamente. Motivo: probable conflicto con
  condiciones de uso de esas plataformas (riesgo legal y de continuidad — bloqueo por detección
  de automatización) y coste de mantenimiento de conectores contra sistemas de terceros
  adversarios. No tiene fecha comprometida y no debe presentarse como tal a clientes.
- **MVP2 (siguiente hito tras Fase 1)**: centralización de mensajería de coordinación —
  ingesta de correo con threading (agrupado de hilos multi-participante y multi-dominio vía
  cabeceras `Message-ID`/`In-Reply-To`/`References`) y vinculación de cada hilo a la entidad CAE
  correspondiente (cliente, trabajador, expediente). SLA, KPIs y cuadros de mando quedan pospuestos
  a un MVP3 posterior.

**Principio de diseño (Draft)**: la plataforma no sustituye al gestor — elimina el trabajo que
no es atención (buscar, renombrar, archivar, vigilar caducidades) para que el mismo equipo
atienda a más clientes con la misma calidad de trato. Surge de conversación directa con un
Cliente Fundador que rechaza explícitamente la sustitución de gestores por IA como parte de su
posicionamiento de calidad de servicio. Candidato a informar tanto el desarrollo de producto
como el mensaje de `GO_TO_MARKET.md`.

**Estado real del desarrollo (nota operativa, julio 2026)**: en producción — alta y gestión de
cliente, empresa, trabajador, subcontrata, centro, vehículo, documentación, visitas programadas
y alertas. En despliegue — perfil de consultor para gestión multi-tenant.

*Todo lo anterior es Draft: hipótesis de trabajo pendiente de confirmación explícita del
propietario del producto antes de pasar a Approved.*

## Pregunta abierta: postura frente a Konvergia (Draft, 2026-08-02)

Insumo de `BENCHMARK_PRECIOS_CAE.md` § 1-bis. Konvergia es una red de interoperabilidad entre plataformas CAE asociadas (no una plataforma en sí), sin API pública, gobernada en parte por Once For All (mismo CEO que Dokify). Tres opciones frente a ella, sin decidir aquí:

1. **Adherirse como socio** — negociación de adhesión (business development, no contrato de API) con una red gobernada en parte por un competidor.
2. **Integrar plataforma a plataforma** — contra las plataformas miembro individuales, sin pasar por Konvergia como intermediario.
3. **Orquestar sin membresía** — las contratas ya pueden activar Konvergia por sí mismas desde sus propias cuentas en plataformas miembro; Hydra puede construir valor encima sin ser miembro de la red.

No se decide hasta tener tenants en producción y algo real que ofrecer en cualquier negociación de adhesión.

## Prioridad de integración (Draft, 2026-08-02)

**Twind (CTAIMA) primero** — tiene API REST real documentada y el cliente fundador ya opera sobre esa plataforma. **Konvergia después, si acaso** — ver pregunta abierta de arriba. **Nunca contra CTAIMACAE legacy** — Twind es el objetivo desde el día uno del conector (detalle técnico en `ARQUITECTURA-INTEGRACIONES.md`).

## Módulos futuros detectados vía catálogo API (Draft, 2026-08-02)

El catálogo de API 1.0 legacy de CTAIMA (developers.ctaima.com) expone funcionalidad más allá de la gestión documental: **Control de Accesos, Entradas y Salidas** (listado de presencia en emergencias) y **Autorizaciones de Pagos** (estado documental como condición para pagar facturas a un contratista). Candidatos al backlog conceptual de `docs/PLATFORM.md` § 1, sin priorizar aquí — son insumo de idea, no compromiso de construcción.

## Documentos relacionados

- `docs/PLATFORM.md` § 1 — backlog conceptual de módulos de dominio futuros, sin priorizar.
- `COMPETITOR_ANALYSIS.md` — panorama frente al que se decide esta estrategia.
- `ROADMAP_BUSINESS.md` — calendario de ejecución de esta estrategia.
- `ARQUITECTURA-INTEGRACIONES.md` — alternativa de integración frente a construcción propia; diseño técnico del conector Twind priorizado arriba.
- `BENCHMARK_PRECIOS_CAE.md` — origen de la pregunta abierta sobre Konvergia.

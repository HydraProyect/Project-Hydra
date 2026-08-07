# RESEARCH_BACKLOG — Investigación abierta sobre el ecosistema Inbound

**Tipo**: Operativo
**Estado**: Draft — vivo, se amplía cuando se investigue una plataforma o pregunta nueva.
**Propósito**: Registrar qué falta por investigar sobre el mercado Inbound español, las decisiones tomadas *durante esa investigación* (no decisiones de producto de Hydra), y la metodología/fuentes usadas — para que una futura sesión sepa qué ya se exploró y qué no.

## Qué pertenece aquí

- Líneas de investigación abiertas sobre plataformas externas, priorizadas.
- Decisiones tomadas al redactar esta serie de documentos (terminología a usar, cómo tratar la variabilidad sectorial) — **no son ADR de Hydra**, ver nota abajo.
- Metodología de fuentes y niveles de confianza.
- Preguntas abiertas para el propietario del producto.

## Qué NO pertenece aquí

- Decisiones de arquitectura o producto de Hydra → ADR reales (`ADR-001` a `ADR-004`) y `docs/business/DECISION_LOG.md`.
- Roadmap de construcción de conectores → `ARQUITECTURA-INTEGRACIONES.md` + `docs/business/ROADMAP_BUSINESS.md`.

## ⚠️ Nota sobre las "decisiones" de este documento

La investigación original incluía un `13_DECISION_LOG.md` con doce decisiones (`DEC-001` a `DEC-012`). **No son decisiones de Hydra** — son criterios editoriales que quien escribió la investigación siguió para mantenerla consistente (p. ej. "usar vocabulario propio en vez de terminología de cada plataforma", "los requisitos son configuración, no modelo fijo"). Se resumen en § "Criterios editoriales seguidos por esta investigación" únicamente como contexto de por qué el resto de documentos de esta carpeta están redactados como están. Ninguna decisión real de Hydra (dominio, arquitectura, negocio) se registra aquí — eso vive en los ADR técnicos o en `docs/business/DECISION_LOG.md`.

## Preguntas abiertas para el propietario del producto

Estas preguntas surgieron al organizar este material y no tienen respuesta en ningún documento existente de Hydra:

1. **¿Qué es "Outbound" en Hydra?** El material recibido usa "Inbound" para las plataformas externas que Hydra debería consumir (Nalanda, CTAIMA...). Se menciona que este material también debe dirigir "las implementaciones de Outbound", pero ese término no aparece definido en ningún documento técnico o de negocio existente. Antes de escribir nada bajo ese nombre hace falta que el propietario del producto lo defina — ¿es el sentido inverso (Hydra empujando datos hacia sistemas externos)? ¿Es un canal de salida distinto (notificaciones, informes)? ¿Es otra cosa? **Actualización**: `OUTBOUND_USAGE_ANALYSIS.md` recoge la primera evidencia empírica (un export operativo real del equipo de gestión CAE) de que, en la práctica, "Outbound" se usa hoy para el flujo de subir documentación propia a los portales de Clientes Principales — pero esto sigue sin ser una definición formal de producto confirmada.
2. **¿Vale la pena el ángulo de agregación multi-plataforma** (`MARKET_GAPS_AND_POSITIONING.md`) frente al posicionamiento actual de Hydra como gestor CAE interno del SPA/contratista? Depende de invertir en conectores reales que hoy no existen.
3. **¿Merece la pena ampliar `RequisitoDocumental`** de texto libre a un motor configurable con vigencia/validación/target (como describe `INBOUND_DOMAIN_GLOSSARY.md`)? Solo si hay demanda real confirmada — regla YAGNI de `CLAUDE.md`.
4. **¿Hay demanda real para modelar `Maquinaria` y `Actividad`** como entidades de dominio? Ambas aparecen consistentemente en el mercado (sobre todo construcción/industria) pero no existen hoy en `DOMAIN.md` y no se construyen sin caso de uso confirmado.

## Líneas de investigación pendientes (priorizadas por el material original)

| Prioridad | Significado |
|---|---|
| P0 | Crítica para el conocimiento del dominio |
| P1 | Muy importante |
| P2 | Conveniente |
| P3 | Investigación futura |

### P0

- **Arquitectura funcional de plataformas**: jerarquía Empresa→Centro→Actividad, modelo de requisitos/documental/incidencias/validación de cada proveedor real.
- **Motores de configuración**: cómo permiten los proveedores personalizar catálogos, plantillas, reglas, herencia, configuración por cliente/centro/actividad.
- **Flujos reales**: documentar de punta a punta (alta de empresa/trabajador, presentación, validación, corrección, renovación, incidencias) usando plataformas reales, no solo material comercial.
- **Integraciones disponibles**: API pública/privada, webhooks, exportaciones/importaciones, SSO/OAuth/SAML, automatización — por proveedor. Para CTAIMA/Twind ya hay un avance real en `ARQUITECTURA-INTEGRACIONES.md` § 5.1 (catálogo `developers.ctaima.com`, niveles STANDARD/EXTRA/ADVANTAGE).
- **Automatización**: qué operaciones son candidatas reales (carga/descarga documental, consulta de estados, seguimiento de incidencias, sincronización de empresas/trabajadores).

### P1

- Experiencia de usuario comparada entre plataformas (navegación, tiempo por tarea, accesibilidad).
- Modelos de permisos, roles, delegaciones de cada proveedor (relevante para comparar con el modelo de `ADR-004-delegacion-consultoras-cae.md`).
- Modelos de auditoría y trazabilidad.
- Comportamiento con grandes clientes (miles de trabajadores/empresas/documentos) — relevante para escalabilidad.

### P2

- IA aplicada (OCR, clasificación documental, extracción de metadatos, validaciones asistidas, búsqueda semántica) — comparar con el patrón "sugerencia, nunca automática" ya usado en Hydra (`DeteccionTrabajador`).
- Aplicaciones móviles: capacidades y limitaciones.
- Analítica: KPIs, dashboards, informes habituales del mercado.
- Modelos de homologación de proveedores (cuestionarios, puntuaciones).

### P3

- Tendencias internacionales (Europa, Reino Unido, LATAM, EE. UU.).
- Estándares de interoperabilidad y esquemas documentales abiertos.
- Seguimiento normativo (cambios regulatorios, firma electrónica, identidad digital).

## Criterios editoriales seguidos por esta investigación (contexto, no decisiones de Hydra)

- Usar vocabulario propio e independiente de cualquier proveedor (de ahí el glosario de `INBOUND_DOMAIN_GLOSSARY.md`).
- Mantener un catálogo documental universal independiente de las plataformas externas.
- Traducir todos los estados observados a un conjunto reducido de estados funcionales propios.
- No fijar requisitos como parte del modelo — tratarlos como configuración, dada la enorme variabilidad entre Clientes Principales.
- El dominio funcional es único para todos los sectores; las diferencias se resuelven mediante configuración (ver `SECTOR_AND_TRENDS.md`).
- Un documento tiene siempre un único titular funcional (Empresa/Trabajador/Vehículo/Maquinaria/Centro/Actividad).
- La relación Documento↔Requisito admite cualquier cardinalidad (1:1, 1:N, N:1, N:N).
- Tratar toda esta documentación como "living documentation" — se revisa cuando aparece evidencia nueva, no es estática.

## Metodología y fuentes

### Niveles de confianza usados en toda la investigación

| Nivel | Descripción |
|---|---|
| A | Fuente oficial del fabricante o entidad |
| B | Fuente oficial del cliente o partner |
| C | Publicación especializada |
| D | Información comercial o secundaria |
| E | Información no verificada |

### Tipos de fuente admitidos

Sitios web oficiales, documentación técnica/manuales, vídeos oficiales (webinars, demos), casos de éxito de clientes, licitaciones públicas, organismos públicos (INSST, Ministerio de Trabajo, comunidades autónomas), asociaciones empresariales, publicaciones especializadas.

### Excluido explícitamente de la investigación

Información confidencial, obtenida por ingeniería inversa, protegida por acuerdos de confidencialidad, interna de clientes, datos personales, código propietario.

### Limitaciones reconocidas por la investigación original

- Basada principalmente en información públicamente accesible — funcionalidades avanzadas o configuraciones específicas de cliente pueden no estar documentadas.
- APIs privadas y procesos internos rara vez son visibles sin acceso a un entorno real.
- Recomendación explícita para investigación futura: acceso a entornos de demo, entrevistas con gestores CAE/validadores/contratistas/grandes Clientes Principales/partners tecnológicos.

## Anexo — Titulares y actores del ecosistema (referencia rápida)

**Titulares documentales observados**: Empresa, Trabajador, Vehículo, Maquinaria, Centro de Trabajo, Actividad.

**Actores del ecosistema observados**: Cliente Principal, Empresa Contratista, Subcontrata, Gestor Documental/CAE, Validador (interno/externo/mixto), Trabajador, Responsable del Centro, Administrador de plataforma.

**Modelos de negocio observados en plataformas externas**: Cliente Principal paga, Contratista paga, modelo mixto, licencia corporativa. Ver `docs/business/COMPETITOR_ANALYSIS.md` y `docs/business/BENCHMARK_PRECIOS_CAE.md` para precios reales verificados, más fiables que esta lista genérica.

## Documentos relacionados

- Todos los documentos de `docs/business/inbound/` — esta investigación es transversal a todos ellos.
- `docs/business/DECISION_LOG.md` — registro real de decisiones de negocio de Hydra (distinto de los criterios editoriales de este documento).
- `ADR-001` a `ADR-004` — decisiones técnicas reales de Hydra.

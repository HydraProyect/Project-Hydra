# OUTBOUND_CAE — Definición del modelo CAE Outbound (BPO documental hacia plataformas de clientes)

**Tipo**: Estratégico
**Estado**: Approved — confirmado por el propietario del producto el 2026-08-07 (ver `DECISION_LOG.md`).
**Propósito**: Fijar la definición oficial de "CAE Outbound" como concepto de negocio de Hydra — distinto del uso de "Inbound" en `docs/business/inbound/` (investigación de plataformas externas) — y su relación con decisiones y documentos ya existentes que ya asumían este flujo sin nombrarlo formalmente.

## Qué pertenece aquí

- Definición formal de CAE Outbound y sus sinónimos aceptados.
- Marco legal citado por el propietario del producto (Art. 24 Ley 31/1995, RD 171/2004, Art. 19 LPRL).
- Diferencia operativa entre CAE Inbound y CAE Outbound (los dos roles que una misma empresa puede jugar).
- Funciones y beneficios declarados del servicio Outbound externalizado.

## Qué NO pertenece aquí

- Investigación de mercado sobre plataformas Inbound (Nalanda, CTAIMA, Dokify...) → `docs/business/inbound/`. Ver nota de desambiguación abajo — es un eje de clasificación distinto, aunque relacionado.
- Ranking de plataformas por uso real (qué plataformas concentran más trabajo Outbound) → `docs/business/inbound/OUTBOUND_USAGE_ANALYSIS.md`.
- Diseño técnico de conectores/APIs → `ARQUITECTURA-INTEGRACIONES.md`.
- Verificación legal de los artículos citados o desarrollo de una postura de cumplimiento normativo propia de Hydra: el contenido legal de este documento es el aportado por el propietario del producto como contexto de negocio, no una revisión legal confirmada — mismo criterio que `RGPD-TRATAMIENTO-DATOS.md` (que tampoco sustituye revisión legal real, `ADR-003-saas-multitenant.md` § condiciones de salida).
- Decisión de si Hydra ofrece este servicio como BPO propio, como add-on de producto, o solo construye herramientas de soporte → pendiente, no se fija aquí.

## Definición

**CAE Outbound** (sinónimos usados indistintamente: **CAE Saliente**, **CAE Externo**) es la delegación integral de las tareas de gestión, carga, actualización y seguimiento de la documentación preventiva de una empresa hacia las plataformas digitales de sus clientes. Es un modelo de externalización (BPO — Business Process Outsourcing) que, según el marco citado por el propietario del producto, opera en el ámbito regulado por el Artículo 24 de la Ley 31/1995 de Prevención de Riesgos Laborales y el Real Decreto 171/2004.

A diferencia del modelo Inbound (donde la empresa monta su propio software/plataforma para controlar a sus contratistas), el modelo Outbound pone el foco en la documentación que la empresa emite hacia el exterior para poder entrar a trabajar en centros ajenos.

## CAE Inbound vs. CAE Outbound — dos roles que una misma empresa puede jugar

| Característica | CAE Inbound (Entrante) | CAE Outbound (Saliente) |
|---|---|---|
| Rol de la empresa | Titular o Principal (contrata a otros) | Contratista o Subcontrata |
| Flujo del proceso | Sus proveedores suben documentación a su propia plataforma | Técnicos externos suben la documentación de la empresa a plataformas de sus clientes |
| Acción externalizada | Filtro, validación y control de acceso de terceros | Preparación, actualización y "vuelco" de datos propios en sistemas ajenos |
| Objetivo principal | Evitar la responsabilidad solidaria por fallos de contratas | Conseguir la luz verde (apto) para entrar a trabajar sin perder tiempo |

### Nota de desambiguación con el "Inbound" de `docs/business/inbound/`

El "Inbound"/"Outbound" de esta tabla describe **el rol que juega la empresa cliente de Hydra frente a sus propios clientes** (Titular vs. Contratista) — un eje de negocio. Es un eje distinto del uso de "Inbound" en `docs/business/inbound/` (las plataformas externas del mercado que Hydra podría consumir/sincronizar, ej. Nalanda, CTAIMA, Dokify — un eje de arquitectura de integraciones). No son contradictorios: son la misma realidad vista desde ángulos distintos — la plataforma en la que un Titular valida a sus contratas ("Inbound" en el sentido de esa carpeta) es exactamente la plataforma en la que, desde el lado del Contratista, se sube el trabajo "CAE Outbound" definido aquí. Cualquier documento que use uno de los dos términos debe dejar claro desde qué eje lo hace.

## Funciones del servicio "CAE Outbound" externalizado

Al contratar esta modalidad, el equipo de técnicos preventivos externos asume:

- **Mapeo de plataformas del cliente**: registro y aprendizaje del funcionamiento de cualquier plataforma exigida por los clientes. Ver el ranking real de qué plataformas concentran este trabajo en `docs/business/inbound/OUTBOUND_USAGE_ANALYSIS.md`.
- **Carga y actualización documental**: subida proactiva de TC2, ITA, evaluaciones de riesgos del puesto, aptitudes médicas, formaciones del personal (Art. 19 LPRL) y entrega de EPIs.
- **Gestión de alertas y caducidades**: vigilancia constante para renovar documentos antes de que el cliente bloquee el acceso de los trabajadores a sus instalaciones.
- **Interlocución técnica**: respuesta a los rechazos documentales de los validadores del cliente, corrigiendo errores técnicos en materia preventiva.

## Beneficios legales y operativos declarados

- **Garantía de cumplimiento**: la documentación enviada cumple los criterios del RD 171/2004 (según el marco legal citado por el propietario del producto — no verificado por este documento, ver "Qué NO pertenece aquí").
- **Eliminación del "caos burocrático"**: libera al departamento de PRL interno de tener que aprender y gestionar decenas de plataformas de clientes distintas.
- **Continuidad del negocio**: evita retrasos en obras/servicios por un documento rechazado o bloqueado en la entrada del cliente.

## Nombre comercial del servicio en el mercado

Conocido comercialmente en el sector como **Facility CAE** o **Gestión de Contratas Outbound** — terminología de mercado observada por el propietario del producto, no necesariamente el nombre que Hydra usaría para un producto o add-on propio (esa decisión queda fuera de alcance de este documento).

## Relación con decisiones y trabajo ya existentes en Hydra

Esta definición formaliza algo que ya estaba implícito en el repositorio, sin contradecirlo:

- `DECISION_LOG.md` (entrada 2026-08-05, "Estado del Documento derivado + Acreditación por plataforma destino") ya usaba "el trabajo diario Outbound termina en las plataformas de las titulares" como motivo de esa decisión.
- `docs/business/inbound/OUTBOUND_USAGE_ANALYSIS.md` es el primer dato de uso real de qué plataformas concentran ese trabajo — ahora tiene el marco conceptual formal en el que encajar.
- `ARQUITECTURA-INTEGRACIONES.md` § 3.1 ya prevé la capacidad `EscrituraRemota` (API de escritura hacia un proveedor) — la pieza técnica que, si algún día se construye, automatizaría parte de este trabajo hoy manual. Esto no implica que se vaya a construir — sigue sujeto a la disciplina YAGNI y a la verificación P0 de `docs/business/inbound/RESEARCH_BACKLOG.md` (¿existe API real por plataforma?).

## Documentos relacionados

- `docs/business/UBIQUITOUS_LANGUAGE.md` — entrada normativa "CAE Outbound" en la tabla de términos aprobados.
- `DECISION_LOG.md` — entrada 2026-08-07 con el registro de esta confirmación.
- `docs/business/inbound/README.md` — nota de desambiguación entre el "Inbound" de esa carpeta y el "CAE Inbound" definido aquí.
- `docs/business/inbound/RESEARCH_BACKLOG.md` — pregunta abierta 1 ("¿Qué es Outbound en Hydra?"), ahora respondida por este documento.
- `docs/business/inbound/OUTBOUND_USAGE_ANALYSIS.md` — datos reales de uso que este documento contextualiza.
- `ARQUITECTURA-INTEGRACIONES.md` § 3.1 — capacidad `EscrituraRemota`, pieza técnica relacionada.

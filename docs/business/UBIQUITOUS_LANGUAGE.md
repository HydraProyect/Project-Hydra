# UBIQUITOUS_LANGUAGE — Lenguaje ubicuo de negocio de Hydra

**Tipo**: Normativo (para los términos con Estado `Approved`) / Exploratorio (para los términos `Draft`, ver estado por fila)
**Estado**: Draft — estructura consolidada, la mayoría de los términos nuevos sin definición confirmada todavía.
**Propósito**: Fijar el vocabulario que negocio, producto, arquitectura y código usan **literalmente igual** para referirse a las mismas cosas — tomando el concepto de "Ubiquitous Language" de Domain-Driven Design (Eric Evans): un lenguaje compartido entre quien decide el negocio y quien lo construye, usado sin traducción en conversación, documentación y, cuando aplique, nombres de código. No es un diccionario de consulta pasiva: es el vocabulario que **se usa**, no uno de varios sinónimos posibles. Este documento sustituye al antiguo `GLOSSARY.md` (renombrado) — mismo propósito de evitar definiciones duplicadas o contradictorias, encuadre más preciso ahora que Hydra empieza a mezclar negocio, arquitectura y producto en las mismas conversaciones.

## Cómo usar este documento

- Antes de introducir un término de negocio nuevo en cualquier documento (de `docs/business/` o técnico), se comprueba primero si ya tiene entrada aquí.
- Si no la tiene, se añade una fila en "Términos de negocio — definición pendiente" con estado `Draft` y el documento que corresponde desarrollarlo — no se define in situ en el documento donde apareció por primera vez.
- Ningún documento redefine un término que ya tiene entrada aquí. Si un documento necesita un matiz distinto, es señal de que el término no significa lo mismo en los dos contextos y hace falta un término nuevo — no una segunda definición del mismo nombre.
- Un término pasa a `Approved` cuando el propietario del producto lo confirma explícitamente, y esa confirmación genera una entrada en `DECISION_LOG.md` (regla de `DOCUMENT_STANDARDS.md` § 4).

## Colisiones de nombre — resueltas (2026-07-25)

Estas tres colisiones se detectaron al construir este documento y se resolvieron el mismo día por decisión del propietario del producto (ver `DECISION_LOG.md`). Se conservan aquí, ya resueltas, para que nadie vuelva a introducir el nombre descartado más adelante sin saber por qué se evitó.

| Término descartado | Colisión con | Resolución |
|---|---|---|
| ~~Workspace~~ (de negocio) | "Workspace" ya nombra un concepto técnico existente: el panel de detalle contextual (`ContextWorkspacePanel`, `PLAN-CONTEXT-WORKSPACE.md` / `PLAN-MASTER-DETAIL-WORKSPACE.md`). | El concepto de negocio (una consultora operando en nombre de una empresa gestionada) se llama **Delegated Workspace** — nombre propio, nunca "Workspace" a secas. Ver entrada más abajo. |
| ~~Cliente Final~~ | Riesgo de colisión con **Cliente** (`PROJECT.md` § "Glosario de dominio") y de confusión con "usuario final". | Se descarta "Cliente Final". La familia de términos pasa a ser **Cliente** / **Cliente Directo** / **Cliente Delegante** (ver tabla de términos aprobados). |
| ~~Coordinador CAE~~ | Riesgo de colisión con el rol de autorización ya implementado **Ejecutivo CAE** (`PROJECT.md` § "Glosario de dominio"). | Se descarta como nombre de rol de autorización. El concepto que buscaba nombrar vive en un eje distinto: **cargo organizativo** de negocio, no **rol de autorización** del sistema (ver § "Cargos organizativos vs. roles de autorización"). |

## Términos ya definidos — este documento enlaza, no redefine

| Término | Definición normativa vive en |
|---|---|
| **Estado de Documento** (`Vigente`, `Proximo`, `Urgente`, `Vencido`, `NoAplica`) | `DOMAIN.md` § 68 — eje **documental**, calculado y nunca almacenado. **"Riesgo en visita" es un modificador contextual de `Vigente`**, no un estado (DDL-052, DDL-066). No confundir con el eje de **Acreditación**, más abajo, que es otro eje y no comparte semáforo. |
| **Tenant** | `docs/MULTITENANCY.md` § 1 "Filosofía del Tenant" — la organización que compra y usa Hydra; frontera absoluta de aislamiento. |
| **Cliente** (entidad de dominio CAE), **Centro**, **Empresa**, **Trabajador**, **Documento**, **Asignación** | `PROJECT.md` § "Glosario de dominio" — modelo de dominio CAE, validado contra el Excel real. |
| **Ejecutivo CAE** (y el resto de roles de autorización: Administrador, Supervisor, Consulta) | `PROJECT.md` § "Glosario de dominio", fila "Usuario / Rol" — roles de autorización ya implementados en ASP.NET Core Identity. No se renombran aquí (eso es un cambio técnico, fuera de alcance de este documento); ver § "Cargos organizativos vs. roles de autorización" para el eje de negocio, distinto de este. |
| **Escenario Consultora PRL** / **Escenario Contratista directa** | `docs/MULTITENANCY.md` § 2 "Escenarios de negocio" — los dos perfiles de tenant ya modelados técnicamente. |

## Términos de negocio — aprobados (2026-07-25)

Confirmados por el propietario del producto en esta conversación. Entrada registrada en `DECISION_LOG.md`.

| Término | Definición oficial | Documento fuente |
|---|---|---|
| **Cliente** (de negocio, distinto de la entidad de dominio del mismo nombre — ver nota) | Cualquier organización que contrata Hydra. | `BUSINESS_MODEL.md` |
| **Cliente Directo** | Cliente que gestiona su propia operación CAE dentro de Hydra (equivale al "Escenario 2 — Contratista directa" de `docs/MULTITENANCY.md` § 2). | `BUSINESS_MODEL.md` |
| **Cliente Delegante** | Cliente que delega la operación de su CAE en una Consultora (equivale al "Escenario 1 — Consultora PRL" de `docs/MULTITENANCY.md` § 2, visto desde la empresa gestionada, no desde la consultora). | `BUSINESS_MODEL.md` |
| **Delegated Workspace** | Nombre oficial del espacio en el que una Consultora opera en nombre de un Cliente Delegante. Nombre decidido para evitar la colisión con el "Workspace" técnico existente (ver tabla de colisiones). Diseño funcional/técnico completo (modelo de dominio, resolución de sesión, reporting transversal) en `ADR-004-delegacion-consultoras-cae.md`. | `BUSINESS_ARCHITECTURE.md` / `ADR-004-delegacion-consultoras-cae.md` |
| **CAE Outbound** (sinónimos: **CAE Saliente**, **CAE Externo** — mismo término) | Delegación integral de las tareas de gestión, carga, actualización y seguimiento de la documentación preventiva de una empresa hacia las plataformas digitales de sus clientes — la empresa actúa como Contratista/Subcontrata (rol opuesto a **CAE Inbound**, donde actúa como Titular/Principal). | `OUTBOUND_CAE.md` |
| **Acreditación** | Estado de un Documento frente a un acceso de plataforma Inbound destino concreto (Dokify, Nalanda, Twind…) — la subida de los documentos Outbound de Hydra a las plataformas Inbound de las titulares, vía integraciones existentes o futuras. Estados: Pendiente de subir / Subida / Aceptada / Rechazada / No requerida. Confirmado 2026-08-08 (`DECISION_LOG.md`). **Nunca "Incidencia"** para lo documental — colisión real con `INBOUND_DOMAIN_GLOSSARY.md`. | `PLAN-EJECUCION-UX.md` § Parte 2 |

> Nota de desambiguación: "Cliente" tiene ahora dos sentidos legítimos y no intercambiables en el repositorio — la entidad de dominio CAE (`PROJECT.md`, p. ej. Retail Iberia S.A., Bebidas del Norte S.A.) y la organización que paga por Hydra (esta tabla, p. ej. ArcoSPA Prevención S.L. o Ibertec S.A.). El contexto (documento de dominio/producto vs. documento de negocio) desambigua cuál aplica; ningún documento debe asumir que son lo mismo sin decirlo. Si esta ambigüedad demuestra ser un problema real al desarrollar `BUSINESS_MODEL.md`, es candidata a resolverse con un término distinto en ese momento — no antes.

## Cargos organizativos vs. roles de autorización

Son dos ejes distintos y no deben mezclarse en un único listado:

- **Rol de autorización** = lo que el sistema entiende y aplica como permiso (`Administrador`, `Supervisor`, `Ejecutivo CAE`, `Consulta` — ya implementados, `PROJECT.md` § "Glosario de dominio"). Vive en código (ASP.NET Core Identity) y no cambia por una decisión de este documento.
- **Cargo organizativo** = cómo se llama la persona en el organigrama de negocio de una Consultora o Cliente Directo — no tiene por qué corresponder 1:1 con un rol de autorización (una misma persona con cargo "Director Consultora" puede operar con rol de autorización "Administrador" en varios tenants gestionados).

| Cargo organizativo (Draft — propuesta, pendiente de confirmar la lista completa) | Nota |
|---|---|
| **Director Consultora** | Responsable máximo de una Consultora sobre el conjunto de empresas gestionadas. |
| **Responsable de Operaciones** | Gestión operativa del día a día dentro de una Consultora o Cliente Directo. |
| **Líder de Equipo** | Coordina a un subconjunto de usuarios dentro de una organización. |
| **Gestor CAE** | Ejecuta la gestión documental CAE día a día. Es un cargo de negocio — no renombra el rol de autorización "Ejecutivo CAE" ya implementado; la relación entre ambos (¿todo Gestor CAE opera con rol Ejecutivo CAE?) queda pendiente de confirmar en `BUSINESS_ARCHITECTURE.md`. |

Este eje queda `Draft`: la distinción cargo/rol está decidida, la lista exacta de cargos y su mapeo a roles de autorización todavía no.

## Términos de negocio — definición pendiente (Draft)

Ninguno de estos términos tiene definición confirmada. La columna "Nota de trabajo" describe el uso previsto tal como aparece hoy en la documentación existente, no una definición decidida.

| Término | Estado | Nota de trabajo | Se desarrolla en |
|---|---|---|---|
| **Organización** | Draft | Posible término genérico para la entidad que compra Hydra (Consultora o Cliente Directo), previo a decidir si es sinónimo de "Tenant" o un concepto distinto (una Organización con varios Tenants). | `BUSINESS_MODEL.md` |
| **Consultora** | Draft | Ya usado de forma informal y consistente en `ADR-003`, `docs/MULTITENANCY.md` § 2, `PROJECT.md`, `ICP.md`: organización que compra Hydra y gestiona la CAE de varios Clientes Delegantes. Pendiente confirmar como término formal único. Modelo técnico (Consultora = Tenant sin datos operativos propios) desarrollado en `ADR-004-delegacion-consultoras-cae.md` § 5.1. | `BUSINESS_MODEL.md` |
| **Delegación** | Draft | Mecanismo por el que una Consultora opera un Delegated Workspace en nombre de un Cliente Delegante. Relación exacta con "Operador Delegado" y los cargos de § "Cargos organizativos" desarrollada en `ADR-004-delegacion-consultoras-cae.md` (entidades `DelegacionTenant`/`AsignacionOperadorDelegado`, reversible sin migración). | `BUSINESS_ARCHITECTURE.md` |
| **Tenant Activo** | Draft | Posible distinción entre un tenant con suscripción vigente y en uso frente a uno de prueba o cancelado. | `PRICING.md` / `UNIT_ECONOMICS.md` |
| **Partner** | Draft | Canal de distribución indirecto. Aclarar si una Consultora puede ser Partner y Cliente a la vez. | `BUSINESS_ARCHITECTURE.md` / `GO_TO_MARKET.md` |
| **Marketplace** | Draft | Posible monetización de la futura Plataforma de Integraciones (`ARQUITECTURA-INTEGRACIONES.md`) como canal comercial. Ni el diseño técnico ni el modelo de negocio lo dan por decidido todavía. | `BUSINESS_MODEL.md` |
| **Operador Delegado** | Draft | Ver "Delegación" — es la misma pieza del modelo vista desde el rol operativo dentro de un Delegated Workspace, no un concepto aparte (confirmado en `ADR-004-delegacion-consultoras-cae.md`: usuario de la Consultora autorizado vía `AsignacionOperadorDelegado` a operar un tenant cliente concreto). | `BUSINESS_ARCHITECTURE.md` |
| **Propietario de datos** | Draft | Relacionado con la premisa ya anticipada en `DATA_OWNERSHIP.md`: el tenant es propietario de sus datos, Hydra actúa como encargado del tratamiento. Pregunta abierta: en un Cliente Delegante, ¿el propietario es siempre el tenant técnico o puede haber un propietario distinto por empresa gestionada dentro del Delegated Workspace? | `DATA_OWNERSHIP.md` |
| **Servicio Profesional** | Draft | Servicio vendido además de la licencia SaaS (onboarding, migración, formación, soporte premium, desarrollo a medida). | `PROFESSIONAL_SERVICES.md` |
| **Add-on** | Draft | Capacidad o módulo contratable por separado de un plan base. Relacionado con `FeatureFlag`/`TenantFeatureFlag` (`docs/PLATFORM.md` § 4). Aclarar si es sinónimo de "módulo" (unidad de código) o un concepto puramente comercial (add-on = se paga aparte). | `PRICING.md` |
| **Plan** | Draft | Nivel de suscripción que determina features y límites incluidos — `Tenant → Plan → Features → Límites` (esbozo en `docs/PLATFORM.md` § 4). | `PRICING.md` |
| **Suscripción** | Draft | Relación comercial recurrente entre un Tenant y un Plan. | `PRICING.md` / `UNIT_ECONOMICS.md` |
| **Datos de Servicio** | Draft | Patrón *Service Data* de Zendesk: el contenido que el tenant introduce en la plataforma (documentos, datos de trabajadores, mensajes) — distinto de los datos de cuenta (identificación del tenant/usuarios) y de los datos de uso (telemetría/logs). Usado por los Términos y Condiciones de Uso y el resto del paquete legal para separar "de quién son los datos" de "qué datos son". Ver `docs/business/legal/LEGAL_FRAMEWORK.md` § 2.4 y `DATA_OWNERSHIP.md`. | `docs/business/legal/TERMINOS_Y_CONDICIONES.md` |

## Reglas de este documento

- Ningún término pasa de `Draft` a `Approved` sin confirmación explícita del propietario del producto, registrada el mismo día en `DECISION_LOG.md` (regla general de `DOCUMENT_STANDARDS.md` § 3-4).
- Al confirmar un término, este documento se actualiza en el mismo cambio que el documento temático correspondiente — nunca por separado ni con retraso.
- Una colisión de nombre resuelta (tabla de arriba) no se reabre implícitamente: si alguien reintroduce el nombre descartado en un documento nuevo, es una regresión, no una alternativa válida.

## Documentos relacionados

- `DOCUMENT_STANDARDS.md` § 7 — regla de uso de este documento.
- `DECISION_LOG.md` — entrada del 2026-07-25 con el motivo completo de las tres resoluciones de nombre.
- `PROJECT.md` § "Glosario de dominio" — glosario técnico de dominio CAE y roles de autorización, fuente de los términos ya definidos.
- `docs/MULTITENANCY.md` § 1-2 — definición de Tenant y escenarios de negocio.
- Todos los documentos de `docs/business/` — cada uno es la fuente de desarrollo de los términos que le corresponden en las tablas de arriba.

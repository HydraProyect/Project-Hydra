# GLOSSARY — Glosario oficial de términos de negocio

**Tipo**: Normativo (para los términos ya definidos y enlazados) / Exploratorio (para los términos nuevos, ver estado por fila)
**Estado**: Draft — estructura creada, la mayoría de los términos nuevos sin definición confirmada todavía.
**Propósito**: Que cada término de negocio tenga una única definición en todo el repositorio. Sin este documento, dos documentos distintos pueden usar "Consultora" y "Proveedor Externo" (o cualquier otro par) para referirse a lo mismo, o el mismo término para dos cosas distintas — el coste no se nota con 12 documentos, se nota con 40-50. Ver `DOCUMENT_STANDARDS.md` § 7 para la regla de uso.

## Cómo usar este glosario

- Antes de introducir un término de negocio nuevo en cualquier documento (de `docs/business/` o técnico), se comprueba primero si ya tiene entrada aquí.
- Si no la tiene, se añade una fila en "Términos de negocio — definición pendiente" con su estado como `Draft` y una nota de a qué documento pertenece desarrollarlo — no se define in situ en el documento donde apareció por primera vez.
- Ningún documento redefine un término que ya tiene entrada aquí. Si un documento necesita un matiz distinto, es señal de que el término no significa lo mismo en los dos contextos y hace falta un término nuevo — no una segunda definición del mismo nombre.

## ⚠️ Colisiones de nombre detectadas (resolver antes de usar el término en negocio)

| Término | Conflicto | Nota |
|---|---|---|
| **Workspace** | `PLAN-CONTEXT-WORKSPACE.md` y `PLAN-MASTER-DETAIL-WORKSPACE.md` ya usan "Workspace" para un concepto técnico de navegación existente: el panel de detalle contextual (`ContextWorkspacePanel`, instancia única en el layout). | Si el uso de negocio pretende nombrar algo distinto (p. ej. "el espacio operativo de un tenant" o un concepto de delegación), necesita un nombre propio — reutilizar "Workspace" aquí crearía exactamente la ambigüedad que este glosario existe para evitar. No añadir una entrada de negocio para "Workspace" hasta resolver esto. |
| **Cliente Final** | `PROJECT.md` § "Glosario de dominio" ya define **Cliente** como la empresa titular de Centros de Trabajo (destinatario de la coordinación CAE). | Antes de dar de alta "Cliente Final" como término distinto, confirmar si aporta un matiz real (p. ej. distinguir "Cliente" desde el ángulo comercial de una Consultora) o si es el mismo concepto con otro nombre — en ese caso se usa "Cliente" y se retira "Cliente Final" de la lista. |
| **Coordinador CAE** | `PROJECT.md` § "Glosario de dominio" ya define el rol interno **Ejecutivo CAE** (uno de los cuatro roles: Administrador, Supervisor, Ejecutivo CAE, Consulta). | Confirmar si "Coordinador CAE" es un rol nuevo (p. ej. de una Consultora operando sobre varios tenants) o el mismo rol con otro nombre en el contexto de negocio. |

## Términos ya definidos — este glosario enlaza, no redefine

| Término | Definición normativa vive en |
|---|---|
| **Tenant** | `docs/MULTITENANCY.md` § 1 "Filosofía del Tenant" — la organización que compra y usa Hydra; frontera absoluta de aislamiento. |
| **Cliente**, **Centro**, **Empresa**, **Trabajador**, **Documento**, **Asignación**, **Usuario/Rol** (Administrador, Supervisor, Ejecutivo CAE, Consulta) | `PROJECT.md` § "Glosario de dominio" — modelo de dominio CAE, validado contra el Excel real. |
| **Escenario Consultora PRL** / **Escenario Contratista directa** | `docs/MULTITENANCY.md` § 2 "Escenarios de negocio" — los dos perfiles de tenant ya modelados técnicamente. |

## Términos de negocio — definición pendiente (Draft)

Ninguno de estos términos tiene definición confirmada. La columna "Nota de trabajo" describe el uso previsto tal como aparece hoy en la documentación existente, no una definición decidida.

| Término | Estado | Nota de trabajo | Se desarrolla en |
|---|---|---|---|
| **Organización** | Draft | Posible término genérico para la entidad que compra Hydra (Consultora o Empresa contratista), previo a decidir si es sinónimo de "Tenant" o un concepto distinto (una Organización con varios Tenants). | `BUSINESS_MODEL.md` |
| **Workspace** (de negocio) | Draft — bloqueado | Ver colisión de nombre arriba. No usar hasta resolver el conflicto con el Workspace técnico existente. | — |
| **Cliente Final** | Draft — posible duplicado | Ver colisión con "Cliente" arriba. | `ICP.md` / `BUSINESS_ARCHITECTURE.md` |
| **Consultora** | Draft | Ya usado de forma informal y consistente en `ADR-003`, `docs/MULTITENANCY.md` § 2, `PROJECT.md`, `ICP.md`: organización que compra Hydra y gestiona la CAE de varias empresas contratistas frente a los clientes finales de estas. Pendiente confirmar como término formal único. | `ICP.md` / `BUSINESS_ARCHITECTURE.md` |
| **Delegación** | Draft | Mecanismo por el que una Consultora actúa en nombre de una Empresa contratista. Relación exacta con "Operador Delegado" y "Director CAE Externo" por definir conjuntamente — probablemente son piezas del mismo modelo, no tres conceptos independientes. | `BUSINESS_ARCHITECTURE.md` |
| **Tenant Activo** | Draft | Posible distinción entre un tenant con suscripción vigente y en uso frente a uno de prueba o cancelado. | `PRICING.md` / `UNIT_ECONOMICS.md` |
| **Coordinador CAE** | Draft — posible duplicado | Ver colisión con "Ejecutivo CAE" arriba. | `BUSINESS_ARCHITECTURE.md` |
| **Director CAE Externo** | Draft | Rol dentro de una Consultora con visibilidad/control sobre varias empresas gestionadas. Definir junto con "Delegación" y "Operador Delegado". | `BUSINESS_ARCHITECTURE.md` |
| **Partner** | Draft | Canal de distribución indirecto. Aclarar si una Consultora puede ser Partner y cliente a la vez. | `BUSINESS_ARCHITECTURE.md` / `GO_TO_MARKET.md` |
| **Marketplace** | Draft | Posible monetización de la futura Plataforma de Integraciones (`ARQUITECTURA-INTEGRACIONES.md`) como canal comercial. Ni el diseño técnico ni el modelo de negocio lo dan por decidido todavía. | `BUSINESS_MODEL.md` |
| **Operador Delegado** | Draft | Ver "Delegación" — probablemente la misma pieza del modelo vista desde el rol operativo, no un concepto aparte. | `BUSINESS_ARCHITECTURE.md` |
| **Propietario de datos** | Draft | Relacionado con la premisa ya anticipada en `DATA_OWNERSHIP.md`: el tenant es propietario de sus datos, Hydra actúa como encargado del tratamiento. Pregunta abierta: en el escenario Consultora, ¿el propietario es siempre el tenant técnico o puede haber un propietario distinto por Empresa gestionada? | `DATA_OWNERSHIP.md` |
| **Servicio Profesional** | Draft | Servicio vendido además de la licencia SaaS (onboarding, migración, formación, soporte premium, desarrollo a medida). | `PROFESSIONAL_SERVICES.md` |
| **Add-on** | Draft | Capacidad o módulo contratable por separado de un plan base. Relacionado con `FeatureFlag`/`TenantFeatureFlag` (`docs/PLATFORM.md` § 4). Aclarar si es sinónimo de "módulo" (unidad de código) o un concepto puramente comercial (add-on = se paga aparte). | `PRICING.md` |
| **Plan** | Draft | Nivel de suscripción que determina features y límites incluidos — `Tenant → Plan → Features → Límites` (esbozo en `docs/PLATFORM.md` § 4). | `PRICING.md` |
| **Suscripción** | Draft | Relación comercial recurrente entre un Tenant y un Plan. | `PRICING.md` / `UNIT_ECONOMICS.md` |

## Reglas de este glosario

- Ningún término pasa de `Draft` a definición confirmada sin que el documento que lo desarrolla (columna "Se desarrolla en") pase primero a `Approved` — el glosario reflexiona la definición decidida allí, no la inventa aquí.
- Al confirmar un término, este documento se actualiza en el mismo cambio que el documento temático correspondiente, y la decisión se registra en `DECISION_LOG.md` si introduce o cambia un concepto usado en más de un documento.
- Las colisiones de nombre de la tabla de arriba se resuelven antes de que el término correspondiente use el nombre en cuestión en cualquier documento nuevo — no se documenta primero y se resuelve el conflicto después.

## Documentos relacionados

- `DOCUMENT_STANDARDS.md` § 7 — regla de uso del glosario.
- `PROJECT.md` § "Glosario de dominio" — glosario técnico de dominio CAE, fuente de los términos ya definidos.
- `docs/MULTITENANCY.md` § 1-2 — definición de Tenant y escenarios de negocio.
- Todos los documentos de `docs/business/` — cada uno es la fuente de desarrollo de los términos que le corresponden en la tabla de arriba.

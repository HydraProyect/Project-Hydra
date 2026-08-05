# CANONICAL_MODEL_DRAFT — Propuesta de modelo canónico para conectores Inbound (histórico)

**Tipo**: Operativo
**Estado**: Draft, **superseded en la práctica por `ARQUITECTURA-INTEGRACIONES.md`** — ver § "Por qué se conserva" antes de usar cualquier nombre o decisión de este documento.
**Propósito**: Registrar, con contexto, una propuesta externa de arquitectura de conectores que llegó junto al resto de la investigación de mercado. Se conserva porque el razonamiento agnóstico de proveedor es correcto y coincide con el que Hydra ya adoptó — pero los nombres, entidades y ADR de este documento **no son los de Hydra** y no deben usarse en código ni en documentación nueva.

## Qué pertenece aquí

- El contenido original de la propuesta (entidades canónicas, contrato de conector, roadmap por fases), con anotaciones señalando qué ya existe en Hydra con otro nombre y qué no se adoptó.

## Qué NO pertenece aquí

- Diseño real de conectores → `ARQUITECTURA-INTEGRACIONES.md` (documento normativo, con parte ya construida).
- Guía práctica para construir un conector → `docs/INTEGRATION_GUIDELINES.md`.

## Por qué se conserva este documento en vez de descartarlo

El razonamiento de fondo — "Hydra nunca debe conocer nombres propios de plataformas, cada conector traduce a un modelo propio, las capacidades se declaran y el orquestador decide en función de ellas, no de quién es el proveedor" — es **el mismo principio rector** que `ARQUITECTURA-INTEGRACIONES.md` § 1 ya adoptó de forma independiente y más concreta: *"Hydra no conoce Dokify; Hydra conoce Proveedores de Integración, y de cada proveedor conoce sus capacidades — no su nombre."* Eso confirma que la idea es sólida. Lo que no se adopta es la implementación propuesta aquí, porque Hydra ya tiene una versión más específica, en C#, con nombres en español coherentes con `CODING_STANDARDS.md`, y **parcialmente construida** (conectores reales de Microsoft 365 y WhatsApp, `ARQUITECTURA-INTEGRACIONES.md` § 12).

### ⚠️ Los "ADR-005"/"ADR-006" de este documento no son ADR de Hydra

Este documento usa numeración `ADR-005`/`ADR-006` para sus propias decisiones. **Esa numeración no tiene ninguna relación con la secuencia real de ADR de Hydra** (`ADR-001-multitenant-guia-tecnica.md`, `ADR-002-single-tenant.md` [superseded], `ADR-003-saas-multitenant.md`, `ADR-004-delegacion-consultoras-cae.md`). Si en el futuro se formaliza una decisión real sobre modelo canónico de conectores, le corresponde `ADR-005` en la secuencia real del repositorio — y su contenido sería el de `ARQUITECTURA-INTEGRACIONES.md`, no el de este documento.

## Equivalencia de conceptos: propuesta original → Hydra real

| Este documento (propuesta externa) | `ARQUITECTURA-INTEGRACIONES.md` (real, parcialmente construida) |
|---|---|
| `Platform` | `ProveedorIntegracion` (catálogo global) |
| `Connector` | `ConexionIntegracion` (instancia por tenant) + `IIntegrationProvider` (contrato de Application) |
| `Synchronization` | `SincronizacionIntegracion` |
| `Capabilities` (`SupportsWorkers`, `SupportsVehicles`, `SupportsWebhooks`...) | `CapacidadesIntegracion` ([Flags] enum: `Trabajadores`, `Vehiculos`, `Documentos`, `Webhooks`...) — mismo patrón, declarado por `VersionApiProveedor`, no por el proveedor en abstracto |
| Connector Contract (`Authenticate`, `Synchronize`, `HealthCheck`...) | `IIntegrationProvider` (`ValidarCredencialesAsync`, `SincronizarAsync`, `ManejarWebhookAsync`, `ComprobarSaludAsync`) |
| `Mapper` por plataforma | Adaptador por versión de API en `Infrastructure/Integraciones/Providers/` (p. ej. `DokifyV3IntegrationProvider`) |
| Motor de normalización (`Platform → Connector → Mapper → Canonical Model → Business Rules → Persistence`) | Mismo flujo, ya implementado para M365/WhatsApp: `Webhook → Integration Layer → Evento (MediatR.INotification) → handlers` |
| `Company`, `Worker`, `Vehicle`, `Document`, `Requirement`, `Incident` | `EntidadIntegracionDto` — forma universal derivada de los DTOs de lectura ya existentes de Cliente/Empresa/Trabajador/Documento (no un modelo paralelo nuevo) |

## Contenido original (resumen, sin las entidades ya cubiertas por la tabla anterior)

### Principios (coinciden con `ARQUITECTURA-INTEGRACIONES.md` § 1-2)

- **Platform Agnostic**: el núcleo del dominio nunca conoce nombres propios de proveedor (`Worker`, no `NalandaWorker`).
- **Connector Responsibility**: toda traducción pertenece al conector, nunca al dominio.
- **Canonical First**: todo dato entrante se convierte primero al modelo propio antes de aplicar reglas de negocio.
- **Lossless Mapping**: la conversión debe ser reversible cuando sea posible.

### Recomendaciones arquitectónicas (ya aplicadas en `ARQUITECTURA-INTEGRACIONES.md`)

- Nunca almacenar DTOs externos sin convertir primero.
- Los IDs externos nunca son clave primaria — se almacenan como identificador externo aparte (`MensajeExterno.MessageId`/`wamid` en el caso real de M365/WhatsApp).
- Separar modelo externo, modelo canónico, modelo de persistencia y modelo de lectura.
- Los conectores deben ser reemplazables sin afectar al núcleo del dominio.

### Roadmap original (histórico, no vigente)

| Fase (documento original) | Estado declarado allí | Estado real en Hydra |
|---|---|---|
| Fase 1 — Canonical Model | ✔ (declarado completo en el documento original) | El modelo real (`EntidadIntegracionDto`, `CapacidadesIntegracion`) existe en `ARQUITECTURA-INTEGRACIONES.md` § 3-4 |
| Fase 2 — Connector SDK | Pendiente | Contratos definidos (`IIntegrationProvider`/`IIntegrationProviderFactory`), sin SDK genérico construido — ver `ARQUITECTURA-INTEGRACIONES.md` § 13 |
| Fase 3 — Mapping Engine | Pendiente | No existe un motor genérico; cada conector real (M365, WhatsApp) mapea directamente contra su API, sin pasar por el framework genérico (decisión explícita, § 12.6 y § 12.7 de `ARQUITECTURA-INTEGRACIONES.md`) |
| Fase 4 — Synchronization Engine | Pendiente | Job de fondo con ámbito de tenant + `PerfilSincronizacion` ya diseñado (`ARQUITECTURA-INTEGRACIONES.md` § 6.3) |
| Fase 5 — Conflict Resolution | Pendiente | Sin diseño |
| Fase 6 — Auto Discovery | Pendiente | Sin diseño, sin caso de uso confirmado |
| Fase 7 — AI Mapping | Pendiente | Sin diseño, sin caso de uso confirmado |

## Documentos relacionados

- `ARQUITECTURA-INTEGRACIONES.md` — documento normativo real, prevalece sobre este en cualquier conflicto.
- `docs/INTEGRATION_GUIDELINES.md` — guía práctica para el primer conector real, cuando se priorice.
- `INBOUND_DOMAIN_GLOSSARY.md` — vocabulario de mercado que un conector real necesitaría mapear.

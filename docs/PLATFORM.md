# PLATFORM.md — Qué es Hydra realmente

**Estado**: Visión, no arquitectura técnica ni implementación. Este es el documento que responde una pregunta que ningún otro documento responde directamente: *¿qué es Hydra?* Sirve para que cualquier funcionalidad nueva se ubique correctamente (¿es kernel o es negocio?) y para que un servicio de plataforma no termine acoplado al dominio CAE por descuido. **Última pieza de la fase de consolidación documental** — ver § 6.

## 1. Hydra ya no es solo un gestor CAE

Hace unas semanas, Hydra era "la aplicación que gestiona Coordinación de Actividades Empresariales". Hoy, con la decisión SaaS multi-tenant (`ADR-003`) y el diseño de una Plataforma de Integraciones (`ARQUITECTURA-INTEGRACIONES.md`), Hydra es una **plataforma**: un núcleo de capacidades transversales (el *Platform Kernel*) sobre el que CAE es el primer módulo de negocio real — no el único que existirá necesariamente.

```
Hydra Platform
 │
 ├── Platform Kernel (capacidades transversales — no conocen CAE)
 │     ├── MultiTenant
 │     ├── Identity
 │     ├── Authorization
 │     ├── Integrations
 │     ├── AI
 │     ├── Notifications
 │     ├── Storage
 │     ├── Observability
 │     ├── Background Jobs
 │     ├── Feature Flags
 │     └── Licensing
 │
 └── Domain Modules (conocen el negocio, consumen el kernel)
       ├── CAE (existente: Cliente, Empresa, Subcontrata, Centro,
       │        Trabajador, Vehículo, Documento, Asignación, Visita...)
       └── Futuros, sin priorizar (backlog conceptual, no roadmap):
             Incidents, Training, PPE, Billing, Analytics...
```

## 2. El principio de separación

El Kernel **no conoce** ninguna entidad de CAE (`Cliente`, `Documento`...) — si una capacidad del kernel necesita saber "para quién" trabaja, es siempre por `TenantId`, nunca por una entidad de dominio. Los Domain Modules consumen el kernel a través de interfaces en `Application`, nunca instanciando directamente una librería de infraestructura concreta (`new BlobClient(...)` dentro de `Domain`/`Application` es exactamente lo que este principio prohíbe). Es la misma disciplina que el proyecto ya aplica y que las capacidades nuevas solo tienen que seguir, no inventar.

## 3. Catálogo del kernel — estado real, no aspiracional

| Capacidad | Estado hoy | Dónde está diseñada/implementada |
|---|---|---|
| **MultiTenant** | Diseñado, implementación pendiente | `ADR-003`, `docs/MULTITENANCY.md`, `INFORME-MULTITENANT.md`, `PLAN-MIGRACION-MULTITENANT.md` |
| **Identity** | Implementado (ASP.NET Core Identity + SSO Entra ID opcional) | `ARCHITECTURE.md` |
| **Authorization** | Implementado (roles + `IAlcanceDatosService` por cartera) | `DOMAIN.md`, `ARCHITECTURE.md` |
| **Integrations** | Diseñado, no implementado | `ARQUITECTURA-INTEGRACIONES.md`, `docs/INTEGRATION_GUIDELINES.md` |
| **AI** | Ya sigue el patrón correcto pero acoplado a un caso de uso concreto — ver § 4 | `IExtraccionTrabajadoresIaService` (Application) / `AnthropicExtraccionTrabajadoresIaService` (Infrastructure) |
| **Notifications** | Ya sigue el patrón correcto para dos canales — ver § 4 | `IEmailService`/`GraphEmailService`, `NotificacionUsuario` (interno) |
| **Storage** | Ya sigue el patrón correcto para un backend — ver § 4 | `IFileStorageService` (Application), disco local (Infrastructure) |
| **Observability** | Parcial (Sentry opcional, "inerte por defecto") | `ROADMAP.md` § Iniciativa de hardening |
| **Background Jobs** | Diseñado (ámbito de tenant explícito), sin infraestructura general de jobs todavía | `PLAN-MIGRACION-MULTITENANT.md` § 4.7 |
| **Feature Flags** | No existe — esbozo en § 4 | — |
| **Licensing** | No existe — esbozo en § 4 | — |

La columna "estado hoy" es deliberadamente honesta: varias capacidades **ya** siguen el patrón correcto (interfaz en Application, implementación intercambiable en Infrastructure) porque el proyecto ya lo aplicaba antes de que existiera este documento — `PLATFORM.md` no inventa la disciplina, la nombra y la generaliza donde hace falta.

## 4. Lo que falta generalizar (esbozo, no diseño completo — se detalla cuando haya un segundo caso real)

- **AI Provider**: hoy `IExtraccionTrabajadoresIaService` es específico de un caso de uso (detección de altas/bajas). El día que aparezca un segundo caso de uso de IA con un proveedor distinto (OpenAI, Azure OpenAI, Gemini, Mistral) o el mismo proveedor con otra capacidad, se generaliza a un `IAIProvider` con capacidades propias — mismo patrón exacto que `CapacidadesIntegracion` (`ARQUITECTURA-INTEGRACIONES.md` § 3.1). No se diseña en detalle todavía porque solo hay un caso de uso real (YAGNI).
- **Storage Provider**: `IFileStorageService` ya es la abstracción correcta. Generalizar a Blob/S3/Azure Storage/SharePoint es extender esa misma interfaz cuando exista necesidad real (ej. migración a almacenamiento cloud junto con PostgreSQL, ver `ADR-003`), no antes.
- **Notification Provider**: `IEmailService` (email) y `NotificacionUsuario` (interno) ya son dos canales desacoplados. SMS/WhatsApp/Teams/Slack se añaden como implementaciones nuevas de una abstracción de "canal de notificación" cuando haya un canal real que lo pida.
- **Feature Flags**: esto sí se introduce como concepto ahora (no en implementación) porque cada capacidad transversal nueva (IA, cada `ConexionIntegracion`, Billing el día que exista) necesita poder activarse/desactivarse por tenant sin `if`s dispersos. Modelo mínimo: `FeatureFlag` (catálogo global: `Codigo`, `Nombre`) + `TenantFeatureFlag` (`TenantId`, `FeatureFlagId`, `Habilitado`) + `IFeatureFlagService.EstaHabilitadoAsync(codigo)`, consumido igual que `IAlcanceDatosService` hoy. Una `ConexionIntegracion` habilitada implica su flag correspondiente habilitado, pero un flag puede existir sin integración detrás (ej. "IA" como capacidad general).
- **Licensing**: esbozo, no diseño — `Tenant → Plan (Starter/Professional/Enterprise) → Features incluidas (referencian `FeatureFlag`) → Límites` (nº usuarios, nº Centros, nº conexiones activas...). Importante: un límite de plan (ej. "máximo 5 Centros") **no** es un middleware oculto de plataforma — es una regla de negocio real que vive donde ya viven las demás (el handler de `CrearCentroCommand`, protegida como cualquier invariante de dominio, `CODING_STANDARDS.md`). No se documentan planes/precios concretos aquí — es una decisión de negocio a confirmar aparte, igual que RGPD (`CLAUDE.md`). **TODO**: cuando existan planes/precios reales, viven en `docs/business/PRICING.md` (fuente oficial) — este documento solo referencia esa decisión, nunca fija cifras.

## 5. Pregunta de orientación para cualquier sesión futura

Antes de escribir un servicio nuevo que no sea puramente de negocio CAE: **¿esto es un módulo de dominio o una capacidad del kernel?** Si es del kernel, ¿ya existe una interfaz general (§ 3) o hace falta generalizar una existente (§ 4)? Nunca acoplar un Domain Module directamente a una librería de infraestructura concreta.

## 6. Cierre de la fase de documentación

Con este documento se completa la disciplina **Dominio → Arquitectura → Plataforma → Implementación** (`CLAUDE.md`) para el kernel completo, y con ella la fase de consolidación documental de `ADR-003` (consolidación → aprobación → plan de migración → **implementación** → validación). Recomendación explícita, y criterio compartido: **aquí termina la fase de documentación**. Las decisiones que quedan abiertas (planes comerciales concretos, módulos de negocio futuros, proveedores concretos de IA/Storage/Notification) se documentan cuando exista una necesidad real que las fuerce, no antes — seguir ampliando documentación sin código real que la valide es el riesgo de parálisis por análisis que este mismo documento existe para evitar, no para cometer. Siguiente paso: implementación de multi-tenant según `PLAN-MIGRACION-MULTITENANT.md`.

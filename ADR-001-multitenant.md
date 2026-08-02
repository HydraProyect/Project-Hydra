# ADR-001 — Modelo multi-tenant

> ✅ **REACTIVADO (2026-07-23) por `ADR-003-saas-multitenant.md`.**
> La pausa de 2026-07-18 (`ADR-002`) quedó superseded: la vía SaaS multi-tenant es de nuevo el objetivo del producto y se implementa in-place en este repositorio. Este ADR vuelve a ser la **guía técnica vigente** de implementación (modelo `TenantId` por fila, filtro global, interceptor de sellado, índices compuestos). El documento normativo de multi-tenancy es `docs/MULTITENANCY.md`; la auditoría de queries de abajo (39 en su momento) quedó desactualizada ya una vez — pasó a 46 (ver `docs/archive/INFORME-MULTITENANT.md` § 2.5) y hoy son más de 80 (`find src/CaeManager.Application -iname "*Query.cs" | wc -l`, verificado 2026-08-02) — el número concreto no importa: lo que protege el filtro global es "todas, cualquiera que sea la cifra actual", ver `docs/MULTITENANCY.md`.

**Estado**: Decidido (2026-07-17) · en pausa 2026-07-18 (`ADR-002`) · **reactivado 2026-07-23 (`ADR-003`)**.

## Decisión

CAE Manager se construye como **SaaS multi-cliente**: el destino del producto no es una única organización de uso interno, sino un producto vendible a varios clientes PRL independientes entre sí, cada uno con su propio conjunto de Clientes/Empresas/Trabajadores/Documentos.

Esto no debe confundirse con la entidad `Cliente` que ya existe en el dominio (la empresa a la que CAE Manager presta servicio de coordinación de actividades empresariales). El tenant es un nivel **por encima** de eso: cada tenant es una organización PRL contratante de CAE Manager, y dentro de un tenant pueden existir muchos `Cliente` en el sentido actual del dominio.

## Modelo elegido: `TenantId` por fila, no base de datos por tenant

Dos opciones evaluadas:

| | `TenantId` en cada tabla + filtro global | Base de datos separada por tenant |
|---|---|---|
| Coste de implementación | Bajo — extensión natural del patrón que ya existe | Alto — requiere enrutamiento de conexión, aprovisionamiento por tenant, migraciones N veces |
| Aislamiento | Lógico (a nivel de query) | Físico (a nivel de proceso/archivo) |
| Coste operativo (backups, migraciones) | Uno solo, para todos los tenants | Se multiplica por cada tenant |
| Encaja con el stack actual | Sí — EF Core Global Query Filters, ya en uso para soft-delete | No — SQLite de archivo único no está pensado para N bases vivas por proceso |

Se elige **`TenantId` en cada tabla con Global Query Filter**, por la misma razón que ya se adoptó `EstaEliminado` + `HasQueryFilter` para soft-delete (ver `CentroConfiguration.cs`, `ClienteConfiguration.cs`, etc. en `Infrastructure/Persistence/Configurations/`): es el mismo mecanismo de EF Core, ya probado en este código base, aplicado a una segunda dimensión de filtrado. Añadir `TenantId` es una extensión de un patrón existente, no una reescritura de la capa de datos.

Cuando el volumen de un tenant individual crezca lo suficiente para justificarlo, migrar ese tenant a una base de datos propia sigue siendo posible más adelante — la capa `Infrastructure` ya está diseñada para que el proveedor/conexión sea intercambiable (ver `ARCHITECTURE.md`). No es una decisión que haya que acertar a la primera.

## El punto crítico a auditar antes de implementar

Hoy **ninguna** query de `Application/*/Queries` filtra por un `TenantId` global — todas confían en filtros explícitos por `ClienteId`/`EmpresaId`/etc., reforzados desde la Fase 31 por `IAlcanceDatosService` (alcance por **rol dentro de un mismo tenant**: qué Clientes ve un Gestor CAE, un Coordinador, etc.).

Esto es una capa de aislamiento distinta y **no sustituye** al aislamiento por tenant: `IAlcanceDatosService` decide qué ve un usuario *dentro* de su organización; un futuro filtro de tenant tendría que decidir qué fila pertenece a *qué organización*, antes de que el alcance por rol entre en juego. Sin `TenantId`, dos organizaciones clientes de CAE Manager compartirían literalmente todas las filas de todas las tablas, aisladas solo por lo que cada Query decida filtrar manualmente — un solo query nuevo sin ese filtro sería una fuga de datos entre tenants.

## Auditoría de `Application/*/Queries` (2026-07-17)

Revisadas las 39 queries del proyecto. Hallazgo principal: **ninguna usa SQL crudo** (`FromSqlRaw`/`ExecuteSqlRaw`/`FromSqlInterpolated`) ni `IgnoreQueryFilters()` — todo el acceso a datos pasa por LINQ sobre `IApplicationDbContext`, que es exactamente lo que hace seguro el mecanismo ya probado con `EstaEliminado`: un `HasQueryFilter(x => x.TenantId == tenantActual)` aplicado en cada `*Configuration.cs` (igual que hoy `HasQueryFilter(x => !x.EstaEliminado)`) protegería automáticamente **las 39 queries a la vez**, sin tener que tocar cada handler uno por uno — 17 de ellas ya usan `IAlcanceDatosService` para el alcance por rol, y las otras 22 (búsquedas por Id, selectores de catálogo, auditoría, importación, reportes) quedarían cubiertas igual por el filtro global, sin cambio de código en la capa Application.

Esto simplifica bastante la implementación futura: no es "auditar y modificar 39 queries", es "añadir un filtro global + un servicio que resuelva el tenant actual" (mismo patrón que `ICurrentUserService`/`IAlcanceDatosService`), y confirmar que **nada nuevo** empiece a usar SQL crudo o `IgnoreQueryFilters()` sin pasar por revisión.

Dos puntos que sí necesitan trabajo explícito, no cubiertos por el filtro de lectura:

1. **Sellado de `TenantId` en escritura.** El filtro global protege lecturas; en creación, cada entidad necesita su `TenantId` asignado — lo más seguro es un `SaveChanges` interceptor (mismo lugar donde hoy probablemente se sellan campos de auditoría) en vez de confiar en que cada Command lo pase explícito, por la misma razón que `AutorizacionEscrituraBehavior` (Fase 31) se implementó como pipeline behavior y no repitiendo la validación en cada Command.

2. **Índices únicos hoy globales que deberían pasar a ser únicos *por tenant*** — siete encontrados, y el más relevante para el dominio de negocio:
   - `Cliente.Cif`, `Empresa.Cif`, `Empresa.RazonSocial`, `Subcontrata.RazonSocial`, `TipoDocumento.Nombre`, `Trabajador.Dni`, `Vehiculo.NumeroPlaca` — todos con `IsUnique()` simple hoy.
   - Caso de negocio real, no solo teórico: una misma Empresa (mismo CIF real) o un mismo Trabajador (mismo DNI real) puede legítimamente ser cliente de **dos organizaciones PRL distintas** que ambas usan CAE Manager — hoy el índice único global lo bloquearía como si fuera un duplicado dentro del mismo tenant. Todos estos índices deben pasar a compuestos `(TenantId, Cif)`, `(TenantId, Dni)`, etc. como parte del mismo cambio que añade `TenantId`, no después.

## Implicación en billing/self-signup

No se invierte en autoservicio de alta de tenants ni en facturación mientras el aislamiento de datos por tenant no esté implementado y auditado — vender acceso self-service a un producto sin ese aislamiento sería el error más caro posible de cometer primero.

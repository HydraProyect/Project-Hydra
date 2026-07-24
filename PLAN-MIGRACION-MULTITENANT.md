# Plan de migración detallado — Multi-tenancy (`TenantId`)

**Estado**: Plan de ejecución (Fase 3 de la secuencia de `ADR-003-saas-multitenant.md`). Desarrolla a nivel de pasos concretos las etapas 0–5 de `INFORME-MULTITENANT.md` § 12. **No implementado** — este documento se aprueba antes de escribir la primera migración EF Core.

Prerequisito ya cumplido: documentación consolidada (`ADR-003`, `docs/MULTITENANCY.md`, `DOMAIN.md`, y `PROJECT.md`/`ROADMAP.md`/`CLAUDE.md`/`ARCHITECTURE.md`/`DATABASE.md` actualizados).

---

## 0. Alcance exacto (qué toca cada etapa)

### 0.1 Tablas afectadas (25 `*Configuration.cs`, verificado en el código)

Todas reciben `TenantId`, salvo las anotadas:

`Cliente`, `Centro`, `PlataformaAcceso`, `Empresa`, `EmpresaCliente`, `CredencialAccesoEmpresa`, `Subcontrata`, `SubcontrataCliente`, `SubcontrataEmpresa`, `CredencialAccesoSubcontrata`, `Trabajador`, `DeteccionTrabajador`, `Vehiculo`, `TipoDocumento`, `TipoDocumentoCentro`, `ConfiguracionIaDocumentoCliente`, `Documento`, `Asignacion`, `Visita`, `VisitaTrabajador`, `RequisitoDocumental`, `Alerta`, `NotificacionUsuario`, `ParametroSistema`, `RegistroAuditoria`.

Más, fuera de `*Configuration.cs` porque vive en Identity: `AspNetUsers` (`ApplicationUser`).

**Sin `TenantId`** (confirmado en `docs/MULTITENANCY.md` § 4.1 y § 7): `AspNetRoles`, `AspNetUserRoles`, `AspNetUserClaims`, etc. (particionadas por usuario, que ya lleva tenant), y la propia tabla `Tenant`.

### 0.2 Nueva tabla

`Tenant`: `Id` (Guid, PK), `Nombre` (string), `Estado` (enum: Activo/Suspendido), `CreadoEnUtc` (datetime). Sin más campos en v1 (billing/plan quedan fuera, YAGNI — `ADR-001`).

### 0.3 Índices únicos que cambian (los 7 de `ADR-001`, confirmados en el código hoy)

| Tabla | Índice hoy | Índice destino |
|---|---|---|
| `Cliente` | `Cif` | `(TenantId, Cif)` |
| `Empresa` | `Cif` | `(TenantId, Cif)` |
| `Empresa` | `RazonSocial` | `(TenantId, RazonSocial)` |
| `Subcontrata` | `RazonSocial` | `(TenantId, RazonSocial)` |
| `TipoDocumento` | `Nombre` | `(TenantId, Nombre)` |
| `Trabajador` | `Dni` | `(TenantId, Dni)` |
| `Vehiculo` | `NumeroPlaca` | `(TenantId, NumeroPlaca)` |

Índices únicos de tablas de unión (ya compuestos hoy) se les antepone `TenantId` igual: `EmpresaCliente (EmpresaId, ClienteId)` → `(TenantId, EmpresaId, ClienteId)`, y así con `SubcontrataCliente`, `SubcontrataEmpresa`, `TipoDocumentoCentro`, `ConfiguracionIaDocumentoCliente`, `VisitaTrabajador`, `Asignacion (TrabajadorId, CentroId, FechaAlta)`, `CredencialAccesoEmpresa.EmpresaId` (único), `CredencialAccesoSubcontrata.SubcontrataId` (único), `PlataformaAcceso.CentroId` (único).

**Excepción deliberada, sin cambio**: `AspNetUsers.NormalizedUserName`/`NormalizedEmail` — quedan **globalmente únicos**, no `(TenantId, ...)`. Es la limitación v1 ya documentada en `docs/MULTITENANCY.md` § 8 (resolución de tenant por claim, no por subdominio): el login necesita resolver el usuario por email antes de conocer el tenant, así que dos tenants no pueden compartir el mismo email de login todavía. No tocar este índice en ninguna etapa.

### 0.4 `ParametroSistema`: cambio de semántica, no solo de esquema

Hoy es fila única global (umbrales 30/15). Pasa a una fila por tenant. Esto es el único cambio conceptual real (`INFORME-MULTITENANT.md` § 14) — se trata en la Etapa 2 como parte del backfill, no como un `ADD COLUMN` más.

---

## 1. Etapa 0 — Ensayo (obligatoria, bloquea todo lo siguiente)

1. **Backup verificado** de la base de datos de producción (SQLite: copia de archivo + `PRAGMA integrity_check` sobre la copia).
2. **Restauración de prueba** en un entorno aislado, arrancando la app contra esa copia para confirmar que el backup es realmente restaurable (no solo que el archivo existe).
3. **Ensayo completo de las Etapas 1–4** (de este plan) contra esa copia, de principio a fin, antes de tocar producción.
4. **Ampliar `CaeManager.IntegrationTests` / `MigracionesTests`** con: (a) aplicar todas las migraciones nuevas contra una BD SQLite de archivo temporal vacía y verificar que no falla; (b) un test de "backfill" que parte de una BD con datos sintéticos (fixture) representativos de cada tabla de § 0.1 y verifica que tras la Etapa 2 ninguna fila tiene `TenantId` nulo.
5. **Criterio de salida de la Etapa 0**: el ensayo completo (pasos 1–4) se ejecuta sin intervención manual además de los comandos documentados aquí, y el test de backfill pasa en CI.

No se avanza a la Etapa 1 sin este criterio cumplido.

---

## 2. Etapa 1 — Esquema aditivo (nullable, cero impacto funcional)

✅ **Completa y validada (2026-07-23)**. `Tenant` (agregado raíz) + `EntidadConTenant` (nueva base intermedia entre `Entity` y `EntidadBase`, con `TenantId` nullable) en Domain; las 16 clases que heredaban directo de `Entity` pasan a heredar de `EntidadConTenant`; `ApplicationUser.TenantId` en Identity; `TenantConfiguration` + `DbSet<Tenant>`/`IApplicationDbContext.Tenants` en Infrastructure/Application; migración `AgregarTenantNullable` (25 tablas: las 24 de Domain + `AspNetUsers`, más la tabla `Tenants` nueva). Validado con: `dotnet build -warnaserror` (0/0), `dotnet format --verify-no-changes` (limpio), `CaeManager.Domain.Tests` 119/119, `CaeManager.Application.Tests` 39/39, `CaeManager.IntegrationTests` 22/24 (los 2 fallos son el mismo problema preexistente de LibreOffice no instalado en el entorno de desarrollo, no relacionado con este cambio — ver `ROADMAP.md`), `CaeManager.Web.Tests` 7/7. Tests nuevos: `TenantTests` (10, Domain) y 3 en `MigracionesTests` (Integration: persistencia de `Tenant`, `TenantId` nulo por defecto sin interceptor todavía, columna nullable verificada por `PRAGMA table_info`).

Objetivo: desplegable sin que nada del comportamiento actual cambie.

1. **Migración EF Core `AgregarTenant`**: crea la tabla `Tenant` (Domain: `src/CaeManager.Domain/Tenants/Tenant.cs`, agregado raíz nuevo, con repositorio propio como el resto — ver `ADR-003`/`DOMAIN.md` § Agregados raíz).
2. **Migración EF Core `AgregarTenantIdNullable`**: añade `TenantId` (`Guid?`, sin índice todavía salvo los ya existentes que se dejan intactos) a las 24 tablas de § 0.1 más `AspNetUsers`. Una sola migración para las 24+1 tablas (no una por tabla) — reduce el número de despliegues intermedios.
3. **`ParametroSistemaConfiguration`**: se quita el carácter singleton (si hoy fuerza `HasData`/PK fija) y se prepara para múltiples filas — sin `TenantId` todavía obligatorio, ver Etapa 2.
4. **Deploy**. Sin cambios de comportamiento: nadie lee ni escribe `TenantId` todavía. Verificación: la app sigue funcionando exactamente igual, `dotnet ef database update` no falla, `MigracionesTests` en verde.

---

## 3. Etapa 2 — Backfill (crear el tenant #1 y sellar todas las filas)

✅ **Completa y validada (2026-07-23)**. `TenantSeedData` (Id fijo `00000000-0000-0000-0000-000000000001`, nombre placeholder "Organización principal" — a renombrar tras el despliegue real con `Tenant.RenombrarA`) sembrado vía `HasData` en `TenantConfiguration`. Migración `SembrarTenantPorDefecto`: `InsertData` del tenant (autogenerado por el diff de `HasData`) + `UPDATE` de backfill escrito a mano (SQL crudo controlado dentro de la migración, sobre las 25 tablas de § 0.1) `WHERE TenantId IS NULL`, con su reverso simétrico en `Down()`. Validado con: build `-warnaserror` (0/0), `dotnet format` limpio, `Domain.Tests` 119/119, `Application.Tests` 39/39, `Web.Tests` 7/7, `IntegrationTests` 26/28 (los 2 fallos son el mismo problema preexistente de LibreOffice, no relacionado). Tests nuevos y decisivos: `BackfillTenantPorDefectoTests` — migra solo hasta `AgregarTenantNullable`, inserta filas sintéticas con `TenantId` NULL (simulando datos de producción **preexistentes**, no una base vacía), aplica `SembrarTenantPorDefecto`, y confirma que quedan selladas al tenant por defecto; más 3 tests en `MigracionesTests` verificando que el catálogo (`TipoDocumento`, `ParametroSistema`) también queda sellado.

1. **Migración/script de datos `SembrarTenantPorDefecto`**: inserta una fila en `Tenant` (`Nombre` = nombre de la organización actual, `Estado` = Activo). Este Id se referencia como *tenant por defecto* durante el resto de la etapa.
2. **`UPDATE` por tabla** (una sentencia por cada una de las 24+1 tablas de § 0.1): `SET TenantId = @tenantPorDefecto WHERE TenantId IS NULL`. Se ejecuta dentro de la migración EF Core (no un script suelto fuera de control de versiones), para que quede en el historial de migraciones igual que cualquier otro cambio de esquema.
3. **`ParametroSistema`**: en vez de un `UPDATE`, la fila única existente pasa a llevar `TenantId = tenantPorDefecto` (sigue siendo una sola fila, ahora perteneciente a un tenant — el resto de tenants futuros la sembrarán al aprovisionarse, ver § 6).
4. **`TipoDocumento`**: los 15+ tipos semilla existentes pasan a pertenecer al tenant por defecto (mismo `UPDATE`). A partir de aquí son **su** catálogo editable — un tenant nuevo recibe una copia de esta plantilla al aprovisionarse (§ 6), no una referencia compartida.
5. **Verificación obligatoria antes de continuar**: query de recuento `SELECT COUNT(*) FROM <tabla> WHERE TenantId IS NULL` para las 25 tablas debe devolver 0 en todas. Se automatiza como el test de la Etapa 0 punto 4, ejecutado ahora contra la BD real post-backfill (no solo contra el fixture sintético).
6. **Deploy**. Sigue sin haber filtro activo — el `TenantId` ya está sellado en todas las filas, pero todavía no se usa para nada. Comportamiento observable: ninguno.

---

## 4. Etapa 3 — Cierre (el único deploy con riesgo real)

✅ **Completa y validada (2026-07-23)** — implementada y probada en esta sesión (entorno de desarrollo/CI; la aplicación contra el entorno de producción real queda pendiente del propio despliegue, ver condiciones de `ADR-003`). Piezas construidas:

- `ITenantActual` (Application, síncrono — EF Core lo necesita así dentro de `HasQueryFilter`) + `TenantActualAmbiental` (Infrastructure, settable — jobs de fondo/migraciones/tests) + `TenantActual` (Web, lee el claim `tenant_id` vía `AuthenticationStateProvider`, mismo patrón que `CurrentUserService`).
- `TenantClaimsPrincipalFactory` (`IUserClaimsPrincipalFactory<ApplicationUser>`): añade el claim `tenant_id` al construir el `ClaimsPrincipal` en el login (desde `user.TenantId`, ya en memoria — sin consulta adicional, sin riesgo de recursión con el filtro global porque `AspNetUsers` queda deliberadamente **sin** filtro de tenant, ya que el login necesita resolver el usuario antes de conocer su tenant).
- `TenantSelladoInterceptor`: sella `TenantId` en toda entidad `Added` desde `ITenantActual` (lanza si no hay tenant resuelto — fallo cerrado) y rechaza `Modified`/`Deleted` de una entidad de otro tenant.
- Filtro global **centralizado** en `CaeManagerDbContext.OnModelCreating` (no repartido en los 25 `*Configuration.cs`) — 9 agregados combinan soft-delete + tenant, 16 tablas de unión/satélite solo tenant. Se retiraron los `HasQueryFilter` sueltos de soft-delete de las 9 configuraciones (quedarían reemplazados igualmente; centralizarlo hace visible de un vistazo que ninguno se perdió).
- `EntidadConTenant.TenantId` y `ApplicationUser.TenantId`: `Guid?` → `Guid` (NOT NULL). `HasData` de `TipoDocumento`/`ParametroSistema` con `TenantId` explícito. `IdentitySeeder`/`DatosPruebaSeeder` sellan `TenantId` al crear usuarios.
- Migración `CerrarTenantId`: `AlterColumn` a NOT NULL en las 25 tablas + los 16 índices únicos compuestos (7 simples + 9 de unión/satélite) con `TenantId` primero. `dotnet ef migrations has-pending-model-changes` confirma cero deriva de modelo tras aplicarla.

**Tests decisivos** (`AislamientoMultiTenantTests`, nuevo — dos `CaeManagerDbContext` sobre el mismo archivo SQLite, cada uno con su propio tenant, exactamente el escenario real): un Cliente creado por el tenant A es invisible para B y visible para A; lo mismo para una entidad `EntidadConTenant` sin soft-delete (Alerta); el interceptor sella el tenant sin que el código lo asigne; crear sin tenant resuelto lanza; modificar una entidad de otro tenant (cargada vía `IgnoreQueryFilters` justificado) lanza; el mismo CIF en dos tenants se permite, duplicado en el mismo tenant se rechaza por el índice compuesto. Más los tests existentes de `MigracionesTests`/`AlcancePorIdTests`/`DeteccionTrabajadoresServiceTests` actualizados para el nuevo constructor de `CaeManagerDbContext` (necesitaban registrar el interceptor manualmente, al no pasar por `AddInfrastructure`).

Validado con: build `-warnaserror` (0/0), `dotnet format --verify-no-changes` (limpio), `dotnet ef migrations has-pending-model-changes` (sin cambios pendientes), `Domain.Tests` 119/119, `Application.Tests` 39/39, `Web.Tests` 7/7, `IntegrationTests` 32/34 (los 2 fallos son el mismo problema preexistente de LibreOffice, no relacionado).

Este es el paso que ADR-002 quería evitar con el fork; se ejecuta con la red de seguridad de las Etapas 0–2 ya completadas.

1. **Migración EF Core `CerrarTenantId`**: `TenantId` pasa de `Guid?` a `Guid` (NOT NULL) en las 25 tablas. En SQLite esto es un table-rebuild gestionado por EF Core (recrea la tabla, copia datos, renombra) — es exactamente la operación que se ensayó en la Etapa 0 y por la que la copia de backup es indispensable aquí, no antes.
2. **Misma migración o la siguiente**: sustituir los 7 índices únicos simples + los índices únicos compuestos existentes por sus versiones con `TenantId` primero (§ 0.3).
3. **`ITenantActual`** (Application, nueva interfaz) + implementación en Infrastructure que resuelve el tenant desde el claim `tenant_id` de la sesión (`docs/MULTITENANCY.md` § 8) — mismo patrón que `ICurrentUserService`. Se registra en DI antes de activar el filtro (paso siguiente), nunca al revés.
4. **Filtro global combinado** en cada una de las 25 `*Configuration.cs`: `HasQueryFilter(x => !x.EstaEliminado && x.TenantId == tenantActual)` — **sustituyendo**, no añadiendo, el `HasQueryFilter` existente de soft-delete (EF Core solo permite uno por entidad; añadir un segundo silenciosamente desactivaría el de soft-delete — riesgo ya señalado en `docs/MULTITENANCY.md` § 4.2, verificar explícitamente en revisión de código que las 9 configuraciones que hoy ya tienen `HasQueryFilter` para soft-delete quedan con el filtro combinado, no duplicado).
5. **Interceptor `TenantSellauditInterceptor`** (o ampliar `AuditoriaInterceptor` existente en `src/CaeManager.Infrastructure/Auditing/`, a decidir en implementación cuál es más consistente con el patrón ya usado) en `SaveChanges`: sella `TenantId = tenantActual` en toda entidad `Added`, y **rechaza** (excepción) cualquier entidad `Modified`/`Deleted` cuyo `TenantId` en BD no coincida con `tenantActual` — defensa adicional a la del filtro de lectura, para el caso de una entidad cargada por otra vía (raro, pero barato de proteger).
6. **Login**: al autenticar (local y SSO), estampar el claim `tenant_id` desde `ApplicationUser.TenantId` en la cookie de Identity.
7. **Jobs de fondo** (generación de alertas, notificaciones, detección IA — identificar los `IHostedService`/jobs programados existentes): se modifican para iterar tenants activos y abrir un ámbito de `ITenantActual` explícito por cada uno, nunca ejecutar sin tenant resuelto (`docs/MULTITENANCY.md` § 8.4).
8. **Deploy único**, fuera de horario de uso si es posible, con el backup de la Etapa 0 fresco (repetir backup justo antes, no reutilizar el de la Etapa 0). Este es el paso que convierte "sellado pero sin usar" en "aislamiento activo".

**Plan de rollback de esta etapa**: si tras el deploy aparece un problema, el rollback es restaurar el backup pre-Etapa-3 (las Etapas 1–2 son aditivas y no necesitan deshacerse) — no se intenta un rollback de esquema en caliente sobre una tabla ya recreada NOT NULL. Por eso el backup inmediatamente anterior a esta etapa, no el de la Etapa 0, es el que realmente importa operativamente.

---

## 5. Etapa 4 — Archivos

✅ **Punto 1 completo y validado (2026-07-23)**. ⬜ **Punto 2 diferido explícitamente** (ver nota).

1. ✅ `IFileStorageService`/`DiskFileStorageService`: las rutas nuevas se escriben bajo `{tenantId}/...` (carpeta = `Guid.ToString("N")`). Registro de DI cambiado de `Singleton` a `Scoped` — dependía implícitamente de nada, ahora depende de `ITenantActual` (scoped), y un singleton con una dependencia scoped sería una dependencia cautiva (capturaría el primer tenant resuelto para siempre). `GuardarAsync` sin tenant resuelto lanza (fallo cerrado, mismo criterio que el interceptor). `AbrirAsync` valida que el segmento de tenant del identificador coincide con el tenant actual — un identificador de otro tenant, aunque se conozca exacto, se comporta como "no existe" (`FileNotFoundException`), mismo principio que el fix IDOR del Issue #18 para Ids de entidades. Sanea cada segmento de ruta por separado (protección contra path traversal, reforzada respecto a la versión anterior).
2. ⬜ **Diferido**: el comando de mantenimiento que migraría archivos preexistentes a la carpeta del tenant por defecto no se construye todavía — no hay ningún archivo real que migrar (esta sesión no tiene acceso a la base de datos de producción real, y un despliegue nuevo/vacío no tiene archivos planos que mover). Se construye cuando se ejecute la migración real contra producción (junto con las Etapas 0–3, con el mismo criterio de ensayo sobre copia). Diseño ya acordado en el punto 2 original de esta sección — no repetido aquí, sigue vigente como especificación de lo que hay que construir en ese momento.

**Tests** (`DiskFileStorageServiceTests`, nuevo): guarda bajo la carpeta del tenant; el propio tenant puede reabrir lo que guardó; **otro tenant no puede abrirlo aunque conozca el identificador exacto**; sin tenant resuelto no se puede guardar ni abrir nada; un identificador con intento de path traversal no escapa de la carpeta del tenant. Validado con: build `-warnaserror` (0/0), `dotnet format` limpio, `Domain.Tests` 119/119, `Application.Tests` 39/39, `Web.Tests` 7/7, `IntegrationTests` 38/40 (los 2 fallos son el mismo problema preexistente de LibreOffice, no relacionado).

---

## 6. Aprovisionamiento de un tenant nuevo (queda diseñado aquí, se construye cuando haya un segundo tenant real — YAGNI, no antes)

Caso de uso que las Etapas 1–4 dejan preparado pero no implementan como flujo de producto (no hay UI de alta de tenant en v1, ver `ADR-001` — sin self-signup):

1. Insertar fila en `Tenant`.
2. Copiar la plantilla de `TipoDocumento` (los 15+ tipos, tal como estaban en el momento de crear el tenant o una plantilla mantenida aparte — a decidir en el momento) al nuevo `TenantId`.
3. Sembrar `ParametroSistema` (umbrales 30/15 por defecto) para el nuevo tenant.
4. Crear el primer `ApplicationUser` Administrador del tenant.
5. En ningún paso se referencia ni se copia dato de otro tenant.

---

## 7. Etapa 5 — Verificación y cierre ✅ (completada 2026-07-24)

1. ✅ **Tests de aislamiento por agregado** (`AislamientoPorAgregadoTests`, `CaeManager.IntegrationTests`): un `[Fact]` por cada uno de los 25 tipos con `TenantId` — crea la entidad en el tenant A y verifica que una Query filtrada por el tenant B nunca la devuelve. 25/25 en verde, queda en CI de forma permanente.
2. ✅ **Test del interceptor** (`AislamientoMultiTenantTests`): sellado automático de `TenantId` en alta; intento de modificar/eliminar una entidad de otro tenant (vía `IgnoreQueryFilters()` explícito y justificado, para poder llegar hasta la fila) lanza excepción; sin tenant resuelto, ni lectura ni escritura — fallo cerrado.
3. ✅ **Test de índices** (`AislamientoMultiTenantTests`): el mismo `Cif` en dos tenants distintos se permite; el mismo `Cif` dos veces en el mismo tenant falla con `DbUpdateException` — ambos lados del índice compuesto verificados.
4. ✅ **Verificación end-to-end en navegador real** (`AislamientoMultiTenantE2ETests`, `CaeManager.E2ETests`, Playwright/Chromium contra el binario real de `CaeManager.Web`): dos sesiones de navegador con contextos independientes, tenant A crea un Cliente con nombre único y lo ve en su propio listado; tenant B (sembrado opcionalmente por `SegundoTenantSeeder`, activado vía `SegundoTenant:Activo`) no lo ve, y su listado de Clientes está vacío pese a los ~200 Clientes de datos de prueba sembrados para el tenant A. 2/2 en verde.
   - **Hallazgo real de esta verificación** (exactamente el tipo de regresión que esta etapa existe para atrapar, no detectada por ningún test de integración): al arrancar la app real, `DatosPruebaSeeder` fallaba con `InvalidOperationException` porque no hay sesión HTTP/circuito de Blazor durante el arranque, así que `ITenantActual` (implementación Web, basada en claim de sesión) resolvía a `null`. Corregido con `AmbitoTenantExplicito` (ámbito de tenant explícito basado en `AsyncLocal`, ver `docs/MULTITENANCY.md` § 8.4), usado tanto en la siembra de arranque (`Program.cs`) como en `SegundoTenantSeeder`. Verificado arrancando el proceso real dos veces (con y sin segundo tenant) y con la suite completa de tests E2E existente en verde.
5. ✅ **Cierre**: `ROADMAP.md` actualizado marcando la fase multi-tenant como completada.

---

## 8. Fuera de alcance de este plan (deuda ya identificada, no se aborda aquí)

SSO por tenant, subdominios, migración a PostgreSQL, DPA/Términos de Uso por tenant, self-signup/billing, cuotas de IA por tenant — todos son condiciones de salida a producción SaaS (`ADR-003`) o backlog (`INFORME-MULTITENANT.md` § 16), con su propio plan cuando corresponda. Este plan cubre exclusivamente el aislamiento de datos por `TenantId`.

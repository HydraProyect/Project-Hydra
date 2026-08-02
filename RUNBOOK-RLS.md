# Runbook — activar Row-Level Security de PostgreSQL en producción

Este documento cubre el punto P2 #21 de `docs/business/MATURITY_REVIEW.md`: Row-Level Security (RLS) como segunda línea de aislamiento por tenant, bajo el filtro global de EF Core (ver `docs/MULTITENANCY.md` § 4).

## Qué hace ya el código, sin que nadie toque nada en Railway

La migración `HabilitarRlsPostgres` (`src/CaeManager.Migrations.PostgreSQL/Migrations/20260801120000_HabilitarRlsPostgres.cs`), aplicada automáticamente al arrancar como cualquier otra migración:

1. Habilita `ROW LEVEL SECURITY` con `FORCE` en las 40 tablas que llevan `TenantId` (las mismas de `CaeManagerDbContext.OnModelCreating` — ni una más ni una menos). `ClavesApi` (P3-29) se creó después de esta migración y se quedó fuera de la lista hasta `20260802165602_HabilitarRlsClavesApi`, que la añade con la misma política — son 41 tablas en total desde entonces.
2. Crea una política `aislamiento_tenant` por tabla: solo son visibles/escribibles las filas cuyo `TenantId` coincide con la variable de sesión `app.tenant_id`.
3. Crea el rol `cae_app_runtime` — **sin contraseña (`NOLOGIN`)**, así que hoy no lo usa nadie — con `NOSUPERUSER` y sin `BYPASSRLS`, y le concede los privilegios de DML (`SELECT`/`INSERT`/`UPDATE`/`DELETE`) sobre todas las tablas, incluidas las que no llevan `TenantId` (Identity, `Tenants`, `DelegacionesTenant`...).
4. `TenantRlsConnectionInterceptor` (`src/CaeManager.Infrastructure/Persistence/Interceptors/`) fija `app.tenant_id` en cada conexión abierta, leyendo el mismo `ITenantActual` que ya usa el filtro global de EF.

**Con esto solo, RLS está construido pero inerte**: PostgreSQL nunca aplica RLS al propietario de la tabla ni a un superusuario, y hoy la aplicación conecta con ese rol (`ConnectionStrings:CaeManagerDb`, el mismo para migraciones y para runtime). Las políticas existen, están correctamente probadas (ver `AislamientoRlsPostgresTests`), pero no restringen nada todavía en producción hasta el paso siguiente.

## Lo que falta — un cambio de credenciales real, deliberadamente no automatizado

Rotar la conexión de runtime a un rol restringido es un cambio sobre la base de datos de producción con un secreto nuevo. No vive en una migración (un secreto no pertenece al código fuente) ni se ejecuta solo — lo hace quien tenga acceso real a Railway:

1. **Dar login al rol** (elige una, ambas terminan igual):
   - Convertir `cae_app_runtime` en rol de login: `ALTER ROLE cae_app_runtime LOGIN PASSWORD '<contraseña generada, larga y aleatoria>';`
   - O crear un rol de login aparte y hacerlo miembro: `CREATE ROLE cae_app_runtime_login LOGIN PASSWORD '...'; GRANT cae_app_runtime TO cae_app_runtime_login;` (más fácil de rotar sin tocar los `GRANT` de tabla, que quedan en el rol de grupo).
2. **Configurar `ConnectionStrings__CaeManagerDbRuntime` en Railway** (variable de entorno, mismo mecanismo que el resto de secretos — ver `DEPLOY.md`) con la cadena de conexión que usa ese rol de login. `ConnectionStrings__CaeManagerDb` **no cambia** — sigue siendo el rol propietario, y sigue siendo el único que usan las migraciones al arrancar (ver `Program.cs`, bloque de `MigrateAsync`).
3. **Desplegar y verificar**:
   - El arranque debe completar las migraciones sin error (usan `CaeManagerDb`, sin cambios).
   - Una vez arriba, confirmar que las consultas normales siguen funcionando (usan ahora `CaeManagerDbRuntime`).
   - Confirmar que RLS filtra de verdad: `SET ROLE cae_app_runtime; SELECT count(*) FROM "Clientes";` sin haber fijado `app.tenant_id` antes debe devolver `0` filas aunque existan datos.
4. **Guardar la contraseña** en el gestor de secretos que use el equipo (no en ningún documento de este repositorio).

## Por qué no se automatiza más

- Este entorno de desarrollo no tiene acceso a la base de datos de producción de Railway — no se puede ejecutar este paso desde aquí, solo dejarlo documentado y ejecutable en dos comandos.
- Rotar una credencial de producción es una acción de las que este proyecto trata como "confirmar antes de actuar" (ver `CLAUDE.md`), no algo que deba ocurrir como efecto colateral de aplicar una migración.
- Mientras `CaeManagerDbRuntime` no esté configurado, el comportamiento de la aplicación es exactamente el de antes de esta migración — cero riesgo de romper el arranque por desplegar esto.

## Revertir

`dotnet ef database update <migración anterior a HabilitarRlsPostgres>` ejecuta el `Down()`: quita las políticas, desactiva RLS y borra el rol `cae_app_runtime`. Si ya se completó la activación de este runbook, hay que quitar antes la membresía/login del paso 1 (`DROP ROLE` falla si el rol tiene dependientes) y volver a apuntar `CaeManagerDbRuntime` a `CaeManagerDb` o eliminar la variable.

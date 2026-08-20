using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Rol de base de datos <c>cae_app_soporte</c>: el enforcement de solo
    /// lectura del plano 3 (ADR-011 § 4bis.7.4), en la capa de datos y no en la
    /// de aplicación.
    ///
    /// <b>Por qué en la base y no solo en el pipeline.</b> Hasta ahora la
    /// promesa "una sesión de soporte no escribe" la sostenía
    /// <c>AutorizacionEscrituraBehavior</c>, un behavior de MediatR. Eso cubre
    /// los Commands, que es por donde pasa toda la escritura interactiva de
    /// hoy — pero es una lista que hay que acordarse de respetar: un repositorio
    /// llamado directamente desde un componente, un endpoint que haga
    /// <c>SaveChanges</c> por su cuenta, un job futuro. El propio ADR lo dice:
    /// el solo-lectura no puede colgar de un único behavior. Con este rol la
    /// escritura falla en Postgres, y ahí no hay lista que respetar.
    ///
    /// <b>Qué NO es este rol.</b> No sustituye a <c>cae_app_runtime</c>, que es
    /// el rol restringido con el que la aplicación debería conectar siempre
    /// (rotación pendiente, es un paso de operación). Son ortogonales:
    /// <c>cae_app_runtime</c> quita el bypass de RLS, y este además quita la
    /// escritura. Se activa por sesión con <c>SET ROLE</c>, no por cadena de
    /// conexión, porque la condición que lo dispara —que la petición venga por
    /// una sesión privilegiada— cambia de una petición a la siguiente.
    ///
    /// <b><c>NOBYPASSRLS</c> es la propiedad que de verdad importa</b>, igual
    /// que en <c>cae_app_runtime</c>: sin ella el rol ignoraría las políticas de
    /// aislamiento por tenant, y el solo-lectura habría comprado una fuga mucho
    /// peor que la que evita.
    ///
    /// Sin privilegios sobre secuencias a propósito, y no es un olvido de
    /// simetría con <c>cae_app_runtime</c>: un rol que no inserta no necesita
    /// generar identificadores, y concederlo sería dejar una puerta entreabierta
    /// sin ningún caso de uso detrás.
    ///
    /// Pura DDL de servidor: no cambia el modelo de EF, así que no hay diffs que
    /// aplicar en el snapshot.
    /// </summary>
    public partial class RolSoporteSoloLectura : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    -- Mismo BEGIN/EXCEPTION que HabilitarRlsPostgres, y por el mismo motivo:
    -- los roles son objetos de CLUSTER, no de base de datos. Con una base
    -- efímera por clase de test, dos migraciones concurrentes pueden pasar las
    -- dos el IF NOT EXISTS antes de que ninguna confirme, y la perdedora
    -- reventaría con un duplicate key sobre pg_authid_rolname_index en vez de
    -- un error legible.
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cae_app_soporte') THEN
            CREATE ROLE cae_app_soporte NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
        END IF;
    EXCEPTION WHEN duplicate_object OR unique_violation THEN
        NULL;
    END;

    GRANT USAGE ON SCHEMA public TO cae_app_soporte;
    GRANT SELECT ON ALL TABLES IN SCHEMA public TO cae_app_soporte;

    -- Cubre las tablas que creen migraciones futuras sin tener que volver aquí.
    -- Solo SELECT: una tabla nueva nace legible para el soporte y no escribible,
    -- que es la postura correcta por defecto.
    ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO cae_app_soporte;
END $$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cae_app_soporte') THEN
        ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE SELECT ON TABLES FROM cae_app_soporte;
        REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM cae_app_soporte;
        REVOKE USAGE ON SCHEMA public FROM cae_app_soporte;
        -- Si el entorno concedió la membresía a un rol de login real
        -- (GRANT cae_app_soporte TO ...), hay que revocarla a mano antes de que
        -- DROP ROLE pueda completarse: ese rol de login no existe en el código
        -- fuente y no se puede adivinar desde aquí.
        DROP ROLE cae_app_soporte;
    END IF;
END $$;
");
        }
    }
}

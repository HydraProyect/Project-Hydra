using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Segunda línea de aislamiento por tenant bajo el filtro global de EF
    /// Core (ver docs/MULTITENANCY.md § 4.2 y RUNBOOK-RLS.md, P2 #21 de
    /// docs/business/MATURITY_REVIEW.md). Habilita Row-Level Security con
    /// FORCE en las 40 tablas que llevan TenantId (ver
    /// CaeManagerDbContext.OnModelCreating — las mismas 15+25 de la lista de
    /// HasQueryFilter, ni una más ni una menos) y crea el rol restringido
    /// <c>cae_app_runtime</c> que RUNBOOK-RLS.md documenta cómo activar en
    /// producción. No toca AspNetUsers/Tenants/DelegacionesTenant/
    /// AsignacionesOperadorDelegado: son catálogos globales o de Identity sin
    /// TenantId por diseño (docs/MULTITENANCY.md § 7-8), igual que en el
    /// filtro de EF.
    ///
    /// Pura DDL/SQL de servidor: no cambia el modelo de EF (ninguna entidad
    /// ni propiedad nueva), así que no hay diffs que aplicar en
    /// CaeManagerDbContextModelSnapshot.cs.
    /// </summary>
    public partial class HabilitarRlsPostgres : Migration
    {
        // Las mismas 40 tablas que builder.Entity&lt;T&gt;().HasQueryFilter(...)
        // en CaeManagerDbContext.OnModelCreating (15 con soft delete + 25
        // solo-tenant). Toda tabla nueva con TenantId añade su nombre aquí en
        // su propia migración, igual que añade su línea de HasQueryFilter.
        private static readonly string[] TablasConTenant =
        [
            "Clientes", "Centros", "Documentos", "Empresas", "RequisitosDocumentales", "Subcontratas",
            "Trabajadores", "Vehiculos", "Visitas", "Proyectos", "Evaluaciones", "Incidencias",
            "TarifasCliente", "ConversacionesCorreo", "MacrosRespuesta",
            "Alertas", "Asignaciones", "RegistrosAuditoria", "CanalesGestionDocumental", "ParametrosSistema",
            "ConfiguracionesIaDocumentoCliente", "TiposDocumento", "TiposDocumentoCentros",
            "CredencialesAccesoEmpresa", "EmpresasClientes", "NotificacionesUsuario",
            "CredencialesAccesoSubcontrata", "SubcontratasClientes", "SubcontratasEmpresas",
            "DeteccionesTrabajador", "ExtraccionesIaCache", "AuditoriasExtraccionIa", "VisitasTrabajadores",
            "RevisionesIaDocumento", "ProyectosTecnicos", "AprobacionesDocumento", "MensajesCorreo",
            "ParticipantesConversacion", "RegistrosActividadSoporte", "SolicitudesPurga",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var arrayTablas = string.Join(",", System.Array.ConvertAll(TablasConTenant, t => $"'{t}'"));

            migrationBuilder.Sql($@"
DO $$
DECLARE
    tabla text;
BEGIN
    -- NOLOGIN: sin contraseña, no se puede usar para conectar tal cual. Es
    -- deliberado — RUNBOOK-RLS.md documenta cómo GRANT-earlo a un rol de
    -- login real (o convertirlo en uno) sin que ningún secreto viva en el
    -- código fuente. NOBYPASSRLS es la propiedad que de verdad importa: sin
    -- ella, este rol (como cualquier rol con BYPASSRLS o un superusuario)
    -- ignoraría las políticas de más abajo igual que hoy las ignora el rol
    -- propietario.
    --
    -- El IF NOT EXISTS por sí solo no basta: cae_app_runtime es un rol de
    -- CLUSTER (pg_roles/pg_authid es catálogo compartido por todo el
    -- cluster, no por base de datos), así que si esta migración se aplica
    -- en paralelo contra varias bases de datos de test del mismo cluster
    -- (patrón habitual: una base efímera por clase de test), dos
    -- transacciones pueden pasar el NOT EXISTS a la vez antes de que
    -- ninguna confirme el CREATE ROLE, y la segunda revienta con un
    -- duplicate key value violates unique constraint pg_authid_rolname_index
    -- en vez de un error legible — visto reproducido en CI de PR #49
    -- (E2E con exit 134 y 70+ tests de AislamientoPorAgregadoTests caídos
    -- porque la app no arrancaba). BEGIN/EXCEPTION aquí sí es atómico
    -- frente a esa carrera: si otra transacción ganó la carrera, esta
    -- captura el duplicado y sigue sin fallar.
    BEGIN
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cae_app_runtime') THEN
            CREATE ROLE cae_app_runtime NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
        END IF;
    EXCEPTION WHEN duplicate_object OR unique_violation THEN
        NULL;
    END;

    FOREACH tabla IN ARRAY ARRAY[{arrayTablas}]
    LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY;', tabla);
        -- FORCE: sin esto, RLS tampoco restringiría al propietario de la
        -- tabla (el rol con el que migran hoy todos los entornos) — solo a
        -- roles ajenos. Con FORCE, la única forma de que un acceso NO se
        -- filtre es ser superusuario (nunca lo será cae_app_runtime) o tener
        -- el atributo BYPASSRLS (tampoco).
        EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY;', tabla);
        EXECUTE format('DROP POLICY IF EXISTS aislamiento_tenant ON %I;', tabla);
        -- NULLIF(..., '')::uuid da NULL cuando TenantRlsConnectionInterceptor
        -- fija la cadena vacía (sin tenant resuelto) — NULL no iguala ningún
        -- TenantId real, así que la fila queda oculta: mismo fallo cerrado
        -- que ITenantActual.TenantId devolviendo null (ver
        -- docs/MULTITENANCY.md § 4.5). WITH CHECK aplica la misma regla a
        -- INSERT/UPDATE, como refuerzo de TenantSelladoInterceptor.
        EXECUTE format(
            'CREATE POLICY aislamiento_tenant ON %I USING (""TenantId"" = NULLIF(current_setting(''app.tenant_id'', true), '''')::uuid) WITH CHECK (""TenantId"" = NULLIF(current_setting(''app.tenant_id'', true), '''')::uuid);',
            tabla);
    END LOOP;

    -- El resto del esquema (Identity, Tenants, DelegacionesTenant,
    -- AsignacionesOperadorDelegado...) no lleva TenantId ni RLS por diseño,
    -- pero cae_app_runtime necesita seguir pudiendo leerlo/escribirlo igual
    -- que el rol propietario hoy. ALTER DEFAULT PRIVILEGES cubre además las
    -- tablas que creen migraciones futuras sin tener que acordarse de volver
    -- aquí a conceder permisos cada vez.
    GRANT USAGE ON SCHEMA public TO cae_app_runtime;
    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO cae_app_runtime;
    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO cae_app_runtime;
    ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO cae_app_runtime;
    ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO cae_app_runtime;
END $$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var arrayTablas = string.Join(",", System.Array.ConvertAll(TablasConTenant, t => $"'{t}'"));

            migrationBuilder.Sql($@"
DO $$
DECLARE
    tabla text;
BEGIN
    FOREACH tabla IN ARRAY ARRAY[{arrayTablas}]
    LOOP
        EXECUTE format('DROP POLICY IF EXISTS aislamiento_tenant ON %I;', tabla);
        EXECUTE format('ALTER TABLE %I NO FORCE ROW LEVEL SECURITY;', tabla);
        EXECUTE format('ALTER TABLE %I DISABLE ROW LEVEL SECURITY;', tabla);
    END LOOP;

    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'cae_app_runtime') THEN
        ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM cae_app_runtime;
        ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE USAGE, SELECT ON SEQUENCES FROM cae_app_runtime;
        REVOKE ALL PRIVILEGES ON ALL TABLES IN SCHEMA public FROM cae_app_runtime;
        REVOKE ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public FROM cae_app_runtime;
        REVOKE USAGE ON SCHEMA public FROM cae_app_runtime;
        -- Si RUNBOOK-RLS.md se siguió en este entorno, un rol de login real
        -- es miembro de cae_app_runtime (GRANT cae_app_runtime TO ...) — hay
        -- que revocar esa membresía a mano antes de que DROP ROLE pueda
        -- completarse; no se automatiza aquí porque ese rol de login no
        -- existe en el código fuente (RUNBOOK-RLS.md § provisión).
        DROP ROLE cae_app_runtime;
    END IF;
END $$;
");
        }
    }
}

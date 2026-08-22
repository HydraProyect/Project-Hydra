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
    -- El rol lo provee el BOOTSTRAP DE CLÚSTER
    -- (deploy/bootstrap/roles-de-cluster.sql), no esta migración.
    --
    -- pg_authid es un catálogo compartido: crear un rol desde la migración de
    -- UNA base es un error de nivel, y no era teórico. Seis migradores
    -- entraban aquí a la vez y tres fallaban con 42704 dentro de este mismo
    -- bloque, en la sentencia siguiente a la creación protegida: tragarse el
    -- duplicate_object no garantiza que el rol sea utilizable a continuación.
    --
    -- Lo que queda abajo son privilegios sobre objetos de ESTA base, que sí le
    -- corresponden. Si el bootstrap no se ejecutó, esto falla con un 42704
    -- inmediato e idéntico en todos los migradores: un contrato incumplido
    -- debe romper siempre, no a veces.

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
        -- El rol NO se borra aquí. Es un objeto de clúster: destruirlo desde
        -- el Down de UNA base se lo quitaría a todas las demás del mismo
        -- clúster. Si crear es responsabilidad del bootstrap, destruir
        -- tampoco puede pertenecer a la migración de una base.
    END IF;
END $$;
");
        }
    }
}

using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarOrdenMenuLateral : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrdenMenuLateral",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrdenGrupos = table.Column<List<string>>(type: "text[]", nullable: false),
                    OrdenEnlaces = table.Column<List<string>>(type: "text[]", nullable: false),
                    ActualizadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActualizadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrdenMenuLateral", x => x.Id);
                    table.CheckConstraint("CK_OrdenMenuLateral_FilaUnica", "\"Id\" = '0dde0000-0000-4000-8000-00000000e4a1'");
                });

            // ── RLS: tabla de plataforma, quinta categoría ──────────────────
            //
            // Precedente: EstadoBootstrapPlataforma. Fila única del SISTEMA, sin
            // TenantId: no pertenece a ningún Tenant y no se aísla por tenant.
            // Lo que se protege no es quién la ve (el orden del menú no es un
            // secreto ni una autoridad: cada usuario sigue viendo solo lo que su
            // rol le permite) sino quién la ESCRIBE.
            //
            //   LECTURA   todos, desde cualquier Tenant (USING true).
            //   ESCRITURA solo el Actor de Plataforma TALVEG con una concesión
            //             AdminPlataforma GLOBAL vigente. Es el mismo predicado que
            //             IAutorizacionAdminPlataforma.PuedeGlobalmenteAsync, que es
            //             la barrera en Application; esto la repite debajo.
            //   BORRADO   sin política: ningún rol sujeto a RLS puede borrar la
            //             fila. "Restablecer" es guardar listas vacías.
            //
            // app_es_admin_plataforma (20260916203428) NO sirve: no mira
            // EsAlcanceGlobal, y una concesión acotada a un Tenant no puede
            // decidir algo que ven todos los Tenants. De ahí la función nueva.
            //
            // CON FORCE, como el bootstrap: sin él la política no ataría al
            // propietario de la tabla. No toca ninguna política de las tablas de
            // Tenant.
            migrationBuilder.Sql(@"
CREATE FUNCTION app_es_admin_plataforma_global(usuario uuid) RETURNS boolean
  LANGUAGE sql STABLE SECURITY DEFINER
  SET search_path = pg_catalog, pg_temp AS $$
  SELECT EXISTS (SELECT 1 FROM public.""ConcesionesPrivilegio"" c
    WHERE c.""UsuarioPlataformaId"" = usuario AND c.""Capacidad"" = 'AdminPlataforma'
      AND c.""EsAlcanceGlobal""
      AND c.""Estado"" = 'Vigente' AND c.""VigenciaDesde"" <= now()
      AND (c.""VigenciaHasta"" IS NULL OR now() < c.""VigenciaHasta""));
$$;
REVOKE ALL ON FUNCTION app_es_admin_plataforma_global(uuid) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app_es_admin_plataforma_global(uuid) TO cae_app_runtime;

ALTER TABLE ""OrdenMenuLateral"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""OrdenMenuLateral"" FORCE ROW LEVEL SECURITY;

CREATE POLICY orden_menu_lectura_de_todos ON ""OrdenMenuLateral""
    FOR SELECT
    USING (true);

CREATE POLICY orden_menu_alta_por_admin_plataforma_global ON ""OrdenMenuLateral""
    FOR INSERT
    WITH CHECK (app_es_admin_plataforma_global(NULLIF(current_setting('app.usuario_id', true), '')::uuid));

CREATE POLICY orden_menu_cambio_por_admin_plataforma_global ON ""OrdenMenuLateral""
    FOR UPDATE
    USING (app_es_admin_plataforma_global(NULLIF(current_setting('app.usuario_id', true), '')::uuid))
    WITH CHECK (app_es_admin_plataforma_global(NULLIF(current_setting('app.usuario_id', true), '')::uuid));
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrdenMenuLateral");

            migrationBuilder.Sql("DROP FUNCTION IF EXISTS app_es_admin_plataforma_global(uuid);");
        }
    }
}

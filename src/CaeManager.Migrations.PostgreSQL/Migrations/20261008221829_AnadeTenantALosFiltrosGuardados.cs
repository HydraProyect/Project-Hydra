using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Los filtros guardados pasan a pertenecer al Tenant en el que se guardan
    /// (decisión D4 del 2026-10-08): clave (Tenant, Usuario, Pantalla, Nombre),
    /// con el mismo aislamiento que el resto de tablas por Tenant.
    ///
    /// <para>
    /// <b>Las filas que había se borran.</b> No llevaban Tenant y no hay forma
    /// fiable de deducirlo: un Gestor CAE con Asignación de Cartera pudo guardar
    /// el filtro estando en cualquiera de sus Tenants, y los identificadores que
    /// guarda (Empresa, Gestor CAE) solo significan algo en aquel. Son
    /// preferencias que el usuario rehace en segundos y todavía no hay datos de
    /// cliente real; adivinar el Tenant dejaría filtros que no filtran nada.
    /// </para>
    /// </summary>
    public partial class AnadeTenantALosFiltrosGuardados : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FiltrosGuardados_UsuarioId_Pantalla",
                table: "FiltrosGuardados");

            // Tabla vacía antes de añadir la columna: así entra NOT NULL sin un
            // valor por defecto que después habría que retirar.
            migrationBuilder.Sql("""DELETE FROM public."FiltrosGuardados";""");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "FiltrosGuardados",
                type: "uuid",
                nullable: false);

            migrationBuilder.CreateIndex(
                name: "IX_FiltrosGuardados_TenantId_UsuarioId_Pantalla_Nombre",
                table: "FiltrosGuardados",
                columns: new[] { "TenantId", "UsuarioId", "Pantalla", "Nombre" },
                unique: true);

            // Mismo aislamiento que el resto de tablas por Tenant (CoberturaRlsDelModeloTests): RLS + FORCE +
            // política aislamiento_tenant. Los permisos de la tabla ya los concedió la línea base.
            migrationBuilder.Sql(
                """
                ALTER TABLE public."FiltrosGuardados" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."FiltrosGuardados" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."FiltrosGuardados" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS aislamiento_tenant ON public."FiltrosGuardados";
                ALTER TABLE ONLY public."FiltrosGuardados" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."FiltrosGuardados" DISABLE ROW LEVEL SECURITY;
                """);

            migrationBuilder.DropIndex(
                name: "IX_FiltrosGuardados_TenantId_UsuarioId_Pantalla_Nombre",
                table: "FiltrosGuardados");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "FiltrosGuardados");

            migrationBuilder.CreateIndex(
                name: "IX_FiltrosGuardados_UsuarioId_Pantalla",
                table: "FiltrosGuardados",
                columns: new[] { "UsuarioId", "Pantalla" });
        }
    }
}

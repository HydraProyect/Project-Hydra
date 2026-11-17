using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeArchivosPendientesDeReferencia : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArchivosPendientesDeReferencia",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Clave = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    GuardadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArchivosPendientesDeReferencia", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArchivosPendientesDeReferencia_TenantId_Clave",
                table: "ArchivosPendientesDeReferencia",
                columns: new[] { "TenantId", "Clave" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ArchivosPendientesDeReferencia_TenantId_GuardadoEnUtc",
                table: "ArchivosPendientesDeReferencia",
                columns: new[] { "TenantId", "GuardadoEnUtc" });

            // Outbox de archivos guardados antes de su Documento o versión de plantilla (issue #2123): las filas son del
            // Tenant propietario de los datos, con la misma política por Tenant que el resto de sus tablas. El runtime las
            // apunta, las lee para barrer y las borra (alta confirmada o barrido); nunca las cambia, así que no hay UPDATE.
            // Ni Soporte TALVEG ni aprovisionamiento tienen acceso.
            migrationBuilder.Sql("""
                ALTER TABLE public."ArchivosPendientesDeReferencia" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."ArchivosPendientesDeReferencia" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."ArchivosPendientesDeReferencia"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."ArchivosPendientesDeReferencia" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT, DELETE ON TABLE public."ArchivosPendientesDeReferencia" TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArchivosPendientesDeReferencia");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeSolicitudesExportacionCompleta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SolicitudesExportacionCompleta",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SolicitadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    SolicitadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Estado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ContrasenaProtegida = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    UbicacionFichero = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TamanoBytes = table.Column<long>(type: "bigint", nullable: true),
                    IniciadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DisponibleEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CaducaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinalizadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MotivoFallo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolicitudesExportacionCompleta", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesExportacionCompleta_TenantId_Estado_SolicitadaEn~",
                table: "SolicitudesExportacionCompleta",
                columns: new[] { "TenantId", "Estado", "SolicitadaEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesExportacionCompleta_TenantId_Viva",
                table: "SolicitudesExportacionCompleta",
                column: "TenantId",
                unique: true,
                filter: "\"Estado\" IN ('Pendiente', 'EnCurso')");

            // Exportación completa en autoservicio (#2008, D-8): tabla del Tenant propietario con RLS forzada y la
            // política de aislamiento. Solo cae_app_runtime la usa (la solicitud, la descarga y el servicio de fondo,
            // todos bajo el contexto del propio Tenant). Soporte TALVEG no tiene nada que leer aquí: la fila guarda
            // la contraseña del zip protegida y la ubicación del fichero, así que no se le concede ni SELECT.
            migrationBuilder.Sql("""
                ALTER TABLE public."SolicitudesExportacionCompleta" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."SolicitudesExportacionCompleta" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."SolicitudesExportacionCompleta"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."SolicitudesExportacionCompleta" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT, UPDATE ON TABLE public."SolicitudesExportacionCompleta" TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SolicitudesExportacionCompleta");
        }
    }
}

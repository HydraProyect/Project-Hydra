using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeUsuariosControlAccesoCentro : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "AspNetRoles",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[] { new Guid("30000000-0000-0000-0000-000000000007"), "30000000-0000-0000-0000-000000000007", "ControlAcceso", "CONTROLACCESO" });
            migrationBuilder.CreateTable(
                name: "UsuariosControlAccesoCentro",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsuariosControlAccesoCentro", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsuariosControlAccesoCentro_Centros_TenantId_CentroId",
                        columns: x => new { x.TenantId, x.CentroId },
                        principalTable: "Centros",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsuariosControlAccesoCentro_Centro",
                table: "UsuariosControlAccesoCentro",
                columns: new[] { "TenantId", "CentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_UsuariosControlAccesoCentro_Unico",
                table: "UsuariosControlAccesoCentro",
                columns: new[] { "TenantId", "UsuarioId", "CentroId" },
                unique: true);

            // RLS forzada por Tenant propietario y privilegios mínimos. Aditiva: una tabla nueva
            // que la ranura anterior no lee. El runtime no actualiza filas (solo asigna y retira
            // Centros); soporte solo lee; el rol de aprovisionamiento no toca la tabla.
            migrationBuilder.Sql("""
                ALTER TABLE public."UsuariosControlAccesoCentro" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."UsuariosControlAccesoCentro" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."UsuariosControlAccesoCentro"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON public."UsuariosControlAccesoCentro"
                    FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT, DELETE ON public."UsuariosControlAccesoCentro" TO cae_app_runtime;
                GRANT SELECT ON public."UsuariosControlAccesoCentro" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AspNetRoles",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000007"));
            migrationBuilder.DropTable(
                name: "UsuariosControlAccesoCentro");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class CierreMensualTenantBeneficiario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CierresMensualesTenantBeneficiario",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Anyo = table.Column<int>(type: "integer", nullable: false),
                    Mes = table.Column<int>(type: "integer", nullable: false),
                    FotoJson = table.Column<string>(type: "jsonb", nullable: false),
                    Estimado = table.Column<bool>(type: "boolean", nullable: false),
                    CerradoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReabiertoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MotivoReapertura = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CierresMensualesTenantBeneficiario", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CierresMensualesTenantBeneficiario_TenantId_Anyo_Mes",
                table: "CierresMensualesTenantBeneficiario",
                columns: new[] { "TenantId", "Anyo", "Mes" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE public."CierresMensualesTenantBeneficiario" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."CierresMensualesTenantBeneficiario" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."CierresMensualesTenantBeneficiario"
                    USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
                    WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CierresMensualesTenantBeneficiario" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."CierresMensualesTenantBeneficiario" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CierresMensualesTenantBeneficiario");
        }
    }
}

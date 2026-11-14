using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeCobrosLiquidacionOperadorCae : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CobrosLiquidacionOperadorCae",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PropietarioTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Anyo = table.Column<int>(type: "integer", nullable: false),
                    Mes = table.Column<int>(type: "integer", nullable: false),
                    Estado = table.Column<int>(type: "integer", nullable: false),
                    ReferenciaFacturaEmisor = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    MarcadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    MarcadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CobrosLiquidacionOperadorCae", x => x.Id);
                    table.CheckConstraint("CK_CobrosLiquidacionOperadorCae_EstadoConocido", "\"Estado\" IN (1, 2, 3)");
                    table.CheckConstraint("CK_CobrosLiquidacionOperadorCae_MesValido", "\"Mes\" BETWEEN 1 AND 12");
                    table.CheckConstraint("CK_CobrosLiquidacionOperadorCae_OperadorNoEsElTenantBeneficiar~", "\"PropietarioTenantId\" <> \"TenantId\"");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CobrosLiquidacionOperadorCae_TenantId_PropietarioTenantId_A~",
                table: "CobrosLiquidacionOperadorCae",
                columns: new[] { "TenantId", "PropietarioTenantId", "Anyo", "Mes" },
                unique: true);

            // Las marcas de cobro son del Operador CAE externo que emite la liquidación (TenantId): el Tenant
            // beneficiario (PropietarioTenantId) no las lee. El runtime solo consulta, crea y actualiza la marca (estado,
            // referencia, quién y cuándo la puso y el token de concurrencia; nunca el Tenant, el Operador ni el mes); nunca la borra (la retirada de un Tenant de demostración usa su propia identidad). Soporte no tiene acceso.
            migrationBuilder.Sql("""
                ALTER TABLE public."CobrosLiquidacionOperadorCae" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."CobrosLiquidacionOperadorCae" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."CobrosLiquidacionOperadorCae"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."CobrosLiquidacionOperadorCae" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT ON TABLE public."CobrosLiquidacionOperadorCae" TO cae_app_runtime;
                GRANT UPDATE ("Estado", "ReferenciaFacturaEmisor", "MarcadaPorUsuarioId", "MarcadaEnUtc", "Version")
                    ON TABLE public."CobrosLiquidacionOperadorCae" TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CobrosLiquidacionOperadorCae");
        }
    }
}

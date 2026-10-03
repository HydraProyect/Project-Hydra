using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class ToleranciaDeAccesoPorCentroYClienteEmpresarial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ToleranciaDias",
                table: "TiposDocumentoCentros",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ToleranciasDocumentoClienteEmpresarial",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteEmpresarialId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToleranciaDias = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ToleranciasDocumentoClienteEmpresarial", x => x.Id);
                    table.CheckConstraint("CK_ToleranciasDocumentoClienteEmpresarial_ToleranciaDias", "\"ToleranciaDias\" >= 0 AND \"ToleranciaDias\" <= 365");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_TiposDocumentoCentros_ToleranciaDias",
                table: "TiposDocumentoCentros",
                sql: "\"ToleranciaDias\" IS NULL OR (\"ToleranciaDias\" >= 0 AND \"ToleranciaDias\" <= 365)");

            migrationBuilder.CreateIndex(
                name: "IX_ToleranciasDocumentoClienteEmpresarial_TenantId_ClienteEmpr~",
                table: "ToleranciasDocumentoClienteEmpresarial",
                columns: new[] { "TenantId", "ClienteEmpresarialId", "TipoDocumentoId" },
                unique: true);

            // Mismo aislamiento que el resto de tablas tenantizadas (CoberturaRlsDelModeloTests): RLS + FORCE +
            // política aislamiento_tenant, y los permisos de sus vecinas (ConfiguracionesIaDocumentoCliente). Migración
            // aditiva: no toca datos ni políticas existentes.
            migrationBuilder.Sql(
                """
                ALTER TABLE public."ToleranciasDocumentoClienteEmpresarial" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."ToleranciasDocumentoClienteEmpresarial" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."ToleranciasDocumentoClienteEmpresarial" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."ToleranciasDocumentoClienteEmpresarial" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."ToleranciasDocumentoClienteEmpresarial" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ToleranciasDocumentoClienteEmpresarial");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TiposDocumentoCentros_ToleranciaDias",
                table: "TiposDocumentoCentros");

            migrationBuilder.DropColumn(
                name: "ToleranciaDias",
                table: "TiposDocumentoCentros");
        }
    }
}

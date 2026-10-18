using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadePlataformaCaeDeClienteEmpresarial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccesosPlataformaCaeEmpresa",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteEmpresarialId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Usuario = table.Column<string>(type: "text", nullable: true),
                    Contrasena = table.Column<string>(type: "text", nullable: true),
                    ContrasenaCambiadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ContrasenaCambiadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccesosPlataformaCaeEmpresa", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccesosPlataformaCaeEmpresa_Empresas_TenantId_ClienteEmpres~",
                        columns: x => new { x.TenantId, x.ClienteEmpresarialId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AccesosPlataformaCaeEmpresa_Empresas_TenantId_EmpresaId",
                        columns: x => new { x.TenantId, x.EmpresaId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlataformasCaeClienteEmpresarial",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteEmpresarialId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UrlPortal = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CodigoCliente = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlataformasCaeClienteEmpresarial", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlataformasCaeClienteEmpresarial_Empresas_TenantId_ClienteE~",
                        columns: x => new { x.TenantId, x.ClienteEmpresarialId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccesosPlataformaCaeEmpresa_TenantId_ClienteEmpresarialId_E~",
                table: "AccesosPlataformaCaeEmpresa",
                columns: new[] { "TenantId", "ClienteEmpresarialId", "EmpresaId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccesosPlataformaCaeEmpresa_TenantId_EmpresaId",
                table: "AccesosPlataformaCaeEmpresa",
                columns: new[] { "TenantId", "EmpresaId" });

            migrationBuilder.CreateIndex(
                name: "IX_PlataformasCaeClienteEmpresarial_TenantId_ClienteEmpresaria~",
                table: "PlataformasCaeClienteEmpresarial",
                columns: new[] { "TenantId", "ClienteEmpresarialId" },
                unique: true);

            // Aislamiento por Tenant, mismo patrón que el resto de tablas con TenantId
            // (CoberturaRlsDelModeloTests). La tabla de accesos guarda credenciales de
            // terceros: la Sesión Privilegiada de soporte (cae_app_soporte) no la lee ni
            // siquiera cifrada; la de la plataforma (nombre, portal, código) sí, como el
            // resto del Tenant.
            migrationBuilder.Sql("""
                ALTER TABLE "PlataformasCaeClienteEmpresarial" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "PlataformasCaeClienteEmpresarial" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON "PlataformasCaeClienteEmpresarial"
                    USING ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                GRANT SELECT, INSERT, UPDATE, DELETE ON "PlataformasCaeClienteEmpresarial" TO cae_app_runtime;
                GRANT SELECT ON "PlataformasCaeClienteEmpresarial" TO cae_app_soporte;

                ALTER TABLE "AccesosPlataformaCaeEmpresa" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "AccesosPlataformaCaeEmpresa" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON "AccesosPlataformaCaeEmpresa"
                    USING ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                GRANT SELECT, INSERT, UPDATE, DELETE ON "AccesosPlataformaCaeEmpresa" TO cae_app_runtime;
                REVOKE ALL ON "AccesosPlataformaCaeEmpresa" FROM cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccesosPlataformaCaeEmpresa");

            migrationBuilder.DropTable(
                name: "PlataformasCaeClienteEmpresarial");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarSolicitudConexionMicrosoft365 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SolicitudesConexionMicrosoft365",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioSolicitanteId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: true),
                    GestorPropietarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    FechaExpiracionUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolicitudesConexionMicrosoft365", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesConexionMicrosoft365_FechaExpiracionUtc",
                table: "SolicitudesConexionMicrosoft365",
                column: "FechaExpiracionUtc");

            // RLS en la misma migración que crea la tabla: es EntidadConTenant, y
            // los ratchets CoberturaRlsDelModeloTests/PoliticasRlsCubrenModeloTests
            // exigen política para toda tabla con TenantId. El filtro global de EF
            // ya la protege; esto es la segunda línea, sobre cae_app_runtime
            // (RUNBOOK-RLS.md).
            migrationBuilder.Sql(@"
ALTER TABLE ""SolicitudesConexionMicrosoft365"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""SolicitudesConexionMicrosoft365"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""SolicitudesConexionMicrosoft365"";
CREATE POLICY aislamiento_tenant ON ""SolicitudesConexionMicrosoft365""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SolicitudesConexionMicrosoft365");
        }
    }
}

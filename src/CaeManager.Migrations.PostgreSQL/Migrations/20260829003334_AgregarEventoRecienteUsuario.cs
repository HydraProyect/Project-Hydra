using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarEventoRecienteUsuario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventosRecientesUsuario",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    EntidadId = table.Column<Guid>(type: "uuid", nullable: true),
                    Titulo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Subtitulo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UrlDestino = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    OcurridoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventosRecientesUsuario", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventosRecientesUsuario_TenantId_UsuarioId_OcurridoEnUtc",
                table: "EventosRecientesUsuario",
                columns: new[] { "TenantId", "UsuarioId", "OcurridoEnUtc" },
                descending: new[] { false, false, true });

            // RLS en la misma migración que crea la tabla: es EntidadConTenant, y
            // los ratchets CoberturaRlsDelModeloTests/PoliticasRlsCubrenModeloTests
            // exigen política para toda tabla con TenantId. El filtro global de EF
            // ya la protege; esto es la segunda línea, sobre cae_app_runtime
            // (RUNBOOK-RLS.md).
            migrationBuilder.Sql(@"
ALTER TABLE ""EventosRecientesUsuario"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""EventosRecientesUsuario"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""EventosRecientesUsuario"";
CREATE POLICY aislamiento_tenant ON ""EventosRecientesUsuario""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EventosRecientesUsuario");
        }
    }
}

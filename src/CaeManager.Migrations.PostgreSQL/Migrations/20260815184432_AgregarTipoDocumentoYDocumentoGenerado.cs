using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarTipoDocumentoYDocumentoGenerado : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TipoDocumentoId",
                table: "PlantillasDocumento",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "DocumentosGenerados",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlantillaDocumentoVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "uuid", nullable: true),
                    EmpresaId = table.Column<Guid>(type: "uuid", nullable: true),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: true),
                    DatosUtilizadosJson = table.Column<string>(type: "text", nullable: false),
                    GeneradoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeneradoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Estado = table.Column<string>(type: "text", nullable: false),
                    CodigoSeguroVerificacion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    HashSha256Impreso = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SelloElectronicoAplicadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentosGenerados", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentosGenerados_Documentos_DocumentoId",
                        column: x => x.DocumentoId,
                        principalTable: "Documentos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DocumentosGenerados_PlantillasDocumentoVersion_PlantillaDoc~",
                        column: x => x.PlantillaDocumentoVersionId,
                        principalTable: "PlantillasDocumentoVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasDocumento_TenantId_Id",
                table: "PlantillasDocumento",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasDocumento_TenantId_TipoDocumentoId",
                table: "PlantillasDocumento",
                columns: new[] { "TenantId", "TipoDocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentosGenerados_DocumentoId",
                table: "DocumentosGenerados",
                column: "DocumentoId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentosGenerados_PlantillaDocumentoVersionId",
                table: "DocumentosGenerados",
                column: "PlantillaDocumentoVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentosGenerados_TenantId_EmpresaId",
                table: "DocumentosGenerados",
                columns: new[] { "TenantId", "EmpresaId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentosGenerados_TenantId_PlantillaDocumentoVersionId",
                table: "DocumentosGenerados",
                columns: new[] { "TenantId", "PlantillaDocumentoVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentosGenerados_TenantId_TrabajadorId",
                table: "DocumentosGenerados",
                columns: new[] { "TenantId", "TrabajadorId" });

            migrationBuilder.AddForeignKey(
                name: "FK_PlantillasDocumento_TiposDocumento_TenantId_TipoDocumentoId",
                table: "PlantillasDocumento",
                columns: new[] { "TenantId", "TipoDocumentoId" },
                principalTable: "TiposDocumento",
                principalColumns: new[] { "TenantId", "Id" },
                onDelete: ReferentialAction.Restrict);

            // RLS en la misma tanda que crea la tabla — mismo criterio que
            // AgregarPlantillasDocumento.
            migrationBuilder.Sql(@"
ALTER TABLE ""DocumentosGenerados"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""DocumentosGenerados"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""DocumentosGenerados"";
CREATE POLICY aislamiento_tenant ON ""DocumentosGenerados""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlantillasDocumento_TiposDocumento_TenantId_TipoDocumentoId",
                table: "PlantillasDocumento");

            migrationBuilder.DropTable(
                name: "DocumentosGenerados");

            migrationBuilder.DropIndex(
                name: "IX_PlantillasDocumento_TenantId_Id",
                table: "PlantillasDocumento");

            migrationBuilder.DropIndex(
                name: "IX_PlantillasDocumento_TenantId_TipoDocumentoId",
                table: "PlantillasDocumento");

            migrationBuilder.DropColumn(
                name: "TipoDocumentoId",
                table: "PlantillasDocumento");
        }
    }
}

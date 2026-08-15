using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarPlantillasDocumento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlantillasDocumento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Origen = table.Column<string>(type: "text", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Descripcion = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AmbitoAplicacion = table.Column<string>(type: "text", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: true),
                    CodigoFormato = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    FormatoOrigen = table.Column<string>(type: "text", nullable: false),
                    Estado = table.Column<string>(type: "text", nullable: false),
                    VersionActualId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlantillasDocumento", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlantillasDocumentoVersion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlantillaDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    NumeroVersion = table.Column<int>(type: "integer", nullable: false),
                    ArchivoOriginalUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    HashSha256ArchivoOriginal = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EstadoConfiguracion = table.Column<string>(type: "text", nullable: false),
                    ConfirmadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConfirmadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VigenciaLegalHasta = table.Column<DateOnly>(type: "date", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlantillasDocumentoVersion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlantillasDocumentoVersion_PlantillasDocumento_PlantillaDoc~",
                        column: x => x.PlantillaDocumentoId,
                        principalTable: "PlantillasDocumento",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlantillasElemento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlantillaDocumentoVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<string>(type: "text", nullable: false),
                    Pagina = table.Column<int>(type: "integer", nullable: false),
                    X = table.Column<double>(type: "double precision", nullable: false),
                    Y = table.Column<double>(type: "double precision", nullable: false),
                    Ancho = table.Column<double>(type: "double precision", nullable: false),
                    Alto = table.Column<double>(type: "double precision", nullable: false),
                    EtiquetaVisible = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FuenteDato = table.Column<string>(type: "text", nullable: true),
                    ValorConstante = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Formato = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Obligatorio = table.Column<bool>(type: "boolean", nullable: false),
                    RolFirmante = table.Column<string>(type: "text", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlantillasElemento", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlantillasElemento_PlantillasDocumentoVersion_PlantillaDocu~",
                        column: x => x.PlantillaDocumentoVersionId,
                        principalTable: "PlantillasDocumentoVersion",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasDocumento_TenantId_CentroId",
                table: "PlantillasDocumento",
                columns: new[] { "TenantId", "CentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasDocumento_TenantId_ClienteId",
                table: "PlantillasDocumento",
                columns: new[] { "TenantId", "ClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasDocumento_VersionActualId",
                table: "PlantillasDocumento",
                column: "VersionActualId");

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasDocumentoVersion_PlantillaDocumentoId",
                table: "PlantillasDocumentoVersion",
                column: "PlantillaDocumentoId");

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasDocumentoVersion_TenantId_PlantillaDocumentoId_Nu~",
                table: "PlantillasDocumentoVersion",
                columns: new[] { "TenantId", "PlantillaDocumentoId", "NumeroVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlantillasElemento_PlantillaDocumentoVersionId",
                table: "PlantillasElemento",
                column: "PlantillaDocumentoVersionId");

            migrationBuilder.AddForeignKey(
                name: "FK_PlantillasDocumento_PlantillasDocumentoVersion_VersionActua~",
                table: "PlantillasDocumento",
                column: "VersionActualId",
                principalTable: "PlantillasDocumentoVersion",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // RLS en la misma tanda que crea las tablas — mismo criterio que
            // AgregarFirmaEnCampoDocumento.
            foreach (var tabla in TablasConRls)
            {
                migrationBuilder.Sql($@"
ALTER TABLE ""{tabla}"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""{tabla}"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""{tabla}"";
CREATE POLICY aislamiento_tenant ON ""{tabla}""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
            }
        }

        private static readonly string[] TablasConRls =
        [
            "PlantillasDocumento",
            "PlantillasDocumentoVersion",
            "PlantillasElemento",
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PlantillasDocumento_PlantillasDocumentoVersion_VersionActua~",
                table: "PlantillasDocumento");

            migrationBuilder.DropTable(
                name: "PlantillasElemento");

            migrationBuilder.DropTable(
                name: "PlantillasDocumentoVersion");

            migrationBuilder.DropTable(
                name: "PlantillasDocumento");
        }
    }
}

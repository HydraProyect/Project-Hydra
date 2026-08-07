using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarValidacionDocumentoOficial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Ninguno" y no "" (lo que generó dotnet ef): la columna se lee
            // con HasConversion<string> a enum, y una cadena vacía en las
            // filas preexistentes rompería el parseo al primer SELECT.
            migrationBuilder.AddColumn<string>(
                name: "PerfilDocumentoOficial",
                table: "TiposDocumento",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Ninguno");

            migrationBuilder.CreateTable(
                name: "FirmasDigitalesDocumento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Indice = table.Column<int>(type: "integer", nullable: false),
                    Estado = table.Column<string>(type: "text", nullable: false),
                    CadenaConfiable = table.Column<bool>(type: "boolean", nullable: false),
                    Revocacion = table.Column<string>(type: "text", nullable: false),
                    EmisorCertificado = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    NumeroSerieCertificado = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EsSelloDeOrgano = table.Column<bool>(type: "boolean", nullable: false),
                    FirmanteNombre = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    FirmanteNif = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    FechaFirmaUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TieneSelloDeTiempo = table.Column<bool>(type: "boolean", nullable: false),
                    CubreDocumentoCompleto = table.Column<bool>(type: "boolean", nullable: false),
                    MotivoInvalidez = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    VerificadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FirmasDigitalesDocumento", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FirmasDigitalesDocumento_Documentos_DocumentoId",
                        column: x => x.DocumentoId,
                        principalTable: "Documentos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VerificacionesDocumentoOficial",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Perfil = table.Column<string>(type: "text", nullable: false),
                    NivelConfianza = table.Column<string>(type: "text", nullable: false),
                    CodigoVerificacion = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CifDetectado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    RazonSocialDetectada = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    FechaEmisionDetectada = table.Column<DateOnly>(type: "date", nullable: true),
                    PeriodoDetectado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ResultadoCotejo = table.Column<string>(type: "text", nullable: false),
                    Decision = table.Column<string>(type: "text", nullable: false),
                    Motivos = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificacionesDocumentoOficial", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VerificacionesDocumentoOficial_Documentos_DocumentoId",
                        column: x => x.DocumentoId,
                        principalTable: "Documentos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000001"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000002"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000003"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000004"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000005"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000006"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000007"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000008"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-000000000009"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000a"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000b"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000c"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000d"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000e"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("10000000-0000-0000-0000-00000000000f"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                column: "PerfilDocumentoOficial",
                value: "CorrienteTgss");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000002"),
                column: "PerfilDocumentoOficial",
                value: "CorrienteAeat");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000003"),
                column: "PerfilDocumentoOficial",
                value: "Ita");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000004"),
                column: "PerfilDocumentoOficial",
                value: "Rlc");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000005"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000006"),
                column: "PerfilDocumentoOficial",
                value: "Rlc");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000007"),
                column: "PerfilDocumentoOficial",
                value: "Rnt");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000008"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000009"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000a-0000-0000-0000-00000000000a"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000b-0000-0000-0000-00000000000b"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000c-0000-0000-0000-00000000000c"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("2000000d-0000-0000-0000-00000000000d"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000001"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000002"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000003"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("30000000-0000-0000-0000-000000000004"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000001"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000002"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000003"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000004"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000005"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000006"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000007"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000008"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000000-0000-0000-0000-000000000009"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000a-0000-0000-0000-00000000000a"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000b-0000-0000-0000-00000000000b"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000c-0000-0000-0000-00000000000c"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000d-0000-0000-0000-00000000000d"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000e-0000-0000-0000-00000000000e"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("4000000f-0000-0000-0000-00000000000f"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000010-0000-0000-0000-000000000010"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("40000011-0000-0000-0000-000000000011"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000001"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000002"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000003"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000004"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000005"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000006"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000007"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000008"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000009"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000a-0000-0000-0000-00000000000a"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000b-0000-0000-0000-00000000000b"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000c-0000-0000-0000-00000000000c"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000d-0000-0000-0000-00000000000d"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000e-0000-0000-0000-00000000000e"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("5000000f-0000-0000-0000-00000000000f"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000010-0000-0000-0000-000000000010"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000011-0000-0000-0000-000000000011"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000012-0000-0000-0000-000000000012"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000013-0000-0000-0000-000000000013"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000014-0000-0000-0000-000000000014"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.UpdateData(
                table: "TiposDocumento",
                keyColumn: "Id",
                keyValue: new Guid("50000015-0000-0000-0000-000000000015"),
                column: "PerfilDocumentoOficial",
                value: "Ninguno");

            migrationBuilder.CreateIndex(
                name: "IX_FirmasDigitalesDocumento_DocumentoId",
                table: "FirmasDigitalesDocumento",
                column: "DocumentoId");

            migrationBuilder.CreateIndex(
                name: "IX_FirmasDigitalesDocumento_TenantId_DocumentoId",
                table: "FirmasDigitalesDocumento",
                columns: new[] { "TenantId", "DocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_VerificacionesDocumentoOficial_DocumentoId",
                table: "VerificacionesDocumentoOficial",
                column: "DocumentoId");

            migrationBuilder.CreateIndex(
                name: "IX_VerificacionesDocumentoOficial_TenantId_DocumentoId",
                table: "VerificacionesDocumentoOficial",
                columns: new[] { "TenantId", "DocumentoId" },
                unique: true);

            // RLS en la misma tanda que crea las tablas — mismo criterio que
            // HabilitarRlsSolicitudPrioridadDocumento: el filtro global de EF
            // ya las protege; esto es la segunda línea (RLS sobre
            // cae_app_runtime, ver RUNBOOK-RLS.md).
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
            "FirmasDigitalesDocumento",
            "VerificacionesDocumentoOficial",
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FirmasDigitalesDocumento");

            migrationBuilder.DropTable(
                name: "VerificacionesDocumentoOficial");

            migrationBuilder.DropColumn(
                name: "PerfilDocumentoOficial",
                table: "TiposDocumento");
        }
    }
}

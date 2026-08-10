using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarNivelServicioYVerificacionExternaSubcontrata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NivelServicio",
                table: "Subcontratas",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "VerificacionesExternaSubcontrata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcontrataId = table.Column<Guid>(type: "uuid", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    FechaVerificacion = table.Column<DateOnly>(type: "date", nullable: false),
                    Resultado = table.Column<int>(type: "integer", nullable: false),
                    ValidoHasta = table.Column<DateOnly>(type: "date", nullable: true),
                    EvidenciaArchivoRuta = table.Column<string>(type: "text", nullable: true),
                    EvidenciaNombreArchivo = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true),
                    UsuarioVerificadorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Observaciones = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VerificacionesExternaSubcontrata", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VerificacionesExternaSubcontrata_Centros_TenantId_CentroId",
                        columns: x => new { x.TenantId, x.CentroId },
                        principalTable: "Centros",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VerificacionesExternaSubcontrata_Subcontratas_TenantId_Subc~",
                        columns: x => new { x.TenantId, x.SubcontrataId },
                        principalTable: "Subcontratas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VerificacionesExternaSubcontrata_TiposDocumento_TenantId_Ti~",
                        columns: x => new { x.TenantId, x.TipoDocumentoId },
                        principalTable: "TiposDocumento",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VerificacionesExternaSubcontrata_TenantId_CentroId",
                table: "VerificacionesExternaSubcontrata",
                columns: new[] { "TenantId", "CentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_VerificacionesExternaSubcontrata_TenantId_SubcontrataId_Cen~",
                table: "VerificacionesExternaSubcontrata",
                columns: new[] { "TenantId", "SubcontrataId", "CentroId", "TipoDocumentoId" });

            migrationBuilder.CreateIndex(
                name: "IX_VerificacionesExternaSubcontrata_TenantId_TipoDocumentoId",
                table: "VerificacionesExternaSubcontrata",
                columns: new[] { "TenantId", "TipoDocumentoId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VerificacionesExternaSubcontrata");

            migrationBuilder.DropColumn(
                name: "NivelServicio",
                table: "Subcontratas");
        }
    }
}

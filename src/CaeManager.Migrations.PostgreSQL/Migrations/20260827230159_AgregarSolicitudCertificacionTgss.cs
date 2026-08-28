using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarSolicitudCertificacionTgss : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SolicitudesCertificacionTgss",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    FechaSolicitud = table.Column<DateOnly>(type: "date", nullable: false),
                    SolicitadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Resultado = table.Column<int>(type: "integer", nullable: true),
                    FechaRespuesta = table.Column<DateOnly>(type: "date", nullable: true),
                    RespuestaRegistradaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvidenciaArchivoRuta = table.Column<string>(type: "text", nullable: true),
                    EvidenciaNombreArchivo = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: true),
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
                    table.PrimaryKey("PK_SolicitudesCertificacionTgss", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SolicitudesCertificacionTgss_Empresas_TenantId_ClienteId",
                        columns: x => new { x.TenantId, x.ClienteId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SolicitudesCertificacionTgss_Empresas_TenantId_EmpresaId",
                        columns: x => new { x.TenantId, x.EmpresaId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesCertificacionTgss_TenantId_ClienteId",
                table: "SolicitudesCertificacionTgss",
                columns: new[] { "TenantId", "ClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesCertificacionTgss_TenantId_EmpresaId_ClienteId_F~",
                table: "SolicitudesCertificacionTgss",
                columns: new[] { "TenantId", "EmpresaId", "ClienteId", "FechaSolicitud" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SolicitudesCertificacionTgss");
        }
    }
}

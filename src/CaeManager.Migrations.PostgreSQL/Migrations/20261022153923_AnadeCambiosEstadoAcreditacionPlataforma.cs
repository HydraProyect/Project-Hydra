using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeCambiosEstadoAcreditacionPlataforma : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "UltimoCambioEstadoAManoId",
                table: "AcreditacionesDocumentoPlataforma",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CambiosEstadoAcreditacionPlataforma",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AcreditacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EstadoAnterior = table.Column<int>(type: "integer", nullable: false),
                    EstadoVigenciaAnterior = table.Column<int>(type: "integer", nullable: false),
                    FechaVencimientoAnterior = table.Column<DateOnly>(type: "date", nullable: true),
                    Estado = table.Column<int>(type: "integer", nullable: false),
                    Fecha = table.Column<DateOnly>(type: "date", nullable: false),
                    Motivo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Nota = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    MomentoUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeshechoUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CambiosEstadoAcreditacionPlataforma", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CambiosEstadoAcreditacionPlataforma_AcreditacionesDocumento~",
                        column: x => x.AcreditacionId,
                        principalTable: "AcreditacionesDocumentoPlataforma",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CambiosEstadoAcreditacionPlataforma_AcreditacionId",
                table: "CambiosEstadoAcreditacionPlataforma",
                column: "AcreditacionId");

            migrationBuilder.CreateIndex(
                name: "IX_CambiosEstadoAcreditacionPlataforma_TenantId_AcreditacionId~",
                table: "CambiosEstadoAcreditacionPlataforma",
                columns: new[] { "TenantId", "AcreditacionId", "MomentoUtc" });

                columns: new[] { "TenantId", "AcreditacionId", "MomentoUtc" });
                table: "CambiosEstadoAcreditacionPlataforma",
                name: "IX_CambiosEstadoAcreditacionPlataforma_TenantId_AcreditacionId~",
            migrationBuilder.CreateIndex(

                column: "AcreditacionId");
                table: "CambiosEstadoAcreditacionPlataforma",
                name: "IX_CambiosEstadoAcreditacionPlataforma_AcreditacionId",
            migrationBuilder.CreateIndex(

                });
                        onDelete: ReferentialAction.Cascade);
                        principalColumn: "Id",
                        principalTable: "AcreditacionesDocumentoPlataforma",
                        column: x => x.AcreditacionId,
                        name: "FK_CambiosEstadoAcreditacionPlataforma_AcreditacionesDocumento~",
                    table.ForeignKey(
                    table.PrimaryKey("PK_CambiosEstadoAcreditacionPlataforma", x => x.Id);
                {
                constraints: table =>
                },
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                    DeshechoUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MomentoUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    Nota = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Motivo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Fecha = table.Column<DateOnly>(type: "date", nullable: false),
                    Estado = table.Column<int>(type: "integer", nullable: false),
                    FechaVencimientoAnterior = table.Column<DateOnly>(type: "date", nullable: true),
                    EstadoVigenciaAnterior = table.Column<int>(type: "integer", nullable: false),
                    EstadoAnterior = table.Column<int>(type: "integer", nullable: false),
                    AcreditacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                {
                columns: table => new
                name: "CambiosEstadoAcreditacionPlataforma",
            migrationBuilder.CreateTable(
        {
        protected override void Up(MigrationBuilder migrationBuilder)
        /// <inheritdoc />
    {
    public partial class AnadeCambiosEstadoAcreditacionPlataforma : Migration
    /// <inheritdoc />
{
namespace CaeManager.Migrations.PostgreSQL.Migrations

#nullable disable

using Microsoft.EntityFrameworkCore.Migrations;
using System;
}
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CambiosEstadoAcreditacionPlataforma");

            migrationBuilder.DropColumn(
                name: "UltimoCambioEstadoAManoId",
                table: "AcreditacionesDocumentoPlataforma");
        }
    }
}

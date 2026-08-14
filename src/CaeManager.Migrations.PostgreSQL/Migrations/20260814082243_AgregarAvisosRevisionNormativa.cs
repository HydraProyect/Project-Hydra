using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarAvisosRevisionNormativa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AvisosRevisionNormativa",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdentificadorBoe = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FechaPublicacion = table.Column<DateOnly>(type: "date", nullable: false),
                    Titulo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UrlHtml = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    NormaVigilada = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DetectadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Revisado = table.Column<bool>(type: "boolean", nullable: false),
                    RevisadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevisadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    NotasRevision = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AvisosRevisionNormativa", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AvisosRevisionNormativa_IdentificadorBoe",
                table: "AvisosRevisionNormativa",
                column: "IdentificadorBoe",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AvisosRevisionNormativa_Revisado",
                table: "AvisosRevisionNormativa",
                column: "Revisado");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AvisosRevisionNormativa");
        }
    }
}

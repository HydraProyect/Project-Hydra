using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarAceptacionTerminos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AceptacionesTerminos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionDocumento = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FechaAceptacionUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AceptacionesTerminos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AceptacionesTerminos_UsuarioId_VersionDocumento",
                table: "AceptacionesTerminos",
                columns: new[] { "UsuarioId", "VersionDocumento" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AceptacionesTerminos");
        }
    }
}

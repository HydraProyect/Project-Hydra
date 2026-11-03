using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeAutoriaNotaContactoAgenda : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "NotasActualizadasEnUtc",
                table: "ContactosAgenda",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "NotasAutorUsuarioId",
                table: "ContactosAgenda",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotasActualizadasEnUtc",
                table: "ContactosAgenda");

            migrationBuilder.DropColumn(
                name: "NotasAutorUsuarioId",
                table: "ContactosAgenda");
        }
    }
}

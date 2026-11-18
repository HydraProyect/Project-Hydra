using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeAutorVersionDocumento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SubidoPorActorRealUsuarioId",
                table: "Documentos",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubidoPorUsuarioId",
                table: "Documentos",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubidoPorActorRealUsuarioId",
                table: "Documentos");

            migrationBuilder.DropColumn(
                name: "SubidoPorUsuarioId",
                table: "Documentos");
        }
    }
}

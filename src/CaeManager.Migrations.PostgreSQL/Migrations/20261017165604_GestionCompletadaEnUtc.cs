using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class GestionCompletadaEnUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Sin relleno: las Gestiones completadas antes de esta migración quedan con CompletadaEnUtc = null,
            // porque no hay fuente fiable del instante real. La tabla ya tiene RLS (aislamiento_tenant) y la
            // columna nueva la hereda.
            migrationBuilder.AddColumn<DateTime>(
                name: "CompletadaEnUtc",
                table: "Gestiones",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletadaEnUtc",
                table: "Gestiones");
        }
    }
}

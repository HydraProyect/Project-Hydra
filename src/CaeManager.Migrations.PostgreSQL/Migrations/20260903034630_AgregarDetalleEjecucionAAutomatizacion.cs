using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarDetalleEjecucionAAutomatizacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "UltimoMensajeError",
                table: "EstadosAutomatizacion",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UltimosElementosAfectados",
                table: "EstadosAutomatizacion",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UltimosElementosEvaluados",
                table: "EstadosAutomatizacion",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UltimoMensajeError",
                table: "EstadosAutomatizacion");

            migrationBuilder.DropColumn(
                name: "UltimosElementosAfectados",
                table: "EstadosAutomatizacion");

            migrationBuilder.DropColumn(
                name: "UltimosElementosEvaluados",
                table: "EstadosAutomatizacion");
        }
    }
}

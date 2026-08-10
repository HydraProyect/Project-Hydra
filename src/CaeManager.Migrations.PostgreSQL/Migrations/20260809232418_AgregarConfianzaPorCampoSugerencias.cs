using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarConfianzaPorCampoSugerencias : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConfianzaCentro",
                table: "SugerenciasVisitaCorreo",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ConfianzaFechas",
                table: "SugerenciasVisitaCorreo",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ConfianzaTipoDocumento",
                table: "SugerenciasGestionCorreo",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ConfianzaTrabajador",
                table: "SugerenciasGestionCorreo",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConfianzaCentro",
                table: "SugerenciasVisitaCorreo");

            migrationBuilder.DropColumn(
                name: "ConfianzaFechas",
                table: "SugerenciasVisitaCorreo");

            migrationBuilder.DropColumn(
                name: "ConfianzaTipoDocumento",
                table: "SugerenciasGestionCorreo");

            migrationBuilder.DropColumn(
                name: "ConfianzaTrabajador",
                table: "SugerenciasGestionCorreo");
        }
    }
}

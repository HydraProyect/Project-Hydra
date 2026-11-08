using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeBuzonCompartidoMicrosoft365 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BuzonCompartido",
                table: "SolicitudesConexionMicrosoft365",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EsBuzonCompartido",
                table: "ConexionesIntegracion",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BuzonCompartido",
                table: "SolicitudesConexionMicrosoft365");

            migrationBuilder.DropColumn(
                name: "EsBuzonCompartido",
                table: "ConexionesIntegracion");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeFichaCentro : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AtmosferasExplosivas",
                table: "Centros",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ControlAccesosNombre",
                table: "Centros",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ControlAccesosTelefono",
                table: "Centros",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EspaciosConfinados",
                table: "Centros",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HorarioAcceso",
                table: "Centros",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TecnicoPrevencionCorreo",
                table: "Centros",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TecnicoPrevencionNombre",
                table: "Centros",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TecnicoPrevencionTelefono",
                table: "Centros",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TrabajosEnAltura",
                table: "Centros",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AtmosferasExplosivas",
                table: "Centros");

            migrationBuilder.DropColumn(
                name: "ControlAccesosNombre",
                table: "Centros");

            migrationBuilder.DropColumn(
                name: "ControlAccesosTelefono",
                table: "Centros");

            migrationBuilder.DropColumn(
                name: "EspaciosConfinados",
                table: "Centros");

            migrationBuilder.DropColumn(
                name: "HorarioAcceso",
                table: "Centros");

            migrationBuilder.DropColumn(
                name: "TecnicoPrevencionCorreo",
                table: "Centros");

            migrationBuilder.DropColumn(
                name: "TecnicoPrevencionNombre",
                table: "Centros");

            migrationBuilder.DropColumn(
                name: "TecnicoPrevencionTelefono",
                table: "Centros");

            migrationBuilder.DropColumn(
                name: "TrabajosEnAltura",
                table: "Centros");
        }
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeOrigenCambioEstadoAcreditacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Origen",
                table: "CambiosEstadoAcreditacionPlataforma",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Todos los cambios anotados hasta hoy salieron de Centro 360 (OrigenCambioEstadoAcreditacion.Centro360 = 0),
            // y el DEFAULT 0 los rellena. Se conserva a propósito: durante el relevo azul/verde, o tras volver a la imagen
            // anterior, esa imagen inserta cambios sin conocer la columna, y todos salen de Centro 360, así que 0 es su
            // origen verdadero. La imagen nueva no depende del DEFAULT: el modelo no declara valor por defecto y EF envía
            // siempre el origen que fija el dominio, también cuando es Centro 360.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Origen",
                table: "CambiosEstadoAcreditacionPlataforma");
        }
    }
}

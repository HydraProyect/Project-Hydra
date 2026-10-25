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

            // Todos los cambios anotados hasta hoy salieron de Centro 360 (OrigenCambioEstadoAcreditacion.Centro360 = 0):
            // el DEFAULT solo sirve para rellenarlos. Después se retira, como en AnadeVersionPlataformaCaeClienteEmpresarial:
            // el dominio fija el origen de cada cambio nuevo, y un INSERT que lo omita debe fallar en vez de quedar como
            // si viniera de Centro 360 (sería un rastro de auditoría falso para un «Lo he enviado» de la extensión).
            migrationBuilder.Sql("""
                ALTER TABLE public."CambiosEstadoAcreditacionPlataforma" ALTER COLUMN "Origen" DROP DEFAULT;
                """);
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

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RecuperacionHuecoCursorCompuestoM365 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecuperacionHuecoCursorDesempate",
                table: "ConexionesIntegracion",
                type: "text",
                nullable: true);

            // Issue #2263: desempate del cursor compuesto (recepción, Id). Columna nullable, sin relleno: la ranura anterior
            // del despliegue azul/verde no la conoce (EF solo nombra las columnas de su modelo) y sigue funcionando, y las
            // filas existentes nacen con el cursor solo por fecha. Si esa ranura anula la recuperación o mueve el cursor sin
            // borrar el desempate, este deja de valer solo: lleva la recuperación y el segundo a los que pertenece
            // (ConexionIntegracion.RecuperacionHuecoCursorId). Soporte TALVEG lee ConexionesIntegracion columna a columna
            // (SoporteSinColumnasSecretasRestantes): sin el grant, cargar la entidad bajo cae_app_soporte daría 42501. Es
            // un identificador de recuperación, un instante y el Id de un mensaje de Graph, no un secreto. No cambia
            // políticas RLS ni roles.
            migrationBuilder.Sql("""
                GRANT SELECT ("RecuperacionHuecoCursorDesempate") ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecuperacionHuecoCursorDesempate",
                table: "ConexionesIntegracion");
        }
    }
}

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

            migrationBuilder.AddColumn<bool>(
                name: "RecuperacionHuecoRecorridaPorId",
                table: "ConexionesIntegracion",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Aditiva (expandir de expandir y contraer, issue #2263): una columna nullable y
            // otra con default false. La ranura anterior (blue/green) sigue funcionando sin
            // conocerlas, y esta entrega no las escribe con valor: solo respeta el indicador
            // de recorrido por Id, que pondra la entrega siguiente.
            // Soporte TALVEG lee ConexionesIntegracion columna a columna: sin este GRANT, su
            // lectura de la fila acaba en 42501. No cambia RLS ni roles.
            migrationBuilder.Sql("""
                GRANT SELECT ("RecuperacionHuecoCursorDesempate", "RecuperacionHuecoRecorridaPorId") ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RecuperacionHuecoCursorDesempate",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "RecuperacionHuecoRecorridaPorId",
                table: "ConexionesIntegracion");
        }
    }
}

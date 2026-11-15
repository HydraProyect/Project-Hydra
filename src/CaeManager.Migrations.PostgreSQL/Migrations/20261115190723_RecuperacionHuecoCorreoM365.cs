using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class RecuperacionHuecoCorreoM365 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CubreDesdeUtc",
                table: "EventosWebhook",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecuperacionHuecoCubreDesdeUtc",
                table: "ConexionesIntegracion",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecuperacionHuecoCursorUtc",
                table: "ConexionesIntegracion",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecuperacionHuecoId",
                table: "ConexionesIntegracion",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RecuperacionHuecoListadoCompleto",
                table: "ConexionesIntegracion",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Columnas nullable o con valor por defecto, sin relleno: la ranura anterior del despliegue azul/verde no las
            // conoce (EF solo nombra las columnas de su modelo) y sigue funcionando; las filas existentes nacen sin
            // recuperación en curso. Soporte TALVEG lee estas dos tablas columna a columna
            // (SoporteSinColumnasSecretasRestantes, SoporteSinCodigoVerificacionNiPayloadWebhook): sin estos grants, cargar
            // la entidad bajo cae_app_soporte daría 42501. Son fechas, un identificador de generación y un indicador. No cambia
            // políticas RLS ni roles.
            migrationBuilder.Sql("""
                GRANT SELECT ("RecuperacionHuecoCubreDesdeUtc", "RecuperacionHuecoCursorUtc", "RecuperacionHuecoId",
                              "RecuperacionHuecoListadoCompleto")
                    ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;
                GRANT SELECT ("CubreDesdeUtc") ON TABLE public."EventosWebhook" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CubreDesdeUtc",
                table: "EventosWebhook");

            migrationBuilder.DropColumn(
                name: "RecuperacionHuecoCubreDesdeUtc",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "RecuperacionHuecoCursorUtc",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "RecuperacionHuecoId",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "RecuperacionHuecoListadoCompleto",
                table: "ConexionesIntegracion");
        }
    }
}

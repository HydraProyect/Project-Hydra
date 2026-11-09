using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeHuecoPendienteConexion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "HuecoPendienteDesdeUtc",
                table: "ConexionesIntegracion",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReintentoVerificacionNoAntesDeUtc",
                table: "ConexionesIntegracion",
                type: "timestamp with time zone",
                nullable: true);

            // Dos columnas nullable nuevas, sin reescribir datos: la ranura anterior del despliegue azul/verde no las
            // conoce y sigue funcionando. HuecoPendienteDesdeUtc null es "sin hueco"; puesta, Conexiones avisa de posible
            // correo no recuperado desde esa fecha. Las conexiones que ya estaban en ConError antes de esta migración no
            // tienen marca ni aviso. ReintentoVerificacionNoAntesDeUtc es el calendario del reintento acotado de la
            // verificación de identidad (también guarda el Retry-After de Microsoft); null es "sin programar".
            //
            // Soporte TALVEG lee esta tabla columna a columna (SoporteSinColumnasSecretasRestantes): sin este grant,
            // cargar la entidad bajo cae_app_soporte daría 42501. Es una fecha, no un secreto. Mismo patrón que
            // 20261109200000_AnadeIdentidadVerificadaConexion.
            migrationBuilder.Sql("""
                GRANT SELECT ("HuecoPendienteDesdeUtc", "ReintentoVerificacionNoAntesDeUtc") ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReintentoVerificacionNoAntesDeUtc",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "HuecoPendienteDesdeUtc",
                table: "ConexionesIntegracion");
        }
    }
}
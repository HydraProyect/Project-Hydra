using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeIdentidadVerificadaConexion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "IdentidadVerificadaUtc",
                table: "ConexionesIntegracion",
                type: "timestamp with time zone",
                nullable: true);

            // Solo una columna nullable nueva, sin reescribir datos: la ranura anterior del despliegue azul/verde no la
            // conoce y sigue funcionando. Null es "sin verificar"; las conexiones existentes se verifican en el
            // siguiente ciclo de renovación (o al reactivarlas).
            //
            // Soporte TALVEG lee esta tabla columna a columna (SoporteSinColumnasSecretasRestantes): sin este grant,
            // cargar la entidad bajo cae_app_soporte daría 42501. Es una fecha, no un secreto. Mismo patrón que
            // 20261109190000_AnadeBuzonCompartidoMicrosoft365.
            migrationBuilder.Sql("""
                GRANT SELECT ("IdentidadVerificadaUtc") ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IdentidadVerificadaUtc",
                table: "ConexionesIntegracion");
        }
    }
}

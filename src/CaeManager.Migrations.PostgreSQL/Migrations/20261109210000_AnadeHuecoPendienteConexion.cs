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
                name: "CorreoNoRecuperadoDesdeUtc",
                table: "ConexionesIntegracion",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "HuecoPendienteDesdeUtc",
                table: "ConexionesIntegracion",
                type: "timestamp with time zone",
                nullable: true);

            // Solo dos columnas nullable nuevas, sin reescribir datos: la ranura anterior del despliegue azul/verde no
            // las conoce y sigue funcionando. HuecoPendienteDesdeUtc null es "sin hueco por leer";
            // CorreoNoRecuperadoDesdeUtc null es "sin aviso". Las conexiones que ya estaban en ConError antes de esta
            // migración no tienen marca: su hueco no se lee.
            //
            // Soporte TALVEG lee esta tabla columna a columna (SoporteSinColumnasSecretasRestantes): sin este grant,
            // cargar la entidad bajo cae_app_soporte daría 42501. Son fechas, no secretos. Mismo patrón que
            // 20261109200000_AnadeIdentidadVerificadaConexion.
            migrationBuilder.Sql("""
                GRANT SELECT ("HuecoPendienteDesdeUtc", "CorreoNoRecuperadoDesdeUtc") ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CorreoNoRecuperadoDesdeUtc",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "HuecoPendienteDesdeUtc",
                table: "ConexionesIntegracion");
        }
    }
}
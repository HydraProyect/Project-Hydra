using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeMarcaHuecoPorRegistrarConexion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Una columna nullable nueva, sin relleno ni reescritura de datos: la ranura anterior del despliegue azul/verde
            // no la conoce, no la lee ni la escribe (EF solo nombra las columnas de su modelo) y sigue funcionando. Null es
            // "nada por registrar", así que las filas existentes nacen sin aviso. Desconectar la fija en el mismo guardado
            // que deja la conexión Deshabilitada y la vacía al registrar la marca del hueco (HuecoPendienteDesdeUtc); si ese
            // guardado no llega a la base, la reparan Desconectar repetido, Reparar en Conexiones y el ciclo diario de
            // suscripciones (Codex, ronda 18). Una desconexión hecha por la ranura anterior no la fija: es el comportamiento
            // de antes de esta columna.
            migrationBuilder.AddColumn<DateTime>(
                name: "MarcaHuecoPorRegistrarDesdeUtc",
                table: "ConexionesIntegracion",
                type: "timestamp with time zone",
                nullable: true);

            // Soporte TALVEG lee esta tabla columna a columna (SoporteSinColumnasSecretasRestantes): sin este grant, cargar
            // la entidad o el listado de Conexiones bajo cae_app_soporte daría 42501. Es una fecha, no un secreto. Mismo
            // patrón que 20261111150000_AnadeHuecoPendienteConexion; no cambia políticas RLS ni roles.
            migrationBuilder.Sql("""
                GRANT SELECT ("MarcaHuecoPorRegistrarDesdeUtc") ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MarcaHuecoPorRegistrarDesdeUtc",
                table: "ConexionesIntegracion");
        }
    }
}
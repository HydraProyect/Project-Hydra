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
            // 20261111140000_AnadeIdentidadVerificadaConexion.
            migrationBuilder.Sql("""
                GRANT SELECT ("HuecoPendienteDesdeUtc", "ReintentoVerificacionNoAntesDeUtc") ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;
                """);

            // Guarda transitoria del alta de buzones compartidos (ReclamacionBuzonIntegracion.CuentaParaGuardaTransitoria).
            // Las reclamaciones anteriores a IdentidadBuzon (20261111130000) no están en su índice único: mientras quede
            // alguna sin completar en la plataforma, un alias de su buzón podría reclamarse como compartido en otro Tenant
            // propietario. La restricción de exclusión rechaza una reclamación compartida mientras exista una heredada que
            // cuente, y la inversa. PostgreSQL la comprueba como los índices únicos de la tabla: en la misma escritura, con
            // la misma identidad (cae_app_runtime) y sin mirar la RLS; no hay lectura nueva entre Tenants ni grant nuevo.
            // Es transitoria y queda inerte cuando ninguna fila cuenta.
            //
            // Sin relleno: las filas existentes nacen con FueraDeGuardaTransitoriaUtc null y cuentan las que no tienen
            // identidad (falla cerrado), y EsBuzonCompartido false. El ciclo diario las saca de la guarda cuando completa
            // su identidad o ve su conexión terminal. La ranura anterior del azul/verde inserta sin conocer las columnas:
            // las suyas sin identidad cuentan igual (los valores por defecto), y una suya que choque con una compartida da
            // 23P01, que esa ranura propaga como un error de guardado. Grant de tabla a Soporte TALVEG: cubre las columnas.
            migrationBuilder.AddColumn<bool>(
                name: "EsBuzonCompartido",
                table: "ReclamacionesBuzonIntegracion",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "FueraDeGuardaTransitoriaUtc",
                table: "ReclamacionesBuzonIntegracion",
                type: "timestamp with time zone",
                nullable: true);

            // btree_gist (línea base) da el operador <> sobre integer: choca una fila heredada que cuenta (0) con una
            // compartida (1); dos heredadas o dos compartidas no chocan entre sí. Una no compartida con identidad queda
            // fuera del predicado, así que completar identidades no choca con nada.
            migrationBuilder.Sql("""
                ALTER TABLE public."ReclamacionesBuzonIntegracion"
                    ADD CONSTRAINT "EX_ReclamacionesBuzonIntegracion_GuardaTransitoriaCompartidos"
                    EXCLUDE USING gist ((CASE WHEN "IdentidadBuzon" IS NULL THEN 0 ELSE 1 END) WITH <>)
                    WHERE (("IdentidadBuzon" IS NULL AND "FueraDeGuardaTransitoriaUtc" IS NULL) OR "EsBuzonCompartido");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE public."ReclamacionesBuzonIntegracion"
                    DROP CONSTRAINT IF EXISTS "EX_ReclamacionesBuzonIntegracion_GuardaTransitoriaCompartidos";
                """);

            migrationBuilder.DropColumn(
                name: "FueraDeGuardaTransitoriaUtc",
                table: "ReclamacionesBuzonIntegracion");

            migrationBuilder.DropColumn(
                name: "EsBuzonCompartido",
                table: "ReclamacionesBuzonIntegracion");

            migrationBuilder.DropColumn(
                name: "ReintentoVerificacionNoAntesDeUtc",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "HuecoPendienteDesdeUtc",
                table: "ConexionesIntegracion");
        }
    }
}
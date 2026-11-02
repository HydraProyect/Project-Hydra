using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeAcuseAutomaticoBuzon : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Con DEFAULT en base: mientras el relevo cambia de ranura, la versión anterior sigue dando de alta buzones
            // sin conocer la columna, y un buzón nace siempre con el acuse desactivado.
            migrationBuilder.AddColumn<bool>(
                name: "AcuseAutomaticoActivo",
                table: "ConexionesIntegracion",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Soporte TALVEG lee esta tabla columna a columna desde SoporteSinColumnasSecretasRestantes: sin este
            // grant, cargar la entidad bajo cae_app_soporte daría 42501. No es un dato secreto.
            migrationBuilder.Sql("""
                GRANT SELECT ("AcuseAutomaticoActivo") ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // DROP COLUMN se lleva también el grant de la columna.
            migrationBuilder.DropColumn(
                name: "AcuseAutomaticoActivo",
                table: "ConexionesIntegracion");
        }
    }
}

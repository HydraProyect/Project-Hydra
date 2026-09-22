using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Idioma de la interfaz por cuenta (<c>ApplicationUser.Idioma</c>, enum
    /// <c>IdiomaPreferido</c>). Se guarda como el entero del enum, igual que
    /// <c>Tema</c>; el valor por defecto <c>0</c> es <c>IdiomaPreferido.Espanol</c>,
    /// así que todas las cuentas existentes quedan en es-ES, que es como se
    /// veía el producto hasta ahora. La correspondencia con el nombre de
    /// cultura (es-ES/ca-ES) no vive en la columna sino en
    /// <c>CulturaUsuarioCookie</c> (Web).
    /// </summary>
    public partial class AgregarIdiomaPreferidoAUsuario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Idioma",
                table: "AspNetUsers",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Idioma",
                table: "AspNetUsers");
        }
    }
}

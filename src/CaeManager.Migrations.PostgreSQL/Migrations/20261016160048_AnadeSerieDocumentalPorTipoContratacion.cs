using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeSerieDocumentalPorTipoContratacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Serie documental por tipo de contratacion (mascara TiposContratacion). Toda fila existente, de cualquier
            // Tenant, nace en 7 (CuentaAjena | Autonomo | Voluntario): el mismo comportamiento que antes de la columna.
            // El valor por defecto de la columna lo fija sin UPDATE, asi que no depende de lo que el rol migrador vea con
            // FORCE ROW LEVEL SECURITY. Por eso se quitan los UpdateData que EF genera para las filas de HasData: con
            // el DEFAULT ya valen 7 y, bajo RLS, el UPDATE del migrador no tocaria ninguna fila.
            migrationBuilder.AddColumn<int>(
                name: "TiposContratacionDeSerie",
                table: "TiposDocumento",
                type: "integer",
                nullable: false,
                defaultValue: 7);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TiposContratacionDeSerie",
                table: "TiposDocumento");
        }
    }
}

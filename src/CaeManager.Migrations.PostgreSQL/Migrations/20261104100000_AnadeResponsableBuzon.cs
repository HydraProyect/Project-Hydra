using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeResponsableBuzon : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ResponsableUsuarioId",
                table: "ConexionesIntegracion",
                type: "uuid",
                nullable: true);

            // Soporte TALVEG lee esta tabla columna a columna desde SoporteSinColumnasSecretasRestantes: sin este
            // grant, cargar la entidad bajo cae_app_soporte daría 42501. No es un dato secreto.
            migrationBuilder.Sql("""
                GRANT SELECT ("ResponsableUsuarioId") ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // DROP COLUMN se lleva también el grant de la columna.
            migrationBuilder.DropColumn(
                name: "ResponsableUsuarioId",
                table: "ConexionesIntegracion");
        }
    }
}

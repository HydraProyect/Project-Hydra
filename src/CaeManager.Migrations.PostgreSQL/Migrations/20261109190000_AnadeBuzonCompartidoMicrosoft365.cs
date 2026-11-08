using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeBuzonCompartidoMicrosoft365 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BuzonCompartido",
                table: "SolicitudesConexionMicrosoft365",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ConsentidoPorUsuarioId",
                table: "ConexionesIntegracion",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectorioEntraId",
                table: "ConexionesIntegracion",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EsBuzonCompartido",
                table: "ConexionesIntegracion",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConexionesIntegracion_BuzonCompartidoConDirectorio",
                table: "ConexionesIntegracion",
                sql: "NOT \"EsBuzonCompartido\" OR (\"DirectorioEntraId\" IS NOT NULL AND \"GestorPropietarioId\" IS NULL)");

            // Soporte TALVEG lee esta tabla columna a columna desde SoporteSinColumnasSecretasRestantes: sin este
            // grant, cargar la entidad bajo cae_app_soporte daría 42501. Ninguna de las tres es un secreto: el
            // directorio es un identificador público de Entra, y el token de aplicación nunca se guarda.
            // SolicitudesConexionMicrosoft365 queda entera fuera de Soporte (SoporteSinJsonSensibles).
            migrationBuilder.Sql("""
                GRANT SELECT ("EsBuzonCompartido", "DirectorioEntraId", "ConsentidoPorUsuarioId")
                    ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ConexionesIntegracion_BuzonCompartidoConDirectorio",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "BuzonCompartido",
                table: "SolicitudesConexionMicrosoft365");

            migrationBuilder.DropColumn(
                name: "ConsentidoPorUsuarioId",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "DirectorioEntraId",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "EsBuzonCompartido",
                table: "ConexionesIntegracion");
        }
    }
}

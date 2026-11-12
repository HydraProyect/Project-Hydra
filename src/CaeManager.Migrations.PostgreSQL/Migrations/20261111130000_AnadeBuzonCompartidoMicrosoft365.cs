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

            migrationBuilder.AddColumn<string>(
                name: "IdentidadBuzon",
                table: "ReclamacionesBuzonIntegracion",
                type: "character varying(512)",
                maxLength: 512,
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

            migrationBuilder.AddColumn<string>(
                name: "IdentidadBuzon",
                table: "ConexionesIntegracion",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReclamacionesBuzonIntegracion_IdentidadBuzon",
                table: "ReclamacionesBuzonIntegracion",
                column: "IdentidadBuzon",
                unique: true,
                filter: "\"IdentidadBuzon\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConexionesIntegracion_BuzonCompartidoConDirectorio",
                table: "ConexionesIntegracion",
                sql: "NOT \"EsBuzonCompartido\" OR (\"DirectorioEntraId\" IS NOT NULL AND \"GestorPropietarioId\" IS NULL AND \"ClienteId\" IS NULL)");

            // Soporte TALVEG lee esta tabla columna a columna desde SoporteSinColumnasSecretasRestantes: sin este
            // grant, cargar la entidad bajo cae_app_soporte daría 42501. Ninguna de las cuatro es un secreto: el
            // directorio es un identificador público de Entra, la identidad del buzón es el Id de su bandeja de
            // entrada, y el token de aplicación nunca se guarda. SolicitudesConexionMicrosoft365 queda entera fuera
            // de Soporte (SoporteSinJsonSensibles); ReclamacionesBuzonIntegracion tiene grant de tabla y RLS.
            migrationBuilder.Sql("""
                GRANT SELECT ("EsBuzonCompartido", "DirectorioEntraId", "ConsentidoPorUsuarioId", "IdentidadBuzon")
                    ON TABLE public."ConexionesIntegracion" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReclamacionesBuzonIntegracion_IdentidadBuzon",
                table: "ReclamacionesBuzonIntegracion");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ConexionesIntegracion_BuzonCompartidoConDirectorio",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "BuzonCompartido",
                table: "SolicitudesConexionMicrosoft365");

            migrationBuilder.DropColumn(
                name: "IdentidadBuzon",
                table: "ReclamacionesBuzonIntegracion");

            migrationBuilder.DropColumn(
                name: "ConsentidoPorUsuarioId",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "DirectorioEntraId",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "EsBuzonCompartido",
                table: "ConexionesIntegracion");

            migrationBuilder.DropColumn(
                name: "IdentidadBuzon",
                table: "ConexionesIntegracion");
        }
    }
}

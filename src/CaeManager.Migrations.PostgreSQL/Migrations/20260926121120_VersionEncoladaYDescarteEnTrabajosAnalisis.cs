using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Una verificación IA que llega tarde ya no pisa una decisión manual: el
    /// trabajo guarda la versión del Documento para la que se encoló
    /// (<c>VersionDocumentoEncolada</c>) y, si se descarta, por qué
    /// (<c>MotivoDescarte</c>, con el estado nuevo <c>Descartado</c>, que es
    /// texto y no necesita cambio de esquema). Ambas nulas en las filas
    /// existentes: un trabajo encolado antes de esta migración solo comprueba
    /// la decisión manual posterior, no la versión.
    /// </summary>
    public partial class VersionEncoladaYDescarteEnTrabajosAnalisis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MotivoDescarte",
                table: "TrabajosAnalisisDocumento",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VersionDocumentoEncolada",
                table: "TrabajosAnalisisDocumento",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MotivoDescarte",
                table: "TrabajosAnalisisDocumento");

            migrationBuilder.DropColumn(
                name: "VersionDocumentoEncolada",
                table: "TrabajosAnalisisDocumento");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// «Por gestionar» deja de calcularse sobre los documentos y pasa a ser un estado
    /// guardado en la Visita (decisión del propietario, 2026-10-09): la fecha en que se
    /// envió el paquete de acreditación o se marcó a mano.
    ///
    /// <para>
    /// <b>Las Visitas que había quedan sin fecha, es decir, «Por gestionar».</b> No se
    /// rellena nada: lo que antes pintaba «Completa» se calculaba en memoria con el
    /// semáforo de cada documento y no existe en SQL, y que los documentos estuvieran
    /// vigentes nunca significó que alguien los hubiera enviado. Todavía no hay datos de
    /// cliente real.
    /// </para>
    /// </summary>
    public partial class AnadeDocumentacionGestionadaALasVisitas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DocumentacionGestionadaEnUtc",
                table: "Visitas",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DocumentacionGestionadaEnUtc",
                table: "Visitas");
        }
    }
}

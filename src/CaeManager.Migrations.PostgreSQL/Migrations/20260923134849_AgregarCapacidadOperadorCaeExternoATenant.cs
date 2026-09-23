using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// P11 (2026-09-23): sustituye el criterio interino
    /// <c>PerfilVocabulario == Consultora &amp;&amp; !EsPlataforma</c> por una capacidad
    /// explícita (<see cref="Domain.Tenants.Tenant.PuedeActuarComoOperadorCaeExterno"/>).
    ///
    /// El backfill concede la capacidad a todo Tenant que ya cumplía el
    /// criterio interino antes de esta migración — sin él, cualquier Operador
    /// CAE externo real ya aprovisionado (producción o demo persistente en
    /// staging) perdería de golpe su elegibilidad en <c>/delegaciones</c> al
    /// desplegar este cambio.
    /// </summary>
    public partial class AgregarCapacidadOperadorCaeExternoATenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PuedeActuarComoOperadorCaeExterno",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "Tenants",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                column: "PuedeActuarComoOperadorCaeExterno",
                value: false);

            migrationBuilder.Sql(
                "UPDATE \"Tenants\" SET \"PuedeActuarComoOperadorCaeExterno\" = TRUE " +
                "WHERE \"PerfilVocabulario\" = 'Consultora' AND \"EsPlataforma\" = FALSE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PuedeActuarComoOperadorCaeExterno",
                table: "Tenants");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// <c>Tenants.EsParticipanteTec</c>, el marcador único de Tenant participante TEC (D3, acordado con
    /// Inbound el 2026-11-19). Solo esquema: nace en falso, sin backfill, y su <c>DEFAULT</c> cubre a la
    /// ranura anterior de un relevo azul/verde, que inserta sin conocerla. El <c>UpdateData</c> del
    /// Tenant #1 lo genera EF porque la siembra declara todos los marcadores, y deja el mismo valor.
    /// <c>Tenants</c> no tiene RLS ni permisos por columna que cambien aquí; quién puede escribir el
    /// marcador lo fija la PR de lógica de D3.
    /// </summary>
    public partial class AnadeEsParticipanteTecATenants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EsParticipanteTec",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "Tenants",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                column: "EsParticipanteTec",
                value: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EsParticipanteTec",
                table: "Tenants");
        }
    }
}

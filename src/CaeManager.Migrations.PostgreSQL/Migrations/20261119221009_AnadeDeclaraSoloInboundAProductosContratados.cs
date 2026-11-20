using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// <c>ProductosContratados.DeclaraSoloInbound</c>, la declaración «solo Inbound» de la opción A' (decisión de Chris del
    /// 2026-11-19, DECISION_LOG e496f33): la marca el Actor de Plataforma TALVEG al contratar o corregir la línea Inbound, y
    /// <c>TenantSoloInbound</c> la exige, junto a la línea Inbound vigente y ninguna línea Outbound, para cerrar Outbound.
    ///
    /// <para>
    /// <b>Filas existentes.</b> Nace en <c>false</c> sin backfill: los Tenants con la línea Inbound contratada antes de la
    /// columna conservan Outbound. Es lo que pide la decisión para los Tenants históricos.
    /// </para>
    ///
    /// <para>
    /// <b>Privilegios y RLS, sin cambios.</b> <c>cae_app_runtime</c> la lee con su SELECT de tabla entera y la política
    /// <c>aislamiento_tenant</c>; <c>cae_app_aprovisionamiento</c> la escribe con su INSERT de tabla entera
    /// (<c>ContrataLineaInboundPorAprovisionamiento</c>) y no la lee: su SELECT por columnas
    /// (<c>FinalizaLineaInboundPorAprovisionamiento</c>) no la incluye. El CHECK
    /// <c>CK_ProductosContratados_DeclaraSoloInboundEnInbound</c> repite la invariante del dominio: solo va marcada en una
    /// fila de la línea Inbound.
    /// </para>
    ///
    /// <para>
    /// <b>Ranura anterior.</b> No conoce la columna: su INSERT toma el <c>DEFAULT false</c>, que cumple el CHECK, y su
    /// lectura no la pide.
    /// </para>
    /// </summary>
    public partial class AnadeDeclaraSoloInboundAProductosContratados : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DeclaraSoloInbound",
                table: "ProductosContratados",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProductosContratados_DeclaraSoloInboundEnInbound",
                table: "ProductosContratados",
                sql: "NOT \"DeclaraSoloInbound\" OR \"Linea\" = 'Inbound'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ProductosContratados_DeclaraSoloInboundEnInbound",
                table: "ProductosContratados");

            migrationBuilder.DropColumn(
                name: "DeclaraSoloInbound",
                table: "ProductosContratados");
        }
    }
}

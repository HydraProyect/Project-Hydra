using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarCatalogoProveedoresPlataformaCae : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DominiosProveedorPlataformaCae",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProveedorPlataformaCaeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Dominio = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DominiosProveedorPlataformaCae", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProveedoresPlataformaCae",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Codigo = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Nombre = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Grupo = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    Activo = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProveedoresPlataformaCae", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "DominiosProveedorPlataformaCae",
                columns: new[] { "Id", "Dominio", "ProveedorPlataformaCaeId" },
                values: new object[,]
                {
                    { new Guid("70000000-0000-0000-0000-000000000001"), "nalandaglobal.com", new Guid("60000000-0000-0000-0000-000000000001") },
                    { new Guid("70000000-0000-0000-0000-000000000002"), "dokify.net", new Guid("60000000-0000-0000-0000-000000000002") },
                    { new Guid("70000000-0000-0000-0000-000000000003"), "app.twind.io", new Guid("60000000-0000-0000-0000-000000000003") },
                    { new Guid("70000000-0000-0000-0000-000000000004"), "ctaimacae.net", new Guid("60000000-0000-0000-0000-000000000004") },
                    { new Guid("70000000-0000-0000-0000-000000000005"), "e-coordina.es", new Guid("60000000-0000-0000-0000-000000000005") },
                    { new Guid("70000000-0000-0000-0000-000000000006"), "metacontratas.com", new Guid("60000000-0000-0000-0000-000000000006") },
                    { new Guid("70000000-0000-0000-0000-000000000007"), "adding-plus.com", new Guid("60000000-0000-0000-0000-000000000007") },
                    { new Guid("70000000-0000-0000-0000-000000000008"), "ucae.es", new Guid("60000000-0000-0000-0000-000000000008") },
                    { new Guid("70000000-0000-0000-0000-000000000009"), "validate.es", new Guid("60000000-0000-0000-0000-000000000009") },
                    { new Guid("70000000-0001-0000-0000-000000000003"), "ctaima.com", new Guid("60000000-0000-0000-0000-000000000003") },
                    { new Guid("70000000-0001-0000-0000-000000000007"), "coordinaplus.net", new Guid("60000000-0000-0000-0000-000000000007") },
                    { new Guid("70000000-0001-0000-0000-000000000009"), "validate.network", new Guid("60000000-0000-0000-0000-000000000009") },
                    { new Guid("7000000a-0000-0000-0000-000000000001"), "egestiona.com", new Guid("6000000a-0000-0000-0000-000000000001") },
                    { new Guid("7000000a-0000-0000-0000-000000000002"), "smartosh.com", new Guid("6000000a-0000-0000-0000-000000000002") },
                    { new Guid("7000000a-0000-0000-0000-000000000003"), "ecogestor.com", new Guid("6000000a-0000-0000-0000-000000000003") },
                    { new Guid("7000000a-0000-0000-0000-000000000004"), "sabentis.com", new Guid("6000000a-0000-0000-0000-000000000004") },
                    { new Guid("7000000a-0000-0000-0000-000000000005"), "unifikas.com", new Guid("6000000a-0000-0000-0000-000000000005") },
                    { new Guid("7000000a-0000-0000-0000-000000000006"), "quironprevencion.com", new Guid("6000000a-0000-0000-0000-000000000006") },
                    { new Guid("7000000a-0000-0000-0000-000000000007"), "previntegral.com", new Guid("6000000a-0000-0000-0000-000000000007") },
                    { new Guid("7000000a-0000-0000-0000-000000000008"), "vithas.es", new Guid("6000000a-0000-0000-0000-000000000008") },
                    { new Guid("7000000a-0000-0000-0000-000000000009"), "ergasia.es", new Guid("6000000a-0000-0000-0000-000000000009") },
                    { new Guid("7000000a-0001-0000-0000-000000000001"), "egestiona.es", new Guid("6000000a-0000-0000-0000-000000000001") },
                    { new Guid("7000000a-0001-0000-0000-000000000004"), "quironprevencion.com", new Guid("6000000a-0000-0000-0000-000000000004") },
                    { new Guid("7000000b-0000-0000-0000-000000000001"), "valoraprevencion.es", new Guid("6000000b-0000-0000-0000-000000000001") },
                    { new Guid("7000000b-0000-0000-0000-000000000002"), "playcae.com", new Guid("6000000b-0000-0000-0000-000000000002") },
                    { new Guid("7000000b-0000-0000-0000-000000000004"), "archbus.com", new Guid("6000000b-0000-0000-0000-000000000004") },
                    { new Guid("7000000b-0000-0000-0000-000000000005"), "opground.com", new Guid("6000000b-0000-0000-0000-000000000005") }
                });

            migrationBuilder.InsertData(
                table: "ProveedoresPlataformaCae",
                columns: new[] { "Id", "Activo", "Codigo", "Grupo", "Nombre" },
                values: new object[,]
                {
                    { new Guid("60000000-0000-0000-0000-000000000001"), true, "nalanda", "Once For All", "Nalanda" },
                    { new Guid("60000000-0000-0000-0000-000000000002"), true, "dokify", "Once For All", "Dokify" },
                    { new Guid("60000000-0000-0000-0000-000000000003"), true, "twind", "Twind (CTAIMA Group)", "Twind" },
                    { new Guid("60000000-0000-0000-0000-000000000004"), true, "ctaimacae-legacy", "Twind (CTAIMA Group)", "CTAIMACAE (legacy)" },
                    { new Guid("60000000-0000-0000-0000-000000000005"), true, "e-coordina", "Twind (CTAIMA Group)", "e-coordina" },
                    { new Guid("60000000-0000-0000-0000-000000000006"), true, "metacontratas", null, "Metacontratas" },
                    { new Guid("60000000-0000-0000-0000-000000000007"), true, "coordinaplus", "Addingplus", "CoordinaPlus" },
                    { new Guid("60000000-0000-0000-0000-000000000008"), true, "ucae", null, "UCAE" },
                    { new Guid("60000000-0000-0000-0000-000000000009"), true, "validate", null, "Validate" },
                    { new Guid("6000000a-0000-0000-0000-000000000001"), true, "egestiona", null, "eGestiona" },
                    { new Guid("6000000a-0000-0000-0000-000000000002"), true, "smartosh", "Prevencontrol", "SmartOSH" },
                    { new Guid("6000000a-0000-0000-0000-000000000003"), true, "ecogestor", "Eurofins", "EcoGestor" },
                    { new Guid("6000000a-0000-0000-0000-000000000004"), true, "sabentis", null, "Sabentis" },
                    { new Guid("6000000a-0000-0000-0000-000000000005"), true, "unifikas", null, "Unifikas" },
                    { new Guid("6000000a-0000-0000-0000-000000000006"), true, "quiron-prevencion", "Quirónprevención", "Quirón Prevención" },
                    { new Guid("6000000a-0000-0000-0000-000000000007"), true, "previntegral", null, "Previntegral" },
                    { new Guid("6000000a-0000-0000-0000-000000000008"), true, "norprevencion", "Vithas", "Norprevención" },
                    { new Guid("6000000a-0000-0000-0000-000000000009"), true, "ergasia", null, "Ergasia" },
                    { new Guid("6000000b-0000-0000-0000-000000000001"), true, "valora", null, "Valora" },
                    { new Guid("6000000b-0000-0000-0000-000000000002"), true, "playcae", null, "PlayCAE" },
                    { new Guid("6000000b-0000-0000-0000-000000000003"), true, "docuprl", null, "DocuPRL" },
                    { new Guid("6000000b-0000-0000-0000-000000000004"), false, "arch", null, "Arch" },
                    { new Guid("6000000b-0000-0000-0000-000000000005"), false, "opground", null, "Opground" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_DominiosProveedorPlataformaCae_Dominio",
                table: "DominiosProveedorPlataformaCae",
                column: "Dominio");

            migrationBuilder.CreateIndex(
                name: "IX_ProveedoresPlataformaCae_Codigo",
                table: "ProveedoresPlataformaCae",
                column: "Codigo",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DominiosProveedorPlataformaCae");

            migrationBuilder.DropTable(
                name: "ProveedoresPlataformaCae");
        }
    }
}

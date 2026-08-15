using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarContactoAgendaRol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContactosAgendaRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactoAgendaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rol = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactosAgendaRoles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContactosAgendaRoles_ContactosAgenda_ContactoAgendaId",
                        column: x => x.ContactoAgendaId,
                        principalTable: "ContactosAgenda",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgendaRoles_ContactoAgendaId",
                table: "ContactosAgendaRoles",
                column: "ContactoAgendaId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgendaRoles_TenantId_ContactoAgendaId_Rol",
                table: "ContactosAgendaRoles",
                columns: new[] { "TenantId", "ContactoAgendaId", "Rol" },
                unique: true);

            // RLS en la misma tanda que crea la tabla — mismo criterio que
            // AgregarFirmaEnCampoDocumento.
            foreach (var tabla in TablasConRls)
            {
                migrationBuilder.Sql($@"
ALTER TABLE ""{tabla}"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""{tabla}"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""{tabla}"";
CREATE POLICY aislamiento_tenant ON ""{tabla}""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
            }
        }

        private static readonly string[] TablasConRls =
        [
            "ContactosAgendaRoles",
        ];

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContactosAgendaRoles");
        }
    }
}

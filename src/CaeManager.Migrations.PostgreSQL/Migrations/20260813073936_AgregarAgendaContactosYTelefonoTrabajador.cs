using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarAgendaContactosYTelefonoTrabajador : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Telefono",
                table: "Trabajadores",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ContactosAgenda",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: true),
                    EmpresaId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubcontrataId = table.Column<Guid>(type: "uuid", nullable: true),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: true),
                    Nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Telefono = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Cargo = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    Notas = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EsPredeterminado = table.Column<bool>(type: "boolean", nullable: false),
                    RecibeProgramacionVisitas = table.Column<bool>(type: "boolean", nullable: false),
                    RecibeFacturacion = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactosAgenda", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContactosAgenda_Centros_CentroId",
                        column: x => x.CentroId,
                        principalTable: "Centros",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContactosAgenda_Clientes_ClienteId",
                        column: x => x.ClienteId,
                        principalTable: "Clientes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContactosAgenda_Empresas_EmpresaId",
                        column: x => x.EmpresaId,
                        principalTable: "Empresas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContactosAgenda_Subcontratas_SubcontrataId",
                        column: x => x.SubcontrataId,
                        principalTable: "Subcontratas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ContactosAgendaTiposDocumento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactoAgendaId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoDocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContactosAgendaTiposDocumento", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContactosAgendaTiposDocumento_ContactosAgenda_ContactoAgend~",
                        column: x => x.ContactoAgendaId,
                        principalTable: "ContactosAgenda",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ContactosAgendaTiposDocumento_TiposDocumento_TipoDocumentoId",
                        column: x => x.TipoDocumentoId,
                        principalTable: "TiposDocumento",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Trabajadores_TenantId_Telefono",
                table: "Trabajadores",
                columns: new[] { "TenantId", "Telefono" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgenda_CentroId",
                table: "ContactosAgenda",
                column: "CentroId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgenda_ClienteId",
                table: "ContactosAgenda",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgenda_EmpresaId",
                table: "ContactosAgenda",
                column: "EmpresaId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgenda_SubcontrataId",
                table: "ContactosAgenda",
                column: "SubcontrataId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgenda_TenantId_CentroId",
                table: "ContactosAgenda",
                columns: new[] { "TenantId", "CentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgenda_TenantId_ClienteId",
                table: "ContactosAgenda",
                columns: new[] { "TenantId", "ClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgenda_TenantId_EmpresaId",
                table: "ContactosAgenda",
                columns: new[] { "TenantId", "EmpresaId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgenda_TenantId_SubcontrataId",
                table: "ContactosAgenda",
                columns: new[] { "TenantId", "SubcontrataId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgendaTiposDocumento_ContactoAgendaId",
                table: "ContactosAgendaTiposDocumento",
                column: "ContactoAgendaId");

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgendaTiposDocumento_TenantId_ContactoAgendaId_Tip~",
                table: "ContactosAgendaTiposDocumento",
                columns: new[] { "TenantId", "ContactoAgendaId", "TipoDocumentoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ContactosAgendaTiposDocumento_TipoDocumentoId",
                table: "ContactosAgendaTiposDocumento",
                column: "TipoDocumentoId");

            // CHECK XOR del propietario polimórfico — mismo criterio y misma
            // función (num_nonnulls) que "CK_Documentos_PropietarioXor": EF no
            // genera esta constraint, y sin ella la BD acepta un contacto con
            // dos dueños o con ninguno.
            migrationBuilder.Sql(
                """
                ALTER TABLE "ContactosAgenda" ADD CONSTRAINT "CK_ContactosAgenda_PropietarioXor"
                CHECK (num_nonnulls("ClienteId", "EmpresaId", "SubcontrataId", "CentroId") = 1);
                """);

            // Segunda línea de aislamiento (RUNBOOK-RLS.md). Sin esto,
            // PoliticasRlsCubrenModeloTests falla en CI — que es justo para lo
            // que existe ese test.
            migrationBuilder.Sql(@"
ALTER TABLE ""ContactosAgenda"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""ContactosAgenda"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""ContactosAgenda"";
CREATE POLICY aislamiento_tenant ON ""ContactosAgenda""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);

ALTER TABLE ""ContactosAgendaTiposDocumento"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""ContactosAgendaTiposDocumento"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""ContactosAgendaTiposDocumento"";
CREATE POLICY aislamiento_tenant ON ""ContactosAgendaTiposDocumento""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Las políticas RLS y el CHECK caen solos con las tablas; explícito
            // igual, para que revertir deje el esquema como estaba de verdad.
            migrationBuilder.Sql(@"
DROP POLICY IF EXISTS aislamiento_tenant ON ""ContactosAgendaTiposDocumento"";
DROP POLICY IF EXISTS aislamiento_tenant ON ""ContactosAgenda"";
ALTER TABLE ""ContactosAgenda"" DROP CONSTRAINT IF EXISTS ""CK_ContactosAgenda_PropietarioXor"";
");

            migrationBuilder.DropTable(
                name: "ContactosAgendaTiposDocumento");

            migrationBuilder.DropTable(
                name: "ContactosAgenda");

            migrationBuilder.DropIndex(
                name: "IX_Trabajadores_TenantId_Telefono",
                table: "Trabajadores");

            migrationBuilder.DropColumn(
                name: "Telefono",
                table: "Trabajadores");
        }
    }
}

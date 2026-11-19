using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

/// <summary>
/// Plazo de conservación de los Documentos por Tenant propietario y por tipo de documento (decisión del 2026-11-18,
/// R1 a R4). Aditiva:
/// <list type="bullet">
/// <item><c>ParametrosSistema."AniosRetencionDocumentos"</c>, NOT NULL con valor por defecto 5: todo Tenant propietario
/// existente queda en 5 años (R2), el mismo plazo global que regía hasta ahora.</item>
/// <item><c>TiposDocumento."AniosRetencionDocumentos"</c>, nulo: ningún tipo trae plazo propio y todos heredan el del
/// Tenant propietario.</item>
/// <item>Los dos con CHECK de 1 a 5 años enteros (R1), como defensa en la base además del dominio.</item>
/// <item><c>DocumentosPropuestosPurga</c>: la lista exacta de Documentos que propuso cada solicitud de purga (R4). La
/// ejecución purga esa lista y nada más, así que acortar un plazo nunca amplía una solicitud abierta. FK compuestas con
/// el Tenant hacia la solicitud y el Documento, RLS forzada como sus vecinas y, para el runtime, solo lectura y alta: la
/// lista no se edita, y desaparece por cascada con su solicitud o su Documento. Sin concesiones a Soporte TALVEG.</item>
/// </list>
/// El generador emitió además un <c>UpdateData</c> por cada fila sembrada con el valor que la columna ya recibe al
/// añadirse (5 o nulo); se retiran por redundantes. El modelo y el snapshot no cambian por ello.
/// </summary>
public partial class AnadePlazoRetencionDocumentosPorTenant : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "AniosRetencionDocumentos",
            table: "TiposDocumento",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "AniosRetencionDocumentos",
            table: "ParametrosSistema",
            type: "integer",
            nullable: false,
            defaultValue: 5);

        migrationBuilder.CreateTable(
            name: "DocumentosPropuestosPurga",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                SolicitudPurgaId = table.Column<Guid>(type: "uuid", nullable: false),
                DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DocumentosPropuestosPurga", x => x.Id);
                table.ForeignKey(
                    name: "FK_DocumentosPropuestosPurga_Documentos_TenantId_DocumentoId",
                    columns: x => new { x.TenantId, x.DocumentoId },
                    principalTable: "Documentos",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_DocumentosPropuestosPurga_SolicitudesPurga_TenantId_Solicit~",
                    columns: x => new { x.TenantId, x.SolicitudPurgaId },
                    principalTable: "SolicitudesPurga",
                    principalColumns: new[] { "TenantId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.AddCheckConstraint(
            name: "CK_TiposDocumento_AniosRetencionDocumentos",
            table: "TiposDocumento",
            sql: "\"AniosRetencionDocumentos\" IS NULL OR \"AniosRetencionDocumentos\" BETWEEN 1 AND 5");

        migrationBuilder.AddCheckConstraint(
            name: "CK_ParametrosSistema_AniosRetencionDocumentos",
            table: "ParametrosSistema",
            sql: "\"AniosRetencionDocumentos\" BETWEEN 1 AND 5");

        migrationBuilder.CreateIndex(
            name: "IX_DocumentosPropuestosPurga_TenantId_DocumentoId",
            table: "DocumentosPropuestosPurga",
            columns: new[] { "TenantId", "DocumentoId" });

        migrationBuilder.CreateIndex(
            name: "IX_DocumentosPropuestosPurga_TenantId_SolicitudPurgaId_Documen~",
            table: "DocumentosPropuestosPurga",
            columns: new[] { "TenantId", "SolicitudPurgaId", "DocumentoId" },
            unique: true);

        migrationBuilder.Sql("""
            ALTER TABLE public."DocumentosPropuestosPurga" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE ONLY public."DocumentosPropuestosPurga" FORCE ROW LEVEL SECURITY;
            CREATE POLICY aislamiento_tenant ON public."DocumentosPropuestosPurga"
                USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
            REVOKE ALL ON TABLE public."DocumentosPropuestosPurga" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
            GRANT SELECT, INSERT ON TABLE public."DocumentosPropuestosPurga" TO cae_app_runtime;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "DocumentosPropuestosPurga");

        migrationBuilder.DropCheckConstraint(
            name: "CK_TiposDocumento_AniosRetencionDocumentos",
            table: "TiposDocumento");

        migrationBuilder.DropCheckConstraint(
            name: "CK_ParametrosSistema_AniosRetencionDocumentos",
            table: "ParametrosSistema");

        migrationBuilder.DropColumn(
            name: "AniosRetencionDocumentos",
            table: "TiposDocumento");

        migrationBuilder.DropColumn(
            name: "AniosRetencionDocumentos",
            table: "ParametrosSistema");
    }
}

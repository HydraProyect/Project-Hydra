using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// F4 — introduce RelacionEmpresarial (ADR-011 § 2.4;
    /// f4-diseno-fisico-relacionempresarial-2026-08-26.md en el repositorio
    /// de negocio para el diseño completo, su revisión adversaria y la
    /// segunda pasada con evidencia real). NO retira las tres tablas legacy
    /// (EmpresasClientes/SubcontratasEmpresas/SubcontratasClientes): esta
    /// migración las puebla en RelacionEmpresarial una sola vez (esquema
    /// aditivo); la doble escritura en los comandos que las mantienen vivas
    /// entra en el mismo PR pero en un cambio de Application, no aquí.
    ///
    /// <c>ProyectoId</c> queda deliberadamente fuera del esquema — sin FK,
    /// índice ni semántica de cardinalidad demostrada, se difiere sin
    /// evidencia de que haga falta (§ 2 del diseño físico). La aciclicidad
    /// de <c>EnmarcadaEnId</c> NO está garantizada aquí — demostrado
    /// experimentalmente que el esquema acepta un ciclo de 2 pasos; la
    /// garantía vive en <c>IRelacionEmpresarialRepository.CreariaUnCicloAsync</c>.
    /// </summary>
    public partial class AgregarRelacionEmpresarial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RelacionesEmpresariales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProveedoraId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnmarcadaEnId = table.Column<Guid>(type: "uuid", nullable: true),
                    VigenciaDesde = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VigenciaHasta = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OrigenVigencia = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelacionesEmpresariales", x => x.Id);
                    table.UniqueConstraint("AK_RelacionesEmpresariales_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_RelacionesEmpresariales_NoAutorreferencia", "\"ProveedoraId\" <> \"ClienteId\"");
                    table.CheckConstraint("CK_RelacionesEmpresariales_NoEnmarcadaEnSiMisma", "\"EnmarcadaEnId\" IS DISTINCT FROM \"Id\"");
                    table.CheckConstraint("CK_RelacionesEmpresariales_VigenciaOrdenada", "\"VigenciaHasta\" IS NULL OR \"VigenciaHasta\" >= \"VigenciaDesde\"");
                    table.ForeignKey(
                        name: "FK_RelacionesEmpresariales_Empresas_TenantId_ClienteId",
                        columns: x => new { x.TenantId, x.ClienteId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RelacionesEmpresariales_Empresas_TenantId_ProveedoraId",
                        columns: x => new { x.TenantId, x.ProveedoraId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RelacionesEmpresariales_RelacionesEmpresariales_TenantId_En~",
                        columns: x => new { x.TenantId, x.EnmarcadaEnId },
                        principalTable: "RelacionesEmpresariales",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RelacionesEmpresariales_ParActivo",
                table: "RelacionesEmpresariales",
                columns: new[] { "TenantId", "ProveedoraId", "ClienteId" },
                unique: true,
                filter: "\"VigenciaHasta\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RelacionesEmpresariales_TenantId_ClienteId",
                table: "RelacionesEmpresariales",
                columns: new[] { "TenantId", "ClienteId" })
                .Annotation("Npgsql:IndexInclude", new[] { "ProveedoraId" });

            migrationBuilder.CreateIndex(
                name: "IX_RelacionesEmpresariales_TenantId_EnmarcadaEnId",
                table: "RelacionesEmpresariales",
                columns: new[] { "TenantId", "EnmarcadaEnId" });

            migrationBuilder.CreateIndex(
                name: "IX_RelacionesEmpresariales_TenantId_Id",
                table: "RelacionesEmpresariales",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RelacionesEmpresariales_TenantId_ProveedoraId",
                table: "RelacionesEmpresariales",
                columns: new[] { "TenantId", "ProveedoraId" })
                .Annotation("Npgsql:IndexInclude", new[] { "ClienteId" });

            // Segunda línea de aislamiento (RLS bajo cae_app_runtime) — mismo
            // patrón que HabilitarRlsVerificacionesExternaSubcontrata. El
            // filtro global de EF ya la protege; esto añade la garantía a
            // nivel de base de datos.
            migrationBuilder.Sql(@"
ALTER TABLE ""RelacionesEmpresariales"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""RelacionesEmpresariales"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS aislamiento_tenant ON ""RelacionesEmpresariales"";
CREATE POLICY aislamiento_tenant ON ""RelacionesEmpresariales""
    USING (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
    WITH CHECK (""TenantId"" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
");

            // Migración de datos, una sola vez: las 224 filas actuales de las
            // tres tablas legacy, exactamente con la lógica verificada en la
            // segunda revisión adversaria de F4 (17/33 SubcontratasClientes
            // con un único candidato coherente a EnmarcadaEnId; 16/33 sin
            // resolución automática — nunca heurística silenciosa, quedan con
            // EnmarcadaEnId NULL pendientes de decisión humana).
            //
            // VigenciaDesde = fecha de alta conocida de la CONTRAPARTE (nunca
            // de la Empresa propia) — cota de referencia del sistema, no
            // hecho contractual (ADR-011 § 17). OrigenVigencia siempre
            // InferidaPorMigracion para estas filas.
            migrationBuilder.Sql(@"
INSERT INTO ""RelacionesEmpresariales"" (""Id"",""TenantId"",""ProveedoraId"",""ClienteId"",""EnmarcadaEnId"",""VigenciaDesde"",""VigenciaHasta"",""OrigenVigencia"",""CreadoEnUtc"")
SELECT gen_random_uuid(), ec.""TenantId"", ec.""EmpresaId"", ec.""ClienteId"", NULL, cli.""CreadoEnUtc"", NULL, 'InferidaPorMigracion', now()
FROM ""EmpresasClientes"" ec JOIN ""Empresas"" cli ON cli.""Id"" = ec.""ClienteId"";

INSERT INTO ""RelacionesEmpresariales"" (""Id"",""TenantId"",""ProveedoraId"",""ClienteId"",""EnmarcadaEnId"",""VigenciaDesde"",""VigenciaHasta"",""OrigenVigencia"",""CreadoEnUtc"")
SELECT gen_random_uuid(), se.""TenantId"", se.""SubcontrataId"", se.""EmpresaId"", NULL, sub.""CreadoEnUtc"", NULL, 'InferidaPorMigracion', now()
FROM ""SubcontratasEmpresas"" se JOIN ""Empresas"" sub ON sub.""Id"" = se.""SubcontrataId"";

INSERT INTO ""RelacionesEmpresariales"" (""Id"",""TenantId"",""ProveedoraId"",""ClienteId"",""EnmarcadaEnId"",""VigenciaDesde"",""VigenciaHasta"",""OrigenVigencia"",""CreadoEnUtc"")
SELECT gen_random_uuid(), sc.""TenantId"", sc.""SubcontrataId"", sc.""ClienteId"",
    CASE WHEN cand.n_candidatos = 1 THEN cand.unico_id ELSE NULL END,
    subc.""CreadoEnUtc"", NULL, 'InferidaPorMigracion', now()
FROM ""SubcontratasClientes"" sc
JOIN ""Empresas"" subc ON subc.""Id"" = sc.""SubcontrataId""
CROSS JOIN LATERAL (
    SELECT COUNT(*) AS n_candidatos, (array_agg(r.""Id""))[1] AS unico_id
    FROM ""RelacionesEmpresariales"" r
    JOIN ""SubcontratasEmpresas"" se ON se.""TenantId"" = sc.""TenantId"" AND se.""SubcontrataId"" = sc.""SubcontrataId"" AND se.""EmpresaId"" = r.""ProveedoraId""
    WHERE r.""TenantId"" = sc.""TenantId"" AND r.""ClienteId"" = sc.""ClienteId"" AND r.""EnmarcadaEnId"" IS NULL
) cand;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RelacionesEmpresariales");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Cierre de F4 — retirada física de las tres tablas puente legacy.
    /// Desde F4.2c (PR #291) <c>RelacionEmpresarial</c> es su única fuente de
    /// escritura y no les quedaba ningún lector de producción.
    ///
    /// <para>
    /// <b>Decisión humana que autoriza este DROP (2026-08-27)</b>: 16 de las
    /// 33 filas de <c>SubcontratasClientes</c> quedaron con
    /// <c>EnmarcadaEnId</c> NULL en el backfill de F4 por no tener candidato
    /// único derivable. NO se resuelven a mano: la evidencia mostró que los
    /// datos de ambos entornos son del seeder (razones sociales de
    /// demostración) y que 13 de esas 16 no tienen ningún candidato posible,
    /// luego su NULL es correcto y no "pendiente". Fabricar semántica
    /// histórica para datos de demostración no tiene valor de negocio.
    /// </para>
    ///
    /// <para>
    /// <b>Preservación forense — condición de la decisión</b>: antes de este
    /// DROP se exportaron íntegras las tres tablas desde producción a un
    /// artefacto de migración identificado e inmutable, vinculado a esta
    /// misma migración:
    /// <c>Project-Hydra-Negocio/tecnico/artefactos-migracion/f4-tablas-puente-produccion-2026-08-27.sql</c>
    /// — 224 INSERTs (158 EmpresasClientes + 33 SubcontratasClientes + 33
    /// SubcontratasEmpresas, el desglose exacto que verificó R5),
    /// SHA-256 <c>83b52c9149ecd22b82ff766ef30795ac67c5bdfc33a5c645e21edc28555901a6</c>.
    /// Vive en el repositorio de negocio y NO en este, que es público: es
    /// un volcado de datos de producción. Su función es exclusivamente
    /// reconstrucción ante una auditoría posterior — <b>no</b> es un
    /// mecanismo operativo ni una fuente de verdad.
    /// </para>
    ///
    /// <para>
    /// <b>Sobre el <c>Down</c></b>: recrea las tablas VACÍAS, con sus FKs e
    /// índices — no restaura datos (para eso está el artefacto). Y recrea
    /// además sus políticas RLS, que el <c>DROP TABLE</c> se lleva por
    /// delante: sin ese bloque, un rollback dejaría tres tablas con
    /// <c>TenantId</c> sin ningún aislamiento, que es peor que no poder
    /// revertir. EF no genera esa parte porque la RLS vive en SQL crudo
    /// (<c>20260801120000_HabilitarRlsPostgres</c>), de donde se copia el
    /// patrón literal.
    /// </para>
    /// </summary>
    public partial class F4CierreDropTablasPuente : Migration
    {
        private static readonly string[] TablasPuente =
            ["EmpresasClientes", "SubcontratasClientes", "SubcontratasEmpresas"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmpresasClientes");

            migrationBuilder.DropTable(
                name: "SubcontratasClientes");

            migrationBuilder.DropTable(
                name: "SubcontratasEmpresas");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmpresasClientes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmpresasClientes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmpresasClientes_Empresas_TenantId_ClienteId",
                        columns: x => new { x.TenantId, x.ClienteId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EmpresasClientes_Empresas_TenantId_EmpresaId",
                        columns: x => new { x.TenantId, x.EmpresaId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubcontratasClientes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcontrataId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubcontratasClientes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubcontratasClientes_Empresas_TenantId_ClienteId",
                        columns: x => new { x.TenantId, x.ClienteId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubcontratasClientes_Empresas_TenantId_SubcontrataId",
                        columns: x => new { x.TenantId, x.SubcontrataId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubcontratasEmpresas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EmpresaId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubcontrataId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubcontratasEmpresas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubcontratasEmpresas_Empresas_TenantId_EmpresaId",
                        columns: x => new { x.TenantId, x.EmpresaId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubcontratasEmpresas_Empresas_TenantId_SubcontrataId",
                        columns: x => new { x.TenantId, x.SubcontrataId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmpresasClientes_ClienteId",
                table: "EmpresasClientes",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_EmpresasClientes_TenantId_ClienteId",
                table: "EmpresasClientes",
                columns: new[] { "TenantId", "ClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmpresasClientes_TenantId_EmpresaId_ClienteId",
                table: "EmpresasClientes",
                columns: new[] { "TenantId", "EmpresaId", "ClienteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasClientes_ClienteId",
                table: "SubcontratasClientes",
                column: "ClienteId");

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasClientes_TenantId_ClienteId",
                table: "SubcontratasClientes",
                columns: new[] { "TenantId", "ClienteId" });

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasClientes_TenantId_SubcontrataId_ClienteId",
                table: "SubcontratasClientes",
                columns: new[] { "TenantId", "SubcontrataId", "ClienteId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasEmpresas_EmpresaId",
                table: "SubcontratasEmpresas",
                column: "EmpresaId");

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasEmpresas_TenantId_EmpresaId",
                table: "SubcontratasEmpresas",
                columns: new[] { "TenantId", "EmpresaId" });

            migrationBuilder.CreateIndex(
                name: "IX_SubcontratasEmpresas_TenantId_SubcontrataId_EmpresaId",
                table: "SubcontratasEmpresas",
                columns: new[] { "TenantId", "SubcontrataId", "EmpresaId" },
                unique: true);

            // Las políticas RLS murieron con el DROP TABLE del Up. Recrearlas
            // aquí no es cosmética: sin este bloque, revertir dejaría tres
            // tablas con TenantId completamente abiertas entre tenants.
            // Patrón copiado literal de 20260801120000_HabilitarRlsPostgres.
            foreach (var tabla in TablasPuente)
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE "{tabla}" ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE "{tabla}" FORCE ROW LEVEL SECURITY;
                    DROP POLICY IF EXISTS aislamiento_tenant ON "{tabla}";
                    CREATE POLICY aislamiento_tenant ON "{tabla}"
                        USING ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                    """);
            }
        }
    }
}

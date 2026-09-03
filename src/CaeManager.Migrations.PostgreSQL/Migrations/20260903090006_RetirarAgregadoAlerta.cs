using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// REC-069/DEC-23: retira la tabla Alertas y el agregado de dominio que la
    /// respaldaba — ningún camino de producción la escribía ni la leía (gates
    /// 1-3 re-medidos en el handoff HO-069-01), la notificación real va por
    /// <c>NotificacionUsuario</c> y correo, y <c>ObtenerAlertasQuery</c> ya
    /// calculaba en vivo sobre Documentos, nunca desde esta tabla.
    ///
    /// <para>
    /// Gate 4 (filas existentes) medido en local sobre 2026-09-03: 0 filas.
    /// Staging y producción no son accesibles desde esta sesión (REC-130); el
    /// propietario debe ejecutar la misma consulta allí antes de autorizar el
    /// merge (ver RETURN PACKAGE de HO-069-01).
    /// </para>
    ///
    /// <para>
    /// <b>Sobre el <c>Down</c></b>: recrea la tabla (con el mismo orden físico
    /// de columnas que <c>20260731235023_LineaBase</c>), su FK y sus índices
    /// (EF los conoce), y además su política RLS, que el <c>DROP TABLE</c> del
    /// <c>Up</c> se lleva por delante — sin ese bloque, un rollback dejaría la
    /// tabla con <c>TenantId</c> sin ningún aislamiento. EF no genera esa
    /// parte porque la RLS vive en SQL crudo
    /// (<c>20260801120000_HabilitarRlsPostgres</c>), de donde se copia el
    /// patrón literal (mismo criterio que <c>F4CierreDropTablasPuente</c>).
    /// Los privilegios de <c>cae_app_runtime</c> sobre la tabla recreada los
    /// cubre <c>ALTER DEFAULT PRIVILEGES</c>, ya establecido por esa misma
    /// migración para toda tabla nueva — no hace falta repetirlo aquí.
    /// </para>
    /// </summary>
    public partial class RetirarAgregadoAlerta : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Alertas");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Alertas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nivel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FechaGeneracionUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alertas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Alertas_Documentos_TenantId_DocumentoId",
                        columns: x => new { x.TenantId, x.DocumentoId },
                        principalTable: "Documentos",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Alertas_DocumentoId",
                table: "Alertas",
                column: "DocumentoId");

            migrationBuilder.CreateIndex(
                name: "IX_Alertas_TenantId_DocumentoId",
                table: "Alertas",
                columns: new[] { "TenantId", "DocumentoId" });

            // La política RLS murió con el DROP TABLE del Up. Recrearla aquí
            // no es cosmética: sin este bloque, revertir dejaría la tabla con
            // TenantId completamente abierta entre tenants.
            // Patrón copiado literal de 20260801120000_HabilitarRlsPostgres.
            migrationBuilder.Sql("""
                ALTER TABLE "Alertas" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "Alertas" FORCE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS aislamiento_tenant ON "Alertas";
                CREATE POLICY aislamiento_tenant ON "Alertas"
                    USING ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }
    }
}

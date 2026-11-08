using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeGestionesAutomaticas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GestionesAutomaticas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    Origen = table.Column<int>(type: "integer", nullable: false),
                    NacidaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    SustituidaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SustituidaPorGestionId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GestionesAutomaticas", x => x.Id);
                    table.UniqueConstraint("AK_GestionesAutomaticas_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_GestionesAutomaticas_Documentos_TenantId_DocumentoId",
                        columns: x => new { x.TenantId, x.DocumentoId },
                        principalTable: "Documentos",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PasosGestionAutomatica",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GestionAutomaticaId = table.Column<Guid>(type: "uuid", nullable: false),
                    AcreditacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasosGestionAutomatica", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PasosGestionAutomatica_AcreditacionesDocumentoPlataforma_Te~",
                        columns: x => new { x.TenantId, x.AcreditacionId },
                        principalTable: "AcreditacionesDocumentoPlataforma",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PasosGestionAutomatica_GestionesAutomaticas_TenantId_Gestio~",
                        columns: x => new { x.TenantId, x.GestionAutomaticaId },
                        principalTable: "GestionesAutomaticas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GestionesAutomaticas_TenantId_DocumentoId",
                table: "GestionesAutomaticas",
                columns: new[] { "TenantId", "DocumentoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PasosGestionAutomatica_TenantId_AcreditacionId",
                table: "PasosGestionAutomatica",
                columns: new[] { "TenantId", "AcreditacionId" });

            migrationBuilder.CreateIndex(
                name: "IX_PasosGestionAutomatica_TenantId_GestionAutomaticaId_Acredit~",
                table: "PasosGestionAutomatica",
                columns: new[] { "TenantId", "GestionAutomaticaId", "AcreditacionId" },
                unique: true);

            // Aislamiento por Tenant igual que las tablas vecinas (AcreditacionesDocumentoPlataforma, Gestiones):
            // RLS forzada con la política aislamiento_tenant, y las claves foráneas compuestas por Tenant de arriba
            // impiden colgar una Gestión o un paso de un Documento o una acreditación de otro Tenant.
            // Soporte TALVEG lee, como en las acreditaciones que agrupa (especificación § 3: «en lectura»). Los pasos
            // no se modifican nunca: se fijan al nacer la Gestión y solo desaparecen en cascada.
            migrationBuilder.Sql("""
                ALTER TABLE public."GestionesAutomaticas" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."GestionesAutomaticas" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."GestionesAutomaticas"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."GestionesAutomaticas" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public."GestionesAutomaticas" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."GestionesAutomaticas" TO cae_app_soporte;

                ALTER TABLE public."PasosGestionAutomatica" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."PasosGestionAutomatica" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."PasosGestionAutomatica"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."PasosGestionAutomatica" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT, DELETE ON TABLE public."PasosGestionAutomatica" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."PasosGestionAutomatica" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PasosGestionAutomatica");

            migrationBuilder.DropTable(
                name: "GestionesAutomaticas");
        }
    }
}

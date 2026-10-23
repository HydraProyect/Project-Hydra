using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeContratosAcceso : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ContratosAcceso",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClienteEmpresarialId = table.Column<Guid>(type: "uuid", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmpresaContratistaId = table.Column<Guid>(type: "uuid", nullable: false),
                    FechaInicio = table.Column<DateOnly>(type: "date", nullable: false),
                    FechaFin = table.Column<DateOnly>(type: "date", nullable: true),
                    FechaCierreReal = table.Column<DateOnly>(type: "date", nullable: true),
                    Notas = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ContratosAcceso", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ContratosAcceso_Centros_TenantId_CentroId_ClienteEmpresaria~",
                        columns: x => new { x.TenantId, x.CentroId, x.ClienteEmpresarialId },
                        principalTable: "Centros",
                        principalColumns: new[] { "TenantId", "Id", "ClienteId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContratosAcceso_Empresas_TenantId_ClienteEmpresarialId",
                        columns: x => new { x.TenantId, x.ClienteEmpresarialId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ContratosAcceso_Empresas_TenantId_EmpresaContratistaId",
                        columns: x => new { x.TenantId, x.EmpresaContratistaId },
                        principalTable: "Empresas",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.CheckConstraint("CK_ContratosAcceso_CierreNoAnteriorAlInicio", "\"FechaCierreReal\" IS NULL OR \"FechaCierreReal\" >= \"FechaInicio\"");
                    table.CheckConstraint("CK_ContratosAcceso_ContratistaDistintaDelCliente", "\"ClienteEmpresarialId\" <> \"EmpresaContratistaId\"");
                    table.CheckConstraint("CK_ContratosAcceso_FinNoAnteriorAlInicio", "\"FechaFin\" IS NULL OR \"FechaFin\" >= \"FechaInicio\"");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ContratosAcceso_TenantId_CentroId",
                table: "ContratosAcceso",
                columns: new[] { "TenantId", "CentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContratosAcceso_TenantId_CentroId_ClienteEmpresarialId",
                table: "ContratosAcceso",
                columns: new[] { "TenantId", "CentroId", "ClienteEmpresarialId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContratosAcceso_TenantId_ClienteEmpresarialId",
                table: "ContratosAcceso",
                columns: new[] { "TenantId", "ClienteEmpresarialId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContratosAcceso_TenantId_EmpresaContratistaId",
                table: "ContratosAcceso",
                columns: new[] { "TenantId", "EmpresaContratistaId" });

            migrationBuilder.CreateIndex(
                name: "IX_ContratosAcceso_TenantId_Id",
                table: "ContratosAcceso",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            // Tabla de negocio del Tenant del Cliente empresarial: RLS estándar por TenantId, como Proyectos. A
            // diferencia de Proyectos, el runtime no recibe DELETE: el contrato de acceso solo se da de baja con soft
            // delete (UPDATE de EstaEliminado), ningún camino de la aplicación lo borra físicamente, y la retirada de
            // un Tenant de demo borra con la identidad de bootstrap, no con la de runtime (RetiradaTenantDemoService).
            // Soporte TALVEG solo lee. Sin GRANT a cae_app_aprovisionamiento, igual que Proyectos: el aprovisionamiento
            // de un Tenant no crea contratos de acceso.
            migrationBuilder.Sql("""
                ALTER TABLE public."ContratosAcceso" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."ContratosAcceso" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."ContratosAcceso"
                    USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
                    WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                REVOKE ALL ON TABLE public."ContratosAcceso" FROM cae_app_runtime;
                GRANT SELECT,INSERT,UPDATE ON TABLE public."ContratosAcceso" TO cae_app_runtime;
                REVOKE ALL ON TABLE public."ContratosAcceso" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."ContratosAcceso" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ContratosAcceso");
        }
    }
}

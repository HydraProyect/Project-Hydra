using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeProductosContratados : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProductosContratados",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Linea = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PagadorStripeCustomerId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Tramo = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Modalidad = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VigenteDesdeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    VigenteHastaUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Estado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductosContratados", x => x.Id);
                    table.CheckConstraint("CK_ProductosContratados_Estado", "\"Estado\" IN ('Vigente', 'Finalizado')");
                    table.CheckConstraint("CK_ProductosContratados_EstadoYVigencia", "(\"Estado\" = 'Vigente' AND \"VigenteHastaUtc\" IS NULL) OR (\"Estado\" = 'Finalizado' AND \"VigenteHastaUtc\" IS NOT NULL AND \"VigenteHastaUtc\" >= \"VigenteDesdeUtc\")");
                    table.CheckConstraint("CK_ProductosContratados_Linea", "\"Linea\" IN ('OutboundGestionPropia')");
                    table.CheckConstraint("CK_ProductosContratados_Modalidad", "\"Modalidad\" IN ('Mensual', 'Anual')");
                    table.CheckConstraint("CK_ProductosContratados_PagadorNoVacio", "btrim(\"PagadorStripeCustomerId\") <> ''");
                    table.CheckConstraint("CK_ProductosContratados_Tramo", "\"Tramo\" IN ('De1A10', 'De11A40', 'De41A80', 'De81A150', 'De151A300')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductosContratados_TenantId_Estado",
                table: "ProductosContratados",
                columns: new[] { "TenantId", "Estado" });

            // Producto Contratado (plano 4, RQ-30, D-6): tabla con Tenant, la del Tenant beneficiario, con la
            // misma RLS que sus vecinas: ENABLE + FORCE y la política aislamiento_tenant.
            //
            // Solo lectura para los roles de la aplicación. ALTER DEFAULT PRIVILEGES de la línea base dejaría a
            // cae_app_runtime con CRUD, y en un Tenant cualquier comando podría cambiar lo que su Tenant
            // beneficiario tiene contratado o quién lo paga. La vía de escritura la abre A6 junto con
            // CompletarAltaAutonomaCommand. cae_app_soporte lee, bajo la misma política, lo que tiene contratado
            // el Tenant de su Sesión Privilegiada; no hay secretos (el Customer de Stripe es un identificador).
            // cae_app_aprovisionamiento no la toca.
            //
            // Compatible con la ranura anterior durante el relevo: solo añade una tabla que la versión anterior
            // no conoce.
            migrationBuilder.Sql(
                """
                ALTER TABLE public."ProductosContratados" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."ProductosContratados" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."ProductosContratados"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."ProductosContratados" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT ON TABLE public."ProductosContratados" TO cae_app_runtime, cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProductosContratados");
        }
    }
}

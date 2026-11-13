using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeAceptacionesContractualesTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AceptacionesContractualesTenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    SolicitudAltaId = table.Column<Guid>(type: "uuid", nullable: true),
                    Documento = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    HashSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    DeclaraFacultades = table.Column<bool>(type: "boolean", nullable: false),
                    Ip = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    AceptadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImportePermanencia = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    RegistradaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AceptacionesContractualesTenant", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AcusesAceptacionContractualTenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AceptacionContractualTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioDestinatarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    EnviadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcusesAceptacionContractualTenant", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AceptacionesContractualesTenant_TenantId_Documento_Aceptada~",
                table: "AceptacionesContractualesTenant",
                columns: new[] { "TenantId", "Documento", "AceptadaEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AceptacionesContractualesTenant_TenantId_SolicitudAltaId_Do~",
                table: "AceptacionesContractualesTenant",
                columns: new[] { "TenantId", "SolicitudAltaId", "Documento" },
                unique: true,
                filter: "\"SolicitudAltaId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AcusesAceptacionContractualTenant_AceptacionContractualTena~",
                table: "AcusesAceptacionContractualTenant",
                columns: new[] { "AceptacionContractualTenantId", "TenantId" });

            // Evidencia contractual del Tenant propietario (#2009): RLS por Tenant y solo INSERT y SELECT para
            // la aplicación. Sin UPDATE ni DELETE, nadie de la aplicación reescribe ni borra lo aceptado ni sus
            // acuses (el REVOKE quita lo que ALTER DEFAULT PRIVILEGES concede a toda tabla nueva). Sin nada para
            // cae_app_soporte: guarda la IP de quien aceptó y no lo necesita para atender. Sin política
            // restrictiva de Administrador en la base de datos: quien inserta en el alta es el procesador del
            // pago, sin usuario en sesión; esa regla la impone RegistroAceptacionContractualAlta.
            migrationBuilder.Sql("""
                ALTER TABLE public."AceptacionesContractualesTenant" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."AceptacionesContractualesTenant" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."AceptacionesContractualesTenant"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."AceptacionesContractualesTenant" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT ON TABLE public."AceptacionesContractualesTenant" TO cae_app_runtime;

                ALTER TABLE public."AcusesAceptacionContractualTenant" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."AcusesAceptacionContractualTenant" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."AcusesAceptacionContractualTenant"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."AcusesAceptacionContractualTenant" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT ON TABLE public."AcusesAceptacionContractualTenant" TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AceptacionesContractualesTenant");

            migrationBuilder.DropTable(
                name: "AcusesAceptacionContractualTenant");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeSolicitudesDerechosInteresado : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConcesionesCapacidadTenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Capacidad = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ConcedidaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConcedidaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevocadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevocadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConcesionesCapacidadTenant", x => x.Id);
                    table.CheckConstraint("CK_ConcesionesCapacidadTenant_Capacidad", "\"Capacidad\" IN ('DerechosDelInteresado')");
                    table.CheckConstraint("CK_ConcesionesCapacidadTenant_RevocacionCoherente", "(\"RevocadaEnUtc\" IS NULL) = (\"RevocadaPorUsuarioId\" IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "SolicitudesDerechoInteresado",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Canal = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    FechaRecepcion = table.Column<DateOnly>(type: "date", nullable: false),
                    NombreInteresado = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TrabajadorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Descripcion = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Estado = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PlazoOrdinario = table.Column<DateOnly>(type: "date", nullable: false),
                    PlazoVencimiento = table.Column<DateOnly>(type: "date", nullable: false),
                    RegistradaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistradaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IdentidadVerificadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdentidadVerificadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TramitacionIniciadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    TramitacionIniciadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MotivoProrroga = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ProrrogadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProrrogadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CerradaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    CerradaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Resultado = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    MotivoDenegacion = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolicitudesDerechoInteresado", x => x.Id);
                    table.CheckConstraint("CK_SolicitudesDerechoInteresado_Canal", "\"Canal\" IN ('CorreoElectronico', 'CorreoPostal', 'Presencial', 'Telefono', 'Otro')");
                    table.CheckConstraint("CK_SolicitudesDerechoInteresado_CierreCoherente", "(\"Estado\" IN ('Resuelta', 'Denegada')) = (\"CerradaPorUsuarioId\" IS NOT NULL AND \"CerradaEnUtc\" IS NOT NULL) AND (\"CerradaPorUsuarioId\" IS NULL) = (\"CerradaEnUtc\" IS NULL)");
                    table.CheckConstraint("CK_SolicitudesDerechoInteresado_Estado", "\"Estado\" IN ('Recibida', 'IdentidadVerificada', 'EnCurso', 'Resuelta', 'Denegada', 'Reenviada')");
                    table.CheckConstraint("CK_SolicitudesDerechoInteresado_MotivoSoloAlDenegar", "(\"Estado\" = 'Denegada') = (\"MotivoDenegacion\" IS NOT NULL)");
                    table.CheckConstraint("CK_SolicitudesDerechoInteresado_Plazo", "\"PlazoVencimiento\" >= \"PlazoOrdinario\" AND \"PlazoOrdinario\" > \"FechaRecepcion\" AND \"PlazoVencimiento\" <= (\"FechaRecepcion\" + INTERVAL '3 months')::date");
                    table.CheckConstraint("CK_SolicitudesDerechoInteresado_ProrrogaCoherente", "(\"ProrrogadaEnUtc\" IS NULL) = (\"ProrrogadaPorUsuarioId\" IS NULL) AND (\"ProrrogadaEnUtc\" IS NULL) = (\"MotivoProrroga\" IS NULL) AND (\"ProrrogadaEnUtc\" IS NOT NULL OR \"PlazoVencimiento\" = \"PlazoOrdinario\")");
                    table.CheckConstraint("CK_SolicitudesDerechoInteresado_ResueltaConIdentidadVerificada", "\"Estado\" <> 'Resuelta' OR (\"IdentidadVerificadaEnUtc\" IS NOT NULL AND \"TramitacionIniciadaEnUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_SolicitudesDerechoInteresado_ResultadoSoloAlResolver", "\"Resultado\" IS NULL OR \"Estado\" = 'Resuelta'");
                    table.CheckConstraint("CK_SolicitudesDerechoInteresado_Tipo", "\"Tipo\" IN ('Acceso', 'Rectificacion', 'Supresion', 'Limitacion', 'Portabilidad', 'Oposicion', 'DecisionesAutomatizadas')");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConcesionesCapacidadTenant_UnaVigentePorUsuarioYCapacidad",
                table: "ConcesionesCapacidadTenant",
                columns: new[] { "TenantId", "UsuarioId", "Capacidad" },
                unique: true,
                filter: "\"RevocadaEnUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesDerechoInteresado_TenantId_PlazoVencimiento",
                table: "SolicitudesDerechoInteresado",
                columns: new[] { "TenantId", "PlazoVencimiento" });

            // Las dos tablas son del Tenant propietario (#2002, decisión D-3 del 2026-11-15): aislamiento por Tenant con
            // FORCE, también para el dueño de la tabla. El runtime consulta, registra y actualiza solo las columnas de
            // cada paso (la solicitud: estado, quién y cuándo dio cada paso, la prórroga, el cierre y el token de
            // concurrencia; la concesión: su revocación y el token); nunca reescribe lo registrado ni borra (la retirada
            // de un Tenant de demostración usa su propia identidad). Soporte TALVEG y el aprovisionamiento no tienen
            // acceso: la solicitud lleva datos personales del interesado y quién puede resolverla es cosa del Tenant.
            migrationBuilder.Sql("""
                ALTER TABLE public."SolicitudesDerechoInteresado" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."SolicitudesDerechoInteresado" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."SolicitudesDerechoInteresado"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."SolicitudesDerechoInteresado" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT ON TABLE public."SolicitudesDerechoInteresado" TO cae_app_runtime;
                GRANT UPDATE ("Estado", "PlazoVencimiento",
                              "IdentidadVerificadaPorUsuarioId", "IdentidadVerificadaEnUtc",
                              "TramitacionIniciadaPorUsuarioId", "TramitacionIniciadaEnUtc",
                              "MotivoProrroga", "ProrrogadaPorUsuarioId", "ProrrogadaEnUtc",
                              "CerradaPorUsuarioId", "CerradaEnUtc", "Resultado", "MotivoDenegacion", "Version")
                    ON TABLE public."SolicitudesDerechoInteresado" TO cae_app_runtime;

                ALTER TABLE public."ConcesionesCapacidadTenant" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."ConcesionesCapacidadTenant" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."ConcesionesCapacidadTenant"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."ConcesionesCapacidadTenant" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT ON TABLE public."ConcesionesCapacidadTenant" TO cae_app_runtime;
                GRANT UPDATE ("RevocadaPorUsuarioId", "RevocadaEnUtc", "Version")
                    ON TABLE public."ConcesionesCapacidadTenant" TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConcesionesCapacidadTenant");

            migrationBuilder.DropTable(
                name: "SolicitudesDerechoInteresado");
        }
    }
}

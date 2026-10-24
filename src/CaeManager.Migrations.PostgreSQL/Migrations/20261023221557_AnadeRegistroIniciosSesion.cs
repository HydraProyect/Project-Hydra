using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeRegistroIniciosSesion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PoliticasPaisesInicioSesion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Activa = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ActualizadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PaisesPermitidos = table.Column<List<string>>(type: "text[]", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PoliticasPaisesInicioSesion", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RegistrosInicioSesion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorRealUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    OcurridoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Metodo = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Resultado = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    DireccionIp = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    PaisIso = table.Column<string>(type: "character(2)", fixedLength: true, maxLength: 2, nullable: true),
                    Ciudad = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Latitud = table.Column<double>(type: "double precision", nullable: true),
                    Longitud = table.Column<double>(type: "double precision", nullable: true),
                    HuellaDispositivo = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Dispositivo = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Senales = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RegistrosInicioSesion", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PoliticasPaisesInicioSesion_TenantId",
                table: "PoliticasPaisesInicioSesion",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosInicioSesion_OcurridoEnUtc",
                table: "RegistrosInicioSesion",
                column: "OcurridoEnUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosInicioSesion_TenantId_OcurridoEnUtc",
                table: "RegistrosInicioSesion",
                columns: new[] { "TenantId", "OcurridoEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RegistrosInicioSesion_TenantId_UsuarioId_OcurridoEnUtc",
                table: "RegistrosInicioSesion",
                columns: new[] { "TenantId", "UsuarioId", "OcurridoEnUtc" });

            // Inicios de sesión sospechosos. Las dos tablas viven en el Tenant propietario de la cuenta, con la RLS
            // estándar (aislamiento_tenant sobre app.tenant_id, ENABLE + FORCE), como el resto de la auditoría.
            //
            // "RegistrosInicioSesion" es append-only: cae_app_runtime solo SELECT e INSERT (sin UPDATE ni DELETE, lo
            // vigila AuditoriaSoloInsercionBajoRuntimeTests); cae_app_soporte, SELECT.
            // "PoliticasPaisesInicioSesion" es configuración del Tenant (bloqueo por país, desactivado por defecto: sin
            // fila no bloquea): runtime SELECT, INSERT y UPDATE; soporte, SELECT.
            //
            // Retención de 12 meses: app_purgar_inicios_sesion(), SECURITY DEFINER, sin parámetros y con el corte
            // escrito dentro, así que el runtime puede pedir la purga pero no adelantarla ni borrar una fila concreta.
            // Como las funciones de AnadeLecturaInboundEntreTenants, depende de que su propietario (el rol que aplica las
            // migraciones) sea superusuario o tenga BYPASSRLS: con FORCE ROW LEVEL SECURITY, sin eso el DELETE no vería
            // ninguna fila y la purga «funcionaría» sin borrar nada. La función lo comprueba y falla en vez de callar.
            migrationBuilder.Sql(
                """
                ALTER TABLE public."RegistrosInicioSesion" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."RegistrosInicioSesion" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."RegistrosInicioSesion"
                    USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
                    WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                REVOKE ALL ON TABLE public."RegistrosInicioSesion" FROM cae_app_runtime;
                GRANT SELECT, INSERT ON TABLE public."RegistrosInicioSesion" TO cae_app_runtime;
                REVOKE ALL ON TABLE public."RegistrosInicioSesion" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."RegistrosInicioSesion" TO cae_app_soporte;
                REVOKE ALL ON TABLE public."RegistrosInicioSesion" FROM cae_app_aprovisionamiento;

                ALTER TABLE public."PoliticasPaisesInicioSesion" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."PoliticasPaisesInicioSesion" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."PoliticasPaisesInicioSesion"
                    USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
                    WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                REVOKE ALL ON TABLE public."PoliticasPaisesInicioSesion" FROM cae_app_runtime;
                GRANT SELECT, INSERT, UPDATE ON TABLE public."PoliticasPaisesInicioSesion" TO cae_app_runtime;
                REVOKE ALL ON TABLE public."PoliticasPaisesInicioSesion" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."PoliticasPaisesInicioSesion" TO cae_app_soporte;
                REVOKE ALL ON TABLE public."PoliticasPaisesInicioSesion" FROM cae_app_aprovisionamiento;

                CREATE FUNCTION public.app_purgar_inicios_sesion() RETURNS integer
                    LANGUAGE plpgsql SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                DECLARE
                    v_filas integer;
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles
                                    WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                        RAISE EXCEPTION 'El rol % no evita la RLS: la purga de inicios de sesión no vería ninguna fila', current_user;
                    END IF;
                    DELETE FROM public."RegistrosInicioSesion"
                     WHERE "OcurridoEnUtc" < pg_catalog.now() - interval '12 months';
                    GET DIAGNOSTICS v_filas = ROW_COUNT;
                    RETURN v_filas;
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_purgar_inicios_sesion() FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_purgar_inicios_sesion() TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION public.app_purgar_inicios_sesion();");

            migrationBuilder.DropTable(
                name: "PoliticasPaisesInicioSesion");

            migrationBuilder.DropTable(
                name: "RegistrosInicioSesion");
        }
    }
}

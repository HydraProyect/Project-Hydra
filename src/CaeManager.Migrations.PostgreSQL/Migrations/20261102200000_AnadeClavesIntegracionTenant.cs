using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Clave de Integración del Tenant, incremento 1 de la vía de integración duradera (decisiones del
    /// 2026-10-31; OWASP ASVS 4.0.3 V4.1.3 y V2.10.4). Otra tabla y no una columna en <c>ClavesApi</c>,
    /// porque sus invariantes son opuestas a las de la Clave de Soporte TALVEG.
    ///
    /// <para>
    /// <c>ClavesIntegracionTenant</c> nace con RLS forzada y la política <c>aislamiento_tenant</c>.
    /// <c>cae_app_runtime</c> lee, inserta y actualiza, y no borra: una clave se revoca, no desaparece.
    /// <c>cae_app_soporte</c> lee por columnas todas salvo <c>HashClave</c> y no escribe. La migración
    /// retira antes lo que los privilegios por defecto de la línea base conceden a una tabla nueva.
    /// </para>
    ///
    /// <para>
    /// <c>app_tenant_de_clave_integracion</c> es la única lectura sin Tenant: <c>SECURITY DEFINER</c>,
    /// devuelve un <c>uuid</c> y está acotada al hash, como <c>app_tenant_de_clave_api</c>, pero no
    /// comparte tabla con ella. Solo la ejecuta <c>cae_app_runtime</c> y, como las demás resoluciones
    /// transversales, exige el contexto firmado. El runtime solo actualiza las columnas de estado: el
    /// hash, los alcances, el Tenant y la autoría no cambian tras emitir.
    /// </para>
    ///
    /// <para>
    /// <c>Tenants.IntegracionHabilitada</c> es la marca de disponibilidad comercial por Tenant (D-5). Nace
    /// en falso, y su <c>DEFAULT</c> cubre a la ranura anterior de un relevo azul/verde, que inserta sin
    /// conocerla. Ni la tabla ni la función las toca el código anterior, así que no hay ventana rota.
    /// </para>
    /// </summary>
    public partial class AnadeClavesIntegracionTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IntegracionHabilitada",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ClavesIntegracionTenant",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SistemaConsumidor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PrefijoVisible = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    HashClave = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Alcances = table.Column<List<string>>(type: "text[]", nullable: false),
                    CaducaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SuspendidaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MotivoSuspension = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RevocadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    InvalidadaDefinitivamenteEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SucedeAClaveId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    UltimoUsoUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClavesIntegracionTenant", x => x.Id);
                });

            migrationBuilder.UpdateData(
                table: "Tenants",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000001"),
                column: "IntegracionHabilitada",
                value: false);

            migrationBuilder.CreateIndex(
                name: "IX_ClavesIntegracionTenant_HashClave",
                table: "ClavesIntegracionTenant",
                column: "HashClave",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClavesIntegracionTenant_TenantId_SucedeAClaveId",
                table: "ClavesIntegracionTenant",
                columns: new[] { "TenantId", "SucedeAClaveId" },
                unique: true,
                filter: "\"SucedeAClaveId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClavesIntegracionTenant_TenantId",
                table: "ClavesIntegracionTenant",
                column: "TenantId");

            migrationBuilder.Sql("""
                ALTER TABLE public."ClavesIntegracionTenant" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."ClavesIntegracionTenant" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."ClavesIntegracionTenant"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."ClavesIntegracionTenant" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT ON TABLE public."ClavesIntegracionTenant" TO cae_app_runtime;
                GRANT UPDATE ("CaducaEnUtc", "SuspendidaEnUtc", "MotivoSuspension", "RevocadaEnUtc",
                              "InvalidadaDefinitivamenteEnUtc", "UltimoUsoUtc", "Version",
                              "EstaEliminado", "EliminadoEnUtc", "EliminadoPorUsuarioId")
                    ON TABLE public."ClavesIntegracionTenant" TO cae_app_runtime;
                GRANT SELECT ("Id", "TenantId", "Nombre", "SistemaConsumidor", "PrefijoVisible", "Alcances", "CaducaEnUtc",
                              "SuspendidaEnUtc", "MotivoSuspension", "RevocadaEnUtc", "InvalidadaDefinitivamenteEnUtc",
                              "SucedeAClaveId", "CreadaPorUsuarioId", "UltimoUsoUtc", "Version", "CreadoEnUtc",
                              "EstaEliminado", "EliminadoEnUtc", "EliminadoPorUsuarioId")
                    ON TABLE public."ClavesIntegracionTenant" TO cae_app_soporte;

                CREATE FUNCTION public.app_tenant_de_clave_integracion(hash_clave text) RETURNS uuid
                    LANGUAGE plpgsql STABLE SECURITY DEFINER
                    SET search_path TO 'pg_catalog', 'pg_temp'
                    AS $$
                BEGIN
                    PERFORM public.app_exigir_contexto_firmado();
                    RETURN (SELECT c."TenantId" FROM public."ClavesIntegracionTenant" c
                             WHERE c."HashClave" = hash_clave AND c."EstaEliminado" = false);
                END;
                $$;
                REVOKE ALL ON FUNCTION public.app_tenant_de_clave_integracion(text) FROM PUBLIC, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT EXECUTE ON FUNCTION public.app_tenant_de_clave_integracion(text) TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP FUNCTION public.app_tenant_de_clave_integracion(text);""");

            migrationBuilder.DropTable(
                name: "ClavesIntegracionTenant");

            migrationBuilder.DropColumn(
                name: "IntegracionHabilitada",
                table: "Tenants");
        }
    }
}

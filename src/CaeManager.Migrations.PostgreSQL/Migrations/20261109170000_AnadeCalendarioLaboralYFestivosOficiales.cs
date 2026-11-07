using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeCalendarioLaboralYFestivosOficiales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CalendariosLaboralesPersona",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Comunidad = table.Column<int>(type: "integer", nullable: true),
                    Municipio = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    AplicaJornadaIntensiva = table.Column<bool>(type: "boolean", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CalendariosLaboralesPersona", x => x.Id);
                    table.CheckConstraint("CK_CalendariosLaboralesPersona_MunicipioConComunidad", "\"Municipio\" IS NULL OR \"Comunidad\" IS NOT NULL");
                });

            migrationBuilder.CreateTable(
                name: "FestivosOficiales",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Fecha = table.Column<DateOnly>(type: "date", nullable: false),
                    Nombre = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Ambito = table.Column<int>(type: "integer", nullable: false),
                    Comunidad = table.Column<int>(type: "integer", nullable: true),
                    Municipio = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FestivosOficiales", x => x.Id);
                    table.CheckConstraint("CK_FestivosOficiales_ComunidadSegunAmbito", "(\"Ambito\" = 1 AND \"Comunidad\" IS NULL) OR (\"Ambito\" IN (2, 3) AND \"Comunidad\" IS NOT NULL)");
                    table.CheckConstraint("CK_FestivosOficiales_MunicipioSoloLocal", "(\"Ambito\" = 3) = (\"Municipio\" IS NOT NULL)");
                });

            migrationBuilder.InsertData(
                table: "FestivosOficiales",
                columns: new[] { "Id", "Ambito", "Comunidad", "Fecha", "Municipio", "Nombre" },
                values: new object[,]
                {
                    { new Guid("000b4920-0001-0000-4645-535400000000"), 1, null, new DateOnly(2026, 1, 1), null, "Año Nuevo" },
                    { new Guid("000b4925-0001-0000-4645-535400000000"), 1, null, new DateOnly(2026, 1, 6), null, "Epifanía del Señor" },
                    { new Guid("000b496d-0002-0010-4645-535400000000"), 2, 16, new DateOnly(2026, 3, 19), null, "San José" },
                    { new Guid("000b497b-0002-0002-4645-535400000000"), 2, 2, new DateOnly(2026, 4, 2), null, "Jueves Santo" },
                    { new Guid("000b497b-0002-000b-4645-535400000000"), 2, 11, new DateOnly(2026, 4, 2), null, "Jueves Santo" },
                    { new Guid("000b497b-0002-0010-4645-535400000000"), 2, 16, new DateOnly(2026, 4, 2), null, "Jueves Santo" },
                    { new Guid("000b497c-0001-0000-4645-535400000000"), 1, null, new DateOnly(2026, 4, 3), null, "Viernes Santo" },
                    { new Guid("000b497f-0002-0009-4645-535400000000"), 2, 9, new DateOnly(2026, 4, 6), null, "Lunes de Pascua" },
                    { new Guid("000b497f-0002-0010-4645-535400000000"), 2, 16, new DateOnly(2026, 4, 6), null, "Lunes de Pascua" },
                    { new Guid("000b4990-0002-0002-4645-535400000000"), 2, 2, new DateOnly(2026, 4, 23), null, "San Jorge · Día de Aragón" },
                    { new Guid("000b4998-0001-0000-4645-535400000000"), 1, null, new DateOnly(2026, 5, 1), null, "Fiesta del Trabajo" },
                    { new Guid("000b49ce-0002-0009-4645-535400000000"), 2, 9, new DateOnly(2026, 6, 24), null, "San Juan" },
                    { new Guid("000b49ed-0002-0010-4645-535400000000"), 2, 16, new DateOnly(2026, 7, 25), null, "Santiago Apóstol" },
                    { new Guid("000b4a02-0001-0000-4645-535400000000"), 1, null, new DateOnly(2026, 8, 15), null, "Asunción de la Virgen" },
                    { new Guid("000b4a1a-0002-000b-4645-535400000000"), 2, 11, new DateOnly(2026, 9, 8), null, "Día de Extremadura" },
                    { new Guid("000b4a1d-0002-0009-4645-535400000000"), 2, 9, new DateOnly(2026, 9, 11), null, "Fiesta Nacional de Cataluña" },
                    { new Guid("000b4a3c-0001-0000-4645-535400000000"), 1, null, new DateOnly(2026, 10, 12), null, "Fiesta Nacional de España" },
                    { new Guid("000b4a51-0002-0002-4645-535400000000"), 2, 2, new DateOnly(2026, 11, 2), null, "Lunes siguiente a Todos los Santos" },
                    { new Guid("000b4a51-0002-000b-4645-535400000000"), 2, 11, new DateOnly(2026, 11, 2), null, "Lunes siguiente a Todos los Santos" },
                    { new Guid("000b4a74-0002-0002-4645-535400000000"), 2, 2, new DateOnly(2026, 12, 7), null, "Lunes siguiente al Día de la Constitución" },
                    { new Guid("000b4a74-0002-000b-4645-535400000000"), 2, 11, new DateOnly(2026, 12, 7), null, "Lunes siguiente al Día de la Constitución" },
                    { new Guid("000b4a75-0001-0000-4645-535400000000"), 1, null, new DateOnly(2026, 12, 8), null, "Inmaculada Concepción" },
                    { new Guid("000b4a86-0001-0000-4645-535400000000"), 1, null, new DateOnly(2026, 12, 25), null, "Natividad del Señor" },
                    { new Guid("000b4a87-0002-0009-4645-535400000000"), 2, 9, new DateOnly(2026, 12, 26), null, "San Esteban" },
                    { new Guid("000b4a8d-0001-0000-4645-535400000000"), 1, null, new DateOnly(2027, 1, 1), null, "Año Nuevo" },
                    { new Guid("000b4a92-0001-0000-4645-535400000000"), 1, null, new DateOnly(2027, 1, 6), null, "Epifanía del Señor" },
                    { new Guid("000b4ae0-0002-0002-4645-535400000000"), 2, 2, new DateOnly(2027, 3, 25), null, "Jueves Santo" },
                    { new Guid("000b4ae0-0002-000b-4645-535400000000"), 2, 11, new DateOnly(2027, 3, 25), null, "Jueves Santo" },
                    { new Guid("000b4ae0-0002-0010-4645-535400000000"), 2, 16, new DateOnly(2027, 3, 25), null, "Jueves Santo" },
                    { new Guid("000b4ae1-0001-0000-4645-535400000000"), 1, null, new DateOnly(2027, 3, 26), null, "Viernes Santo" },
                    { new Guid("000b4ae4-0002-0009-4645-535400000000"), 2, 9, new DateOnly(2027, 3, 29), null, "Lunes de Pascua" },
                    { new Guid("000b4ae4-0002-0010-4645-535400000000"), 2, 16, new DateOnly(2027, 3, 29), null, "Lunes de Pascua" },
                    { new Guid("000b4afd-0002-0002-4645-535400000000"), 2, 2, new DateOnly(2027, 4, 23), null, "San Jorge · Día de Aragón" },
                    { new Guid("000b4b05-0001-0000-4645-535400000000"), 1, null, new DateOnly(2027, 5, 1), null, "Fiesta del Trabajo" },
                    { new Guid("000b4b3b-0002-0009-4645-535400000000"), 2, 9, new DateOnly(2027, 6, 24), null, "San Juan" },
                    { new Guid("000b4b70-0002-0002-4645-535400000000"), 2, 2, new DateOnly(2027, 8, 16), null, "Lunes siguiente a la Asunción de la Virgen" },
                    { new Guid("000b4b87-0002-000b-4645-535400000000"), 2, 11, new DateOnly(2027, 9, 8), null, "Día de Extremadura" },
                    { new Guid("000b4b8a-0002-0009-4645-535400000000"), 2, 9, new DateOnly(2027, 9, 11), null, "Fiesta Nacional de Cataluña" },
                    { new Guid("000b4ba4-0002-0010-4645-535400000000"), 2, 16, new DateOnly(2027, 10, 7), null, "Aniversario del primer Gobierno Vasco" },
                    { new Guid("000b4ba8-0002-000b-4645-535400000000"), 2, 11, new DateOnly(2027, 10, 11), null, "Traslado de la Asunción de la Virgen" },
                    { new Guid("000b4ba9-0001-0000-4645-535400000000"), 1, null, new DateOnly(2027, 10, 12), null, "Fiesta Nacional de España" },
                    { new Guid("000b4bbd-0001-0000-4645-535400000000"), 1, null, new DateOnly(2027, 11, 1), null, "Todos los Santos" },
                    { new Guid("000b4be0-0001-0000-4645-535400000000"), 1, null, new DateOnly(2027, 12, 6), null, "Día de la Constitución Española" },
                    { new Guid("000b4be2-0001-0000-4645-535400000000"), 1, null, new DateOnly(2027, 12, 8), null, "Inmaculada Concepción" },
                    { new Guid("000b4bf3-0001-0000-4645-535400000000"), 1, null, new DateOnly(2027, 12, 25), null, "Natividad del Señor" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_CalendariosLaboralesPersona_TenantId_UsuarioId",
                table: "CalendariosLaboralesPersona",
                columns: new[] { "TenantId", "UsuarioId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FestivosOficiales_Fecha",
                table: "FestivosOficiales",
                column: "Fecha");

            // El calendario laboral de una persona es dato del Tenant del Operador CAE en el que trabaja: RLS
            // estándar, sin política que lo ensanche. El runtime lo lee y lo da de alta o lo cambia (siembra y
            // CalendarioLaboralPersona.Cambiar); no lo borra: la retirada de un Tenant de demostración no corre
            // con cae_app_runtime.
            migrationBuilder.Sql("""
                ALTER TABLE public."CalendariosLaboralesPersona" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."CalendariosLaboralesPersona" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."CalendariosLaboralesPersona"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."CalendariosLaboralesPersona" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT ON TABLE public."CalendariosLaboralesPersona" TO cae_app_runtime;
                GRANT UPDATE ("Comunidad", "Municipio", "AplicaJornadaIntensiva") ON TABLE public."CalendariosLaboralesPersona" TO cae_app_runtime;
                """);

            // El catálogo oficial de festivos es global (BOE y boletines autonómicos, igual para todos los Tenants):
            // no tiene TenantId ni RLS, como AvisosRevisionNormativa. Lo escriben solo las migraciones (HasData);
            // el runtime solo lo lee.
            migrationBuilder.Sql("""
                REVOKE ALL ON TABLE public."FestivosOficiales" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT ON TABLE public."FestivosOficiales" TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CalendariosLaboralesPersona");

            migrationBuilder.DropTable(
                name: "FestivosOficiales");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadePropuestasRecordatorioConversacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PropuestasRecordatorioConversacion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EsperaDesdeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PropuestaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Estado = table.Column<int>(type: "integer", nullable: false),
                    ResueltaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropuestasRecordatorioConversacion", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropuestasRecordatorioConversacion_Espera_Unica",
                table: "PropuestasRecordatorioConversacion",
                columns: new[] { "TenantId", "ConversacionId", "EsperaDesdeUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropuestasRecordatorioConversacion_Pendiente_Unica",
                table: "PropuestasRecordatorioConversacion",
                columns: new[] { "TenantId", "ConversacionId" },
                unique: true,
                filter: "\"Estado\" = 0");

            // Misma protección que las tablas hermanas de la conversación (NotasInternasConversacion,
            // CambiosEstadoConversacion): FK compuesta con TenantId contra AK_Conversaciones_Id_TenantId, invisible al modelo
            // Fluent a propósito (ver PropuestaRecordatorioConversacionConfiguration), y RLS forzada con la política
            // aislamiento_tenant de siempre —ninguna política existente se toca— y los permisos de cae_app_runtime y
            // cae_app_soporte. Tabla nueva y vacía: sin valores por defecto ni relleno.
            migrationBuilder.Sql(
                """
                ALTER TABLE ONLY public."PropuestasRecordatorioConversacion"
                    ADD CONSTRAINT "FK_PropuestasRecordatorioConversacion_Conversacion_Tenant" FOREIGN KEY ("ConversacionId", "TenantId") REFERENCES public."Conversaciones"("Id", "TenantId") ON DELETE CASCADE;
                ALTER TABLE public."PropuestasRecordatorioConversacion" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."PropuestasRecordatorioConversacion" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."PropuestasRecordatorioConversacion" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."PropuestasRecordatorioConversacion" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."PropuestasRecordatorioConversacion" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropuestasRecordatorioConversacion");
        }
    }
}

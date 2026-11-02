using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeRespuestaSugerida : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RespuestasSugeridas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    MensajeOrigenId = table.Column<Guid>(type: "uuid", nullable: false),
                    Origen = table.Column<int>(type: "integer", nullable: false),
                    MacroId = table.Column<Guid>(type: "uuid", nullable: true),
                    Texto = table.Column<string>(type: "character varying(1048576)", maxLength: 1048576, nullable: false),
                    Estado = table.Column<int>(type: "integer", nullable: false),
                    GeneradaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeneradaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevisadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevisadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RespuestasSugeridas", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RespuestasSugeridas_Activa_Unica",
                table: "RespuestasSugeridas",
                columns: new[] { "TenantId", "ConversacionId" },
                unique: true,
                filter: "\"Estado\" IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_RespuestasSugeridas_ConversacionId_TenantId",
                table: "RespuestasSugeridas",
                columns: new[] { "ConversacionId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_RespuestasSugeridas_MensajeOrigenId_TenantId",
                table: "RespuestasSugeridas",
                columns: new[] { "MensajeOrigenId", "TenantId" });

            // Texto y autoría no admiten UPDATE: la aplicación solo mueve el estado, y nunca borra.
            // Sin concesiones a cae_app_soporte: es contenido de un borrador dirigido a un tercero.
            migrationBuilder.Sql("""
                ALTER TABLE public."RespuestasSugeridas"
                    ADD CONSTRAINT "FK_RespuestasSugeridas_Conversaciones_Tenant"
                        FOREIGN KEY ("ConversacionId", "TenantId")
                        REFERENCES public."Conversaciones"("Id", "TenantId") ON DELETE CASCADE,
                    ADD CONSTRAINT "FK_RespuestasSugeridas_Mensajes_Tenant"
                        FOREIGN KEY ("MensajeOrigenId", "TenantId")
                        REFERENCES public."Mensajes"("Id", "TenantId") ON DELETE CASCADE;

                ALTER TABLE public."RespuestasSugeridas" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."RespuestasSugeridas" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."RespuestasSugeridas"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."RespuestasSugeridas" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT ON TABLE public."RespuestasSugeridas" TO cae_app_runtime;
                GRANT UPDATE ("Estado", "RevisadaPorUsuarioId", "RevisadaEnUtc", "Version",
                              "EstaEliminado", "EliminadoEnUtc", "EliminadoPorUsuarioId")
                    ON TABLE public."RespuestasSugeridas" TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RespuestasSugeridas");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeIntencionesConversacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IntencionesConversacion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    MensajeId = table.Column<Guid>(type: "uuid", nullable: false),
                    IntencionPropuesta = table.Column<int>(type: "integer", nullable: false),
                    IntencionActual = table.Column<int>(type: "integer", nullable: false),
                    Confianza = table.Column<int>(type: "integer", nullable: false),
                    ConfirmadaPorPersona = table.Column<bool>(type: "boolean", nullable: false),
                    TipoEntidadPropuesta = table.Column<int>(type: "integer", nullable: true),
                    EntidadPropuestaId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntencionesConversacion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IntencionesConversacion_Conversaciones_ConversacionId",
                        column: x => x.ConversacionId,
                        principalTable: "Conversaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_IntencionesConversacion_Mensajes_MensajeId",
                        column: x => x.MensajeId,
                        principalTable: "Mensajes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IntencionesConversacion_ConversacionId",
                table: "IntencionesConversacion",
                column: "ConversacionId");

            migrationBuilder.CreateIndex(
                name: "IX_IntencionesConversacion_MensajeId",
                table: "IntencionesConversacion",
                column: "MensajeId");

            migrationBuilder.CreateIndex(
                name: "IX_IntencionesConversacion_TenantId_ConversacionId",
                table: "IntencionesConversacion",
                columns: new[] { "TenantId", "ConversacionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IntencionesConversacion_TenantId_IntencionActual",
                table: "IntencionesConversacion",
                columns: new[] { "TenantId", "IntencionActual" });

            migrationBuilder.Sql(
                """
                ALTER TABLE public."IntencionesConversacion" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."IntencionesConversacion" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."IntencionesConversacion" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."IntencionesConversacion" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."IntencionesConversacion" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IntencionesConversacion");
        }
    }
}

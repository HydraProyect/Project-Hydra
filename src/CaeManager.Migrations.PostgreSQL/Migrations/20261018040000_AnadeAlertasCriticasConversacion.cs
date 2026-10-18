using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations;

[DbContext(typeof(CaeManagerDbContext))]
[Migration("20261018040000_AnadeAlertasCriticasConversacion")]
public partial class AnadeAlertasCriticasConversacion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AlertasCriticasConversacion",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                ConversacionId = table.Column<Guid>(type: "uuid", nullable: false),
                MensajeId = table.Column<Guid>(type: "uuid", nullable: false),
                TextoDisparador = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                ExpresionDetectada = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                OperadorCaeTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                SegundaOpinionIa = table.Column<bool>(type: "boolean", nullable: true),
                ConfianzaIa = table.Column<int>(type: "integer", nullable: true),
                Estado = table.Column<int>(type: "integer", nullable: false),
                DetectadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DecisionPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                DecisionEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_AlertasCriticasConversacion", a => a.Id));
        migrationBuilder.CreateIndex("IX_AlertasCriticasConversacion_TenantId_ConversacionId", "AlertasCriticasConversacion",
            new[] { "TenantId", "ConversacionId" }, unique: true);
        migrationBuilder.Sql("""
            ALTER TABLE "AlertasCriticasConversacion"
                ADD CONSTRAINT "FK_AlertasCriticas_Conversaciones_Tenant" FOREIGN KEY ("ConversacionId", "TenantId")
                    REFERENCES "Conversaciones" ("Id", "TenantId") ON DELETE CASCADE,
                ADD CONSTRAINT "FK_AlertasCriticas_Mensajes_Tenant" FOREIGN KEY ("MensajeId", "TenantId")
                    REFERENCES "Mensajes" ("Id", "TenantId") ON DELETE CASCADE,
                ADD CONSTRAINT "CK_AlertasCriticas_OperadorCae" CHECK ("OperadorCaeTenantId" = "TenantId"),
                ADD CONSTRAINT "CK_AlertasCriticas_Confianza" CHECK ("ConfianzaIa" BETWEEN 0 AND 100);
            ALTER TABLE "AlertasCriticasConversacion" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "AlertasCriticasConversacion" FORCE ROW LEVEL SECURITY;
            CREATE POLICY aislamiento_tenant ON "AlertasCriticasConversacion"
                USING ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                WITH CHECK ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
            GRANT SELECT, INSERT, UPDATE, DELETE ON "AlertasCriticasConversacion" TO cae_app_runtime;
            REVOKE ALL ON "AlertasCriticasConversacion" FROM cae_app_soporte;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("AlertasCriticasConversacion");
}

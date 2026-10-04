using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class PresentacionesDeDocumentoEnCentroYTopeDePeriodicidad : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PresentacionesDocumentoEnCentro",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    CentroId = table.Column<Guid>(type: "uuid", nullable: false),
                    FechaPresentacion = table.Column<DateOnly>(type: "date", nullable: false),
                    Origen = table.Column<int>(type: "integer", nullable: false),
                    RegistradaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PresentacionesDocumentoEnCentro", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PresentacionesDocumentoEnCentro_Centros_TenantId_CentroId",
                        columns: x => new { x.TenantId, x.CentroId },
                        principalTable: "Centros",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PresentacionesDocumentoEnCentro_Documentos_TenantId_Documen~",
                        columns: x => new { x.TenantId, x.DocumentoId },
                        principalTable: "Documentos",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            // Tope de la periodicidad especial (1..120 meses). NOT VALID a propósito: hasta hoy el campo no tenía cota y esta
            // migración no cambia datos existentes en silencio. La restricción se aplica ya a toda fila nueva o modificada; si
            // alguna fila existente la incumple, no la bloquea hasta que se corrija a mano. Una vez revisados los datos:
            //   ALTER TABLE public."TiposDocumentoCentros" VALIDATE CONSTRAINT "CK_TiposDocumentoCentros_PeriodicidadEspecialMeses";
            migrationBuilder.Sql(
                """
                ALTER TABLE public."TiposDocumentoCentros" ADD CONSTRAINT "CK_TiposDocumentoCentros_PeriodicidadEspecialMeses" CHECK ("PeriodicidadEspecialMeses" IS NULL OR ("PeriodicidadEspecialMeses" >= 1 AND "PeriodicidadEspecialMeses" <= 120)) NOT VALID;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_PresentacionesDocumentoEnCentro_TenantId_CentroId",
                table: "PresentacionesDocumentoEnCentro",
                columns: new[] { "TenantId", "CentroId" });

            migrationBuilder.CreateIndex(
                name: "IX_PresentacionesDocumentoEnCentro_TenantId_DocumentoId_Centro~",
                table: "PresentacionesDocumentoEnCentro",
                columns: new[] { "TenantId", "DocumentoId", "CentroId" });

            // Mismo aislamiento que el resto de tablas tenantizadas (CoberturaRlsDelModeloTests): RLS + FORCE + política
            // aislamiento_tenant, y los permisos de sus vecinas (ToleranciasDocumentoClienteEmpresarial). Migración aditiva:
            // no toca datos ni políticas existentes. Los permisos son los de sus vecinas; que el historial sea inmutable es
            // invariante del dominio (la entidad no tiene métodos que modifiquen una presentación), no de los permisos.
            migrationBuilder.Sql(
                """
                ALTER TABLE public."PresentacionesDocumentoEnCentro" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."PresentacionesDocumentoEnCentro" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."PresentacionesDocumentoEnCentro" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."PresentacionesDocumentoEnCentro" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."PresentacionesDocumentoEnCentro" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PresentacionesDocumentoEnCentro");

            migrationBuilder.DropCheckConstraint(
                name: "CK_TiposDocumentoCentros_PeriodicidadEspecialMeses",
                table: "TiposDocumentoCentros");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class PropagacionesDocumento : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PropagacionesDocumento",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentoId = table.Column<Guid>(type: "uuid", nullable: false),
                    CanalGestionDocumentalId = table.Column<Guid>(type: "uuid", nullable: false),
                    HechoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    HechoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConversacionId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropagacionesDocumento", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropagacionesDocumento_CanalesGestionDocumental_TenantId_Ca~",
                        columns: x => new { x.TenantId, x.CanalGestionDocumentalId },
                        principalTable: "CanalesGestionDocumental",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PropagacionesDocumento_Documentos_TenantId_DocumentoId",
                        columns: x => new { x.TenantId, x.DocumentoId },
                        principalTable: "Documentos",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropagacionesDocumento_TenantId_CanalGestionDocumentalId",
                table: "PropagacionesDocumento",
                columns: new[] { "TenantId", "CanalGestionDocumentalId" });

            migrationBuilder.CreateIndex(
                name: "IX_PropagacionesDocumento_TenantId_DocumentoId_CanalGestionDoc~",
                table: "PropagacionesDocumento",
                columns: new[] { "TenantId", "DocumentoId", "CanalGestionDocumentalId" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE "PropagacionesDocumento" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE "PropagacionesDocumento" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON "PropagacionesDocumento"
                    USING ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                GRANT SELECT, INSERT, UPDATE, DELETE ON "PropagacionesDocumento" TO cae_app_runtime;
                GRANT SELECT ON "PropagacionesDocumento" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropagacionesDocumento");
        }
    }
}

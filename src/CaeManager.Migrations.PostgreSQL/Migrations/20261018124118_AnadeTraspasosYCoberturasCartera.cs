using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeTraspasosYCoberturasCartera : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CoberturaAusenciaId",
                table: "AsignacionesCartera",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AusenciasGestorCae",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GestorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Tipo = table.Column<int>(type: "integer", nullable: false),
                    Desde = table.Column<DateOnly>(type: "date", nullable: false),
                    Hasta = table.Column<DateOnly>(type: "date", nullable: true),
                    RegistradaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistradaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CerradaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    Coberturas = table.Column<string>(type: "jsonb", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AusenciasGestorCae", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AusenciasGestorCae_TenantId_GestorUsuarioId",
                table: "AusenciasGestorCae",
                columns: new[] { "TenantId", "GestorUsuarioId" },
                unique: true,
                filter: "\"CerradaEnUtc\" IS NULL");

            // La ausencia pertenece al Tenant del Operador CAE. Las asignaciones conservan sus políticas RLS existentes.
            migrationBuilder.Sql("""
                ALTER TABLE public."AusenciasGestorCae" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."AusenciasGestorCae" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."AusenciasGestorCae"
                    USING ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public."AusenciasGestorCae" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."AusenciasGestorCae" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AusenciasGestorCae");

            migrationBuilder.DropColumn(
                name: "CoberturaAusenciaId",
                table: "AsignacionesCartera");
        }
    }
}

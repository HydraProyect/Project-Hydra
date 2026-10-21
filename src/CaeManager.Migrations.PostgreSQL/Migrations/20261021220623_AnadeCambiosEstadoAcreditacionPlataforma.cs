using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeCambiosEstadoAcreditacionPlataforma : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CambiosEstadoAcreditacionPlataforma",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AcreditacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EstadoAnterior = table.Column<int>(type: "integer", nullable: false),
                    EstadoVigenciaAnterior = table.Column<int>(type: "integer", nullable: false),
                    FechaVencimientoAnterior = table.Column<DateOnly>(type: "date", nullable: true),
                    Estado = table.Column<int>(type: "integer", nullable: false),
                    Fecha = table.Column<DateOnly>(type: "date", nullable: false),
                    Motivo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Nota = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    MomentoUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeshechoUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CambiosEstadoAcreditacionPlataforma", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CambiosEstadoAcreditacionPlataforma_AcreditacionesDocumento~",
                        column: x => x.AcreditacionId,
                        principalTable: "AcreditacionesDocumentoPlataforma",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CambiosEstadoAcreditacionPlataforma_AcreditacionId",
                table: "CambiosEstadoAcreditacionPlataforma",
                column: "AcreditacionId");

            migrationBuilder.CreateIndex(
                name: "IX_CambiosEstadoAcreditacionPlataforma_TenantId_AcreditacionId~",
                table: "CambiosEstadoAcreditacionPlataforma",
                columns: new[] { "TenantId", "AcreditacionId", "MomentoUtc" });

            // Hija de AcreditacionesDocumentoPlataforma, como RechazosAcreditacionDocumentoPlataforma: RLS estándar por
            // TenantId y los mismos privilegios que su hermana (el runtime marca un cambio deshecho con UPDATE y la baja
            // de la acreditación lo arrastra en cascada; Soporte TALVEG solo lee, como el resto del historial).
            migrationBuilder.Sql("""
                ALTER TABLE public."CambiosEstadoAcreditacionPlataforma" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."CambiosEstadoAcreditacionPlataforma" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."CambiosEstadoAcreditacionPlataforma"
                    USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
                    WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                REVOKE ALL ON TABLE public."CambiosEstadoAcreditacionPlataforma" FROM cae_app_runtime;
                GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CambiosEstadoAcreditacionPlataforma" TO cae_app_runtime;
                REVOKE ALL ON TABLE public."CambiosEstadoAcreditacionPlataforma" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."CambiosEstadoAcreditacionPlataforma" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CambiosEstadoAcreditacionPlataforma");
        }
    }
}

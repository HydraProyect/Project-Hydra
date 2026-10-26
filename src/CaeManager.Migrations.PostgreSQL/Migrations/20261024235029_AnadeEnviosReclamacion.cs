using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeEnviosReclamacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EnviosReclamacion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    Huella = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    IniciadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConversacionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReclamacionDocumentalId = table.Column<Guid>(type: "uuid", nullable: true),
                    Entregados = table.Column<List<string>>(type: "text[]", nullable: false),
                    EntregadosEnCopia = table.Column<List<string>>(type: "text[]", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EnviosReclamacion", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EnviosReclamacion_TenantId_UsuarioId_Huella",
                table: "EnviosReclamacion",
                columns: new[] { "TenantId", "UsuarioId", "Huella" });

            // Registro previo del envío de una reclamación (EnvioReclamacion, Z5-4 de la revisión de seguridad del
            // 2026-10-23). Vive en el Tenant propietario de la reclamación con la misma política que
            // "ReclamacionesDocumentales" (aislamiento_tenant sobre app.tenant_id, ENABLE + FORCE).
            //
            // cae_app_runtime: SELECT, INSERT y UPDATE (se crea antes del correo y se completa después); sin DELETE,
            // porque la aplicación no borra estos registros. cae_app_soporte, SELECT, como en la tabla de reclamaciones.
            migrationBuilder.Sql(
                """
                ALTER TABLE public."EnviosReclamacion" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."EnviosReclamacion" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."EnviosReclamacion"
                    USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid))
                    WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                REVOKE ALL ON TABLE public."EnviosReclamacion" FROM cae_app_runtime;
                GRANT SELECT, INSERT, UPDATE ON TABLE public."EnviosReclamacion" TO cae_app_runtime;
                REVOKE ALL ON TABLE public."EnviosReclamacion" FROM cae_app_soporte;
                GRANT SELECT ON TABLE public."EnviosReclamacion" TO cae_app_soporte;
                REVOKE ALL ON TABLE public."EnviosReclamacion" FROM cae_app_aprovisionamiento;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EnviosReclamacion");
        }
    }
}

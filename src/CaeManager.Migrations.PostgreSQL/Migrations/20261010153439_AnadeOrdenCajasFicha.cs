using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeOrdenCajasFicha : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OrdenesCajasFicha",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    TipoFicha = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Claves = table.Column<List<string>>(type: "text[]", nullable: false),
                    ActualizadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrdenesCajasFicha", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrdenesCajasFicha_TenantId_UsuarioId_TipoFicha",
                table: "OrdenesCajasFicha",
                columns: new[] { "TenantId", "UsuarioId", "TipoFicha" },
                unique: true);

            // Mismo aislamiento que el resto de tablas por Tenant (CoberturaRlsDelModeloTests): RLS + FORCE +
            // política aislamiento_tenant. Que la fila sea de un usuario lo acota el handler, como en
            // FiltrosGuardados. Permisos explícitos, los mismos que esa tabla.
            migrationBuilder.Sql(
                """
                ALTER TABLE public."OrdenesCajasFicha" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."OrdenesCajasFicha" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."OrdenesCajasFicha" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."OrdenesCajasFicha" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."OrdenesCajasFicha" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrdenesCajasFicha");
        }
    }
}

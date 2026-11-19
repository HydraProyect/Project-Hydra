using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeCredencialesAcceso : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CredencialesAcceso",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantOrigenId = table.Column<Guid>(type: "uuid", nullable: false),
                    TrabajadorOrigenId = table.Column<Guid>(type: "uuid", nullable: false),
                    SujetoNombre = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SujetoDocumentoIdentidad = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Serie = table.Column<int>(type: "integer", nullable: false),
                    Estado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EmitidaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    EmitidaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RegeneradaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RegeneradaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    MotivoRevocacion = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    RevocadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevocadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    CaducadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CredencialesAcceso", x => x.Id);
                    table.CheckConstraint("CK_CredencialesAcceso_CaducidadCoherente", "(\"Estado\" = 'Caducada') = (\"CaducadaEnUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_CredencialesAcceso_EstadoValido", "\"Estado\" IN ('Emitida', 'Revocada', 'Caducada')");
                    table.CheckConstraint("CK_CredencialesAcceso_MotivoRevocacionCerrado", "\"MotivoRevocacion\" IS NULL OR \"MotivoRevocacion\" IN ('PerdidaOExtravio', 'UsoIndebido', 'FinDeLaRelacion', 'Otro')");
                    table.CheckConstraint("CK_CredencialesAcceso_OrigenEnOtroTenant", "\"TenantOrigenId\" <> \"TenantId\"");
                    table.CheckConstraint("CK_CredencialesAcceso_RevocacionCoherente", "(\"Estado\" = 'Revocada' AND \"MotivoRevocacion\" IS NOT NULL AND \"RevocadaEnUtc\" IS NOT NULL AND \"RevocadaPorUsuarioId\" IS NOT NULL) OR (\"Estado\" <> 'Revocada' AND \"MotivoRevocacion\" IS NULL AND \"RevocadaEnUtc\" IS NULL AND \"RevocadaPorUsuarioId\" IS NULL)");
                    table.CheckConstraint("CK_CredencialesAcceso_SerieDesdeUno", "\"Serie\" >= 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CredencialesAcceso_TenantId_Trabajador_Emitida",
                table: "CredencialesAcceso",
                columns: new[] { "TenantId", "TenantOrigenId", "TrabajadorOrigenId" },
                unique: true,
                filter: "\"Estado\" = 'Emitida'");

            // Aditiva y compatible con la ranura anterior (tabla nueva que el código anterior no toca). RLS forzada por Tenant
            // TCE (TenantId). El runtime inserta y cambia solo lo que mueven emitir, regenerar, revocar y caducar: estado,
            // serie, instantánea del sujeto, trazas de cambio y el token de versión; nunca la procedencia (TenantId,
            // TenantOrigenId, TrabajadorOrigenId), la emisión ni el borrado lógico, y no borra: una credencial se revoca.
            // Soporte y aprovisionamiento no reciben nada: la tabla guarda nombre y documento de Trabajadores de otro Tenant y
            // Soporte no tiene ningún flujo que la lea (si lo tuviera, sería un SELECT concedido por su propia migración).
            migrationBuilder.Sql("""
                ALTER TABLE public."CredencialesAcceso" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."CredencialesAcceso" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."CredencialesAcceso"
                    USING ("TenantId" = (SELECT public.app_ctx_tenant_id()))
                    WITH CHECK ("TenantId" = (SELECT public.app_ctx_tenant_id()));
                REVOKE ALL ON TABLE public."CredencialesAcceso" FROM PUBLIC, cae_app_runtime, cae_app_soporte, cae_app_aprovisionamiento;
                GRANT SELECT, INSERT ON TABLE public."CredencialesAcceso" TO cae_app_runtime;
                GRANT UPDATE ("Serie", "Estado", "SujetoNombre", "SujetoDocumentoIdentidad", "RegeneradaEnUtc",
                              "RegeneradaPorUsuarioId", "MotivoRevocacion", "RevocadaEnUtc", "RevocadaPorUsuarioId",
                              "CaducadaEnUtc", "Version")
                    ON TABLE public."CredencialesAcceso" TO cae_app_runtime;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CredencialesAcceso");
        }
    }
}

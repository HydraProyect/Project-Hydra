using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// ADR-011 § 2.7, enmienda del 2026-10-08 (incremento I3): tabla de las propuestas de apoyo que el
    /// Gestor CAE principal —o el Coordinador CAE principal— de una Asignación de Operación hace a otro
    /// Gestor CAE del mismo Operador CAE. Como máximo una pendiente por destinatario y operación
    /// (<c>IX_PropuestasApoyoCartera_PendienteUnica</c>, índice único parcial). No concede nada por sí
    /// sola y no toca datos existentes.
    /// </summary>
    public partial class AnadePropuestasApoyoCartera : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PropuestasApoyoCartera",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropietarioTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AsignacionOperacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProponenteUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinatarioUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Estado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VigenciaHastaPropuesta = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResueltaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MotivoAnulacion = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    AsignacionCarteraId = table.Column<Guid>(type: "uuid", nullable: true),
                    AsignacionOperadorDelegadoId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PropuestasApoyoCartera", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PropuestasApoyoCartera_AsignacionesCartera_AsignacionCarter~",
                        column: x => x.AsignacionCarteraId,
                        principalTable: "AsignacionesCartera",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PropuestasApoyoCartera_AsignacionesOperacion_AsignacionOper~",
                        columns: x => new { x.AsignacionOperacionId, x.PropietarioTenantId },
                        principalTable: "AsignacionesOperacion",
                        principalColumns: new[] { "Id", "PropietarioTenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PropuestasApoyoCartera_AsignacionCarteraId",
                table: "PropuestasApoyoCartera",
                column: "AsignacionCarteraId");

            migrationBuilder.CreateIndex(
                name: "IX_PropuestasApoyoCartera_AsignacionOperacionId_PropietarioTen~",
                table: "PropuestasApoyoCartera",
                columns: new[] { "AsignacionOperacionId", "PropietarioTenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_PropuestasApoyoCartera_OperadorTenantId_DestinatarioUsuario~",
                table: "PropuestasApoyoCartera",
                columns: new[] { "OperadorTenantId", "DestinatarioUsuarioId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_PropuestasApoyoCartera_OperadorTenantId_ProponenteUsuarioId~",
                table: "PropuestasApoyoCartera",
                columns: new[] { "OperadorTenantId", "ProponenteUsuarioId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_PropuestasApoyoCartera_PendienteUnica",
                table: "PropuestasApoyoCartera",
                columns: new[] { "AsignacionOperacionId", "DestinatarioUsuarioId" },
                unique: true,
                filter: "\"Estado\" = 'Pendiente'");

            // Catálogo del Operador CAE, misma forma que SolicitudesIncorporacionCartera
            // (CoberturaRlsDelModeloTests, categoría 5): RLS activada y SIN FORCE —la retirada de un
            // Tenant de demo corre como propietario y tiene que ver las filas que borra—, una sola
            // política por app.tenant_origen_id y no por app.tenant_id, porque la propuesta se acepta
            // con el Tenant propietario como Tenant activo. Migración aditiva: no toca ninguna
            // política existente.
            migrationBuilder.Sql(
                """
                ALTER TABLE public."PropuestasApoyoCartera" ENABLE ROW LEVEL SECURITY;
                CREATE POLICY operador_de_la_propuesta ON public."PropuestasApoyoCartera" USING (("OperadorTenantId" = (NULLIF(current_setting('app.tenant_origen_id'::text, true), ''::text))::uuid)) WITH CHECK (("OperadorTenantId" = (NULLIF(current_setting('app.tenant_origen_id'::text, true), ''::text))::uuid));
                GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."PropuestasApoyoCartera" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."PropuestasApoyoCartera" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropuestasApoyoCartera");
        }
    }
}

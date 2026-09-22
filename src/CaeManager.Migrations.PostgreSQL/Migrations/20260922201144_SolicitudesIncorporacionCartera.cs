using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class SolicitudesIncorporacionCartera : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SolicitudesIncorporacionCartera",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperadorTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PropietarioTenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    AsignacionOperacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SolicitanteUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    Mensaje = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Estado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResueltaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    ResueltaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MotivoAnulacion = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    AsignacionCarteraId = table.Column<Guid>(type: "uuid", nullable: true),
                    AsignacionOperadorDelegadoId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevocadaPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevocadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SolicitudesIncorporacionCartera", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SolicitudesIncorporacionCartera_AsignacionesCartera_Asignac~",
                        column: x => x.AsignacionCarteraId,
                        principalTable: "AsignacionesCartera",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SolicitudesIncorporacionCartera_AsignacionesOperacion_Asign~",
                        columns: x => new { x.AsignacionOperacionId, x.PropietarioTenantId },
                        principalTable: "AsignacionesOperacion",
                        principalColumns: new[] { "Id", "PropietarioTenantId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesIncorporacionCartera_AsignacionCarteraId",
                table: "SolicitudesIncorporacionCartera",
                column: "AsignacionCarteraId");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesIncorporacionCartera_AsignacionOperacionId_Propi~",
                table: "SolicitudesIncorporacionCartera",
                columns: new[] { "AsignacionOperacionId", "PropietarioTenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesIncorporacionCartera_OperadorTenantId_Estado",
                table: "SolicitudesIncorporacionCartera",
                columns: new[] { "OperadorTenantId", "Estado" });

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesIncorporacionCartera_PendienteUnica",
                table: "SolicitudesIncorporacionCartera",
                columns: new[] { "AsignacionOperacionId", "SolicitanteUsuarioId" },
                unique: true,
                filter: "\"Estado\" = 'Pendiente'");

            migrationBuilder.CreateIndex(
                name: "IX_SolicitudesIncorporacionCartera_SolicitanteUsuarioId",
                table: "SolicitudesIncorporacionCartera",
                column: "SolicitanteUsuarioId");

            // La solicitud es del Operador CAE: su flujo interno entre sus
            // Gestores CAE y sus Coordinadores CAE. Se ve y se escribe solo
            // desde el tenant de origen del usuario, que la selección de
            // workspace no puede cambiar — y por eso sirve también dentro del
            // ámbito del Tenant propietario, donde la aceptación la marca en la
            // misma transacción que crea la cartera. El Tenant propietario no
            // ve la fila: lo que le afecta, la cartera, ya la ve por su
            // posición. Su auditoría sí recoge la aceptación y la revocación,
            // porque se guardan en su ámbito: es la deuda conocida de los
            // catálogos de asignación descrita en AuditoriaInterceptor.
            //
            // Sin FORCE, como los catálogos de asignación y por lo mismo: la
            // retirada de tenants de demo opera como propietario de la tabla.
            // Frente a una sesión de usuario protege que cae_app_runtime no es
            // propietario (CoberturaRlsDelModeloTests, categoría 5).
            migrationBuilder.Sql(@"
ALTER TABLE ""SolicitudesIncorporacionCartera"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""SolicitudesIncorporacionCartera"" NO FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS operador_de_la_solicitud ON ""SolicitudesIncorporacionCartera"";
CREATE POLICY operador_de_la_solicitud ON ""SolicitudesIncorporacionCartera""
    USING (""OperadorTenantId"" = NULLIF(current_setting('app.tenant_origen_id', true), '')::uuid)
    WITH CHECK (""OperadorTenantId"" = NULLIF(current_setting('app.tenant_origen_id', true), '')::uuid);
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                @"DROP POLICY IF EXISTS operador_de_la_solicitud ON ""SolicitudesIncorporacionCartera"";");

            migrationBuilder.DropTable(
                name: "SolicitudesIncorporacionCartera");
        }
    }
}

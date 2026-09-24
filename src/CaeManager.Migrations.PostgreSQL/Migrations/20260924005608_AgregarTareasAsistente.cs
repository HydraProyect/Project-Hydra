using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AgregarTareasAsistente : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TareasAsistente",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorRealUsuarioId = table.Column<Guid>(type: "uuid", nullable: false),
                    UsuarioSimuladoId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantOrigenId = table.Column<Guid>(type: "uuid", nullable: true),
                    ViaAcceso = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ViaAccesoId = table.Column<Guid>(type: "uuid", nullable: true),
                    Estado = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActualizadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PlanConfirmadoPorActorRealUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlanConfirmadoComoUsuarioSimuladoId = table.Column<Guid>(type: "uuid", nullable: true),
                    PlanConfirmadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DescartadaEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TareasAsistente", x => x.Id);
                    table.CheckConstraint("CK_TareasAsistente_ConfirmadaConConfirmacion", "\"Estado\" NOT IN ('Confirmada', 'Terminada') OR (\"PlanConfirmadoEnUtc\" IS NOT NULL AND \"PlanConfirmadoPorActorRealUsuarioId\" IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "PasosTareaAsistente",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TareaAsistenteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Posicion = table.Column<int>(type: "integer", nullable: false),
                    OrdenAsistenteId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DatosJson = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    Resumen = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CamposPendientesJson = table.Column<string>(type: "text", nullable: false),
                    AvisosJson = table.Column<string>(type: "text", nullable: false),
                    AsistidoPorIa = table.Column<bool>(type: "boolean", nullable: false),
                    Estado = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConfirmadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EjecutadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EntidadResultadoId = table.Column<Guid>(type: "uuid", nullable: true),
                    MotivoFallo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ActualizadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasosTareaAsistente", x => x.Id);
                    table.CheckConstraint("CK_PasosTareaAsistente_EjecucionTrasConfirmacion", "\"Estado\" NOT IN ('Confirmado', 'Ejecutado', 'Fallido') OR \"ConfirmadoEnUtc\" IS NOT NULL");
                    table.CheckConstraint("CK_PasosTareaAsistente_EjecutadoConFecha", "\"Estado\" <> 'Ejecutado' OR \"EjecutadoEnUtc\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_PasosTareaAsistente_TareasAsistente_TareaAsistenteId",
                        column: x => x.TareaAsistenteId,
                        principalTable: "TareasAsistente",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TurnosTareaAsistente",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TareaAsistenteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Numero = table.Column<int>(type: "integer", nullable: false),
                    Autor = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TextoOriginal = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    TextoEnmascarado = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    FechaUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TurnosTareaAsistente", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TurnosTareaAsistente_TareasAsistente_TareaAsistenteId",
                        column: x => x.TareaAsistenteId,
                        principalTable: "TareasAsistente",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PasosTareaAsistente_TareaAsistenteId_Posicion",
                table: "PasosTareaAsistente",
                columns: new[] { "TareaAsistenteId", "Posicion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TareasAsistente_TenantId_ActorRealUsuarioId_ActualizadaEnUtc",
                table: "TareasAsistente",
                columns: new[] { "TenantId", "ActorRealUsuarioId", "ActualizadaEnUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TurnosTareaAsistente_TareaAsistenteId_Numero",
                table: "TurnosTareaAsistente",
                columns: new[] { "TareaAsistenteId", "Numero" },
                unique: true);

            // Defensa en profundidad fuera del modelo Fluent, mismo patrón que
            // FkCompuestaTenantComunicaciones: un turno o un paso no puede
            // colgar de una tarea de otro Tenant aunque el Id coincidiera.
            migrationBuilder.Sql(
                """
                ALTER TABLE "TareasAsistente" ADD CONSTRAINT "AK_TareasAsistente_Id_TenantId"
                UNIQUE ("Id", "TenantId");
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "TurnosTareaAsistente" ADD CONSTRAINT "FK_TurnosTareaAsistente_TareasAsistente_TareaAsistenteId_TenantId"
                FOREIGN KEY ("TareaAsistenteId", "TenantId")
                REFERENCES "TareasAsistente" ("Id", "TenantId")
                ON DELETE CASCADE;
                """);

            migrationBuilder.Sql(
                """
                ALTER TABLE "PasosTareaAsistente" ADD CONSTRAINT "FK_PasosTareaAsistente_TareasAsistente_TareaAsistenteId_TenantId"
                FOREIGN KEY ("TareaAsistenteId", "TenantId")
                REFERENCES "TareasAsistente" ("Id", "TenantId")
                ON DELETE CASCADE;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PasosTareaAsistente");

            migrationBuilder.DropTable(
                name: "TurnosTareaAsistente");

            migrationBuilder.DropTable(
                name: "TareasAsistente");
        }
    }
}

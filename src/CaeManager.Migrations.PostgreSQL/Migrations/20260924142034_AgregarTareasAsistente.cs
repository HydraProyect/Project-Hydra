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

            // "Nada se ejecuta sin plan confirmado" también en la base, no solo
            // en el dominio: un paso Confirmado, Ejecutado o Fallido exige que su
            // tarea tenga PlanConfirmadoEnUtc. Trigger de restricción DIFERIDO a
            // propósito: ConfirmarPlan guarda la raíz y sus pasos en la misma
            // transacción y EF no garantiza el orden de esos UPDATE, así que la
            // comprobación se hace al confirmar la transacción. La consulta a la
            // raíz corre con los permisos y la RLS de quien escribe: si no ve la
            // tarea, falla cerrada.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION paso_tarea_asistente_exige_plan_confirmado() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."Estado" IN ('Confirmado', 'Ejecutado', 'Fallido') AND NOT EXISTS (
                        SELECT 1 FROM "TareasAsistente" t
                        WHERE t."Id" = NEW."TareaAsistenteId"
                          AND t."TenantId" = NEW."TenantId"
                          AND t."PlanConfirmadoEnUtc" IS NOT NULL)
                    THEN
                        RAISE EXCEPTION 'El paso % no se confirma ni se ejecuta sin un plan confirmado.', NEW."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NULL;
                END;
                $$;

                CREATE CONSTRAINT TRIGGER "TR_PasosTareaAsistente_ExigePlanConfirmado"
                AFTER INSERT OR UPDATE ON "PasosTareaAsistente"
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW EXECUTE FUNCTION paso_tarea_asistente_exige_plan_confirmado();
                """);

            // Y la confirmación no se deshace: sin esto, borrar PlanConfirmadoEnUtc
            // de la raíz dejaría pasos ejecutados colgando de un plan sin confirmar.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION tarea_asistente_confirmacion_inmutable() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD."PlanConfirmadoEnUtc" IS NOT NULL
                       AND (NEW."PlanConfirmadoEnUtc" IS DISTINCT FROM OLD."PlanConfirmadoEnUtc"
                            OR NEW."PlanConfirmadoPorActorRealUsuarioId" IS DISTINCT FROM OLD."PlanConfirmadoPorActorRealUsuarioId"
                            OR NEW."PlanConfirmadoComoUsuarioSimuladoId" IS DISTINCT FROM OLD."PlanConfirmadoComoUsuarioSimuladoId")
                    THEN
                        RAISE EXCEPTION 'La confirmación del plan de la tarea % no se modifica.', OLD."Id"
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER "TR_TareasAsistente_ConfirmacionInmutable"
                BEFORE UPDATE ON "TareasAsistente"
                FOR EACH ROW EXECUTE FUNCTION tarea_asistente_confirmacion_inmutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS "TR_TareasAsistente_ConfirmacionInmutable" ON "TareasAsistente";
                DROP TRIGGER IF EXISTS "TR_PasosTareaAsistente_ExigePlanConfirmado" ON "PasosTareaAsistente";
                DROP FUNCTION IF EXISTS tarea_asistente_confirmacion_inmutable();
                DROP FUNCTION IF EXISTS paso_tarea_asistente_exige_plan_confirmado();
                """);

            migrationBuilder.DropTable(
                name: "PasosTareaAsistente");

            migrationBuilder.DropTable(
                name: "TurnosTareaAsistente");

            migrationBuilder.DropTable(
                name: "TareasAsistente");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class AnadeEstadosEsperaConversacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Los dos estados nuevos (EsperandoRespuestaExterna = 4, EnEsperaDeTercero = 5) caben en la columna entera de Estado:
            // no hace falta tocar esa columna. EstadoDesdeUtc de las conversaciones que ya existen es el instante de esta
            // migración: no hay fuente fiable de cuándo entraron en su estado, y para las Resueltas significa que los 4 días
            // del cierre automático empiezan a contar desde el despliegue, no que se cierren de golpe las antiguas.
            // El valor por defecto solo sirve para rellenar las filas existentes; se retira al final, porque el modelo no lo
            // declara y toda conversación nueva lo fija el dominio. Es clock_timestamp() y no now() a propósito: now() es
            // estable, y PostgreSQL guarda un valor por defecto estable como «valor de columnas añadidas» del catálogo
            // (atthasmissing/attmissingval) con el instante exacto de cada ejecución, que hace distinta la huella de cada
            // base migrada (EquivalenciaDelClonConLaBaseMigrada). Uno volátil reescribe la tabla y no deja rastro en el
            // catálogo, y además rellena las filas con independencia de RLS, que aquí no se toca.
            migrationBuilder.AddColumn<DateTime>(
                name: "EstadoDesdeUtc",
                table: "Conversaciones",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "clock_timestamp()");

            migrationBuilder.Sql("""ALTER TABLE public."Conversaciones" ALTER COLUMN "EstadoDesdeUtc" DROP DEFAULT;""");

            migrationBuilder.CreateTable(
                name: "CambiosEstadoConversacion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ConversacionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EstadoAnterior = table.Column<int>(type: "integer", nullable: false),
                    EstadoNuevo = table.Column<int>(type: "integer", nullable: false),
                    FechaUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CambiosEstadoConversacion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CambiosEstadoConversacion_Conversaciones_ConversacionId",
                        column: x => x.ConversacionId,
                        principalTable: "Conversaciones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CambiosEstadoConversacion_ConversacionId",
                table: "CambiosEstadoConversacion",
                column: "ConversacionId");

            migrationBuilder.CreateIndex(
                name: "IX_CambiosEstadoConversacion_TenantId_ConversacionId_FechaUtc",
                table: "CambiosEstadoConversacion",
                columns: new[] { "TenantId", "ConversacionId", "FechaUtc" });

            // Misma protección que las tablas hermanas de la conversación (NotasInternasConversacion, Mensajes): FK compuesta con
            // TenantId, invisible al modelo Fluent a propósito (ver ConversacionConfiguration), y RLS forzada con la política
            // aislamiento_tenant de siempre —ninguna política existente se toca— y los permisos de cae_app_runtime y cae_app_soporte.
            migrationBuilder.Sql(
                """
                ALTER TABLE ONLY public."CambiosEstadoConversacion"
                    ADD CONSTRAINT "FK_CambiosEstadoConversacion_Conversaciones_ConversacionId_Tena" FOREIGN KEY ("ConversacionId", "TenantId") REFERENCES public."Conversaciones"("Id", "TenantId") ON DELETE CASCADE;
                ALTER TABLE public."CambiosEstadoConversacion" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE ONLY public."CambiosEstadoConversacion" FORCE ROW LEVEL SECURITY;
                CREATE POLICY aislamiento_tenant ON public."CambiosEstadoConversacion" USING (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid)) WITH CHECK (("TenantId" = (NULLIF(current_setting('app.tenant_id'::text, true), ''::text))::uuid));
                GRANT SELECT,INSERT,DELETE,UPDATE ON TABLE public."CambiosEstadoConversacion" TO cae_app_runtime;
                GRANT SELECT ON TABLE public."CambiosEstadoConversacion" TO cae_app_soporte;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // El código anterior solo conoce los estados 0 a 3: lo que estuviera en espera vuelve a Abierta para no dejar
            // filas con un entero que ese código no sabe nombrar ni contar como vivas.
            migrationBuilder.Sql("""UPDATE public."Conversaciones" SET "Estado" = 0 WHERE "Estado" IN (4, 5);""");

            migrationBuilder.DropTable(
                name: "CambiosEstadoConversacion");

            migrationBuilder.DropColumn(
                name: "EstadoDesdeUtc",
                table: "Conversaciones");
        }
    }
}

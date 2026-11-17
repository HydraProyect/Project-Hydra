using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// El extranjero como modelo de contratación (decisión de Chris de 2026-11-17, DECISION_LOG): <c>TipoContratacion.Extranjero</c>
    /// = 3 y su bit <c>TiposContratacion.Extranjero</c> = 8, con lo que <c>TiposContratacion.Todos</c> pasa de 7 a 15. La máscara
    /// es desde ahora el único criterio para pedir algo a un Trabajador extranjero, y <c>RequisitosPlantilla."SePideA"</c> deja de
    /// leerse en los requisitos de Trabajador (en los de Empresa contratista sigue vigente, ver más abajo).
    /// <para>
    /// Qué traduce. (1) <c>TiposDocumento."TiposContratacionDeSerie"</c>: donde era exactamente el Todos anterior (7) pasa a 15,
    /// para que el tipo se siga pidiendo de serie a todos, ahora también al Extranjero; cualquier otra máscara se queda como está.
    /// (2) <c>RequisitosPlantilla."TiposContratacion"</c> de un requisito de Trabajador: con <c>"SePideA"</c> = Extranjeros, solo el
    /// bit Extranjero (8); con Nacionales, la misma máscara sin el bit Extranjero; con Todos, 7 pasa a 15. (3) Un requisito de
    /// Empresa o de vehículo o máquina no se toca, ni su máscara ni su <c>"SePideA"</c> (decisión de la subcoordinadora de
    /// 2026-11-17: la decisión del Extranjero solo trata del Trabajador y la del 2026-11-14, el país de la Empresa contratista,
    /// sigue vigente). Su 7 anterior el dominio lo sigue aceptando como Todos. <c>"SePideA"</c> no se reescribe en ninguna fila.
    /// </para>
    /// <para>
    /// Máscaras de Trabajador distintas de 7 (por ejemplo, 3) se quedan como están y no se piden al Extranjero: la lista base
    /// del Extranjero llega en la PR 2, con su propio UPDATE de datos sobre esos tipos y sus requisitos de plantilla.
    /// </para>
    /// <para>
    /// Qué no toca. Ningún Trabajador cambia de modelo de contratación: no se deduce el Extranjero del documento de identidad ni
    /// de la nacionalidad. Facturación tampoco: el extranjero cobrable sigue saliendo del documento o de la marca a mano
    /// (<c>CriterioExtranjeroCobrable</c>, <c>VisitaTrabajador."EsExtranjeroCobrable"</c>). El CHECK
    /// <c>CK_RequisitosPlantilla_TiposContratacion</c> se rehace para admitir el bit 8 (se quita antes de traducir y se pone
    /// después).
    /// </para>
    /// <para>
    /// La lectura inversa X-2 (<c>app_inbound_tec_requerimientos()</c>, P0a) no cambia: sigue devolviendo <c>"SePideA"</c>, que
    /// para la Empresa contratista se sigue leyendo y para el Trabajador ya no decide nada.
    /// </para>
    /// <para>
    /// Down revierte con pérdida: un requisito que quedó en 8 vuelve a 7 (el «Se pide a» intacto le devuelve el sentido de
    /// Extranjeros) y los 15 vuelven a 7. Lo escrito después de la migración no se distingue de lo
    /// traducido: un requisito o un tipo de documento que alguien acotó al Extranjero vuelve a pedirse a todos, y un Trabajador
    /// dado de alta como Extranjero conserva el 3, que la ranura anterior no conoce.
    /// </para>
    /// <para>
    /// NO es compatible con la ranura anterior mientras drena: su <c>TiposContratacion.Todos</c> vale 7 y normaliza con
    /// <c>&amp; 7</c>. Guardar allí un tipo de documento de Trabajador que ya esté en 15 lo deja en 7 (deja de pedirse al
    /// Extranjero sin aviso), y editar un requisito con máscara 8 o 15 falla con «tipo de contratación conocido». La ventana es la del relevo azul/verde; se acepta y se declara en la PR.
    /// </para>
    /// </summary>
    public partial class TraduceExtranjeroAModeloDeContratacion : Migration
    {
        // Mismo guardia que ProtegeLecturaIaInicialSalud: sin un migrador que evite la RLS, los UPDATE no verían ninguna fila
        // y la traducción quedaría vacía en silencio.
        private const string GuardiaMigrador =
            """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = current_user AND (rolsuper OR rolbypassrls)) THEN
                    RAISE EXCEPTION 'TraduceExtranjeroAModeloDeContratacion necesita un migrador superusuario o con BYPASSRLS';
                END IF;
            END
            $$;
            """;

        public const string TraduceMascaras =
            """
            UPDATE public."TiposDocumento"
               SET "TiposContratacionDeSerie" = 15
             WHERE "TiposContratacionDeSerie" = 7;

            UPDATE public."RequisitosPlantilla"
               SET "TiposContratacion" = 8
             WHERE "Entidad" = 'Trabajador' AND "SePideA" = 'Extranjeros';

            UPDATE public."RequisitosPlantilla"
               SET "TiposContratacion" = "TiposContratacion" & ~8
             WHERE "Entidad" = 'Trabajador' AND "SePideA" = 'Nacionales';

            UPDATE public."RequisitosPlantilla"
               SET "TiposContratacion" = 15
             WHERE "Entidad" = 'Trabajador' AND "SePideA" = 'Todos' AND "TiposContratacion" = 7;
            """;

        public const string RevierteMascaras =
            """
            UPDATE public."RequisitosPlantilla"
               SET "TiposContratacion" = 7
             WHERE "TiposContratacion" IN (8, 15);

            UPDATE public."RequisitosPlantilla"
               SET "TiposContratacion" = "TiposContratacion" & 7
             WHERE ("TiposContratacion" & ~7) <> 0;

            UPDATE public."TiposDocumento"
               SET "TiposContratacionDeSerie" = 7
             WHERE "TiposContratacionDeSerie" IN (8, 15);

            UPDATE public."TiposDocumento"
               SET "TiposContratacionDeSerie" = "TiposContratacionDeSerie" & 7
             WHERE ("TiposContratacionDeSerie" & ~7) <> 0;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GuardiaMigrador);

            migrationBuilder.DropCheckConstraint(
                name: "CK_RequisitosPlantilla_TiposContratacion",
                table: "RequisitosPlantilla");

            migrationBuilder.Sql(TraduceMascaras);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RequisitosPlantilla_TiposContratacion",
                table: "RequisitosPlantilla",
                sql: "\"TiposContratacion\" > 0 AND (\"TiposContratacion\" & ~15) = 0");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GuardiaMigrador);

            migrationBuilder.DropCheckConstraint(
                name: "CK_RequisitosPlantilla_TiposContratacion",
                table: "RequisitosPlantilla");

            migrationBuilder.Sql(RevierteMascaras);

            migrationBuilder.AddCheckConstraint(
                name: "CK_RequisitosPlantilla_TiposContratacion",
                table: "RequisitosPlantilla",
                sql: "\"TiposContratacion\" > 0 AND (\"TiposContratacion\" & ~7) = 0");
        }
    }
}

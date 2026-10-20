using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Histórico congelado de la exportación de Trabajadores extranjeros del mes (decisión de Chris, 2026-10-20): cada
    /// asignación de un Trabajador a una Visita guarda la foto del Trabajador al asignarlo —si contaba como extranjero
    /// cobrable, su documento y su nombre— para que corregir después el documento o la casilla no altere meses pasados.
    ///
    /// <para>
    /// <b>Relleno</b>: las asignaciones que ya existen toman la foto con el criterio vigente
    /// (<c>CriterioExtranjeroCobrable.Efectivo</c>): la marca a mano del Trabajador si la tiene; si no, cuenta todo
    /// documento que, sin espacios, guiones ni puntos y en mayúsculas, no tenga forma de DNI (7 u 8 dígitos y letra) ni
    /// de NIE (X, Y o Z, 7 dígitos y letra). Sin documento (Trabajador anonimizado) no cuenta, y la foto de un
    /// Trabajador anonimizado no copia su documento ni su nombre.
    /// </para>
    ///
    /// <para>
    /// <b>RLS</b>: <c>VisitasTrabajadores</c> y <c>Trabajadores</c> tienen RLS forzada, que también filtra al propietario
    /// que ejecuta la migración (sin Tenant en el contexto no vería ninguna fila). El relleno quita el FORCE solo durante
    /// la sentencia, dentro de la transacción de la migración (que bloquea las dos tablas mientras dura), y lo repone tal
    /// como estaba. No toca políticas, GRANT ni roles.
    /// </para>
    /// </summary>
    public partial class FotoDelTrabajadorEnLaAsignacionAVisita : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DocumentoIdentidad",
                table: "VisitasTrabajadores",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EsExtranjeroCobrable",
                table: "VisitasTrabajadores",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "NombreMostrado",
                table: "VisitasTrabajadores",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    forzada_visitas boolean;
                    forzada_trabajadores boolean;
                BEGIN
                    SELECT relforcerowsecurity INTO forzada_visitas FROM pg_class WHERE oid = 'public."VisitasTrabajadores"'::regclass;
                    SELECT relforcerowsecurity INTO forzada_trabajadores FROM pg_class WHERE oid = 'public."Trabajadores"'::regclass;
                    IF forzada_visitas THEN ALTER TABLE public."VisitasTrabajadores" NO FORCE ROW LEVEL SECURITY; END IF;
                    IF forzada_trabajadores THEN ALTER TABLE public."Trabajadores" NO FORCE ROW LEVEL SECURITY; END IF;

                    UPDATE public."VisitasTrabajadores" vt
                    SET "EsExtranjeroCobrable" = COALESCE(
                            t."MarcaExtranjeroCobrable",
                            d.normalizado IS NOT NULL AND d.normalizado <> ''
                                AND d.normalizado !~ '^([0-9]{7,8}[A-Z]|[XYZ][0-9]{7}[A-Z])$'),
                        "DocumentoIdentidad" = t."Dni",
                        "NombreMostrado" = CASE WHEN t."AnonimizadoEnUtc" IS NULL
                                                THEN btrim(t."Nombre" || ' ' || t."Apellidos") END
                    FROM public."Trabajadores" t
                    CROSS JOIN LATERAL (
                        SELECT upper(regexp_replace(t."Dni", '[[:space:].-]', '', 'g')) AS normalizado) d
                    WHERE t."Id" = vt."TrabajadorId" AND t."TenantId" = vt."TenantId";

                    IF forzada_visitas THEN ALTER TABLE public."VisitasTrabajadores" FORCE ROW LEVEL SECURITY; END IF;
                    IF forzada_trabajadores THEN ALTER TABLE public."Trabajadores" FORCE ROW LEVEL SECURITY; END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DocumentoIdentidad",
                table: "VisitasTrabajadores");

            migrationBuilder.DropColumn(
                name: "EsExtranjeroCobrable",
                table: "VisitasTrabajadores");

            migrationBuilder.DropColumn(
                name: "NombreMostrado",
                table: "VisitasTrabajadores");
        }
    }
}

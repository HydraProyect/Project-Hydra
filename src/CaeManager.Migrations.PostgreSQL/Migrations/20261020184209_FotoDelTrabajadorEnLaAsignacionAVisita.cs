using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Histórico congelado de la exportación de Trabajadores extranjeros del mes (decisión de Chris, 2026-10-20): cada
    /// asignación de un Trabajador a una Visita guarda la foto del Trabajador al asignarlo —si contaba como extranjero
    /// cobrable y, solo en ese caso, su documento y su nombre (minimización)— para que corregir después el documento o la
    /// casilla no altere meses pasados.
    ///
    /// <para>
    /// <b>Relleno</b>: las asignaciones que ya existen toman la foto con el criterio vigente
    /// (<c>CriterioExtranjeroCobrable.Efectivo</c>): la marca a mano del Trabajador si la tiene; si no, cuenta el
    /// documento que, sin espacios, guiones ni puntos y en mayúsculas, sea un Pasaporte válido: sin forma de DNI (7 u 8
    /// dígitos y letra) ni de NIE (X, Y o Z, 7 dígitos y letra), solo letras y números, entre 5 y 20 caracteres, con algún
    /// dígito y sin espacios internos. Un marcador heredado («SIN DNI», «PENDIENTE») no cuenta. Sin documento (Trabajador
    /// anonimizado) no cuenta, y la foto de un Trabajador anonimizado no copia su nombre.
    /// </para>
    ///
    /// <para>
    /// <b>RLS</b>: como el resto de rellenos (<see cref="LineaBaseCompactada"/>, <c>AnadeTipoContratacionTrabajador</c>),
    /// el migrador es superusuario o tiene BYPASSRLS; con un rol sin él el UPDATE no ve filas y la foto queda en su valor
    /// por defecto (no cobrable). No toca políticas, FORCE, GRANT ni roles.
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
                UPDATE public."VisitasTrabajadores" vt
                SET "EsExtranjeroCobrable" = f.cobrable,
                    "DocumentoIdentidad" = CASE WHEN f.cobrable THEN f.dni END,
                    "NombreMostrado" = CASE WHEN f.cobrable AND f.anonimizado_en IS NULL THEN f.nombre END
                FROM (
                    SELECT t."Id", t."TenantId", t."Dni" AS dni, t."AnonimizadoEnUtc" AS anonimizado_en,
                           btrim(t."Nombre" || ' ' || t."Apellidos") AS nombre,
                           COALESCE(
                               t."MarcaExtranjeroCobrable",
                               d.normalizado IS NOT NULL
                                   AND d.normalizado !~ '^([0-9]{7,8}[A-Z]|[XYZ][0-9]{7}[A-Z])$'
                                   AND d.normalizado ~ '^[A-Z0-9]{5,20}$'
                                   AND d.normalizado ~ '[0-9]'
                                   AND btrim(t."Dni") !~ '[[:space:]]') AS cobrable
                    FROM public."Trabajadores" t
                    CROSS JOIN LATERAL (
                        SELECT NULLIF(upper(regexp_replace(t."Dni", '[[:space:].-]', '', 'g')), '') AS normalizado) d
                ) f
                WHERE f."Id" = vt."TrabajadorId" AND f."TenantId" = vt."TenantId";
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

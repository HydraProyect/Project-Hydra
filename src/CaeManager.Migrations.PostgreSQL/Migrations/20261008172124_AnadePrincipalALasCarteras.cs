using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// ADR-011 § 2.7, enmienda del 2026-10-08 (incremento I1): la Asignación de Cartera gana la marca
    /// <c>EsPrincipal</c> —el Gestor CAE principal, o Coordinador CAE principal, de la Asignación de
    /// Operación— y la base de datos impone sus dos invariantes: como máximo una cartera principal no
    /// cerrada por operación (<c>IX_AsignacionesCartera_PrincipalPorOperacion</c>, índice único parcial;
    /// «no cerrada» incluye Suspendida y Programada) y solo sobre una cartera del Tenant entero con rol
    /// Gestor CAE o Coordinador CAE (<c>CK_AsignacionesCartera_PrincipalSoloGestorCaeTenantEntero</c>).
    ///
    /// <para>
    /// <b>Datos existentes</b>, una sola vez: la operación que tiene <b>una sola</b> cartera viva de Gestor
    /// CAE la marca como principal; la que tiene varias no marca ninguna —no se inventa un responsable por
    /// antigüedad— y queda sin principal hasta que alguien lo designe. «Cartera de Gestor CAE» es la del
    /// Tenant entero, no cerrada, con rol <c>GestorCae</c>; o sin rol propio en una operación interna,
    /// cuando el usuario es Gestor CAE (y solo eso) en Identity. Una cartera de Consulta o de Coordinador
    /// CAE ni cuenta ni se marca. La marca no concede nada en datos: nadie gana ni pierde alcance.
    /// </para>
    ///
    /// <para>
    /// <b>RLS</b>: como <c>ConvierteCarterasPorClienteATenantEntero</c>, la ejecuta el propietario de la
    /// tabla, que no pasa por las políticas (RLS activada, no forzada), así que ve todos los Tenants. No
    /// toca ninguna política. Lo comprueba <c>AnadePrincipalALasCarterasTests</c>.
    /// </para>
    /// </summary>
    public partial class AnadePrincipalALasCarteras : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EsPrincipal",
                table: "AsignacionesCartera",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Va antes del índice único: por construcción marca como mucho una cartera por operación
            // (HAVING count(*) = 1), y si no fuera así el índice haría fallar la migración en vez de
            // dejar dos principales.
            migrationBuilder.Sql("""
                UPDATE "AsignacionesCartera" c
                SET "EsPrincipal" = TRUE,
                    "Version" = md5(random()::text || clock_timestamp()::text)::uuid
                WHERE c."Id" IN (
                    SELECT (array_agg(g."Id"))[1]
                    FROM "AsignacionesCartera" g
                    WHERE g."Estado" <> 'Cerrada'
                      AND g."AmbitoRelacionClienteId" IS NULL AND g."AmbitoCentroId" IS NULL
                      AND g."AmbitoTrabajadorId" IS NULL AND g."AmbitoProyectoId" IS NULL
                      AND (g."Rol" = 'GestorCae'
                           OR (g."Rol" IS NULL
                               AND g."OperadorTenantId" = g."PropietarioTenantId"
                               AND EXISTS (
                                   SELECT 1 FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
                                   WHERE ur."UserId" = g."UsuarioId" AND r."Name" = 'GestorCae')
                               AND NOT EXISTS (
                                   SELECT 1 FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
                                   WHERE ur."UserId" = g."UsuarioId" AND r."Name" <> 'GestorCae')))
                    GROUP BY g."AsignacionOperacionId"
                    HAVING count(*) = 1);
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AsignacionesCartera_PrincipalPorOperacion",
                table: "AsignacionesCartera",
                column: "AsignacionOperacionId",
                unique: true,
                filter: "\"EsPrincipal\" AND \"Estado\" <> 'Cerrada'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AsignacionesCartera_PrincipalSoloGestorCaeTenantEntero",
                table: "AsignacionesCartera",
                sql: "NOT \"EsPrincipal\" OR (\"AmbitoRelacionClienteId\" IS NULL AND \"AmbitoCentroId\" IS NULL AND \"AmbitoTrabajadorId\" IS NULL AND \"AmbitoProyectoId\" IS NULL AND (\"Rol\" IS NULL OR \"Rol\" IN ('GestorCae', 'CoordinadorCae')))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AsignacionesCartera_PrincipalPorOperacion",
                table: "AsignacionesCartera");

            migrationBuilder.DropCheckConstraint(
                name: "CK_AsignacionesCartera_PrincipalSoloGestorCaeTenantEntero",
                table: "AsignacionesCartera");

            migrationBuilder.DropColumn(
                name: "EsPrincipal",
                table: "AsignacionesCartera");
        }
    }
}

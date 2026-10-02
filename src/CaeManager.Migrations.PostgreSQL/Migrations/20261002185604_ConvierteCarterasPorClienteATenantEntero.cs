using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// D-7 (decisión del propietario, 2026-10-02): se retira el reparto de la Asignación de Cartera por
    /// Cliente empresarial (el «incremento 2» de la enmienda 2026-09-23 de ADR-011 § 2.7). Esta migración
    /// <b>convierte una sola vez</b> los datos que ya existen: la cartera de un Gestor CAE es siempre el
    /// Tenant entero.
    ///
    /// <para>
    /// <b>Qué hace</b>, para cada par (Asignación de Operación, usuario) con alguna Asignación de Cartera
    /// por Cliente empresarial vigente:
    /// <list type="number">
    /// <item>abre <b>una</b> cartera universal vigente (si el usuario no tenía ya una), con el rol de la
    /// primera cartera del grupo, <c>VigenciaDesde</c> la más antigua y <c>VigenciaHasta</c> nula solo
    /// si alguna del grupo era indefinida (si no, la más tardía: la conversión nunca alarga una cartera
    /// con caducidad);</item>
    /// <item>cierra, con motivo <c>Reorganizada</c> («el ámbito se partió o se reagrupó»), <b>todas</b>
    /// las carteras por Cliente empresarial no cerradas. El histórico cerrado se conserva.</item>
    /// </list>
    /// Solo convierte lo que concedía alcance hoy: cartera <c>Vigente</c> y sin caducar, bajo una
    /// Asignación de Operación <c>Vigente</c> de ámbito universal o acotada al mismo Cliente empresarial
    /// (el ámbito efectivo era ese Cliente y una universal bajo esa operación da exactamente ese Cliente: no
    /// se pierde ni se gana). Una cartera con caducidad pasada, <c>Programada</c> o <c>Suspendida</c>, o bajo
    /// una operación acotada a otro Cliente (ámbito efectivo vacío), se cierra sin sustituir: convertirla
    /// daría más de lo que daba. Los roles de Propiedad (Administrador, Dirección CAE) nunca se convierten:
    /// una cartera no los concede (decisión del 2026-09-23). En una cartera externa el rol es el de la
    /// propia cartera; en una interna (sin rol) se comprueba el rol de Identity del usuario.
    /// </para>
    ///
    /// <para>
    /// <b>Efecto buscado y deliberado</b>: quien tenía alguna cartera por Cliente empresarial pasa a tener
    /// el Tenant propietario entero (para ver y gestionar), y el Coordinador CAE de ese Gestor CAE lo hereda.
    /// No cambia nada para quien no tenía cartera, ni para otros Tenants, ni para los roles de Propiedad.
    /// Es de una sola vía: es idempotente (una segunda ejecución no encuentra nada que convertir) y
    /// <c>Down</c> no repone el reparto por Cliente empresarial —no se debe reactivar un modo retirado—;
    /// las carteras universales abiertas y las cerradas quedan como están.
    /// </para>
    ///
    /// <para>
    /// <b>RLS</b>: <c>AsignacionesCartera</c> tiene RLS activada pero no forzada, y esta migración la ejecuta
    /// el propietario de la tabla, que no pasa por las políticas: ve todos los Tenants. No debilita ninguna
    /// política (no toca ninguna). Lo comprueba <c>ConvierteCarterasPorClienteATenantEnteroTests</c>.
    /// </para>
    /// </summary>
    public partial class ConvierteCarterasPorClienteATenantEntero : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Una universal por (operación, usuario). Va antes del cierre: lee las carteras que cierra.
            //    El Id se deriva de (operación, usuario) con md5 en vez de generarlo: así la sentencia es
            //    idempotente por construcción y no depende de uuidv7() (PostgreSQL 18), que no existe en el
            //    PostgreSQL 17 que sigue usándose en algunas máquinas de desarrollo; gen_random_uuid lo
            //    prohíbe IdentificadoresDeEntidadUuidV7Tests para Ids de entidad. Son 1-N filas, una por
            //    Gestor CAE con cartera: la localidad de un UUID v7 no aporta nada aquí. El token de
            //    concurrencia (Version) sí es un valor opaco y aleatorio.
            migrationBuilder.Sql("""
                INSERT INTO "AsignacionesCartera"
                    ("Id", "AsignacionOperacionId", "UsuarioId", "Rol", "PropietarioTenantId", "OperadorTenantId",
                     "AmbitoRelacionClienteId", "AmbitoCentroId", "AmbitoTrabajadorId", "AmbitoProyectoId",
                     "VigenciaDesde", "VigenciaHasta", "Estado", "MotivoCierre", "Version", "CreadoEnUtc", "CreadoPorUsuarioId")
                SELECT md5('d7:' || c."AsignacionOperacionId"::text || ':' || c."UsuarioId"::text)::uuid, c."AsignacionOperacionId", c."UsuarioId",
                       (array_agg(c."Rol" ORDER BY c."CreadoEnUtc", c."Id"))[1],
                       c."PropietarioTenantId", c."OperadorTenantId",
                       NULL, NULL, NULL, NULL,
                       MIN(c."VigenciaDesde"),
                       CASE WHEN bool_or(c."VigenciaHasta" IS NULL) THEN NULL ELSE MAX(c."VigenciaHasta") END,
                       'Vigente', NULL, md5(random()::text || clock_timestamp()::text)::uuid, now(), NULL
                FROM "AsignacionesCartera" c
                JOIN "AsignacionesOperacion" o ON o."Id" = c."AsignacionOperacionId"
                WHERE c."Estado" = 'Vigente'
                  AND c."AmbitoRelacionClienteId" IS NOT NULL
                  AND c."AmbitoCentroId" IS NULL AND c."AmbitoTrabajadorId" IS NULL AND c."AmbitoProyectoId" IS NULL
                  AND (c."VigenciaHasta" IS NULL OR c."VigenciaHasta" > now())
                  AND (c."Rol" IS NULL OR c."Rol" NOT IN ('Administrador', 'DireccionCae'))
                  AND o."Estado" = 'Vigente'
                  AND o."VigenciaDesde" <= now() AND (o."VigenciaHasta" IS NULL OR o."VigenciaHasta" > now())
                  AND (o."AmbitoRelacionClienteId" IS NULL OR o."AmbitoRelacionClienteId" = c."AmbitoRelacionClienteId")
                  AND o."AmbitoCentroId" IS NULL AND o."AmbitoTrabajadorId" IS NULL AND o."AmbitoProyectoId" IS NULL
                  AND (c."Rol" IS NOT NULL OR NOT EXISTS (
                      SELECT 1 FROM "AspNetUserRoles" ur JOIN "AspNetRoles" r ON r."Id" = ur."RoleId"
                      WHERE ur."UserId" = c."UsuarioId" AND r."Name" IN ('Administrador', 'DireccionCae')))
                  AND NOT EXISTS (
                      SELECT 1 FROM "AsignacionesCartera" u
                      WHERE u."AsignacionOperacionId" = c."AsignacionOperacionId"
                        AND u."UsuarioId" = c."UsuarioId"
                        AND u."Estado" = 'Vigente'
                        AND u."AmbitoRelacionClienteId" IS NULL AND u."AmbitoCentroId" IS NULL
                        AND u."AmbitoTrabajadorId" IS NULL AND u."AmbitoProyectoId" IS NULL)
                GROUP BY c."AsignacionOperacionId", c."UsuarioId", c."PropietarioTenantId", c."OperadorTenantId"
                ON CONFLICT ("Id") DO NOTHING;
                """);

            // 2) Se cierran todas las carteras por Cliente empresarial que no estén ya cerradas. Mismo cierre
            //    que AsignacionResponsabilidad.Cerrar: adelanta el fin de vigencia, nunca lo alarga.
            migrationBuilder.Sql("""
                UPDATE "AsignacionesCartera"
                SET "Estado" = 'Cerrada',
                    "MotivoCierre" = 'Reorganizada',
                    "VigenciaHasta" = CASE
                        WHEN "VigenciaHasta" IS NULL OR "VigenciaHasta" > now() THEN GREATEST(now(), "VigenciaDesde")
                        ELSE "VigenciaHasta" END,
                    "Version" = md5(random()::text || clock_timestamp()::text)::uuid
                WHERE "Estado" <> 'Cerrada' AND "AmbitoRelacionClienteId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Sin efecto, a propósito: el reparto por Cliente empresarial está retirado y no se repone.
            // Deshacer la conversión dejaría Asignaciones de Cartera que ningún productor sabe escribir.
        }
    }
}

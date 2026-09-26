using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Corrige un defecto real de <see cref="BackfillReclamacionBuzonIntegracionDesdeConexionesExistentes"/>
    /// (fusionada en PR #869, mismo día): su <c>INSERT ... SELECT</c> leía
    /// <c>ConexionesIntegracion</c> con un <c>SELECT</c> plano, sin fijar
    /// <c>app.tenant_id</c>. Esa tabla tiene <c>FORCE ROW LEVEL SECURITY</c>
    /// desde <c>HabilitarRlsIntegraciones</c> (2026-08-02), y el rol que migra
    /// es el propietario, sin <c>BYPASSRLS</c> (mismo patrón ya documentado en
    /// <c>F3cRetiradaClientesSubcontratasLegacy</c> y en
    /// <c>RetirarDniDeRecientesDeTrabajador</c>, esta última del mismo día):
    /// sin <c>app.tenant_id</c>, la política <c>aislamiento_tenant</c> no
    /// empareja ninguna fila y el backfill original insertó CERO filas en
    /// producción real, aunque sus tests dieran verde — la conexión de
    /// pruebas de la migración es superusuario, que ignora RLS.
    ///
    /// <para>
    /// <b>Hallazgo</b>: reportado por otra sesión de Claude (mensaje entre
    /// sesiones, 2026-09-24) por inferencia sobre el código, confirmado aquí
    /// leyendo la política real y el patrón ya usado en el propio repositorio
    /// antes de escribir esta migración.
    /// </para>
    ///
    /// <para>
    /// <b>Por qué una tabla temporal y no un <c>INSERT</c> por Tenant dentro
    /// del bucle</b>: el backfill original resuelve los buzones que ya
    /// quedaron compartidos entre dos Tenants (el bug que el incremento
    /// cierra) a favor de la conexión más antigua, con <c>ORDER BY
    /// "CreadoEnUtc" ASC, "Id" ASC</c> dentro de un único <c>INSERT</c> — el
    /// desempate por <c>Id</c> es un hallazgo ya cerrado de la ronda 2 de
    /// Codex sobre PR #820, con test propio. Si el <c>INSERT</c> se repitiera
    /// una vez por Tenant dentro del bucle, el orden de recorrido de
    /// <c>Tenants</c> — no la fecha de creación — decidiría qué conexión gana
    /// el conflicto de unicidad entre dos Tenants distintos, rompiendo esa
    /// garantía en silencio. En su lugar, el bucle solo recolecta bajo RLS
    /// (una tabla temporal, <c>ON COMMIT DROP</c>, sin política de
    /// aislamiento porque no es una tabla de negocio) y el <c>ORDER BY</c> /
    /// <c>ON CONFLICT DO NOTHING</c> se aplican una sola vez, al final, sobre
    /// el conjunto global — igual que en el original, solo que ahora
    /// alimentado fila a fila desde cada Tenant en vez de un <c>SELECT</c>
    /// que RLS habría vaciado.
    /// </para>
    ///
    /// <para>
    /// Mismo criterio de exclusión que el original (<c>Proveedor = 0</c>,
    /// <c>Estado &lt;&gt; 1</c> Deshabilitada, <c>NOT EstaEliminado</c>) y
    /// mismo contrato: aditiva, solo <c>INSERT</c>, <c>ON CONFLICT DO
    /// NOTHING</c> la hace idempotente frente a un reintento y frente al
    /// backfill original en cualquier entorno donde sí llegó a insertar (por
    /// ejemplo, desarrollo local, donde el rol propietario es superusuario).
    /// <c>Down()</c> revierte esquema, nunca datos — no hay esquema que
    /// revertir aquí.
    /// </para>
    /// </summary>
    public partial class CorregirBackfillReclamacionBuzonIntegracionBajoRls : Migration
    {
        /// <summary>
        /// El SQL de <see cref="Up"/>, expuesto para que los tests lo ejecuten
        /// también con un rol sujeto a RLS (la conexión de pruebas de la
        /// migración es superusuario y no lo está).
        /// </summary>
        public const string SqlCorregirBackfill = """
            DO $$
            DECLARE
                tenant uuid;
            BEGIN
                CREATE TEMP TABLE candidatos_reclamacion_buzon (
                    "BuzonEmail" text,
                    "TenantPropietarioId" uuid,
                    "ConexionIntegracionId" uuid,
                    "CreadoEnUtc" timestamptz
                ) ON COMMIT DROP;

                FOR tenant IN SELECT "Id" FROM "Tenants" LOOP
                    PERFORM set_config('app.tenant_id', tenant::text, true);

                    INSERT INTO candidatos_reclamacion_buzon
                        ("BuzonEmail", "TenantPropietarioId", "ConexionIntegracionId", "CreadoEnUtc")
                    SELECT LOWER(TRIM(BOTH FROM ci."BuzonEmail")), ci."TenantId", ci."Id", ci."CreadoEnUtc"
                    FROM "ConexionesIntegracion" ci
                    WHERE ci."Proveedor" = 0 AND ci."Estado" <> 1 AND NOT ci."EstaEliminado";
                END LOOP;

                PERFORM set_config('app.tenant_id', '', true);

                INSERT INTO "ReclamacionesBuzonIntegracion"
                    ("Id", "BuzonEmail", "TenantPropietarioId", "ConexionIntegracionId", "ReclamadoEnUtc")
                SELECT gen_random_uuid(), c."BuzonEmail", c."TenantPropietarioId", c."ConexionIntegracionId", c."CreadoEnUtc"
                FROM candidatos_reclamacion_buzon c
                ORDER BY c."CreadoEnUtc" ASC, c."ConexionIntegracionId" ASC
                ON CONFLICT ("BuzonEmail") DO NOTHING;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlCorregirBackfill);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A propósito sin DELETE — mismo criterio que el backfill original.
        }
    }
}

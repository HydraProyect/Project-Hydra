using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <summary>
    /// Cierre de F3 — retirada física de las tablas legacy <c>Clientes</c> y
    /// <c>Subcontratas</c>. Desde F3b (PR #279/#280) no reciben escrituras, y
    /// desde este mismo incremento no les queda ningún lector: los dos últimos
    /// (<c>BuscarGlobalQuery</c> y <c>EjecutarImportacionCommand</c>) leen ya
    /// <c>Empresas</c> con los discriminadores <c>EsCritico != null</c> /
    /// <c>NivelServicio != null</c>.
    ///
    /// <para>
    /// <b>ARTEFACTO DE PRESERVACIÓN — GENERADO Y VERIFICADO (2026-08-28).</b>
    /// Igual que el <c>DROP</c> de F4 (<c>F4CierreDropTablasPuente</c>), este
    /// exigía exportar íntegras las dos tablas desde PRODUCCIÓN a un artefacto
    /// inmutable en el repositorio de negocio. Hecho:
    /// <c>tecnico/artefactos-migracion/f3c-clientes-subcontratas-20260828.sql</c>
    /// <code>
    /// SHA-256  1dc62638fe16a6f52638008ccd558ba756dd823136f8c687597d22dc94cd27b5
    /// filas    40 (21 Clientes + 19 Subcontratas)
    /// </code>
    /// Los recuentos coinciden exactos con los medidos en producción antes de
    /// exportar, y el hash se verificó a los dos lados de la copia.
    ///
    /// Las dos comprobaciones del <c>Up()</c> se ejecutaron además A MANO contra
    /// producción antes de desplegar, y dieron cero filas: ninguna fila legacy
    /// sin contraparte en <c>Empresas</c>, ninguna divergencia de
    /// <c>CreadoEnUtc</c>. No sustituyen a la verificación automática —siguen
    /// ahí y siguen abortando— pero convierten el despliegue en algo cuyo
    /// resultado se conoce antes de lanzarlo.
    /// </para>
    ///
    /// <para>
    /// <b>Por qué el artefacto de F3c pesa menos que el de F4, y aun así se
    /// exige</b>: las tres tablas puente de F4 guardaban semántica
    /// (<c>EnmarcadaEnId</c>) que no vivía en ningún otro sitio. Aquí no: la
    /// verificación de abajo aborta el <c>Up()</c> si existe una sola fila
    /// legacy sin contraparte en <c>Empresas</c>, así que al llegar al
    /// <c>DROP</c> el contenido retirado es, por construcción, un subconjunto
    /// de una tabla viva. El artefacto cubre el caso de auditoría posterior
    /// (¿qué decía exactamente la fila congelada?), no la reconstrucción del
    /// dato.
    /// </para>
    ///
    /// <para>
    /// <b>Qué verifica el <c>Up()</c>, y por qué NO es la comparación campo a
    /// campo que describía el diseño</b>
    /// (<c>f3c-diseno-adversario-reconciliacion-2026-08-25.md</c> § 2).
    /// Aquel diseño asumía que entre T1 y F3c ninguna de las dos tablas se
    /// movía. La mitad es cierta —<c>Clientes</c>/<c>Subcontratas</c> quedaron
    /// congeladas— pero <c>Empresas</c> es desde F3b la fuente de escritura
    /// viva: cada edición de una contraparte cambia <c>RazonSocial</c>,
    /// <c>Cif</c>, <c>EsCritico</c>, <c>Notas</c>, <c>EjecutivoUsuarioId</c>,
    /// el borrado lógico y <c>Version</c> en <c>Empresas</c> y no en la fila
    /// congelada. Una igualdad campo a campo abortaría sobre datos correctos
    /// —cada cliente editado desde el 2026-08-26— y no distinguiría eso de un
    /// escritor colado. Es un instrumento que mide otra cosa.
    /// </para>
    ///
    /// <para>
    /// Lo que sí sostiene la propiedad que importa ("nada escribió en las
    /// tablas legacy después de T1") son dos comprobaciones sobre lo que
    /// <b>no puede</b> cambiar legítimamente:
    /// <list type="number">
    /// <item><b>Presencia</b>: toda fila legacy tiene contraparte en
    /// <c>Empresas</c> con el mismo <c>Id</c> y el mismo <c>TenantId</c>. La
    /// única vía que crea esa contraparte es el backfill de F3a
    /// (<c>F3aEmpresasUnificadaPreparacion</c>, el único <c>INSERT INTO
    /// "Empresas"</c> de toda la cadena de migraciones — verificado, no
    /// supuesto). Una fila legacy sin contraparte solo puede haber nacido
    /// DESPUÉS de ese backfill, que es exactamente el escritor residual que
    /// hay que detectar. Y no es hipotético: el <c>UPSERT</c> de T1 que el
    /// diseño exigía a F3b (§ 1, punto 1) <b>nunca se implementó</b> — las
    /// migraciones <c>F3bClienteRepunteoFks</c> y
    /// <c>F3bSubcontrataRepunteoFks</c> no contienen una sola sentencia SQL,
    /// solo repunteo de FKs. Cualquier alta ocurrida en la ventana T0…T1 vive
    /// únicamente en la tabla legacy, y esta comprobación es lo único que
    /// impide que el <c>DROP</c> se la lleve en silencio.</item>
    /// <item><b>Coordenadas inmutables</b>: <c>CreadoEnUtc</c> coincide con el
    /// de su contraparte. El backfill lo copió literal y ninguna operación de
    /// <c>Empresa</c> lo modifica, así que una diferencia aquí significa que
    /// una de las dos filas fue reescrita por un camino no previsto.</item>
    /// </list>
    /// Si cualquiera de las dos falla, el <c>Up()</c> lanza excepción con el
    /// <c>Id</c> exacto y PostgreSQL revierte la migración entera: no se retira
    /// nada. Nunca hay heurística de resolución — una divergencia no es un dato
    /// que arreglar, es la prueba de que falta trabajo de F3b.
    /// </para>
    ///
    /// <para>
    /// <b>Por qué la verificación recorre tenants en vez de desactivar RLS</b>:
    /// las tres tablas tienen <c>FORCE ROW LEVEL SECURITY</c>
    /// (<c>HabilitarRlsPostgres</c>), y el rol que migra es el propietario y NO
    /// tiene <c>BYPASSRLS</c> — sin <c>app.tenant_id</c> fijado, la política no
    /// empareja ninguna fila y la comprobación pasaría en verde <b>sin haber
    /// mirado nada</b>. Ese es el modo de fallo peligroso: un verde vacío justo
    /// antes de un <c>DROP</c>. La alternativa obvia (<c>NO FORCE</c> temporal
    /// sobre <c>Empresas</c>) se descarta: no se debilita RLS para acomodar
    /// código, ni siquiera dentro de una transacción. En su lugar el bloque
    /// itera los tenants de <c>Tenants</c> (catálogo global, fuera de RLS por
    /// diseño) fijando <c>app.tenant_id</c> con
    /// <c>set_config(..., is_local =&gt; true)</c>, que muere con la
    /// transacción.
    /// <b>Residual documentado</b>: una fila legacy cuyo <c>TenantId</c> no
    /// esté en <c>Tenants</c> sería invisible al recorrido. No hay FK que lo
    /// impida, pero tampoco camino de escritura que lo produzca — toda alta
    /// pasó por el sellado de <c>TenantSelladoInterceptor</c>. Se acepta y se
    /// deja escrito en vez de suponer que no existe.
    /// </para>
    ///
    /// <para>
    /// <b>Sobre el <c>Down</c></b>: recrea las tablas VACÍAS con sus índices y
    /// sus políticas RLS — no restaura datos. El bloque RLS no es cosmético:
    /// <c>DROP TABLE</c> se lleva las políticas, y un rollback sin él dejaría
    /// dos tablas con <c>TenantId</c> sin ningún aislamiento entre tenants, que
    /// es peor que no poder revertir. Mismo criterio y mismo patrón literal que
    /// el <c>Down</c> de <c>F4CierreDropTablasPuente</c>.
    /// </para>
    /// </summary>
    public partial class F3cRetiradaClientesSubcontratasLegacy : Migration
    {
        private static readonly string[] TablasLegacy = ["Clientes", "Subcontratas"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                    tenant uuid;
                    hallazgo text;
                    sin_contraparte text := '';
                    coordenadas_divergentes text := '';
                BEGIN
                    FOR tenant IN SELECT "Id" FROM "Tenants" LOOP
                        PERFORM set_config('app.tenant_id', tenant::text, true);

                        SELECT string_agg(linea, ', ') INTO hallazgo FROM (
                            SELECT 'Cliente ' || c."Id"::text AS linea
                            FROM "Clientes" c
                            WHERE NOT EXISTS (
                                SELECT 1 FROM "Empresas" e
                                WHERE e."Id" = c."Id" AND e."TenantId" = c."TenantId")
                            UNION ALL
                            SELECT 'Subcontrata ' || s."Id"::text
                            FROM "Subcontratas" s
                            WHERE NOT EXISTS (
                                SELECT 1 FROM "Empresas" e
                                WHERE e."Id" = s."Id" AND e."TenantId" = s."TenantId")
                        ) q;

                        IF hallazgo IS NOT NULL THEN
                            sin_contraparte := sin_contraparte
                                || format(' [tenant %s] %s', tenant, hallazgo);
                        END IF;

                        SELECT string_agg(linea, ', ') INTO hallazgo FROM (
                            SELECT 'Cliente ' || c."Id"::text || ' (CreadoEnUtc)' AS linea
                            FROM "Clientes" c
                            JOIN "Empresas" e ON e."Id" = c."Id" AND e."TenantId" = c."TenantId"
                            WHERE e."CreadoEnUtc" IS DISTINCT FROM c."CreadoEnUtc"
                            UNION ALL
                            SELECT 'Subcontrata ' || s."Id"::text || ' (CreadoEnUtc)'
                            FROM "Subcontratas" s
                            JOIN "Empresas" e ON e."Id" = s."Id" AND e."TenantId" = s."TenantId"
                            WHERE e."CreadoEnUtc" IS DISTINCT FROM s."CreadoEnUtc"
                        ) q;

                        IF hallazgo IS NOT NULL THEN
                            coordenadas_divergentes := coordenadas_divergentes
                                || format(' [tenant %s] %s', tenant, hallazgo);
                        END IF;
                    END LOOP;

                    IF sin_contraparte <> '' THEN
                        RAISE EXCEPTION
                            'F3c abortada: filas en Clientes/Subcontratas sin contraparte en Empresas:%. Son altas posteriores al backfill de F3a; retirarlas seria perdida de datos. Hay que llevarlas a Empresas antes de repetir esta migracion.',
                            sin_contraparte;
                    END IF;

                    IF coordenadas_divergentes <> '' THEN
                        RAISE EXCEPTION
                            'F3c abortada: CreadoEnUtc diverge entre la fila legacy y su contraparte:%. Algo reescribio una de las dos por un camino no previsto; no se retira nada.',
                            coordenadas_divergentes;
                    END IF;
                END $$;
                """);

            migrationBuilder.DropTable(
                name: "Clientes");

            migrationBuilder.DropTable(
                name: "Subcontratas");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Clientes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Cif = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EjecutivoUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    EsCritico = table.Column<bool>(type: "boolean", nullable: false),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    Notas = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RazonSocial = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clientes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Subcontratas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Cif = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: true),
                    CreadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EliminadoEnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EliminadoPorUsuarioId = table.Column<Guid>(type: "uuid", nullable: true),
                    EstaEliminado = table.Column<bool>(type: "boolean", nullable: false),
                    NivelServicio = table.Column<int>(type: "integer", nullable: false),
                    RazonSocial = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subcontratas", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_EjecutivoUsuarioId",
                table: "Clientes",
                column: "EjecutivoUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_RazonSocial",
                table: "Clientes",
                column: "RazonSocial");

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_TenantId_Cif",
                table: "Clientes",
                columns: new[] { "TenantId", "Cif" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Clientes_TenantId_Id",
                table: "Clientes",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subcontratas_TenantId_Cif",
                table: "Subcontratas",
                columns: new[] { "TenantId", "Cif" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subcontratas_TenantId_Id",
                table: "Subcontratas",
                columns: new[] { "TenantId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subcontratas_TenantId_RazonSocial",
                table: "Subcontratas",
                columns: new[] { "TenantId", "RazonSocial" },
                unique: true);

            // Las políticas RLS murieron con el DROP TABLE del Up. Recrearlas
            // aquí no es cosmética: sin este bloque, revertir dejaría dos
            // tablas con TenantId completamente abiertas entre tenants.
            // Patrón copiado literal de 20260801120000_HabilitarRlsPostgres.
            foreach (var tabla in TablasLegacy)
            {
                migrationBuilder.Sql($"""
                    ALTER TABLE "{tabla}" ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE "{tabla}" FORCE ROW LEVEL SECURITY;
                    DROP POLICY IF EXISTS aislamiento_tenant ON "{tabla}";
                    CREATE POLICY aislamiento_tenant ON "{tabla}"
                        USING ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK ("TenantId" = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                    """);
            }
        }
    }
}

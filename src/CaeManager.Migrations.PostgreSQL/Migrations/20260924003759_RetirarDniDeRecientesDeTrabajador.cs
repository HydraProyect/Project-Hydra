using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Hasta el 2026-09-24 el buscador global (Ctrl+K) ponía el DNI del
    /// Trabajador como subtítulo de cada resultado, y al elegir uno el
    /// Command Palette lo guardaba tal cual en <c>EventosRecientesUsuario.Subtitulo</c>
    /// —y lo volvía a pintar en «Recientes», sin reaplicar la cartera—. Desde
    /// esa fecha el buscador ni busca por DNI ni lo muestra (decisión delegada
    /// en Codex por el propietario, que extiende P4 «base general sin DNI»);
    /// esta migración retira el DNI ya guardado: <c>Subtitulo = NULL</c> en
    /// todo reciente de tipo <c>Trabajador</c>. Un reciente sin subtítulo se
    /// pinta como «Trabajador» a secas; los nuevos guardan la razón social de
    /// la organización empleadora.
    ///
    /// <para>
    /// <b>Por qué recorre los Tenants</b>: la tabla tiene <c>FORCE ROW LEVEL
    /// SECURITY</c> (<c>AgregarEventoRecienteUsuario</c>) y el rol que migra es
    /// el propietario sin <c>BYPASSRLS</c>. Un <c>UPDATE</c> sin
    /// <c>app.tenant_id</c> no emparejaría ninguna fila y la migración daría
    /// verde sin haber borrado nada. Mismo patrón que
    /// <c>F3cRetiradaClientesSubcontratasLegacy</c>: se itera el catálogo global
    /// <c>Tenants</c> fijando <c>app.tenant_id</c> con
    /// <c>set_config(..., is_local =&gt; true)</c>, que muere con la
    /// transacción; nunca se desactiva RLS. <b>Residual</b>: un reciente cuyo
    /// <c>TenantId</c> no esté en <c>Tenants</c> no se ve desde el recorrido;
    /// no hay camino de escritura que lo produzca (todo alta pasa por
    /// <c>TenantSelladoInterceptor</c>).
    /// </para>
    ///
    /// <para>
    /// <b>Down</b> no restaura nada: el DNI retirado no se conserva en ningún
    /// sitio, a propósito (irreversible por diseño).
    /// </para>
    /// </summary>
    public partial class RetirarDniDeRecientesDeTrabajador : Migration
    {
        /// <summary>
        /// El SQL de <see cref="Up"/>, expuesto para que los tests lo ejecuten
        /// también con un rol sujeto a RLS (la conexión de pruebas de la
        /// migración es superusuario y no lo está).
        /// </summary>
        public const string SqlRetirarSubtituloDeTrabajador = @"
DO $$
DECLARE
    tenant uuid;
BEGIN
    FOR tenant IN SELECT ""Id"" FROM ""Tenants"" LOOP
        PERFORM set_config('app.tenant_id', tenant::text, true);

        UPDATE ""EventosRecientesUsuario""
        SET ""Subtitulo"" = NULL
        WHERE ""Tipo"" = 'Trabajador' AND ""Subtitulo"" IS NOT NULL;
    END LOOP;
END $$;
";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(SqlRetirarSubtituloDeTrabajador);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nada que restaurar: el DNI retirado no se guarda en ningún sitio.
        }
    }
}

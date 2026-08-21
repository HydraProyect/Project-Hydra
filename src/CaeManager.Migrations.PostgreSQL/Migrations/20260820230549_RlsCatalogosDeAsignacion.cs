using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Tercera capa de la política de lectura de los catálogos globales de
    /// asignación (ADR-011, endurecimiento E1 del plan): RLS sobre
    /// <c>AsignacionesOperacion</c> y <c>AsignacionesCartera</c>. Las capas 1 y 2
    /// —acotar por posición en Application, y el test de arquitectura que
    /// mantiene corta la lista de sitios que las tocan— ya existen desde F1.
    ///
    /// <b>Estas tablas se protegen de otra manera que las 40 tablas de datos, y
    /// la diferencia no es de comodidad.</b> La propiedad que se busca aquí no
    /// es "esta tabla es un catálogo global" —eso describe por qué una fila
    /// cruza tenants, no quién queda sujeto a la política— sino esta otra, que
    /// es sobre roles:
    ///
    /// <para>
    /// <b>Las sesiones de aplicación restringidas están sujetas a RLS; las
    /// operaciones sistémicas que necesitan visión global, no.</b>
    /// </para>
    ///
    /// De ahí <c>ENABLE</c> sin <c>FORCE</c>. Con <c>FORCE</c>, RLS ataría
    /// también al propietario de la tabla, y por ahí pasan hoy dos caminos
    /// legítimos que necesitan ver el grafo entero y no tienen —ni pueden
    /// tener— un tenant de sesión:
    /// <list type="bullet">
    /// <item><c>AsignacionesOperativasBackfillSeeder</c>, que al arrancar lee
    /// todas las operaciones y carteras de todos los tenants para reconciliar
    /// contra la proyección. Con una política por tenant leería cero filas y
    /// "reconciliaría" contra un vacío: cerraría y recrearía asignaciones en
    /// silencio, que es peor que fallar.</item>
    /// <item><c>ExpiracionAsignacionesHostedService</c>, que caduca vigencias en
    /// segundo plano sin sesión de usuario.</item>
    /// </list>
    ///
    /// La alternativa —una válvula de escape por variable de sesión, del tipo
    /// <c>OR current_setting('app.contexto_sistema') = 'true'</c>— queda
    /// descartada: es un interruptor que cualquiera puede accionar, exactamente
    /// el "TenantId comodín" que ADR-011 § 4bis.3 prohíbe.
    ///
    /// Lo que esta decisión NO hace es dejar los catálogos sin protección frente
    /// a una sesión de usuario: <c>cae_app_runtime</c> y <c>cae_app_soporte</c>
    /// no son propietarios de nada, así que la política los ata igual que a
    /// cualquier otro rol restringido. Y eso se comprueba por rol, no por la
    /// ausencia de <c>FORCE</c>: si mañana cambiara el propietario de las
    /// tablas, el comportamiento podría cambiar sin que <c>relforcerowsecurity</c>
    /// se moviera.
    ///
    /// <para>
    /// <b>La asimetría entre <c>USING</c> y <c>WITH CHECK</c> es el punto
    /// delicado.</b> Se <i>ve</i> por cualquiera de las dos posiciones —
    /// propietario u operador — pero solo se <i>escribe</i> sobre el tenant en
    /// cuyo contexto se está. Con un <c>WITH CHECK</c> simétrico, un operador
    /// podría acuñarse a sí mismo una asignación nombrándose operador sobre un
    /// propietario arbitrario, que es escalada de privilegio por la puerta de
    /// atrás: no necesitaría ver nada ajeno para concedérselo.
    /// </para>
    ///
    /// Las tres tablas del plano 3 (<c>ConcesionesPrivilegio</c>,
    /// <c>SesionesPrivilegiadas</c>, <c>TenantsAlcanzadosPorConcesion</c>) NO
    /// entran aquí, y no como excepción equivalente a esta sino por dependencia
    /// arquitectónica: su política necesitaría una variable de sesión con el
    /// usuario de plataforma actual, y esa identidad todavía no existe. Va con
    /// el incremento que construya la apertura de sesiones privilegiadas.
    ///
    /// Pura DDL de servidor: no cambia el modelo de EF, así que no hay diffs que
    /// aplicar en el snapshot.
    /// </summary>
    public partial class RlsCatalogosDeAsignacion : Migration
    {
        private static readonly string[] CatalogosDeAsignacion =
        [
            "AsignacionesOperacion", "AsignacionesCartera",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var arrayTablas = string.Join(",", System.Array.ConvertAll(CatalogosDeAsignacion, t => $"'{t}'"));

            migrationBuilder.Sql($@"
DO $$
DECLARE
    tabla text;
BEGIN
    FOREACH tabla IN ARRAY ARRAY[{arrayTablas}]
    LOOP
        EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY;', tabla);
        -- Sin FORCE, y es deliberado: ver el comentario de la clase. El seeder
        -- de backfill y el job de expiración operan como propietario y
        -- necesitan ver el grafo completo.
        EXECUTE format('ALTER TABLE %I NO FORCE ROW LEVEL SECURITY;', tabla);
        EXECUTE format('DROP POLICY IF EXISTS posicion_en_la_asignacion ON %I;', tabla);
        EXECUTE format(
            'CREATE POLICY posicion_en_la_asignacion ON %I '
            -- Se ve por cualquiera de las dos posiciones. app.tenant_id es el
            -- workspace activo (propietario); app.tenant_origen_id es el tenant
            -- al que pertenece el usuario, que es el único que la selección de
            -- workspace no puede cambiar.
            'USING (""PropietarioTenantId"" = NULLIF(current_setting(''app.tenant_id'', true), '''')::uuid '
            '    OR ""OperadorTenantId"" = NULLIF(current_setting(''app.tenant_origen_id'', true), '''')::uuid) '
            -- Solo se escribe sobre el propietario contextual. Asimétrico a
            -- propósito: si el operador pudiera escribir por su posición, se
            -- concedería a sí mismo asignaciones sobre propietarios ajenos.
            'WITH CHECK (""PropietarioTenantId"" = NULLIF(current_setting(''app.tenant_id'', true), '''')::uuid);',
            tabla);
    END LOOP;
END $$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            var arrayTablas = string.Join(",", System.Array.ConvertAll(CatalogosDeAsignacion, t => $"'{t}'"));

            migrationBuilder.Sql($@"
DO $$
DECLARE
    tabla text;
BEGIN
    FOREACH tabla IN ARRAY ARRAY[{arrayTablas}]
    LOOP
        EXECUTE format('DROP POLICY IF EXISTS posicion_en_la_asignacion ON %I;', tabla);
        EXECUTE format('ALTER TABLE %I DISABLE ROW LEVEL SECURITY;', tabla);
    END LOOP;
END $$;
");
        }
    }
}

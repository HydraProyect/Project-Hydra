using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CaeManager.Migrations.PostgreSQL.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// RLS del plano de privilegio de plataforma: <c>ConcesionesPrivilegio</c>,
    /// <c>SesionesPrivilegiadas</c> y <c>TenantsAlcanzadosPorConcesion</c>.
    /// Cierra el hueco que F2b-4 dejó declarado como pendiente.
    ///
    /// <b>La autoridad vive en los datos, no en la sesión.</b> La política dice
    /// "las filas que te nombran":
    /// <code>
    /// ConcesionPrivilegio.UsuarioPlataformaId = app.usuario_id
    /// </code>
    /// Ser usuario de plataforma deja de ser algo que se declara al abrir la
    /// conexión y pasa a ser una consecuencia de que existan filas que te
    /// nombren. Por eso <b>no existe</b> una variable
    /// <c>app.usuario_plataforma_id</c>: incrustaría en la sesión una afirmación
    /// de privilegio. <c>app.usuario_id</c> es una coordenada —la identidad
    /// autenticada— exactamente igual que <c>app.tenant_id</c>, y como aquella
    /// no concede nada por sí sola.
    ///
    /// Efecto lateral valioso: la aplicación y Postgres pasan a usar <b>el mismo
    /// predicado</b>. <c>SesionPrivilegiadaActual</c> ya comprobaba
    /// <c>concesion.UsuarioPlataformaId == usuarioId</c>; ahora la base exige lo
    /// mismo. No son dos reglas que haya que mantener sincronizadas: es una
    /// regla comprobada dos veces.
    ///
    /// <para>
    /// <b>Con FORCE, al contrario que los catálogos de asignación.</b> Ahí no se
    /// puso porque había lectores sistémicos legítimos —el backfill y el job de
    /// expiración leen el grafo entero sin tenant de sesión— y FORCE los habría
    /// roto. Aquí la misma pregunta da la respuesta contraria: estas tres tablas
    /// tienen <b>un solo lector</b> (<c>SesionPrivilegiadaActual</c>, siempre
    /// bajo sesión de usuario) y <b>ningún escritor</b>. No hay proceso sistémico
    /// que eximir.
    /// </para>
    ///
    /// Lo que FORCE cambia, dicho con precisión: sin él la política solo ataría
    /// a roles ajenos a la tabla, es decir a nadie hasta que se rote la conexión
    /// a <c>cae_app_runtime</c> — un paso de operación pendiente. Con él ata
    /// también al propietario. <b>Lo que no hace, ni con FORCE, es atar a un
    /// superusuario</b>: ninguna política de Postgres lo hace. Si la conexión de
    /// producción corriera como superusuario, esto seguiría siendo inerte allí,
    /// y eso no se puede comprobar desde el código fuente porque vive en la
    /// cadena de conexión del despliegue. Los tests lo prueban donde sí se puede:
    /// bajo <c>SET ROLE cae_app_runtime</c>, que es el rol al que se rotará.
    ///
    /// <para>
    /// <b>El <c>WITH CHECK</c> es deliberadamente estricto y se sabe que quedará
    /// corto.</b> Solo permite escribir filas que te nombren a ti mismo, así que
    /// conceder un privilegio a OTRO usuario de plataforma quedará bloqueado. Hoy
    /// nadie escribe estas tablas, así que la restricción no cuesta nada, y
    /// relajarla ahora sería anticipar una capacidad que aún no existe. La fase
    /// que construya la apertura de sesiones tendrá que introducir
    /// explícitamente la capacidad de conceder a terceros — como decisión suya,
    /// no como herencia de esta.
    /// </para>
    ///
    /// Pura DDL de servidor: no cambia el modelo de EF, así que no hay diffs que
    /// aplicar en el snapshot.
    /// </summary>
    public partial class RlsPlanoPrivilegioPlataforma : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // La concesión lleva el usuario directamente; las otras dos lo
            // alcanzan a través de ella. El EXISTS anidado no recursa: la
            // política de ConcesionesPrivilegio solo mira una variable de
            // sesión, no otra tabla.
            const string predicadoConcesion =
                @"""UsuarioPlataformaId"" = NULLIF(current_setting('app.usuario_id', true), '')::uuid";

            const string predicadoPorConcesion = @"EXISTS (
    SELECT 1 FROM ""ConcesionesPrivilegio"" c
    WHERE c.""Id"" = ""ConcesionPrivilegioId""
      AND c.""UsuarioPlataformaId"" = NULLIF(current_setting('app.usuario_id', true), '')::uuid)";

            migrationBuilder.Sql($@"
ALTER TABLE ""ConcesionesPrivilegio"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""ConcesionesPrivilegio"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS privilegio_del_usuario ON ""ConcesionesPrivilegio"";
CREATE POLICY privilegio_del_usuario ON ""ConcesionesPrivilegio""
    USING ({predicadoConcesion})
    WITH CHECK ({predicadoConcesion});

ALTER TABLE ""SesionesPrivilegiadas"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""SesionesPrivilegiadas"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS privilegio_del_usuario ON ""SesionesPrivilegiadas"";
CREATE POLICY privilegio_del_usuario ON ""SesionesPrivilegiadas""
    USING ({predicadoPorConcesion})
    WITH CHECK ({predicadoPorConcesion});

ALTER TABLE ""TenantsAlcanzadosPorConcesion"" ENABLE ROW LEVEL SECURITY;
ALTER TABLE ""TenantsAlcanzadosPorConcesion"" FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS privilegio_del_usuario ON ""TenantsAlcanzadosPorConcesion"";
CREATE POLICY privilegio_del_usuario ON ""TenantsAlcanzadosPorConcesion""
    USING ({predicadoPorConcesion})
    WITH CHECK ({predicadoPorConcesion});
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
DECLARE
    tabla text;
BEGIN
    FOREACH tabla IN ARRAY ARRAY['ConcesionesPrivilegio', 'SesionesPrivilegiadas', 'TenantsAlcanzadosPorConcesion']
    LOOP
        EXECUTE format('DROP POLICY IF EXISTS privilegio_del_usuario ON %I;', tabla);
        EXECUTE format('ALTER TABLE %I NO FORCE ROW LEVEL SECURITY;', tabla);
        EXECUTE format('ALTER TABLE %I DISABLE ROW LEVEL SECURITY;', tabla);
    END LOOP;
END $$;
");
        }
    }
}

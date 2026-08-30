using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence;

/// <summary>
/// Comprueba, contra PostgreSQL y no contra la configuración, que la identidad
/// con la que el tráfico va a conectar está de verdad sometida a RLS.
///
/// <para>
/// <b>Por qué no basta con exigir la cadena de conexión.</b> Tener configurado
/// <c>ConnectionStrings:CaeManagerDbRuntime</c> demuestra que alguien escribió
/// una cadena, no que esa cadena autentique como un rol restringido. Apuntarla
/// a <c>postgres</c> —el usuario que el compose de producción ya usa para
/// <c>CaeManagerDb</c>— pasa esa comprobación y deja RLS igual de decorativa:
/// PostgreSQL no aplica políticas ni al superusuario, ni al rol con
/// <c>BYPASSRLS</c>, ni al propietario de la tabla, y en este último caso ni
/// siquiera <c>FORCE ROW LEVEL SECURITY</c> lo cambia si el propietario es
/// además superusuario. Las tres son formas distintas de la misma exención, y
/// hay que descartarlas por separado.
/// </para>
///
/// <para>
/// <b>Qué observa y qué no.</b> Observa la identidad efectiva de la conexión
/// (<c>current_user</c>) y sus atributos en <c>pg_roles</c>, más si esa
/// identidad es propietaria de alguna tabla con RLS activada. No observa si las
/// políticas son correctas ni si cubren todas las tablas: eso lo prueban las
/// migraciones y los tests de aislamiento. Aquí solo se cierra el hueco de que
/// la aplicación conecte con una identidad exenta.
/// </para>
///
/// <para>
/// Se ejecuta una vez, en el arranque, con la conexión del contexto inyectado
/// —la misma que usará el tráfico— porque preguntarle a cualquier otra sería
/// medir un instrumento distinto del que importa.
/// </para>
/// </summary>
public static class VerificacionIdentidadDeRuntime
{
    /// <summary>
    /// Lanza <see cref="InvalidOperationException"/> si la identidad de la
    /// conexión puede saltarse RLS. No devuelve nada a propósito: no hay un
    /// resultado que interpretar, o el arranque continúa o se detiene.
    /// </summary>
    public static async Task ExigirIdentidadSometidaARlsAsync(
        CaeManagerDbContext contexto, CancellationToken cancellationToken = default)
    {
        var conexion = contexto.Database.GetDbConnection();
        var laAbrimosNosotros = conexion.State != System.Data.ConnectionState.Open;

        if (laAbrimosNosotros)
            await contexto.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var comando = conexion.CreateCommand();

            // `rolsuper` y `is_superuser` no son redundantes: el primero es el
            // atributo del rol, el segundo la propiedad de la sesión, y un
            // SET ROLE puede separarlos. Se piden los dos.
            comando.CommandText = """
                SELECT current_user,
                       (SELECT rolsuper      FROM pg_roles WHERE rolname = current_user),
                       (SELECT rolbypassrls  FROM pg_roles WHERE rolname = current_user),
                       current_setting('is_superuser'),
                       EXISTS (SELECT 1
                               FROM pg_class c
                               JOIN pg_namespace n ON n.oid = c.relnamespace
                               WHERE c.relrowsecurity
                                 AND n.nspname = 'public'
                                 AND pg_get_userbyid(c.relowner) = current_user);
                """;

            await using var lector = await comando.ExecuteReaderAsync(cancellationToken);

            if (!await lector.ReadAsync(cancellationToken))
                throw new InvalidOperationException(
                    "No se pudo determinar la identidad de conexión de PostgreSQL: la comprobación de " +
                    "aislamiento no puede darse por buena sin haberla observado.");

            var identidad = lector.GetString(0);
            var esSuperusuario = lector.GetBoolean(1);
            var saltaRls = lector.GetBoolean(2);
            var sesionSuperusuario = lector.GetString(3) == "on";
            var esPropietariaDeTablaConRls = lector.GetBoolean(4);

            var exenciones = new List<string>();
            if (esSuperusuario) exenciones.Add("es superusuario (rolsuper)");
            if (sesionSuperusuario) exenciones.Add("la sesión corre como superusuario (is_superuser=on)");
            if (saltaRls) exenciones.Add("tiene BYPASSRLS");
            if (esPropietariaDeTablaConRls) exenciones.Add("es propietaria de tablas con RLS activada");

            if (exenciones.Count > 0)
                throw new InvalidOperationException(
                    $"La conexión de tráfico autentica como '{identidad}', que está exenta de RLS: " +
                    $"{string.Join("; ", exenciones)}. Las políticas de aislamiento por tenant no se le " +
                    "aplicarían, así que la única barrera restante sería el filtro global de EF Core — " +
                    "que no protege SQL crudo, IgnoreQueryFilters ni las tablas de Identity. Configura " +
                    "ConnectionStrings:CaeManagerDbRuntime con el rol cae_app_runtime " +
                    "(deploy/bootstrap/roles-de-cluster.sql).");
        }
        finally
        {
            if (laAbrimosNosotros)
                await contexto.Database.CloseConnectionAsync();
        }
    }
}

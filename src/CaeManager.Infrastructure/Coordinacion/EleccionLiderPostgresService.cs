using CaeManager.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace CaeManager.Infrastructure.Coordinacion;

/// <summary>
/// Advisory lock de sesión de PostgreSQL (<c>pg_try_advisory_lock</c>): no
/// bloqueante (a diferencia de <c>pg_advisory_lock</c>, que esperaría), y
/// atado al ciclo de vida de la conexión — si el proceso que lo tiene se cae
/// o pierde la conexión, PostgreSQL libera el lock solo, sin que nadie tenga
/// que detectar el fallo ni expirar un TTL. Es justo la propiedad que hace
/// esto seguro sin heartbeat ni renovación explícita.
///
/// <see cref="clave"/> se convierte en un bigint con <c>hashtextextended</c>
/// (calculado por el propio PostgreSQL, no en C#) para que todas las réplicas
/// —sin importar versión de .NET ni arquitectura— lleguen exactamente al
/// mismo identificador de lock para el mismo string.
///
/// Una conexión Npgsql propia, no el <c>CaeManagerDbContext</c> del scope: el
/// lock vive mientras dure <paramref name="trabajo"/> (potencialmente
/// minutos, si hay mucho pendiente en la cola de IA), y un DbContext scoped
/// se recicla por request/circuito — aquí no hay ninguno de los dos.
///
/// <para>
/// <b>Con la identidad del tráfico</b>
/// (<see cref="InfrastructureServiceCollectionExtensions.ResolverCadenaDeTrafico"/>),
/// no con <c>CaeManagerDb</c>: desde P0-2 el contenedor <c>app</c> de staging y
/// producción recibe esa cadena vacía a propósito y solo el <c>migrador</c> la
/// tiene. Leerla aquí hacía que cada servicio con líder fallara en cada ciclo
/// sin hacer nada. <c>pg_try_advisory_lock</c> no exige ningún privilegio, así
/// que <c>cae_app_runtime</c> basta; el lock no toca ninguna tabla, de modo que
/// RLS no interviene.
/// </para>
///
/// <para>
/// La cadena se resuelve una sola vez, al construir el singleton —que ocurre al
/// arrancar el host, porque lo inyectan los hosted services—: una configuración
/// inválida falla el arranque con un mensaje claro en vez de un error por ciclo.
/// </para>
/// </summary>
public class EleccionLiderPostgresService(IConfiguration configuration, IHostEnvironment entorno) : IEleccionLiderService
{
    private readonly string _cadenaConexion =
        InfrastructureServiceCollectionExtensions.ResolverCadenaDeTrafico(configuration, entorno);

    public async Task<bool> IntentarEjecutarComoLiderAsync(
        string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync(cancellationToken);

        await using (var comandoLock = new NpgsqlCommand(
            "SELECT pg_try_advisory_lock(hashtextextended(@clave, 0))", conexion))
        {
            comandoLock.Parameters.AddWithValue("clave", clave);
            var adquirido = (bool)(await comandoLock.ExecuteScalarAsync(cancellationToken))!;
            if (!adquirido)
                return false;
        }

        try
        {
            await trabajo(cancellationToken);
        }
        finally
        {
            // Sin CancellationToken: liberar el lock debe ocurrir aunque la
            // cancelación ya esté pedida — dejarlo sin liberar hasta que la
            // conexión se cierre no es incorrecto (se libera igual), pero
            // retrasa que otra réplica pueda tomar el liderazgo antes de lo
            // necesario.
            await using var comandoUnlock = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtextextended(@clave, 0))", conexion);
            comandoUnlock.Parameters.AddWithValue("clave", clave);
            await comandoUnlock.ExecuteScalarAsync(CancellationToken.None);
        }

        return true;
    }
}

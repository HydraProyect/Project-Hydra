using Microsoft.Extensions.Configuration;
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
/// </summary>
public class EleccionLiderPostgresService(IConfiguration configuration) : IEleccionLiderService
{
    public async Task<bool> IntentarEjecutarComoLiderAsync(
        string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
    {
        var cadenaConexion = configuration.GetConnectionString("CaeManagerDb")
            ?? throw new InvalidOperationException("Falta el connection string CaeManagerDb.");

        await using var conexion = new NpgsqlConnection(cadenaConexion);
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

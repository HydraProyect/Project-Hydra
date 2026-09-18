using Microsoft.Extensions.Logging;
using Npgsql;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Distingue la carrera "el circuito de Blazor se desconecta mientras una
/// consulta sigue en vuelo" (ver <see cref="MainLayout"/>) de un fallo real
/// de base de datos que sí hay que dejar pasar.
///
/// <para>
/// Se manifiesta de tres formas: <see cref="ObjectDisposedException"/> (el
/// <c>DbContext</c> scoped ya se liberó), <see cref="ArgumentOutOfRangeException"/>
/// dentro de <c>NpgsqlDataReader</c>, o una <see cref="NpgsqlException"/>
/// cruda del parser de protocolo de Npgsql. Esta última NO se acepta por
/// "cualquier <c>NpgsqlException</c> no transitoria" — eso también incluye
/// fallos reales de autenticación de Npgsql — sino por el mensaje literal de
/// los dos únicos sitios de Npgsql 10.0.3 que construyen esta carrera
/// concreta: <c>"Received backend message {X} while expecting {Y}. Please
/// file a bug."</c>.
/// </para>
///
/// <para>
/// <see cref="PostgresException"/> (error que el servidor sí reportó),
/// <c>IsTransient</c> (fallo de red real, con <see cref="IOException"/>,
/// <see cref="SocketException"/> o <see cref="TimeoutException"/> como
/// <c>InnerException</c>) y <see cref="NpgsqlOperationInProgressException"/>
/// (bug de concurrencia real) se excluyen siempre, como defensa en
/// profundidad.
/// </para>
///
/// <para>
/// No es <c>static</c>: los cinco sitios que la usan la comparten también
/// como categoría de <see cref="ILogger{TCategoryName}"/>
/// (<c>ILogger&lt;ExcepcionDeCircuitoDesconectado&gt;</c>), y una clase
/// <c>static</c> no puede usarse como argumento de tipo genérico. El
/// constructor privado impide instanciarla de todos modos.
/// </para>
/// </summary>
public sealed class ExcepcionDeCircuitoDesconectado
{
    private ExcepcionDeCircuitoDesconectado() { }

    private const string PrefijoMensajeDesincronizacion = "Received backend message ";
    private const string SufijoMensajeDesincronizacion = " Please file a bug.";

    public static bool Es(Exception ex) =>
        ex is ObjectDisposedException or ArgumentOutOfRangeException
        || (ex is NpgsqlException { IsTransient: false } npgsqlEx
            and not PostgresException
            and not NpgsqlOperationInProgressException
            && EsMensajeDeDesincronizacionDeProtocolo(npgsqlEx.Message));

    private static bool EsMensajeDeDesincronizacionDeProtocolo(string mensaje) =>
        mensaje.StartsWith(PrefijoMensajeDesincronizacion, StringComparison.Ordinal)
        && mensaje.EndsWith(SufijoMensajeDesincronizacion, StringComparison.Ordinal);
}

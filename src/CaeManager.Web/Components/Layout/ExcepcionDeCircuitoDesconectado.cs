using Microsoft.Extensions.Logging;
using Npgsql;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Distingue la carrera "el circuito de Blazor se desconecta mientras una
/// consulta sigue en vuelo" — reproducida en producción (Sentry DOTNET-3,
/// DOTNET-6, ver <see cref="MainLayout"/>) y en CI (REC-166, ver
/// Project-Hydra-Negocio/tecnico/reconciliacion/informes/REC-166-caracterizacion-2026-09-18.md)
/// — de un fallo real de base de datos que sí hay que dejar pasar.
///
/// <para>
/// Esa carrera se manifiesta de tres formas conocidas hoy, según en qué
/// punto exacto del socket la sorprenda la desconexión:
/// <see cref="ObjectDisposedException"/> (el <c>DbContext</c> scoped ya se
/// liberó), <see cref="ArgumentOutOfRangeException"/> dentro de
/// <c>NpgsqlDataReader</c>, o una <see cref="NpgsqlException"/> cruda del
/// parser de protocolo de Npgsql (mensaje literal "Received backend message
/// X while expecting Y. Please file a bug.", <c>Npgsql.Util.Statics.
/// ThrowIfMsgWrongType</c>).
/// </para>
///
/// <para>
/// <b>Por qué la tercera forma NO es "atrapar cualquier <c>NpgsqlException</c>
/// no transitoria"</b> — revisión de Codex sobre la primera versión de esta
/// clase: <c>PostgresException</c>/<c>IsTransient</c>/<c>NpgsqlOperationInProgressException</c>
/// descartan los fallos reales que ESOS tres tipos cubren, pero Npgsql 10.0.3
/// lanza una <c>NpgsqlException</c> cruda, no transitoria y sin ninguno de
/// esos tres, en **más de cuarenta sitios distintos** — casi todos de
/// autenticación real (contraseña ausente, SCRAM/SASL mal negociado, SSL/GSS
/// rechazado: <c>NpgsqlConnector.Auth.cs</c>) que jamás deben tragarse. Medido
/// clonando <c>github.com/npgsql/npgsql</c> en <c>v10.0.3</c> y buscando
/// <c>new NpgsqlException(</c> fuera de esos tres tipos, no supuesto.
/// </para>
///
/// <para>
/// Por eso el filtro final compara el <b>mensaje literal</b> de los dos
/// únicos sitios que construyen esta carrera concreta —
/// <c>Npgsql.Util.Statics.ThrowIfMsgWrongType</c> y
/// <c>NpgsqlConnector.cs:665</c>, los dos con la forma exacta <c>"Received
/// backend message {X} while expecting {Y}. Please file a bug."</c>, y
/// ningún otro sitio del código fuente de Npgsql 10.0.3 comparte ese prefijo
/// y ese sufijo a la vez — en vez de aceptar cualquier <c>NpgsqlException</c>
/// que no encaje en los tres tipos de arriba.
/// </para>
///
/// <para>
/// <b>Los otros tres descartes siguen aplicando</b>, ahora como defensa en
/// profundidad más que como el criterio principal:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="PostgresException"/> (subclase de <see cref="NpgsqlException"/>)
/// son los errores que el servidor SÍ llegó a reportar. Nunca se tragan.
/// </description></item>
/// <item><description>
/// <c>NpgsqlException.IsTransient</c> (fuente: <c>NpgsqlException.cs</c>)
/// solo es <c>true</c> cuando la excepción envuelve un
/// <see cref="IOException"/>, <see cref="SocketException"/> o
/// <see cref="TimeoutException"/> — un fallo de red real. La excepción de
/// esta carrera se construye sin <c>InnerException</c>, así que
/// <c>IsTransient</c> es <c>false</c>.
/// </description></item>
/// <item><description>
/// <see cref="NpgsqlOperationInProgressException"/> es un bug de concurrencia
/// real — dos operaciones a la vez sobre la misma conexión.
/// </description></item>
/// </list>
///
/// <para>
/// No es <c>static</c> a propósito: los cinco sitios que la usan también la
/// comparten como categoría de <see cref="ILogger{TCategoryName}"/>
/// (<c>ILogger&lt;ExcepcionDeCircuitoDesconectado&gt;</c>), para poder
/// filtrar en un solo sitio todo lo que esta carrera descarta — y una clase
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

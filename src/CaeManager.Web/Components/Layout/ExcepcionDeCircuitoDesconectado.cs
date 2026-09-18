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
/// <b>Por qué la tercera forma no es "atrapar <c>NpgsqlException</c> a
/// secas"</b> — eso sí se tragaría fallos reales de base de datos, medido
/// en el código fuente de Npgsql 10.0.3 antes de escribir esto, no supuesto:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="PostgresException"/> (subclase de <see cref="NpgsqlException"/>)
/// son los errores que el servidor SÍ llegó a reportar — violación de
/// constraint, error de sintaxis, lo que sea con un <c>SqlState</c> real.
/// Nunca se tragan.
/// </description></item>
/// <item><description>
/// <c>NpgsqlException.IsTransient</c> (fuente: <c>NpgsqlException.cs</c>)
/// solo es <c>true</c> cuando la excepción envuelve un
/// <see cref="IOException"/>, <see cref="SocketException"/> o
/// <see cref="TimeoutException"/> — un fallo de red real (servidor caído,
/// tiempo de espera agotado). La excepción de esta carrera se construye sin
/// <c>InnerException</c>, así que su <c>IsTransient</c> es <c>false</c>. Un
/// fallo de red real, con <c>IsTransient == true</c>, no se traga aquí — es
/// indistinguible de "la base no responde", que no es esta carrera.
/// </description></item>
/// <item><description>
/// <see cref="NpgsqlOperationInProgressException"/> ("A command is already
/// in progress" / "The connection is already in state...") es un bug de
/// concurrencia real — dos operaciones a la vez sobre la misma conexión — y
/// tampoco se traga, aunque su <c>IsTransient</c> también sea <c>false</c>
/// por no llevar <c>InnerException</c>.
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

    public static bool Es(Exception ex) =>
        ex is ObjectDisposedException or ArgumentOutOfRangeException
        || ex is NpgsqlException { IsTransient: false }
            and not PostgresException
            and not NpgsqlOperationInProgressException;
}

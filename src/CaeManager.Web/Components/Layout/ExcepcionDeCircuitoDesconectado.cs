using Microsoft.Extensions.Logging;
using Npgsql;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// Distingue la carrera "el circuito de Blazor se desconecta mientras una
/// consulta sigue en vuelo" de un fallo real de base de datos que sí hay que
/// dejar pasar, en los cuatro componentes que solo se quedarían sin un dato
/// (<c>SelectorClienteActivo</c>, <c>SelectorTema</c>, <c>NotificacionesPopup</c>
/// y <c>PanelAvisosNormativos</c>).
///
/// <para>
/// <see cref="MainLayout"/> sufre la misma carrera pero <b>no</b> usa este
/// predicado: allí lo que está en juego es un guard de seguridad, y desde
/// 2026-09-18 quien decide no es el tipo de la excepción sino
/// <see cref="Services.EstadoDelCircuito"/> — el tipo no distingue un circuito
/// muerto de uno vivo, que es la única pregunta que importa cuando terminar sin
/// hacer nada significaría mostrar la página sin aplicar el guard.
/// </para>
///
/// <para>
/// Se manifiesta de tres formas: <see cref="ObjectDisposedException"/> (el
/// <c>DbContext</c> scoped ya se liberó), <see cref="ArgumentOutOfRangeException"/>
/// dentro de <c>NpgsqlDataReader</c>, o una <see cref="NpgsqlException"/>
/// cruda del parser de protocolo de Npgsql. Esta última NO se acepta por
/// "cualquier <c>NpgsqlException</c> no transitoria" — eso también incluye
/// fallos reales de autenticación de Npgsql — ni por cualquier mensaje con
/// la forma <c>"Received backend message {X} while expecting {Y}. Please
/// file a bug."</c> — esa plantilla también la emite Npgsql ante un mensaje
/// de protocolo inesperado con la conexión viva, sin relación con esta
/// carrera — sino por los pares <c>{X}</c>/<c>{Y}</c> exactos medidos en
/// esta carrera concreta. Un par nuevo, no visto todavía, debe seguir
/// escalando en vez de tragarse en silencio.
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
/// No es <c>static</c>: los cuatro sitios que la usan la comparten también
/// como categoría de <see cref="ILogger{TCategoryName}"/>
/// (<c>ILogger&lt;ExcepcionDeCircuitoDesconectado&gt;</c>), y una clase
/// <c>static</c> no puede usarse como argumento de tipo genérico. El
/// constructor privado impide instanciarla de todos modos.
/// </para>
/// </summary>
public sealed class ExcepcionDeCircuitoDesconectado
{
    private ExcepcionDeCircuitoDesconectado() { }

    /// <summary>
    /// Mensajes exactos medidos en CI para esta carrera concreta — no un
    /// patrón de prefijo/sufijo, que también acepta pares de Npgsql sin
    /// relación con ella.
    /// </summary>
    private static readonly string[] MensajesDeDesincronizacionDeProtocolo =
    [
        "Received backend message BindComplete while expecting ParseCompleteMessage. Please file a bug.",
        "Received backend message DataRow while expecting ReadyForQuery. Please file a bug.",
    ];

    public static bool Es(Exception ex) =>
        ex is ObjectDisposedException or ArgumentOutOfRangeException
        || (ex is NpgsqlException { IsTransient: false } npgsqlEx
            and not PostgresException
            and not NpgsqlOperationInProgressException
            && MensajesDeDesincronizacionDeProtocolo.Contains(npgsqlEx.Message, StringComparer.Ordinal));
}

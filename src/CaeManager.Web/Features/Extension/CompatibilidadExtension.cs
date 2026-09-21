namespace CaeManager.Web.Features.Extension;

/// <summary>
/// Qué sabe hacer la extensión que hay instalada enfrente.
///
/// <para>
/// Existe por un defecto concreto, señalado en la revisión de Codex del
/// 2026-09-21: la pantalla ofrecía «Conectar a mano» en los tres casos de fallo,
/// pero ese control se incorpora en la <b>0.3.0</b>. A una instalación 0.2.0 se
/// le estaba mandando a pulsar algo que no existe en su ventana — es decir, el
/// camino de recuperación no recuperaba nada justo en el caso que lo motivó.
/// </para>
///
/// <para>
/// La distinción que importa es <b>saber</b> frente a <b>no saber</b>. Cuando la
/// extensión responde, aunque sea para rechazar el enlace, manda su versión y se
/// puede decidir. Cuando no responde, el navegador no distingue «no instalada»
/// de «instalada y anticuada», así que no hay versión que leer: entonces se
/// ofrece el código, pero diciendo en la propia pantalla qué versión hace falta.
/// Callarlo sería repetir el defecto en voz baja.
/// </para>
/// </summary>
public static class CompatibilidadExtension
{
    /// <summary>Primera versión de la extensión con «Conectar a mano» en el popup.</summary>
    public static readonly Version VersionMinimaConexionManual = new(0, 3, 0);

    /// <summary>
    /// ¿Tiene sentido ofrecerle el código de conexión a esta extensión?
    /// </summary>
    /// <param name="versionExtension">
    /// La versión que declaró la extensión, o <c>null</c> si no respondió. Un
    /// valor que no se puede interpretar cuenta como desconocido, no como
    /// antiguo: preferimos ofrecer el código con su aviso de versión antes que
    /// esconder la única salida por no saber leer una cadena.
    /// </param>
    public static bool AdmiteConexionManual(string? versionExtension)
    {
        if (!Version.TryParse(versionExtension, out var version))
            return true;

        return version >= VersionMinimaConexionManual;
    }
}

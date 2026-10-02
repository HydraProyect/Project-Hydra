namespace CaeManager.Web.Services;

/// <summary>
/// Revisión (SHA de commit) que corre en este proceso, para exponerla en la
/// cabecera <see cref="Cabecera"/> de <c>/salud</c> (D-11 del recorrido en vivo:
/// desde fuera no se podía saber qué commit estaba desplegado).
///
/// <para>
/// <b>Contrato de <c>/salud</c> intacto.</b> El cuerpo («Healthy»), el código
/// HTTP y el estado de cada comprobación no cambian: los despliegues
/// (<c>deploy/</c>, <c>healthcheck</c> de Docker, k6) solo leen el código HTTP.
/// Se añade una cabecera, nada más.
/// </para>
///
/// <para>
/// <b>Qué se expone y qué no.</b> El SHA de un commit de un repositorio público
/// no es secreto. Aun así, solo se acepta un SHA hexadecimal de 7 a 40
/// caracteres: cualquier otro valor del entorno (una URL, un token pegado por
/// error) se descarta y sale «desconocida», para que esta cabecera nunca pueda
/// convertirse en un canal de fugas de configuración.
/// </para>
/// </summary>
public static class RevisionDesplegada
{
    public const string Cabecera = "X-Talveg-Revision";
    public const string Variable = "TALVEG_REVISION";
    public const string Desconocida = "desconocida";

    public static string Leer(string? valor)
    {
        var v = valor?.Trim();
        return v is { Length: >= 7 and <= 40 } && v.All(Uri.IsHexDigit) ? v.ToLowerInvariant() : Desconocida;
    }

    /// <summary>Añade la cabecera de revisión a las respuestas de <c>/salud</c>.</summary>
    public static IApplicationBuilder UseRevisionEnSalud(this IApplicationBuilder app, string? valorCrudo)
    {
        var revision = Leer(valorCrudo);
        return app.Use((contexto, siguiente) =>
        {
            if (contexto.Request.Path.Equals("/salud", StringComparison.OrdinalIgnoreCase))
            {
                contexto.Response.OnStarting(() =>
                {
                    contexto.Response.Headers[Cabecera] = revision;
                    return Task.CompletedTask;
                });
            }

            return siguiente();
        });
    }
}

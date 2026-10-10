namespace CaeManager.Web.Components.DesignSystem;

/// <summary>Una agrupación que ofrece un listado: su clave (la que viaja en la URL) y el nombre del campo («Cliente»).</summary>
public sealed record OpcionAgrupar(string Clave, string Etiqueta);

/// <summary>
/// Cómo viaja en la URL la agrupación de un listado (<see cref="DesplegableAgrupar"/>): un solo parámetro,
/// <c>agrupar</c>, que es todo lo que hace falta para reproducir la vista (quien guarde la vista lee solo la URL).
///
/// <list type="bullet">
/// <item>ausente: la agrupación de fábrica de la pantalla, para que la dirección sin parámetros siga siendo la vista de fábrica;</item>
/// <item><c>no</c>: sin agrupar;</item>
/// <item><c>&lt;clave&gt;</c>: esa agrupación (también la de fábrica, escrita a mano);</item>
/// <item>cualquier otro valor: la de fábrica.</item>
/// </list>
///
/// La agrupación es vista, no filtro: «Quitar filtros» no la toca. Los grupos abiertos no viajan.
/// </summary>
/// <param name="claveDeFabrica">Agrupación con la que nace la pantalla; <c>null</c> si nace sin agrupar.</param>
/// <param name="claves">Las agrupaciones que ofrece la pantalla. Ninguna puede ser <c>no</c>.</param>
public sealed class AgrupacionDeLista(string? claveDeFabrica, params string[] claves)
{
    /// <summary>Nombre del parámetro de la URL.</summary>
    public const string Parametro = "agrupar";

    /// <summary>Valor del parámetro para «sin agrupar».</summary>
    public const string SinAgrupar = "no";

    public string? ClaveDeFabrica { get; } = claveDeFabrica;

    /// <summary>La agrupación que pide la URL: la clave, o <c>null</c> para «sin agrupar».</summary>
    public string? Leer(string? valorDeLaUrl) => valorDeLaUrl switch
    {
        SinAgrupar => null,
        not null when claves.Contains(valorDeLaUrl, StringComparer.Ordinal) => valorDeLaUrl,
        _ => ClaveDeFabrica,
    };

    /// <summary>Lo que se escribe en la URL para una agrupación: <c>null</c> (el parámetro se quita) si es la de fábrica.</summary>
    public string? ParaUrl(string? clave) =>
        clave == ClaveDeFabrica ? null : clave ?? SinAgrupar;
}

using Microsoft.AspNetCore.Components.QuickGrid;

namespace CaeManager.Web.Components;

/// <summary>
/// El orden de columna de un listado QuickGrid, en la URL: <c>?orden=columna</c> o
/// <c>?orden=columna-desc</c>, con claves estables en minúsculas que la página declara junto a la
/// propiedad del DTO por la que ordena cada una (la que lee <see cref="LecturaOrden"/>). Sin
/// <c>orden</c>, el de fábrica. Forma parte de la vista del listado: lo llevan los filtros
/// guardados y la vista recordada.
///
/// <para>
/// QuickGrid guarda el orden dentro y no ofrece volver a «sin orden», así que las dos direcciones
/// se conectan así:
/// </para>
/// <list type="bullet">
/// <item>De la rejilla a la URL: el proveedor de datos llama a <see cref="Anotar"/> con el orden de
/// cada petición; si devuelve <c>true</c>, el usuario ordenó y la página escribe <see cref="EnUrl"/>.</item>
/// <item>De la URL a la rejilla: <see cref="Leer"/> valida el valor (uno desconocido es «de fábrica»)
/// y, si cambia, sube <see cref="Version"/>. La rejilla lleva <c>@key="_orden.Version"</c> y cada
/// columna ordenable <c>IsDefaultSortColumn="@_orden.Es(…)" InitialSortDirection="@_orden.Direccion"</c>:
/// al remontarse nace ya ordenada y pide los datos una vez.</item>
/// </list>
/// </summary>
public sealed class OrdenDeRejilla
{
    private const string SufijoDescendente = "-desc";

    private readonly (string Clave, string Propiedad)[] _columnas;
    private readonly string _claveDeFabrica;
    private readonly bool _descendenteDeFabrica;

    /// <summary>Tras <see cref="Leer"/> y hasta que la rejilla remontada pida con el orden nuevo.</summary>
    private bool _remontando;

    /// <param name="columnas">Clave de la URL y propiedad del DTO de cada orden que la rejilla ofrece.</param>
    /// <param name="claveDeFabrica">Clave del orden con el que nace la rejilla, o <c>null</c> si nace sin ordenar.</param>
    /// <param name="descendenteDeFabrica">Dirección de ese orden de fábrica.</param>
    public OrdenDeRejilla((string Clave, string Propiedad)[] columnas, string? claveDeFabrica = null, bool descendenteDeFabrica = false)
    {
        _columnas = columnas;
        _claveDeFabrica = claveDeFabrica ?? string.Empty;
        _descendenteDeFabrica = descendenteDeFabrica;
    }

    /// <summary>El valor vigente de <c>?orden=</c>, ya validado. Vacío: el de fábrica (el parámetro se quita).</summary>
    public string EnUrl { get; private set; } = string.Empty;

    /// <summary>Cambia cuando la URL trae otro orden: es la <c>@key</c> de la rejilla.</summary>
    public int Version { get; private set; }

    /// <summary>La clave por la que nace ordenada la rejilla; vacía si nace sin ordenar.</summary>
    public string Clave => EnUrl.Length == 0 ? _claveDeFabrica : SinSufijo(EnUrl);

    /// <summary>La propiedad del DTO de <see cref="Clave"/>, o <c>null</c> si la rejilla nace sin ordenar.</summary>
    public string? Propiedad => _columnas.FirstOrDefault(c => c.Clave == Clave).Propiedad;

    /// <summary>La dirección con la que nace ordenada la rejilla.</summary>
    public SortDirection Direccion =>
        (EnUrl.Length == 0 ? _descendenteDeFabrica : EnUrl.EndsWith(SufijoDescendente, StringComparison.Ordinal))
            ? SortDirection.Descending : SortDirection.Ascending;

    /// <summary>La rejilla nace ordenada por alguna de estas claves (las de una misma columna).</summary>
    public bool Es(params string[] claves) => Clave.Length > 0 && claves.Contains(Clave);

    /// <summary>
    /// El valor de <c>?orden=</c> que llega (de la URL, de un filtro guardado o de la vista recordada).
    /// No es autoridad: una columna que no existe es «de fábrica». Devuelve <c>true</c> si el orden
    /// cambia: la rejilla se remonta y pide los datos ella, sin que la página recargue.
    /// </summary>
    public bool Leer(string? valor)
    {
        var normalizado = Normalizar(valor);
        if (normalizado == EnUrl)
            return false;

        EnUrl = normalizado;
        Version++;
        _remontando = true;
        return true;
    }

    /// <summary>
    /// El orden de la petición que el proveedor de datos acaba de recibir. Devuelve <c>true</c> si el
    /// usuario ordenó por otra columna u otra dirección: la página escribe <see cref="EnUrl"/> en la URL.
    /// Mientras la rejilla se remonta, una petición de la rejilla saliente (con el orden anterior) se ignora.
    /// </summary>
    public bool Anotar(string? propiedad, bool descendente)
    {
        var pedido = DesdeLaRejilla(propiedad, descendente);
        if (_remontando)
        {
            _remontando = pedido != EnUrl;
            return false;
        }

        if (pedido == EnUrl)
            return false;

        EnUrl = pedido;
        return true;
    }

    private string DesdeLaRejilla(string? propiedad, bool descendente)
    {
        var clave = _columnas.FirstOrDefault(c => c.Propiedad == propiedad).Clave;
        if (clave is null || (clave == _claveDeFabrica && descendente == _descendenteDeFabrica))
            return string.Empty;

        return descendente ? clave + SufijoDescendente : clave;
    }

    private string Normalizar(string? valor)
    {
        var texto = (valor ?? string.Empty).Trim().ToLowerInvariant();
        var descendente = texto.EndsWith(SufijoDescendente, StringComparison.Ordinal);
        var clave = SinSufijo(texto);

        if (!_columnas.Any(c => c.Clave == clave) || (clave == _claveDeFabrica && descendente == _descendenteDeFabrica))
            return string.Empty;

        return descendente ? clave + SufijoDescendente : clave;
    }

    private static string SinSufijo(string valor) =>
        valor.EndsWith(SufijoDescendente, StringComparison.Ordinal) ? valor[..^SufijoDescendente.Length] : valor;
}

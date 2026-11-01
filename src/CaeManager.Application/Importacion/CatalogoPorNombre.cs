using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;

namespace CaeManager.Application.Importacion;

/// <summary>Resultado de buscar un nombre en un <see cref="CatalogoPorNombre{TValor}"/>.</summary>
internal enum ResultadoBusquedaPorNombre
{
    NoExiste,
    Unico,
    Ambiguo,
}

/// <summary>
/// Diccionario nombre → entidad para los emparejamientos de la importación (Centros, razón social de Empresa y
/// Tipos de documento), sin distinguir mayúsculas y que NO falla ni elige al azar cuando dos entidades distintas
/// comparten nombre: la búsqueda devuelve «no existe», «único» o «ambiguo».
///
/// Sustituye a <c>ToDictionaryAsync</c> (lanzaba <see cref="ArgumentException"/> con una clave repetida y tumbaba
/// la importación entera, contra el éxito parcial de DCR-12 B) y a <c>TryAdd</c> (asignaba a la entidad que
/// llegara primera, que puede ser la equivocada). Dos Centros llamados igual bajo distintos Clientes empresariales,
/// o dos razones sociales / Tipos de documento que solo difieren en mayúsculas (los índices únicos de la base
/// distinguen mayúsculas), son datos legítimos: la fila que los cite se omite con su causal propia, y las demás
/// filas del archivo siguen.
/// </summary>
internal sealed class CatalogoPorNombre<TValor>
{
    private readonly Dictionary<string, List<TValor>> _entradas = new(StringComparer.OrdinalIgnoreCase);
    private readonly IEqualityComparer<TValor> _igualdad;

    /// <param name="igualdad">Qué es «la misma entidad» (por defecto, la igualdad de <typeparamref name="TValor"/>): la misma entidad leída dos veces no es una homónima.</param>
    public CatalogoPorNombre(IEqualityComparer<TValor>? igualdad = null) => _igualdad = igualdad ?? EqualityComparer<TValor>.Default;

    public static CatalogoPorNombre<TValor> De<TOrigen>(
        IEnumerable<TOrigen> elementos, Func<TOrigen, string> nombre, Func<TOrigen, TValor> valor)
    {
        var catalogo = new CatalogoPorNombre<TValor>();
        foreach (var elemento in elementos)
            catalogo.Agregar(nombre(elemento), valor(elemento));
        return catalogo;
    }

    /// <summary>Registra una entidad. La misma entidad dos veces cuenta como una; otra distinta con el mismo nombre vuelve ambiguo el nombre.</summary>
    public void Agregar(string nombre, TValor valor)
    {
        if (!_entradas.TryGetValue(nombre, out var lista))
            _entradas[nombre] = lista = [];
        if (!lista.Contains(valor, _igualdad))
            lista.Add(valor);
    }

    /// <summary>Declara a qué entidad se refiere el nombre, sustituyendo cualquier otra con ese nombre (p. ej. la hoja Clientes, que identifica por CIF).</summary>
    public void Fijar(string nombre, TValor valor) => _entradas[nombre] = [valor];

    public ResultadoBusquedaPorNombre Buscar(string nombre, out TValor? valor)
    {
        valor = default;
        if (!_entradas.TryGetValue(nombre, out var lista) || lista.Count == 0)
            return ResultadoBusquedaPorNombre.NoExiste;
        if (lista.Count > 1)
            return ResultadoBusquedaPorNombre.Ambiguo;
        valor = lista[0];
        return ResultadoBusquedaPorNombre.Unico;
    }

    /// <summary>Todas las entradas (un nombre ambiguo aparece una vez por entidad).</summary>
    public IEnumerable<(string Nombre, TValor Valor)> Entradas =>
        _entradas.SelectMany(e => e.Value.Select(v => (e.Key, v)));

    public ResultadoBusquedaPorNombre Buscar(string nombre) => Buscar(nombre, out _);
}

/// <summary>
/// DNI de Trabajadores en la Papelera: el índice único <c>(TenantId, Dni)</c> incluye a los eliminados, así que dar
/// de alta uno con ese DNI haría fallar el guardado entero (23505) aunque el diccionario de Trabajadores activos no
/// lo vea. Se calcula una vez sobre los DNI del plan, ya normalizados.
/// </summary>
internal static class DnisEnPapelera
{
    public static string Normalizar(string dni) => ValidadorIdentificacion.Normalizar(dni) ?? dni.Trim().ToUpperInvariant();

    /// <summary>
    /// Los DNI de <paramref name="dnis"/> que están en la Papelera del Tenant propietario, comparables con
    /// <see cref="Normalizar"/>. No devuelve ningún dato del Trabajador eliminado.
    /// </summary>
    public static async Task<IReadOnlySet<string>> ObtenerAsync(
        ITrabajadorRepository repositorio, IEnumerable<string> dnis, CancellationToken cancellationToken)
    {
        var normalizados = dnis.Select(Normalizar).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return await repositorio.ObtenerDnisEnPapeleraAsync(normalizados, cancellationToken);
    }

    public static string Motivo(string dni) =>
        $"El DNI {dni} corresponde a un trabajador que está en la Papelera: restáuralo desde la Papelera y vuelve a importar el archivo. " +
        "Mientras esté allí no se puede dar de alta otro trabajador con el mismo DNI.";
}

using System.Text.RegularExpressions;

namespace CaeManager.Domain.Empresas;

/// <summary>
/// Normalización común de los nombres de <see cref="DelegacionEmpresa"/> y <see cref="OficioEmpresa"/>: sin espacios
/// en los extremos y con los interiores colapsados a uno. <see cref="Clave"/> es lo que decide si dos nombres son el
/// mismo dentro de una Empresa (sin distinguir mayúsculas ni espacios), el mismo criterio que usó la migración que
/// convirtió los Puestos en Oficios.
/// </summary>
public static partial class NombreDeCatalogo
{
    public static string Normalizar(string? nombre, int longitudMaxima, string queEs)
    {
        var normalizado = Espacios().Replace(nombre?.Trim() ?? string.Empty, " ");
        if (normalizado.Length == 0)
            throw new ArgumentException($"El nombre de {queEs} es obligatorio.", nameof(nombre));
        if (normalizado.Length > longitudMaxima)
            throw new ArgumentException($"El nombre de {queEs} no puede superar {longitudMaxima} caracteres.", nameof(nombre));
        return normalizado;
    }

    public static string Clave(string? nombre) => Espacios().Replace(nombre?.Trim() ?? string.Empty, " ").ToLowerInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Espacios();
}

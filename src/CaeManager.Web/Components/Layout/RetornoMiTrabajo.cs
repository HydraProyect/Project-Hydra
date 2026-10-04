using CaeManager.Web.Components.Account;

namespace CaeManager.Web.Components.Layout;

/// <summary>
/// El camino de vuelta a Mi trabajo tras la acción de una fila. La acción es un POST con
/// recarga completa a <c>/cuenta/cliente-activo</c> que aterriza en la pantalla del ítem;
/// para que el Gestor CAE pueda volver con sus filtros intactos, el destino lleva el parámetro
/// <see cref="Parametro"/> con la URL de Mi trabajo tal como estaba (filtros incluidos) y el
/// layout pinta un enlace con ella. El valor viaja en la URL y por tanto es entrada no
/// fiable: <see cref="Validar"/> solo admite una ruta local de Mi trabajo, nunca otra pantalla
/// ni una URL externa (open redirect).
/// </summary>
public static class RetornoMiTrabajo
{
    public const string Parametro = "volver";
    public const string Ruta = "/mi-trabajo";

    /// <summary>
    /// La URL local de Mi trabajo, o <c>null</c> si el valor no es exactamente eso: vacío, de otra
    /// ruta (<c>/mi-trabajoX</c>, <c>/otra?x=/mi-trabajo</c>), absoluto, protocolo relativo
    /// (<c>//host</c>, <c>/\host</c>) o con caracteres de control.
    /// </summary>
    public static string? Validar(string? valor)
    {
        if (string.IsNullOrEmpty(valor) || RedireccionLocal.Sanear(valor) != valor)
            return null;
        if (!valor.StartsWith(Ruta, StringComparison.Ordinal))
            return null;
        var resto = valor.AsSpan(Ruta.Length);
        return resto.IsEmpty || resto[0] is '?' ? valor : null;
    }

    /// <summary>
    /// El destino de la acción con la vuelta añadida. Si la URL de Mi trabajo no es válida (la
    /// pantalla no está en su ruta, como en un test que la monta en la raíz) el destino sale
    /// intacto.
    /// </summary>
    public static string ComponerDestino(string destino, string? urlMiTrabajo)
    {
        var vuelta = Validar(urlMiTrabajo);
        if (vuelta is null) return destino;
        var separador = destino.Contains('?') ? '&' : '?';
        return $"{destino}{separador}{Parametro}={Uri.EscapeDataString(vuelta)}";
    }
}

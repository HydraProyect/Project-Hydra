using System.Text.RegularExpressions;

namespace CaeManager.Web.Features.Importacion;

/// <summary>
/// Enmascara el DNI/NIE que el Importador enseña en pantalla (plan, avisos, omitidos). Decisión provisional del
/// 2026-11-02, pendiente de Chris: el DNI completo del plan solo lo ve el Administrador (decisión S3 del 2026-09-24);
/// Dirección CAE y Coordinador CAE, que ejecutan cargas masivas desde el 2026-11-01, lo ven enmascarado. Conserva los
/// tres últimos dígitos y la letra, suficientes para reconocer la fila en su propio fichero.
/// </summary>
public static partial class MascaraDni
{
    /// <summary>Solo el Administrador ve el DNI completo; cualquier otro rol, o ninguno, lo ve enmascarado (falla cerrado).</summary>
    public static bool PuedeVerCompleto(System.Security.Claims.ClaimsPrincipal? usuario) =>
        usuario?.IsInRole(CaeManager.Infrastructure.Identity.Roles.Administrador) == true;

    public static string Aplicar(string? texto, bool completo) =>
        completo || string.IsNullOrEmpty(texto) ? texto ?? string.Empty : PatronDni().Replace(texto, Reemplazar);

    /// <summary>
    /// Para un campo que se sabe que es el documento de identidad de un Trabajador: se enmascara el valor entero sea cual
    /// sea su forma, porque el sistema acepta como Pasaporte cualquier valor que no sea DNI o NIE (incluido un DNI sin letra).
    /// </summary>
    public static string AplicarDocumento(string? documento, bool completo)
    {
        if (completo || string.IsNullOrEmpty(documento)) return documento ?? string.Empty;
        var limpio = documento.Trim();
        return limpio.Length <= 4 ? "****" : "****" + limpio[^4..];
    }

    private static string Reemplazar(Match m)
    {
        var valor = m.Value;
        return "****" + valor[^4..];
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:[XYZxyz]?\d{7,8}[A-Za-z]|\d{8})(?![A-Za-z0-9])")]
    private static partial Regex PatronDni();
}

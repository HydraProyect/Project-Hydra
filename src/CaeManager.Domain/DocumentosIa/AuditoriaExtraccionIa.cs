using CaeManager.Domain.Common;

namespace CaeManager.Domain.DocumentosIa;

/// <summary>
/// Registro de auditoría de cada procesamiento por <c>DocumentAIRouterService</c>
/// (ver docs/ARQUITECTURA-IA-DOCUMENTAL.md § 3): proveedor usado, tiempo,
/// coste estimado, páginas, confianza e incidencias — como mínimo lo que
/// pedía el Issue #19. Se escribe siempre, incluso cuando el procesamiento
/// falla (para poder ver fallos recurrentes de un proveedor) o cuando el
/// resultado viene de <see cref="ExtraccionIaCache"/> (proveedor "cache",
/// coste 0 — no se volvió a pagar nada).
/// </summary>
public class AuditoriaExtraccionIa : EntidadConTenant
{
    public const int LongitudHash = 64;
    public const int LongitudMaximaTipoEsperado = 150;
    public const int LongitudMaximaProveedorCodigo = 100;
    public const int LongitudMaximaIncidencias = 1000;

    public string HashSha256 { get; private set; } = string.Empty;
    public string TipoEsperado { get; private set; } = string.Empty;
    public string ProveedorCodigo { get; private set; } = string.Empty;
    public long TiempoProcesamientoMs { get; private set; }
    public decimal? CosteEstimado { get; private set; }
    public int NumeroPaginas { get; private set; }
    public int ConfianzaGeneral { get; private set; }
    public string? Incidencias { get; private set; }
    public DateTime CreadaEnUtc { get; private set; } = DateTime.UtcNow;

    private AuditoriaExtraccionIa()
    {
    }

    private AuditoriaExtraccionIa(
        string hashSha256, string tipoEsperado, string proveedorCodigo, long tiempoProcesamientoMs,
        decimal? costeEstimado, int numeroPaginas, int confianzaGeneral, string? incidencias)
    {
        if (string.IsNullOrWhiteSpace(hashSha256) || hashSha256.Length != LongitudHash)
            throw new ArgumentException($"El hash SHA256 debe tener exactamente {LongitudHash} caracteres.", nameof(hashSha256));
        if (string.IsNullOrWhiteSpace(proveedorCodigo))
            throw new ArgumentException("Debe indicarse el código del proveedor (o \"cache\"/\"ninguno\").", nameof(proveedorCodigo));

        HashSha256 = hashSha256;
        TipoEsperado = tipoEsperado.Length > LongitudMaximaTipoEsperado ? tipoEsperado[..LongitudMaximaTipoEsperado] : tipoEsperado;
        ProveedorCodigo = proveedorCodigo.Length > LongitudMaximaProveedorCodigo ? proveedorCodigo[..LongitudMaximaProveedorCodigo] : proveedorCodigo;
        TiempoProcesamientoMs = tiempoProcesamientoMs;
        CosteEstimado = costeEstimado;
        NumeroPaginas = numeroPaginas;
        ConfianzaGeneral = confianzaGeneral;
        Incidencias = incidencias?.Length > LongitudMaximaIncidencias ? incidencias[..LongitudMaximaIncidencias] : incidencias;
    }

    public static AuditoriaExtraccionIa Crear(
        string hashSha256, string tipoEsperado, string proveedorCodigo, long tiempoProcesamientoMs,
        decimal? costeEstimado, int numeroPaginas, int confianzaGeneral, string? incidencias) =>
        new(hashSha256, tipoEsperado, proveedorCodigo, tiempoProcesamientoMs, costeEstimado, numeroPaginas, confianzaGeneral, incidencias);
}

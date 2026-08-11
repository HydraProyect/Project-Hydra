using CaeManager.Domain.Common;

namespace CaeManager.Domain.Subcontratas;

/// <summary>
/// Subcontrata contratada directamente por un Cliente para prestar servicio
/// a una o varias Empresas dentro de ese Cliente — un nivel adicional en la
/// cadena de coordinación (Cliente → Empresa → Subcontrata), con sus propios
/// Trabajadores y su propia documentación (mismo tratamiento que Empresa).
/// </summary>
public class Subcontrata : EntidadBase
{
    public const int LongitudMaximaRazonSocial = 200;
    public const int LongitudCif = 9;

    public string RazonSocial { get; private set; } = string.Empty;

    /// <summary>
    /// Opcional, mismo criterio que Empresa.Cif: hay Subcontratas ya creadas
    /// sin CIF (ninguna pantalla lo pedía hasta ahora, ver Issue #5) y las
    /// plantillas de importación tampoco lo recogen todavía.
    /// Nota: la obligatoriedad del CIF ya está decidida
    /// (docs/business/DECISION_LOG.md 2026-08-10, ancla de identidad entre
    /// tenants) — se aplica en su propio cambio, no en la fase ADR-005.
    /// </summary>
    public string? Cif { get; private set; }

    /// <summary>
    /// Nivel de servicio contratado (ADR-005). Gestionada = semántica
    /// anterior a la distinción, valor por defecto y de toda fila histórica.
    /// </summary>
    public NivelServicioSubcontrata NivelServicio { get; private set; } = NivelServicioSubcontrata.Gestionada;

    private Subcontrata()
    {
    }

    public Subcontrata(string razonSocial, string? cif = null)
    {
        EstablecerRazonSocial(razonSocial);
        EstablecerCif(cif);
    }

    public void Actualizar(string razonSocial, string? cif)
    {
        EstablecerRazonSocial(razonSocial);
        EstablecerCif(cif);
    }

    /// <summary>
    /// Operación de negocio explícita, no un setter en Actualizar: el cambio
    /// de nivel es un cambio de servicio contratado (ADR-005 § 2.1) y
    /// conserva todo el historial — las verificaciones externas registradas
    /// siguen siendo hechos pasados válidos en ambos sentidos del cambio.
    /// </summary>
    public void CambiarNivelServicio(NivelServicioSubcontrata nivel) => NivelServicio = nivel;

    private void EstablecerRazonSocial(string razonSocial)
    {
        if (string.IsNullOrWhiteSpace(razonSocial))
            throw new ArgumentException("La razón social es obligatoria.", nameof(razonSocial));

        var normalizada = razonSocial.Trim();

        if (normalizada.Length > LongitudMaximaRazonSocial)
            throw new ArgumentException(
                $"La razón social no puede superar {LongitudMaximaRazonSocial} caracteres.", nameof(razonSocial));

        RazonSocial = normalizada;
    }

    private void EstablecerCif(string? cif)
    {
        if (string.IsNullOrWhiteSpace(cif))
        {
            Cif = null;
            return;
        }

        var normalizado = cif.Trim().ToUpperInvariant();
        var resultado = ValidadorIdentificacion.Analizar(normalizado);

        if (resultado.Tipo != TipoIdentificacion.NifEmpresa || !resultado.EsValido)
            throw new ArgumentException("El CIF no es válido.", nameof(cif));

        Cif = normalizado;
    }
}

using CaeManager.Domain.Common;

namespace CaeManager.Domain.Empresas;

/// <summary>
/// Empresa contratista cuyo personal se coordina (la organización que opera
/// CAE Manager). Puede haber más de una razón social — p. ej. una entidad
/// para personal nacional y otra para personal extranjero.
/// </summary>
public class Empresa : EntidadBase
{
    public const int LongitudMaximaRazonSocial = 200;
    public const int LongitudCif = 9;

    public string RazonSocial { get; private set; } = string.Empty;

    /// <summary>
    /// A diferencia de Cliente, el CIF de Empresa es opcional — hay Empresas
    /// ya creadas (sembradas o importadas) sin CIF, y las plantillas de
    /// importación tampoco lo recogen todavía (ver ROADMAP.md).
    /// </summary>
    public string? Cif { get; private set; }

    private Empresa()
    {
    }

    public Empresa(string razonSocial, string? cif = null)
    {
        EstablecerRazonSocial(razonSocial);
        EstablecerCif(cif);
    }

    public void Actualizar(string razonSocial, string? cif)
    {
        EstablecerRazonSocial(razonSocial);
        EstablecerCif(cif);
    }

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

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

    public string RazonSocial { get; private set; } = string.Empty;

    private Empresa()
    {
    }

    public Empresa(string razonSocial)
    {
        EstablecerRazonSocial(razonSocial);
    }

    public void Actualizar(string razonSocial) => EstablecerRazonSocial(razonSocial);

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
}

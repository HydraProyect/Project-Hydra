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

    public string RazonSocial { get; private set; } = string.Empty;

    private Subcontrata()
    {
    }

    public Subcontrata(string razonSocial)
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

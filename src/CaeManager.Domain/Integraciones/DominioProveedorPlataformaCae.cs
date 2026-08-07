using CaeManager.Domain.Common;

namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Un dominio (o sufijo de dominio, subdominios incluidos) que identifica a
/// un <see cref="ProveedorPlataformaCae"/> — la resolución "esta URL de canal
/// pertenece a qué proveedor" matchea contra estas filas
/// (<c>IResolucionProveedorPlataformaCaeService</c>). Varias filas por
/// proveedor son el caso normal (Twind: "app.twind.io" y "ctaima.com");
/// varios proveedores pueden compartir el mismo dominio a propósito
/// (Sabentis/Quirón Prevención comparten "quironprevencion.com" — la
/// resolución debe tratarlo como multi-match, no como error).
///
/// Global como <see cref="ProveedorPlataformaCae"/> — mismo criterio, ver su
/// comentario de clase.
/// </summary>
public class DominioProveedorPlataformaCae : Entity
{
    public const int LongitudMaximaDominio = 200;

    public Guid ProveedorPlataformaCaeId { get; private set; }

    /// <summary>Sin protocolo ni ruta (p. ej. "nalandaglobal.com", no "https://nalandaglobal.com/login") — el matching es por host.</summary>
    public string Dominio { get; private set; } = string.Empty;

    private DominioProveedorPlataformaCae()
    {
    }

    public DominioProveedorPlataformaCae(Guid proveedorPlataformaCaeId, string dominio)
    {
        if (proveedorPlataformaCaeId == Guid.Empty)
            throw new ArgumentException("El dominio debe pertenecer a un proveedor.", nameof(proveedorPlataformaCaeId));

        ProveedorPlataformaCaeId = proveedorPlataformaCaeId;
        EstablecerDominio(dominio);
    }

    private void EstablecerDominio(string dominio)
    {
        if (string.IsNullOrWhiteSpace(dominio))
            throw new ArgumentException("El dominio es obligatorio.", nameof(dominio));

        var normalizado = dominio.Trim().ToLowerInvariant();

        if (normalizado.Length > LongitudMaximaDominio)
            throw new ArgumentException($"El dominio no puede superar {LongitudMaximaDominio} caracteres.", nameof(dominio));

        Dominio = normalizado;
    }
}

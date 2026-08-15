using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Sello habitual de una Empresa, configurado una vez y reutilizado en cada
/// firma en campo que lo incluya (plan Fase A) — una Empresa, un sello
/// guardado: se reemplaza al re-guardar, no se acumulan versiones. Solo por
/// subida (no tiene sentido "dibujar" un sello de empresa — es la
/// reproducción de un sello físico real).
/// </summary>
public class SelloEmpresa : EntidadConTenant
{
    public const int LongitudMaximaImagenUrl = 500;

    public Guid EmpresaId { get; private set; }
    public string ImagenUrl { get; private set; } = string.Empty;
    public DateTime ActualizadaEnUtc { get; private set; }

    private SelloEmpresa()
    {
    }

    public SelloEmpresa(Guid empresaId, string imagenUrl, DateTime actualizadaEnUtc)
    {
        if (empresaId == Guid.Empty)
            throw new ArgumentException("El sello debe pertenecer a una empresa.", nameof(empresaId));
        if (string.IsNullOrWhiteSpace(imagenUrl))
            throw new ArgumentException("La URL de la imagen no puede estar vacía.", nameof(imagenUrl));

        EmpresaId = empresaId;
        ImagenUrl = imagenUrl;
        ActualizadaEnUtc = actualizadaEnUtc;
    }

    public void Reemplazar(string imagenUrl, DateTime actualizadaEnUtc)
    {
        if (string.IsNullOrWhiteSpace(imagenUrl))
            throw new ArgumentException("La URL de la imagen no puede estar vacía.", nameof(imagenUrl));

        ImagenUrl = imagenUrl;
        ActualizadaEnUtc = actualizadaEnUtc;
    }
}

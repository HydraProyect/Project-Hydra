namespace CaeManager.Application.Common;

/// <summary>
/// Normaliza cualquier imagen soportada (foto de un sello físico, típicamente
/// sobre fondo blanco) a PNG, para guardar como <c>FirmaGuardadaUsuario</c>/
/// <c>SelloEmpresa</c> o para una firma ad-hoc subida en el momento de firmar.
/// </summary>
public interface IConversorImagenSelloService
{
    /// <summary>
    /// Si <paramref name="recortarFondoClaro"/> es true, los píxeles por
    /// encima de un umbral de luminosidad se vuelven transparentes —
    /// heurística para el caso común (sello escaneado sobre papel blanco),
    /// no una segmentación real: no recorta un fondo de color o con textura.
    /// </summary>
    byte[] NormalizarAPng(byte[] imagenOriginal, bool recortarFondoClaro);
}

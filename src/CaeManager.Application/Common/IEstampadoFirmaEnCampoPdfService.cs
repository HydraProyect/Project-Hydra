namespace CaeManager.Application.Common;

/// <summary>
/// Estampa una constancia de firma en campo (trazo manuscrito capturado en
/// pantalla, más un sello de empresa opcional) al final de un PDF, en una
/// página nueva dedicada — nunca sobre el contenido existente, para evitar
/// solapes impredecibles. Sin certificado propio de la plataforma (eso es
/// la Fase B): esta interfaz es el punto donde se añadiría, sin tocar el
/// Command que la invoca.
/// </summary>
public interface IEstampadoFirmaEnCampoPdfService
{
    byte[] Estampar(
        byte[] pdfOriginal,
        byte[] firmaPng,
        byte[]? selloPng,
        string firmanteNombre,
        string firmanteRol,
        DateTime firmadoEnUtc,
        string? ubicacion);
}

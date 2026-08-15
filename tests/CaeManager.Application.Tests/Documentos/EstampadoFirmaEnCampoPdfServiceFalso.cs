using System.Text;
using CaeManager.Application.Common;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>Fake en memoria — nunca invoca PdfSharp de verdad, solo produce bytes distintos de la entrada para que el handler tenga algo que hashear/guardar.</summary>
public class EstampadoFirmaEnCampoPdfServiceFalso : IEstampadoFirmaEnCampoPdfService
{
    public byte[]? UltimoFirmaPng { get; private set; }
    public byte[]? UltimoSelloPng { get; private set; }

    public byte[] Estampar(
        byte[] pdfOriginal, byte[] firmaPng, byte[]? selloPng, string firmanteNombre, string firmanteRol, DateTime firmadoEnUtc, string? ubicacion)
    {
        UltimoFirmaPng = firmaPng;
        UltimoSelloPng = selloPng;
        return [.. pdfOriginal, .. Encoding.UTF8.GetBytes($"|firmado:{firmanteNombre}:{firmadoEnUtc:O}")];
    }
}

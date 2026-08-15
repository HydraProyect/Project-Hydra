using CaeManager.Application.Common;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>Fake en memoria — nunca invoca SkiaSharp de verdad; solo registra si se pidió recorte.</summary>
public class ConversorImagenSelloServiceFalso : IConversorImagenSelloService
{
    public bool? UltimoRecortarFondoClaro { get; private set; }

    public byte[] NormalizarAPng(byte[] imagenOriginal, bool recortarFondoClaro)
    {
        UltimoRecortarFondoClaro = recortarFondoClaro;
        return [.. imagenOriginal, 0xFF];
    }
}

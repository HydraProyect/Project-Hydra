using CaeManager.Domain.Common;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>Topes de Dividir. Cada Documento resultante sigue limitado por <see cref="Common.LimitesArchivoSubido"/>.</summary>
public static class LimitesDivisionPdf
{
    /// <summary>Tamaño máximo del PDF original.</summary>
    public const long TamanoMaximoOriginalBytes = 50L * 1024 * 1024;

    public const int TamanoMaximoOriginalMb = (int)(TamanoMaximoOriginalBytes / (1024 * 1024));
}

/// <summary>Códigos de fallo de la lectura de un PDF para dividirlo; la pantalla traduce cada uno.</summary>
public static class ErroresDivisorPdf
{
    public const string ConContrasena = "DivisorPdf.ConContrasena";
    public const string Ilegible = "DivisorPdf.Ilegible";
    public const string DemasiadoGrande = "DivisorPdf.DemasiadoGrande";
    public const string DemasiadasPaginas = "DivisorPdf.DemasiadasPaginas";
    public const string RangoInvalido = "DivisorPdf.RangoInvalido";
}

/// <summary>
/// Abre un PDF para repartirlo en tramos. Trabaja solo en este proceso: ni el PDF ni sus páginas salen de TALVEG.
/// </summary>
public interface IDivisorPdf
{
    Result<IPdfParaDividir> Abrir(byte[] contenidoPdf);
}

/// <summary>PDF ya abierto: se abre una vez y se extraen de él tantos tramos como haga falta.</summary>
public interface IPdfParaDividir : IDisposable
{
    int NumeroPaginas { get; }

    /// <summary>Lleva firma electrónica; un tramo extraído la pierde, así que la pantalla avisa.</summary>
    bool EstaFirmado { get; }

    /// <summary>Extrae las páginas indicadas (empezando en 1, ambas incluidas) como un PDF nuevo.</summary>
    Result<byte[]> ExtraerPaginas(int paginaInicial, int paginaFinal);
}

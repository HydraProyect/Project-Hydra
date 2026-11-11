using CaeManager.Domain.Common;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>Topes de Dividir. Cada Documento resultante sigue limitado por <see cref="Common.LimitesArchivoSubido"/>.</summary>
public static class LimitesDivisionPdf
{
    /// <summary>Tamaño máximo del PDF original.</summary>
    public const long TamanoMaximoOriginalBytes = 50L * 1024 * 1024;

    public const int TamanoMaximoOriginalMb = (int)(TamanoMaximoOriginalBytes / (1024 * 1024));

    /// <summary>
    /// Veces el tamaño del original que coexisten en memoria mientras se extrae un tramo para descargarlo. El factor del
    /// análisis (<see cref="Common.Archivos.LecturaAlmacenadaParaAnalisis.FactorMemoriaAnalisis"/>, el mismo que la
    /// unión) cubre la copia leída del temporal, lo que PDFsharp construye al abrirla y las páginas copiadas al tramo
    /// nuevo. Encima, el guardado: PDFsharp escribe el tramo en un <see cref="MemoryStream"/> que crece doblando su
    /// búfer (hasta el doble del tramo) y luego lo copia con <c>ToArray</c> (otro tramo). Un tramo no pasa del
    /// original en la práctica, así que esas tres copias se acotan con tres originales; si un tramo pasara, quien lo
    /// retiene amplía su reserva sin esperar o falla cerrado.
    /// </summary>
    public const int FactorMemoriaExtraccionTramo = Common.Archivos.LecturaAlmacenadaParaAnalisis.FactorMemoriaAnalisis + 3;

    /// <summary>Lo que se reserva del presupuesto de memoria de documentos para extraer un tramo de un original de este tamaño.</summary>
    public static long MemoriaExtraccionTramo(long bytesOriginal) => bytesOriginal * FactorMemoriaExtraccionTramo;
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

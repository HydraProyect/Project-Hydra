using CaeManager.Domain.Common;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>Topes de Dividir. Cada Documento resultante sigue limitado por <see cref="Common.LimitesArchivoSubido"/>.</summary>
public static class LimitesDivisionPdf
{
    /// <summary>Tamaño máximo del PDF original.</summary>
    public const long TamanoMaximoOriginalBytes = 50L * 1024 * 1024;

    public const int TamanoMaximoOriginalMb = (int)(TamanoMaximoOriginalBytes / (1024 * 1024));

    /// <summary>
    /// Holgura fija del tramo sobre el original: lo que PDFsharp añade al reescribirlo (cabecera, <c>/Info</c>, tabla de
    /// referencias), que en un PDF de pocos kilobytes puede pesar más que el propio original.
    /// </summary>
    public const long HolguraTramoBytes = 64L * 1024;

    /// <summary>
    /// Lo más que puede ocupar un tramo extraído para descargarlo: vez y media el original más <see cref="HolguraTramoBytes"/>.
    /// Un tramo puede pasar del original, porque PDFsharp reescribe sin comprimir los objetos que el original guardaba en
    /// flujos de objetos comprimidos. La cota se aplica mientras PDFsharp escribe, no después: al pasarla, el guardado se
    /// corta y el tramo falla cerrado (<see cref="ErroresDivisorPdf.DemasiadoGrande"/>) sin haber ocupado más.
    /// </summary>
    public static long TamanoMaximoTramo(long bytesOriginal) => bytesOriginal + bytesOriginal / 2 + HolguraTramoBytes;

    /// <summary>
    /// Lo que se reserva del presupuesto de memoria de documentos para extraer un tramo de un original de este tamaño. El
    /// factor del análisis (<see cref="Common.Archivos.LecturaAlmacenadaParaAnalisis.FactorMemoriaAnalisis"/>, el mismo que
    /// la unión) cubre la copia leída del temporal, lo que PDFsharp construye al abrirla y las páginas copiadas al tramo
    /// nuevo. Encima, el guardado, que escribe en un búfer que nunca pasa de <see cref="TamanoMaximoTramo"/>: al crecer
    /// coexisten el búfer viejo y el nuevo, y al final el búfer y la copia que se entrega, así que son dos tramos máximos.
    /// </summary>
    public static long MemoriaExtraccionTramo(long bytesOriginal) =>
        bytesOriginal * Common.Archivos.LecturaAlmacenadaParaAnalisis.FactorMemoriaAnalisis + 2 * TamanoMaximoTramo(bytesOriginal);
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

    /// <summary>Extrae las páginas indicadas (empezando en 1, ambas incluidas) como un PDF nuevo, sin cota de tamaño.</summary>
    Result<byte[]> ExtraerPaginas(int paginaInicial, int paginaFinal) => ExtraerPaginas(paginaInicial, paginaFinal, long.MaxValue);

    /// <summary>
    /// Extrae las páginas indicadas como un PDF nuevo que no puede pasar de <paramref name="tamanoMaximoBytes"/>. La cota se
    /// comprueba mientras se escribe el tramo: al pasarla se corta el guardado y se devuelve
    /// <see cref="ErroresDivisorPdf.DemasiadoGrande"/>, sin haber reservado más que la cota.
    /// </summary>
    Result<byte[]> ExtraerPaginas(int paginaInicial, int paginaFinal, long tamanoMaximoBytes);
}

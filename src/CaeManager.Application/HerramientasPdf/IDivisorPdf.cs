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
    /// corta y el tramo falla cerrado (<see cref="ErroresDivisorPdf.DemasiadoGrande"/>) sin que el búfer del tramo pase de
    /// ella. Es una cota de memoria, no de validez: un PDF legítimo que comprime mucho sus objetos puede pasarla al separar
    /// un tramo grande, y la pantalla lo dice así (con tramos más pequeños cabe), sin invitar a reintentar lo mismo.
    /// </summary>
    public static long TamanoMaximoTramo(long bytesOriginal) => bytesOriginal + bytesOriginal / 2 + HolguraTramoBytes;

    /// <summary>
    /// Lo que se reserva del presupuesto de memoria de documentos para extraer un tramo de un original de este tamaño. El
    /// factor del análisis (<see cref="Common.Archivos.LecturaAlmacenadaParaAnalisis.FactorMemoriaAnalisis"/>, el mismo que
    /// la unión) estima la copia leída del temporal, lo que PDFsharp construye al abrirla y las páginas copiadas al tramo
    /// nuevo; es una estimación, no una cota que se haga cumplir. Lo que sí se acota antes de abrir es la estructura que
    /// PDFsharp descomprime al cargar (<c>AcotadorEstructuraPdf</c>, los mismos topes que la unión), así que un original
    /// fabricado para descomprimirse a gigas se rechaza antes de abrirlo. Encima, el guardado, que escribe en un búfer que
    /// nunca pasa de <see cref="TamanoMaximoTramo"/>: al crecer coexisten el búfer viejo y el nuevo, y al final el búfer y la
    /// copia que se entrega, así que son dos tramos máximos.
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

    /// <summary>La estructura que PDFsharp descomprimiría al abrirlo pasa de los topes, o no se puede medir.</summary>
    public const string DemasiadoComplejo = "DivisorPdf.DemasiadoComplejo";

    public const string RangoInvalido = "DivisorPdf.RangoInvalido";
}

/// <summary>
/// Abre un PDF para repartirlo en tramos. Trabaja solo en este proceso: ni el PDF ni sus páginas salen de TALVEG.
/// </summary>
public interface IDivisorPdf
{
    /// <summary>
    /// Abre el PDF tras acotar su tamaño, sus páginas declaradas y la estructura que se descomprime al cargarlo; lo que
    /// pasa de esos topes no se llega a abrir. Todo lo que parte de aquí (vistas previas, guardar en TALVEG, descargar
    /// tramos) hereda esa cota.
    /// </summary>
    Result<IPdfParaDividir> Abrir(byte[] contenidoPdf);
}

/// <summary>PDF ya abierto: se abre una vez y se extraen de él tantos tramos como haga falta.</summary>
public interface IPdfParaDividir : IDisposable
{
    int NumeroPaginas { get; }

    /// <summary>Lleva firma electrónica; un tramo extraído la pierde, así que la pantalla avisa.</summary>
    bool EstaFirmado { get; }

    /// <summary>
    /// Extrae las páginas indicadas (empezando en 1, ambas incluidas) como un PDF nuevo que no puede pasar de
    /// <paramref name="tamanoMaximoBytes"/>. La cota es del archivo que se escribe: se comprueba mientras se guarda el tramo,
    /// y al pasarla se corta el guardado y se devuelve <see cref="ErroresDivisorPdf.DemasiadoGrande"/> sin que el búfer del
    /// tramo pase de ella. No acota la copia de páginas en memoria, que depende de la estructura ya acotada al abrir.
    /// </summary>
    Result<byte[]> ExtraerPaginas(int paginaInicial, int paginaFinal, long tamanoMaximoBytes);
}

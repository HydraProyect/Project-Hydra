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

    /// <summary>Cabecera de la respuesta del proceso aparte que acompaña a un tramo (estado y longitud), con holgura.</summary>
    public const long CabeceraRespuestaTramoBytes = 64;

    /// <summary>Lo más que el proceso web acepta recibir del proceso aparte por un tramo con esta cota.</summary>
    public static long BytesRespuestaTramo(long cotaTramo) => cotaTramo + CabeceraRespuestaTramoBytes;

    /// <summary>
    /// Lo que el proceso web retiene por cada tramo que recibe con esta cota. PDFsharp ya no abre el original en este
    /// proceso (lo abre el proceso aparte, con su propio límite duro de memoria), así que lo que queda aquí es lo que llega
    /// de él: el lanzador corta la salida que pase de <see cref="BytesRespuestaTramo"/> y la lee en un búfer que crece
    /// doblando, así que al crecer coexisten el viejo (hasta un máximo) y el nuevo (hasta dos), y al final el búfer (hasta
    /// dos) y la copia del tramo que se entrega (hasta uno). En los dos momentos son tres respuestas máximas.
    /// </summary>
    public static long MemoriaRecepcionTramo(long cotaTramo) => 3 * BytesRespuestaTramo(cotaTramo);

    /// <summary>
    /// Lo que se reserva del presupuesto de memoria de documentos para descargar un tramo de un original de este tamaño: la
    /// copia del original leída del temporal, que se envía al proceso aparte, y lo que se recibe de él
    /// (<see cref="MemoriaRecepcionTramo"/>) con la cota de la descarga. Lo que PDFsharp construye al abrir el original y
    /// copiar sus páginas no cuenta aquí: vive en el proceso aparte, que muere si pasa de su límite.
    /// </summary>
    public static long MemoriaExtraccionTramo(long bytesOriginal) =>
        bytesOriginal + MemoriaRecepcionTramo(TamanoMaximoTramo(bytesOriginal));

    /// <summary>
    /// Lo que se reserva para guardar en TALVEG los tramos de un original de este tamaño: la copia del original y la
    /// recepción de un tramo a la vez con la cota del alta (<see cref="Common.LimitesArchivoSubido.TamanoMaximoBytes"/>),
    /// porque los tramos se extraen uno tras otro y cada uno se guarda antes de pedir el siguiente.
    /// </summary>
    public static long MemoriaCreacionTramos(long bytesOriginal) =>
        bytesOriginal + MemoriaRecepcionTramo(Common.LimitesArchivoSubido.TamanoMaximoBytes);

    /// <summary>
    /// Lo que el almacenamiento reserva por su cuenta, con espera, al cifrar un tramo con la cota del alta: tres veces el
    /// claro más 4 KiB de margen (<c>DiskFileStorageService</c>; un test de Web lo ata a lo que reserva de verdad). Guardar lo
    /// pide mientras la pantalla retiene <see cref="MemoriaCreacionTramos"/>.
    /// </summary>
    public const long MemoriaCifradoTramo = 3 * Common.LimitesArchivoSubido.TamanoMaximoBytes + 4 * 1024;

    /// <summary>
    /// Lo que tiene que caber a la vez en el presupuesto para guardar en TALVEG los tramos de un original de este tamaño: la
    /// reserva de la pantalla y la del cifrado de un tramo. Si pasa del presupuesto entero, la segunda reserva esperaría a la
    /// primera, que no se suelta hasta acabar el lote; por eso la pantalla rechaza antes de reservar nada.
    /// </summary>
    public static long MemoriaGuardarTramos(long bytesOriginal) => MemoriaCreacionTramos(bytesOriginal) + MemoriaCifradoTramo;
}

/// <summary>Códigos de fallo de la lectura de un PDF para dividirlo; la pantalla traduce cada uno.</summary>
public static class ErroresDivisorPdf
{
    public const string ConContrasena = "DivisorPdf.ConContrasena";
    public const string Ilegible = "DivisorPdf.Ilegible";
    public const string DemasiadoGrande = "DivisorPdf.DemasiadoGrande";
    public const string DemasiadasPaginas = "DivisorPdf.DemasiadasPaginas";

    /// <summary>
    /// La estructura que PDFsharp descomprimiría al abrirlo pasa de los topes o no se puede medir, o el proceso aparte que
    /// lo abre se quedó sin memoria, agotó su tiempo o murió.
    /// </summary>
    public const string DemasiadoComplejo = "DivisorPdf.DemasiadoComplejo";

    public const string RangoInvalido = "DivisorPdf.RangoInvalido";

    /// <summary>
    /// No se pudo usar el proceso aparte por un fallo de la instancia (arrancarlo, sus tuberías), no del PDF: el PDF no se
    /// llegó a juzgar y reintentar más tarde puede servir. Solo lo produce el proceso web; el hijo no puede responderlo.
    /// </summary>
    public const string FalloInterno = "DivisorPdf.FalloInterno";
}

/// <summary>
/// Abre un PDF para repartirlo en tramos. Ni el PDF ni sus páginas salen de TALVEG, pero el PDF no se abre en el proceso
/// de la aplicación: la implementación lo manda a un proceso aparte con un límite duro de memoria y de tiempo, que es la
/// garantía frente a un PDF hostil. Un primer filtro barato (tamaño, páginas declaradas y estructura que se descomprimiría
/// al cargarlo) descarta antes lo evidente, pero no lo ve todo; lo que se le escapa agota el proceso aparte y sale como
/// <see cref="ErroresDivisorPdf.DemasiadoComplejo"/>.
/// </summary>
public interface IDivisorPdf
{
    /// <summary>
    /// Abre el PDF para contar sus páginas y ver si lleva firma. Un hijo que no consta terminado lanza
    /// <c>EntradaPdfRetenidaException</c> (quien reservó memoria para el PDF no la devuelve), y sin turno libre,
    /// <c>PresupuestoMemoriaAgotadoException</c>.
    /// </summary>
    Task<Result<IPdfParaDividir>> AbrirAsync(byte[] contenidoPdf, CancellationToken cancellationToken);
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
    /// y al pasarla se corta el guardado y se devuelve <see cref="ErroresDivisorPdf.DemasiadoGrande"/>; el proceso web
    /// tampoco acepta del proceso aparte una respuesta que pase de ella. La copia de páginas en memoria la acota el límite
    /// del proceso aparte. Lanza lo mismo que <see cref="IDivisorPdf.AbrirAsync"/>.
    /// </summary>
    Task<Result<byte[]>> ExtraerPaginasAsync(int paginaInicial, int paginaFinal, long tamanoMaximoBytes, CancellationToken cancellationToken);
}

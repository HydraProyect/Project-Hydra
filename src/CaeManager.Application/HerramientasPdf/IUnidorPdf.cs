using CaeManager.Application.Common.Archivos;
using CaeManager.Domain.Common;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>
/// Topes de Unir. Cada PDF que sube una persona sigue limitado por <see cref="Common.LimitesArchivoSubido"/> (10 MB); aquí
/// se acota lo que se une en una sola operación, porque el unido se arma en memoria (OWASP API4:2023, consumo de
/// recursos).
/// </summary>
public static class LimitesUnionPdf
{
    /// <summary>Ficheros que entran en una unión: suficiente para un paquete documental y lejos de agotar memoria.</summary>
    public const int MaximoFicheros = 20;

    /// <summary>Suma de los bytes de todos los PDF de una unión, vengan de una subida o de Documentos.</summary>
    public const long TamanoMaximoTotalBytes = 50L * 1024 * 1024;

    public const int TamanoMaximoTotalMb = (int)(TamanoMaximoTotalBytes / (1024 * 1024));

    /// <summary>Páginas del PDF unido.</summary>
    public const int MaximoPaginasTotales = 500;

    /// <summary>Un PDF unido tiene al menos dos de origen: con uno no hay nada que unir.</summary>
    public const int MinimoFicheros = 2;

    /// <summary>
    /// Objetos que un PDF puede declarar (los de texto plano más los de sus flujos de objetos). PDFsharp los carga todos al
    /// abrirlo, también en modo importación, así que un PDF pequeño con millones de objetos comprimidos agotaría memoria y
    /// CPU antes de poder contar sus páginas. Un PDF real de 500 páginas ronda las decenas de miles: el tope lo dobla con
    /// holgura y sigue acotando el coste.
    /// </summary>
    public const int MaximoObjetosPorPdf = 200_000;

    /// <summary>
    /// Bytes que se descomprimen de los flujos de objetos y de las tablas de referencias comprimidas de un PDF (lo que
    /// PDFsharp analiza al abrirlo, aparte de sus bytes en bruto). Uno real ocupa unos cientos de KB; una bomba de
    /// descompresión pasa de aquí y se rechaza sin terminar de descomprimir.
    /// </summary>
    public const long MaximoBytesEstructuraPorPdf = 32L * 1024 * 1024;

    /// <summary>
    /// Veces el tamaño de cada PDF que una unión retiene en el presupuesto de memoria de documentos: la copia, lo que
    /// PDFsharp construye al abrirlo y su parte del PDF unido. El mismo factor que el análisis de un archivo almacenado.
    /// </summary>
    public const int FactorMemoriaUnion = LecturaAlmacenadaParaAnalisis.FactorMemoriaAnalisis;
}

/// <summary>
/// Códigos de fallo de Unir. Los de entrada (<see cref="ResultadoUnionPdf.CodigoRechazo"/>) dicen qué PDF no sirvió y por
/// qué; los demás son de la operación entera y salen como <c>Result</c> fallido. La pantalla traduce cada uno.
/// </summary>
public static class ErroresUnirPdf
{
    // De una entrada.
    public const string ConContrasena = "UnirPdf.ConContrasena";
    public const string Ilegible = "UnirPdf.Ilegible";
    public const string NoEsPdf = "UnirPdf.NoEsPdf";
    public const string DemasiadoGrande = "UnirPdf.DemasiadoGrande";
    public const string DemasiadasPaginas = "UnirPdf.DemasiadasPaginas";

    /// <summary>El PDF declara más objetos o más estructura comprimida de la que se analiza sin riesgo.</summary>
    public const string DemasiadoComplejo = "UnirPdf.DemasiadoComplejo";
    public const string DocumentoNoDisponible = "UnirPdf.DocumentoNoDisponible";
    public const string ArchivoNoDisponible = "UnirPdf.ArchivoNoDisponible";

    // De la operación.
    public const string NoDisponible = "UnirPdf.NoDisponible";
    public const string TenantCruzado = "UnirPdf.TenantCruzado";
    public const string EmpresaNoGestionable = "UnirPdf.EmpresaNoGestionable";
    public const string NumeroDeFicherosInvalido = "UnirPdf.NumeroDeFicherosInvalido";
    public const string TotalDemasiadoGrande = "UnirPdf.TotalDemasiadoGrande";
    public const string TotalDemasiadasPaginas = "UnirPdf.TotalDemasiadasPaginas";
    public const string RolSinAcceso = "UnirPdf.RolSinAcceso";
    public const string SesionPrivilegiadaSinLectura = "UnirPdf.SesionPrivilegiadaSinLectura";
    public const string SinEspacio = "UnirPdf.SinEspacio";
}

/// <summary>Lo que se sabe de un PDF antes de unirlo.</summary>
/// <param name="EstaFirmado">
/// Lleva firma electrónica; al unirlo la pierde (el unido es un PDF nuevo), así que la pantalla avisa antes.
/// </param>
/// <param name="BytesEstructura">
/// Lo que se descomprime de su estructura al abrirlo (<see cref="LimitesUnionPdf.MaximoBytesEstructuraPorPdf"/>); cuenta en el
/// presupuesto de memoria de la unión.
/// </param>
public record InfoPdfParaUnir(int NumeroPaginas, bool EstaFirmado, long BytesEstructura = 0);

public record PdfUnido(byte[] Contenido, int NumeroPaginas);

/// <summary>
/// Inspecciona y une PDF. Ni los PDF ni sus páginas salen del servidor de TALVEG, pero un PDF ajeno no se abre en el
/// proceso web: la implementación lo analiza en un proceso aparte con un límite duro de memoria y de tiempo, de modo que un
/// PDF hostil agota ese proceso y no la aplicación (decisión de Chris del 2026-11-11 sobre la bomba de PDF). Los PDF llegan
/// ya acotados en tamaño por el llamante; aquí se acota por páginas.
///
/// Un PDF que agota la memoria o el tiempo del análisis sale como <see cref="ErroresUnirPdf.DemasiadoComplejo"/>. Si no hay
/// turno libre para el proceso aparte a tiempo, lanza <see cref="Common.PresupuestoMemoriaAgotadoException"/>, el mismo
/// transitorio que el presupuesto de memoria de documentos («inténtalo de nuevo»). Si no consta que el proceso que recibió
/// los PDF terminara, lanza <see cref="EntradaPdfRetenidaException"/>, también si el llamante canceló.
/// </summary>
public interface IUnidorPdf
{
    /// <summary>Abre el PDF solo para contar páginas y detectar la firma; no devuelve nada que lo reproduzca.</summary>
    Task<Result<InfoPdfParaUnir>> InspeccionarAsync(byte[] contenidoPdf, CancellationToken cancellationToken);

    /// <summary>Une los PDF en el orden recibido en uno nuevo: las páginas se copian, la firma electrónica no se conserva.</summary>
    Task<Result<PdfUnido>> UnirAsync(IReadOnlyList<byte[]> pdfs, CancellationToken cancellationToken);
}

/// <summary>
/// No consta que terminara el proceso aparte que recibió estos PDF (no se pudo matar, no salió, una escritura de su entrada
/// sigue pendiente o el lanzador no volvió): los PDF pueden seguir referenciados y el turno de ese proceso queda retenido
/// hasta reiniciar la instancia. Quien reservó memoria del presupuesto para esos PDF tampoco la devuelve: la pasa a
/// <see cref="Retener"/>. Para la persona es el mismo «inténtalo más tarde» que
/// <see cref="Common.PresupuestoMemoriaAgotadoException"/>.
/// </summary>
public sealed class EntradaPdfRetenidaException : Exception
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<IDisposable> Retenidas = [];

    /// <summary>Sin decir qué entradas: quien la recibe retiene todo lo que reservó para los PDF (el lado seguro).</summary>
    public EntradaPdfRetenidaException()
        : base(Common.PresupuestoMemoriaAgotadoException.MensajeUsuario)
    {
    }

    /// <summary>Solo quedan referenciados los PDF de <paramref name="posiciones"/> (posiciones de la entrada de la unión).</summary>
    public EntradaPdfRetenidaException(IReadOnlyList<int> posiciones, Exception? interna)
        : base(Common.PresupuestoMemoriaAgotadoException.MensajeUsuario, interna)
    {
        ArgumentNullException.ThrowIfNull(posiciones);
        Posiciones = posiciones;
    }

    /// <summary>
    /// Las posiciones de la entrada cuyos PDF recibió el proceso que no consta terminado: lo reservado para ellas se retiene y
    /// lo reservado para las demás se devuelve. <c>null</c> si no se sabe o son todas (la unión): se retiene todo.
    /// </summary>
    public IReadOnlyList<int>? Posiciones { get; }

    /// <summary>Verdadero si el PDF de la posición <paramref name="posicion"/> puede seguir referenciado.</summary>
    public bool Retiene(int posicion) => Posiciones is null || Posiciones.Contains(posicion);

    /// <summary>Reservas que no se devuelven nunca en esta instancia, igual que el turno del proceso que no consta terminado.</summary>
    public static int NumeroRetenidas => Retenidas.Count;

    /// <summary>
    /// Deja <paramref name="reserva"/> sin devolver hasta reiniciar la instancia: se guarda viva y nadie la desecha. Quien la
    /// pasa aquí no debe desecharla después.
    /// </summary>
    public static void Retener(IDisposable? reserva)
    {
        if (reserva is not null)
            Retenidas.Add(reserva);
    }
}

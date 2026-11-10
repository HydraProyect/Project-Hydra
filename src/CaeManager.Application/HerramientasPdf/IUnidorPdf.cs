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
public record InfoPdfParaUnir(int NumeroPaginas, bool EstaFirmado);

public record PdfUnido(byte[] Contenido, int NumeroPaginas);

/// <summary>
/// Inspecciona y une PDF. Trabaja solo en este proceso: ni los PDF ni sus páginas salen de TALVEG. Los PDF llegan ya
/// acotados en tamaño por el llamante; aquí se acota por páginas.
/// </summary>
public interface IUnidorPdf
{
    /// <summary>Abre el PDF solo para contar páginas y detectar la firma; no devuelve nada que lo reproduzca.</summary>
    Result<InfoPdfParaUnir> Inspeccionar(byte[] contenidoPdf);

    /// <summary>Une los PDF en el orden recibido en uno nuevo: las páginas se copian, la firma electrónica no se conserva.</summary>
    Result<PdfUnido> Unir(IReadOnlyList<byte[]> pdfs);
}

using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Trabajadores.Extraccion;

/// <summary>
/// Lectura local, sin IA y sin salir de la aplicación, de los Trabajadores que figuran en un
/// ITA, un RNT o un TC2 en PDF con texto digital. Es la primera pasada de
/// <see cref="ExtraccionTrabajadoresConParserLocalService"/>: solo se da por buena si el
/// documento «cuadra» (ver <see cref="MotivoNoCuadra"/>); si no, quien llama cae a la IA.
/// </summary>
public interface IExtractorTrabajadoresLocal
{
    ResultadoExtraccionLocal Extraer(byte[] contenidoPdf);
}

/// <summary>
/// Una palabra del PDF con su caja en coordenadas de página (origen abajo a la izquierda,
/// como PDF). La produce el adaptador de Infrastructure; el parser solo ve geometría y texto.
/// </summary>
public readonly record struct PalabraPdf(string Texto, double Izquierda, double Derecha, double Inferior, double Superior)
{
    public double Alto => Superior - Inferior;
    public double CentroY => (Superior + Inferior) / 2;
}

/// <param name="ProporcionImagen">Fracción de la página (0 a 1) que cubre su imagen mayor: una página escaneada vale casi 1; un logotipo, casi 0.</param>
/// <param name="PalabrasNoHorizontales">Palabras de la página que el adaptador no incluye en <paramref name="Palabras"/> por no ir en horizontal (marcas de agua, sellos o una página girada entera). El parser necesita saberlo para no dar por completa una página de la que solo ve una parte.</param>
public sealed record PaginaPdf(int Numero, IReadOnlyList<PalabraPdf> Palabras, double ProporcionImagen, int PalabrasNoHorizontales = 0);

/// <summary>Puerto de lectura de palabras con posición de un PDF; la implementación real (PdfPig) vive en Infrastructure.</summary>
public interface IExtractorPalabrasPdf
{
    /// <summary>Lee con el tope de tamaño de la subida (<see cref="Common.LimitesArchivoSubido.TamanoMaximoBytes"/>).</summary>
    Result<IReadOnlyList<PaginaPdf>> ExtraerPalabras(byte[] contenidoPdf);

    /// <summary>Lee con un tope propio: Dividir admite originales mayores que un Documento. El resto de topes no cambia.</summary>
    Result<IReadOnlyList<PaginaPdf>> ExtraerPalabras(byte[] contenidoPdf, long tamanoMaximoBytes);
}

/// <summary>
/// Topes de lectura que comparten el adaptador (que corta antes de materializar el documento) y el
/// parser (que los vuelve a comprobar sobre lo recibido): si divergieran, se leería entero lo que
/// luego se tira.
/// </summary>
public static class LimitesLecturaPalabrasPdf
{
    public const int MaximoPaginas = 300;
    public const int MaximoPalabrasEnTotal = 300_000;
    public static readonly TimeSpan TiempoMaximo = TimeSpan.FromSeconds(30);

    /// <summary>Una página con más palabras no horizontales que esto (o más que horizontales) se considera girada o de otra naturaleza.</summary>
    public const int MaximoPalabrasNoHorizontalesPorPagina = 10;
}

/// <summary>
/// Por qué el parser local no se fía de su propio resultado. <c>null</c> en
/// <see cref="ResultadoExtraccionLocal.Motivo"/> es lo único que significa «cuadra».
/// </summary>
public enum MotivoNoCuadra
{
    /// <summary>El PDF no se pudo abrir, está cifrado o excede los topes de tamaño, páginas o tiempo.</summary>
    PdfNoLegible,

    /// <summary>No hay palabras en todo el documento: escaneado o vacío.</summary>
    SinTexto,

    /// <summary>Alguna página no tiene texto digital (sin palabras o cubierta por una imagen): documento mixto.</summary>
    PaginaSinTextoDigital,

    /// <summary>Una página tiene buena parte de su texto girado o vertical: el parser solo ve la parte horizontal.</summary>
    TextoNoHorizontal,

    /// <summary>Hay algo con forma de DNI/NIE cuya letra de control no cuadra: no se adivina cuál era el correcto.</summary>
    IdentificadorInvalido,

    /// <summary>Un DNI/NIE válido fuera de la tabla (antes de su cabecera) o sin nombre en su fila.</summary>
    IdentificadorSinFila,

    /// <summary>Más de un DNI/NIE en la misma fila: no se sabe a quién pertenece cada nombre.</summary>
    VariosIdentificadoresEnFila,

    /// <summary>Una fila con aspecto de Trabajador (nombre y número largo, o nombre con coma) pero sin DNI/NIE legible: pasaporte, DNI mal impreso. El parser la perdería en silencio.</summary>
    FilaSinIdentificador,

    /// <summary>El nombre de la fila no se separa con certeza en apellidos y nombre, o continúa en la línea siguiente.</summary>
    NombreAmbiguo,

    /// <summary>El mismo DNI/NIE aparece con dos nombres distintos.</summary>
    IdentificadorConNombresDistintos,

    /// <summary>Más de una tabla en una página (otra cabecera) o una mención a bajas: no se sabe qué filas son altas.</summary>
    TablaAmbigua,

    /// <summary>Ningún Trabajador: no hay nada que dar por bueno.</summary>
    SinTrabajadores,

    /// <summary>El parser lanzó una excepción: un fallo nuestro, no del documento. Se distingue para que la métrica no lo esconda entre los PDF rotos.</summary>
    ErrorInterno,
}

/// <summary>
/// Resultado del parser local. Cuando no <see cref="Cuadra"/>, <see cref="Trabajadores"/>
/// va vacío a propósito: una lista parcial no se puede confundir con el resultado.
/// </summary>
public sealed record ResultadoExtraccionLocal(
    IReadOnlyList<TrabajadorExtraidoDto> Trabajadores,
    MotivoNoCuadra? Motivo)
{
    public bool Cuadra => Motivo is null;

    public static ResultadoExtraccionLocal NoCuadra(MotivoNoCuadra motivo) => new([], motivo);
}

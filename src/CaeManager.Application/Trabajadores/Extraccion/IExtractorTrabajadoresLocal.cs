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
public sealed record PaginaPdf(int Numero, IReadOnlyList<PalabraPdf> Palabras, double ProporcionImagen);

/// <summary>Puerto de lectura de palabras con posición de un PDF; la implementación real (PdfPig) vive en Infrastructure.</summary>
public interface IExtractorPalabrasPdf
{
    Result<IReadOnlyList<PaginaPdf>> ExtraerPalabras(byte[] contenidoPdf);
}

/// <summary>
/// Por qué el parser local no se fía de su propio resultado. <c>null</c> en
/// <see cref="ResultadoExtraccionLocal.Motivo"/> es lo único que significa «cuadra».
/// </summary>
public enum MotivoNoCuadra
{
    /// <summary>El PDF no se pudo abrir, está cifrado o excede los topes de tamaño.</summary>
    PdfNoLegible,

    /// <summary>No hay palabras en todo el documento: escaneado o vacío.</summary>
    SinTexto,

    /// <summary>Alguna página no tiene texto digital (sin palabras o cubierta por una imagen): documento mixto.</summary>
    PaginaSinTextoDigital,

    /// <summary>Hay algo con forma de DNI/NIE cuya letra de control no cuadra: no se adivina cuál era el correcto.</summary>
    IdentificadorInvalido,

    /// <summary>Un DNI/NIE válido fuera de la tabla (antes de su cabecera) o sin nombre en su fila.</summary>
    IdentificadorSinFila,

    /// <summary>Más de un DNI/NIE en la misma fila: no se sabe a quién pertenece cada nombre.</summary>
    VariosIdentificadoresEnFila,

    /// <summary>Una fila con número de afiliación pero sin DNI/NIE legible (pasaporte, DNI mal impreso): el parser la perdería en silencio.</summary>
    FilaSinIdentificador,

    /// <summary>El nombre de la fila no se separa con certeza en apellidos y nombre.</summary>
    NombreAmbiguo,

    /// <summary>El mismo DNI/NIE aparece con dos nombres distintos.</summary>
    IdentificadorConNombresDistintos,

    /// <summary>Ningún Trabajador: no hay nada que dar por bueno.</summary>
    SinTrabajadores,
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

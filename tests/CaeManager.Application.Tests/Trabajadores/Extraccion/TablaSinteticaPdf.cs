using CaeManager.Application.Trabajadores.Extraccion;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Tests.Trabajadores.Extraccion;

/// <summary>
/// Páginas con palabras y cajas fabricadas a mano, sin PDF: lo que el parser ve de un ITA/RNT
/// en el que cada celda es un texto en una columna. Los datos son inventados; los DNI/NIE se
/// calculan con la letra de control real para que sean válidos sin copiar ninguno de verdad.
/// </summary>
internal static class TablaSinteticaPdf
{
    public const double AltoDeLetra = 10;
    public const double AnchoDeLetra = 5;
    public const double HuecoEntrePalabras = 3;

    public const double ColumnaAfiliacion = 40;
    public const double ColumnaDocumento = 140;
    public const double ColumnaNombre = 240;
    public const double ColumnaApellidos = 240;
    public const double ColumnaNombrePila = 360;
    public const double ColumnaAlta = 470;

    public static string Dni(int numero)
    {
        var texto = numero.ToString("D8");
        return texto + ValidadorIdentificacion.LetraControlEsperada(texto)!.Value;
    }

    public static string Nie(char prefijo, int numero)
    {
        var texto = prefijo + numero.ToString("D7");
        return texto + ValidadorIdentificacion.LetraControlEsperada(texto)!.Value;
    }

    /// <summary>El mismo número con una letra de control que no es la suya.</summary>
    public static string ConLetraErronea(string identificador)
    {
        var correcta = identificador[^1];
        return identificador[..^1] + (correcta == 'A' ? 'B' : 'A');
    }

    /// <summary>Celdas (texto, x izquierda) de una fila, partidas en palabras que se tocan casi con casi.</summary>
    public static IEnumerable<PalabraPdf> Fila(double y, params (string Texto, double X)[] celdas)
    {
        foreach (var (texto, x) in celdas)
        {
            var cursor = x;
            foreach (var palabra in texto.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var ancho = palabra.Length * AnchoDeLetra;
                yield return new PalabraPdf(palabra, cursor, cursor + ancho, y - (AltoDeLetra / 2), y + (AltoDeLetra / 2));
                cursor += ancho + HuecoEntrePalabras;
            }
        }
    }

    public static IEnumerable<PalabraPdf> CabeceraCombinada(double y) =>
        Fila(y, ("NAF", ColumnaAfiliacion), ("DNI/NIE", ColumnaDocumento), ("Apellidos y nombre", ColumnaNombre), ("Fecha alta", ColumnaAlta));

    public static IEnumerable<PalabraPdf> CabeceraSeparada(double y) =>
        Fila(y, ("NAF", ColumnaAfiliacion), ("DNI", ColumnaDocumento), ("Apellidos", ColumnaApellidos), ("Nombre", ColumnaNombrePila), ("Fecha alta", ColumnaAlta));

    public static IEnumerable<PalabraPdf> FilaCombinada(double y, string afiliacion, string documento, string nombre) =>
        Fila(y, (afiliacion, ColumnaAfiliacion), (documento, ColumnaDocumento), (nombre, ColumnaNombre), ("01/02/2024", ColumnaAlta));

    public static IEnumerable<PalabraPdf> FilaSeparada(double y, string afiliacion, string documento, string apellidos, string nombre) =>
        Fila(y, (afiliacion, ColumnaAfiliacion), (documento, ColumnaDocumento), (apellidos, ColumnaApellidos), (nombre, ColumnaNombrePila), ("01/02/2024", ColumnaAlta));

    public static IEnumerable<PalabraPdf> Titulo(double y) =>
        Fila(y, ("Informe de trabajadores en alta", 40));

    public static PaginaPdf Pagina(int numero, params IEnumerable<PalabraPdf>[] bloques) =>
        new(numero, bloques.SelectMany(b => b).ToList(), ProporcionImagen: 0);

    public static PaginaPdf PaginaEscaneada(int numero) =>
        new(numero, [new PalabraPdf("pie", 10, 30, 5, 15)], ProporcionImagen: 0.95);

    public static PaginaPdf PaginaSinPalabras(int numero) => new(numero, [], ProporcionImagen: 0);

    public static IExtractorTrabajadoresLocal ParserSobre(params PaginaPdf[] paginas) =>
        new ExtractorTrabajadoresLocalService(new ExtractorPalabrasFalso(Result.Exito<IReadOnlyList<PaginaPdf>>(paginas)));

    public static IExtractorTrabajadoresLocal ParserQueNoLee() =>
        new ExtractorTrabajadoresLocalService(new ExtractorPalabrasFalso(
            Result.Fallo<IReadOnlyList<PaginaPdf>>(Error.Crear("ExtractorPalabrasPdf.ArchivoInvalido", "No se pudo leer."))));

    private sealed class ExtractorPalabrasFalso(Result<IReadOnlyList<PaginaPdf>> resultado) : IExtractorPalabrasPdf
    {
        public Result<IReadOnlyList<PaginaPdf>> ExtraerPalabras(byte[] contenidoPdf) => resultado;

        public Result<IReadOnlyList<PaginaPdf>> ExtraerPalabras(byte[] contenidoPdf, long tamanoMaximoBytes) => resultado;
    }
}

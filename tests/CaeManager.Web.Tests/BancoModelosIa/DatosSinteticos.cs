using System.Security.Cryptography;
using System.Text;
using SkiaSharp;

namespace CaeManager.Web.Tests.BancoModelosIa;

/// <summary>
/// Datos inventados y deterministas para el corpus (repositorio público:
/// ningún dato real de personas ni de empresas). Los DNI y NIE que genera
/// esta clase llevan a propósito una letra de control que NO es la que les
/// corresponde: así un número generado aquí no puede coincidir con el
/// documento de nadie.
/// </summary>
public static class DatosSinteticos
{
    private const string LetrasDeControl = "TRWAGMYFPDXBNJZSQVHLCKE";

    private static readonly string[] Nombres =
        ["Marta", "Íñigo", "Lucía", "Andrés", "Nerea", "Óscar", "Beatriz", "Tomás", "Elena", "Rubén", "Aitana", "Jorge", "Noelia", "Samuel", "Irene", "Víctor"];

    private static readonly string[] Apellidos =
        ["Ibáñez", "Carrasco", "Navarro", "Beltrán", "Solís", "Arroyo", "Peña", "Gallego", "Rosales", "Quintana", "Urrutia", "Mora", "Vidal", "Zamora", "Espinosa", "Lozano", "Nieto", "Pardo"];

    /// <summary>Identificador estable por nombre: los candidatos conservan su Id entre ejecuciones y entre modelos.</summary>
    public static Guid Id(string semilla) => new(SHA256.HashData(Encoding.UTF8.GetBytes("banco-modelos-ia:" + semilla)).AsSpan(0, 16));

    public static string Dni(int n)
    {
        var numero = 10_000_000 + (n * 7_919_113L % 80_000_000);
        return $"{numero:00000000}{LetraEquivocada(numero)}";
    }

    public static string Nie(int n)
    {
        var numero = 1_000_000 + (n * 104_729L % 9_000_000);
        return $"X{numero:0000000}{LetraEquivocada(numero)}";
    }

    private static char LetraEquivocada(long numero) => LetrasDeControl[(int)((numero % 23 + 1) % 23)];

    /// <summary>El Trabajador n-ésimo de una plantilla sintética: combinación estable de nombre y dos apellidos.</summary>
    public static TrabajadorEsperado Trabajador(int n) =>
        new(
            Nombres[n % Nombres.Length],
            $"{Apellidos[n * 5 % Apellidos.Length]} {Apellidos[(n * 7 + 3) % Apellidos.Length]}",
            n % 6 == 5 ? Nie(n) : Dni(n));
}

/// <summary>
/// PDF mínimo de texto, escrito a mano y sin dependencias: Courier (una de
/// las catorce tipografías estándar, no hace falta incrustarla) con
/// codificación WinAnsi para las tildes. Determinista byte a byte, de modo
/// que dos modelos reciben exactamente el mismo documento.
/// </summary>
public static class PdfSintetico
{
    public const int LineasPorPagina = 58;

    public static byte[] Crear(IEnumerable<string> lineas)
    {
        var paginas = lineas.Chunk(LineasPorPagina).ToList();
        if (paginas.Count == 0)
            paginas.Add([]);

        var objetos = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Count {paginas.Count} /Kids [{string.Join(' ', paginas.Select((_, i) => $"{4 + i * 2} 0 R"))}] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>",
        };

        foreach (var (pagina, indice) in paginas.Select((p, i) => (p, i)))
        {
            var contenido = new StringBuilder("BT /F1 10 Tf 12.5 TL 50 790 Td\n");
            foreach (var linea in pagina)
                contenido.Append('(').Append(Escapar(linea)).Append(") Tj T*\n");
            contenido.Append("ET");

            objetos.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + indice * 2} 0 R >>");
            objetos.Add($"<< /Length {contenido.Length} >>\nstream\n{contenido}\nendstream");
        }

        var latin1 = Encoding.Latin1;
        using var salida = new MemoryStream();
        void Escribir(string texto) => salida.Write(latin1.GetBytes(texto));

        Escribir("%PDF-1.4\n");
        var posiciones = new List<long>();
        for (var i = 0; i < objetos.Count; i++)
        {
            posiciones.Add(salida.Position);
            Escribir($"{i + 1} 0 obj\n{objetos[i]}\nendobj\n");
        }

        var inicioXref = salida.Position;
        Escribir($"xref\n0 {objetos.Count + 1}\n0000000000 65535 f \n");
        foreach (var posicion in posiciones)
            Escribir($"{posicion:0000000000} 00000 n \n");
        Escribir($"trailer\n<< /Size {objetos.Count + 1} /Root 1 0 R >>\nstartxref\n{inicioXref}\n%%EOF\n");

        return salida.ToArray();
    }

    private static string Escapar(string linea) =>
        linea.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
}

/// <summary>
/// Imagen que imita un documento escaneado: texto girado unos grados, motas
/// y rayas con semilla fija y compresión JPEG agresiva. Es la única entrada
/// del banco sin capa de texto, así que es la que mide de verdad la lectura.
///
/// Las letras se dibujan con una matriz de puntos propia (5 × 7, solo
/// mayúsculas), no con una tipografía del sistema: el runner de CI no tiene
/// ninguna, y con una tipografía distinta en cada máquina dos ejecuciones no
/// leerían el mismo documento. La imagen es la misma en todas partes salvo
/// por lo que varíe el codificador JPEG de la plataforma.
/// </summary>
public static class EscaneoSintetico
{
    private const int Punto = 3;
    private const int Avance = 6 * Punto + 2;

    public static byte[] Crear(IReadOnlyList<string> lineas, int semilla)
    {
        const int ancho = 1240, alto = 1754;
        using var mapa = new SKBitmap(ancho, alto);
        using var lienzo = new SKCanvas(mapa);
        lienzo.Clear(new SKColor(250, 248, 242));

        using var tinta = new SKPaint { Color = new SKColor(40, 40, 48), IsAntialias = true };

        lienzo.Save();
        lienzo.RotateDegrees(2.2f, ancho / 2f, alto / 2f);
        for (var i = 0; i < lineas.Count; i++)
            DibujarLinea(lienzo, tinta, lineas[i], 110, 130 + i * 40);
        lienzo.Restore();

        var azar = new Random(semilla);
        using var mota = new SKPaint { Color = new SKColor(90, 90, 90, 120) };
        for (var i = 0; i < 2500; i++)
            lienzo.DrawCircle(azar.Next(ancho), azar.Next(alto), 1 + (float)azar.NextDouble() * 1.5f, mota);
        using var raya = new SKPaint { Color = new SKColor(120, 120, 120, 90), StrokeWidth = 2 };
        for (var i = 0; i < 6; i++)
        {
            var y = azar.Next(alto);
            lienzo.DrawLine(0, y, ancho, y + azar.Next(-25, 25), raya);
        }

        using var datos = mapa.Encode(SKEncodedImageFormat.Jpeg, 38);
        return datos.ToArray();
    }

    private static void DibujarLinea(SKCanvas lienzo, SKPaint tinta, string linea, int x, int y)
    {
        var texto = TextoBanco.SinTildes(linea).ToUpperInvariant();
        for (var c = 0; c < texto.Length; c++)
        {
            if (!Glifos.TryGetValue(texto[c], out var filas))
                continue;

            for (var fila = 0; fila < filas.Length; fila++)
            {
                for (var columna = 0; columna < 5; columna++)
                {
                    if (filas[fila][columna] == '#')
                        lienzo.DrawRect(x + c * Avance + columna * Punto, y + fila * Punto, Punto, Punto, tinta);
                }
            }
        }
    }

    /// <summary>Los caracteres que el escaneo sabe dibujar. Uno que falte aquí saldría en blanco: lo vigila un test.</summary>
    public static bool SabeDibujar(char caracter) => caracter == ' ' || Glifos.ContainsKey(caracter);

    private static readonly Dictionary<char, string[]> Glifos = new()
    {
        ['A'] = [".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
        ['B'] = ["####.", "#...#", "#...#", "####.", "#...#", "#...#", "####."],
        ['C'] = [".###.", "#...#", "#....", "#....", "#....", "#...#", ".###."],
        ['D'] = ["####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####."],
        ['E'] = ["#####", "#....", "#....", "####.", "#....", "#....", "#####"],
        ['F'] = ["#####", "#....", "#....", "####.", "#....", "#....", "#...."],
        ['G'] = [".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".####"],
        ['H'] = ["#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
        ['I'] = [".###.", "..#..", "..#..", "..#..", "..#..", "..#..", ".###."],
        ['J'] = ["..###", "...#.", "...#.", "...#.", "...#.", "#..#.", ".##.."],
        ['K'] = ["#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#"],
        ['L'] = ["#....", "#....", "#....", "#....", "#....", "#....", "#####"],
        ['M'] = ["#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#"],
        ['N'] = ["#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#", "#...#"],
        ['O'] = [".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
        ['P'] = ["####.", "#...#", "#...#", "####.", "#....", "#....", "#...."],
        ['Q'] = [".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#"],
        ['R'] = ["####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#"],
        ['S'] = [".####", "#....", "#....", ".###.", "....#", "....#", "####."],
        ['T'] = ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."],
        ['U'] = ["#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
        ['V'] = ["#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#.."],
        ['W'] = ["#...#", "#...#", "#...#", "#.#.#", "#.#.#", "##.##", "#...#"],
        ['X'] = ["#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#"],
        ['Y'] = ["#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.."],
        ['Z'] = ["#####", "....#", "...#.", "..#..", ".#...", "#....", "#####"],
        ['0'] = [".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###."],
        ['1'] = ["..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###."],
        ['2'] = [".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"],
        ['3'] = ["####.", "....#", "....#", ".###.", "....#", "....#", "####."],
        ['4'] = ["...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."],
        ['5'] = ["#####", "#....", "####.", "....#", "....#", "#...#", ".###."],
        ['6'] = ["..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###."],
        ['7'] = ["#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..."],
        ['8'] = [".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###."],
        ['9'] = [".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.."],
        [':'] = [".....", "..#..", "..#..", ".....", "..#..", "..#..", "....."],
        ['/'] = ["....#", "....#", "...#.", "..#..", ".#...", "#....", "#...."],
        ['.'] = [".....", ".....", ".....", ".....", ".....", ".##..", ".##.."],
        [','] = [".....", ".....", ".....", ".....", ".##..", "..#..", ".#..."],
        ['-'] = [".....", ".....", ".....", "#####", ".....", ".....", "....."],
    };
}

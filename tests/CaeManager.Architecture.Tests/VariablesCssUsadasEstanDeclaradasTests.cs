using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Toda variable CSS que se usa tiene que estar declarada en algún sitio.
///
/// <para>
/// <b>Por qué hace falta.</b> Una declaración como
/// <c>font-size: var(--font-size-sm)</c> con una variable que no existe y sin
/// valor de reserva es inválida en tiempo de cálculo: el navegador la descarta
/// en silencio y el elemento hereda lo que le toque. No hay error, no hay aviso
/// y en revisión se lee como correcta. Medido el 2026-09-10 sobre
/// <c>origin/main</c>: 8 variables usadas y nunca declaradas, en 11
/// declaraciones de 3 ficheros.
/// </para>
///
/// <para>
/// <b>Con valor de reserva también cuenta.</b>
/// <c>var(--color-superficie-hundida, #f4f4f4)</c> pinta, pero pinta siempre el
/// valor de reserva: el nombre sugiere un token que no existe y el color queda
/// fijo en los dos temas. Tres de las ocho eran así, y la revisión que encontró
/// las otras cinco no las vio precisamente por eso: el fallback no prueba que la
/// variable exista.
/// </para>
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre.</b> Busca declaraciones en
/// <c>.css</c>, <c>.razor</c> y <c>.cs</c> —incluidas las que se escriben dentro
/// de un <c>style</c> en línea o de una cadena de C#, como
/// <c>--estado-vacio-acento</c> en <c>EstadoVacio.razor</c>— y las que se fijan
/// desde JavaScript con <c>setProperty</c>. Lee cada fichero entero, así que ve
/// un <c>var(</c> partido en varias líneas. En los <c>.css</c> los comentarios
/// <c>/* */</c> no declaran ni usan nada. Puede dar <b>falsos negativos por dos
/// vías</b>, y ninguna por la contraria:
/// <list type="bullet">
/// <item><b>Ámbito:</b> una variable declarada en un componente cuenta como
/// declarada para todos.</item>
/// <item><b>Sintaxis:</b> en <c>.cs</c>, <c>.razor</c> y <c>.js</c> un
/// <c>--nombre:</c> escrito en un comentario o en una cadena cualquiera cuenta
/// como declaración. Ahí los comentarios no se quitan a propósito: <c>/*</c>
/// aparece en cadenas legítimas —<c>accept="image/*"</c>— y quitar desde ahí
/// hasta el siguiente <c>*/</c> escondería declaraciones reales, un falso
/// positivo peor que el hueco.</item>
/// </list>
/// No comprueba que el valor sea el adecuado, solo que el nombre exista.
/// </para>
/// </summary>
public class VariablesCssUsadasEstanDeclaradasTests
{
    /// <summary>
    /// Deuda congelada, vacía desde que las ocho medidas el 2026-09-10 se
    /// sustituyeron por tokens reales. Volver a meter una entrada es una decisión
    /// explícita, y por eso el motivo es obligatorio. Clave: el nombre de la
    /// variable.
    /// </summary>
    private static readonly Dictionary<string, string> DeudaCongelada = new();

    private sealed record Referencia(string Variable, string Fichero, int Linea, bool ConFallback);

    private static readonly Regex PatronReferencia = new(@"var\(\s*(--[a-zA-Z0-9_-]+)\s*(,)?", RegexOptions.Compiled);
    private static readonly Regex PatronDeclaracion = new(@"(--[a-zA-Z0-9_-]+)\s*:", RegexOptions.Compiled);
    private static readonly Regex PatronSetProperty = new(@"setProperty\(\s*['""](--[a-zA-Z0-9_-]+)['""]", RegexOptions.Compiled);
    private static readonly Regex ComentarioCss = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void Toda_variable_css_usada_esta_declarada()
    {
        var ficheros = Ficheros();
        ficheros.Should().NotBeEmpty(
            "sin ficheros localizados este trinquete estaría en verde por no mirar nada");

        var declaradas = Declaradas(ficheros);

        var sinDeclarar = ficheros
            .SelectMany(f => ReferenciasEn(f.Ruta, f.Contenido))
            .Where(r => !declaradas.Contains(r.Variable) && !DeudaCongelada.ContainsKey(r.Variable))
            .Select(r => $"{r.Variable}  ({Relativa(r.Fichero)}:{r.Linea}{(r.ConFallback ? ", con valor de reserva" : "")})")
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal);

        string.Join("\n", sinDeclarar).Should().BeEmpty(
            "una variable CSS sin declarar invalida la declaración en silencio, y con valor de reserva "
            + "pinta siempre ese valor aunque el nombre prometa un token. Usa un token de tokens.css, o "
            + "añade la variable a DeudaCongelada CON su motivo");
    }

    /// <summary>
    /// Control positivo: el escáner ve declaraciones en las fuentes que dice
    /// mirar. Si dejara de leer cadenas de C#, <c>--estado-vacio-acento</c> pasaría
    /// a salir como no declarada y este trinquete daría una falsa alarma; si dejara
    /// de leer los <c>.css</c>, todo saldría como no declarado.
    /// </summary>
    [Fact]
    public void El_escaner_ve_declaraciones_en_css_y_en_cadenas_de_csharp()
    {
        var declaradas = Declaradas(Ficheros());

        declaradas.Should().Contain("--color-primary-500", "se declara en tokens.css");
        declaradas.Should().Contain("--estado-vacio-acento",
            "solo se declara dentro de una cadena de C# en EstadoVacio.razor");
    }

    /// <summary>
    /// Prueba de sensibilidad permanente: el detector reconoce una variable sin
    /// declarar tanto sin valor de reserva como con él, y admite espacios dentro
    /// del <c>var( … )</c>. Si alguien relaja la expresión regular, esto se pone
    /// rojo aunque la deuda siga vacía.
    /// </summary>
    [Fact]
    public void El_detector_reconoce_variables_sin_declarar_con_y_sin_valor_de_reserva()
    {
        const string css = ".x { color: var(--no-existe); background: var( --tampoco-existe , #fff ); }";

        var referencias = ReferenciasEn("sintetico.css", css).ToList();

        referencias.Select(r => r.Variable).Should().BeEquivalentTo(["--no-existe", "--tampoco-existe"]);
        referencias.Single(r => r.Variable == "--no-existe").ConFallback.Should().BeFalse();
        referencias.Single(r => r.Variable == "--tampoco-existe").ConFallback.Should().BeTrue();

        Declaradas(Ficheros()).Should().NotContain("--no-existe").And.NotContain("--tampoco-existe",
            "si el escáner las diese por declaradas, el detector de arriba no serviría de nada");
    }

    /// <summary>
    /// Punto ciego que encontró la revisión de Codex en la primera versión: el
    /// detector partía el fichero en líneas antes de buscar, y un <c>var(</c>
    /// con salto de línea tras el paréntesis —CSS válido— no casaba. El segundo
    /// instrumento con el que se validó, un <c>grep</c>, también trabajaba por
    /// líneas, así que compartía el hueco. Medido el 2026-09-10 con un escáner de
    /// fichero entero: 0 casos en el árbol. Esto impide que el primero pase.
    /// </summary>
    [Fact]
    public void El_detector_ve_un_var_partido_en_varias_lineas_y_da_la_linea_del_var()
    {
        const string css = ".x {\n    color: var(\n        --partida-en-dos-lineas\n    );\n}\n";

        var referencia = ReferenciasEn("sintetico.css", css).Should().ContainSingle().Subject;

        referencia.Variable.Should().Be("--partida-en-dos-lineas");
        referencia.Linea.Should().Be(2, "la línea que se informa es la del var(, donde empieza la referencia");
    }

    /// <summary>
    /// El otro punto ciego: un <c>--nombre:</c> escrito en un comentario contaba
    /// como declaración, y un <c>var(--x)</c> en un comentario como uso. En los
    /// <c>.css</c> ya no. La última aserción es el control de que la diferencia la
    /// pone el comentario y no otra cosa.
    /// </summary>
    [Fact]
    public void Lo_escrito_en_un_comentario_css_ni_declara_ni_usa()
    {
        const string css = "/* --solo-en-comentario: 1px; antes era var(--token-retirado) */\n"
            + ".x { color: var(--solo-en-comentario); }";

        Declaradas([("sintetico.css", css)]).Should().NotContain("--solo-en-comentario",
            "un comentario no declara nada: si contase, esta variable saldría como existente sin existir");
        ReferenciasEn("sintetico.css", css).Select(r => r.Variable).Should().Equal(["--solo-en-comentario"],
            "var(--token-retirado) está dentro del comentario: no es un uso");
        ReferenciasEn("sintetico.css", css).Single().Linea.Should().Be(2,
            "quitar el comentario no puede desplazar la línea que se informa");

        Declaradas([("sintetico.css", "--solo-en-comentario: 1px;\n")]).Should().Contain("--solo-en-comentario",
            "fuera del comentario la misma línea sí es una declaración");
    }

    /// <summary>
    /// Declaraciones <c>--nombre:</c> en cualquier fuente, más las que JavaScript
    /// fija con <c>setProperty('--nombre', …)</c>, que no llevan dos puntos. En los
    /// <c>.css</c>, sin contar lo que esté dentro de un comentario.
    /// </summary>
    private static HashSet<string> Declaradas(IEnumerable<(string Ruta, string Contenido)> ficheros)
    {
        var declaradas = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (ruta, contenido) in ficheros)
        {
            var texto = SinComentariosCss(ruta, contenido);

            foreach (Match m in PatronDeclaracion.Matches(texto))
                declaradas.Add(m.Groups[1].Value);

            foreach (Match m in PatronSetProperty.Matches(texto))
                declaradas.Add(m.Groups[1].Value);
        }

        return declaradas;
    }

    /// <summary>
    /// Sobre el fichero entero, no línea a línea: un <c>var(</c> puede partirse en
    /// varias líneas. La línea se calcula después, a partir de la posición.
    /// </summary>
    private static IEnumerable<Referencia> ReferenciasEn(string ruta, string contenido)
    {
        var texto = SinComentariosCss(ruta, contenido);

        foreach (Match m in PatronReferencia.Matches(texto))
            yield return new Referencia(m.Groups[1].Value, ruta, LineaDe(texto, m.Index), m.Groups[2].Success);
    }

    /// <summary>
    /// En un <c>.css</c>, cada comentario <c>/* */</c> se sustituye por espacios del
    /// mismo largo conservando los saltos de línea: así las posiciones —y las
    /// líneas que se calculan con ellas— siguen siendo las del fichero real. En el
    /// resto de fuentes no se toca, por lo que explica el contrato de la clase.
    /// </summary>
    private static string SinComentariosCss(string ruta, string contenido) =>
        ruta.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
            ? ComentarioCss.Replace(contenido, m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()))
            : contenido;

    private static int LineaDe(string texto, int posicion) => texto[..posicion].Count(c => c == '\n') + 1;

    private static List<(string Ruta, string Contenido)> Ficheros()
    {
        var raiz = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web");
        if (!Directory.Exists(raiz)) return [];

        string[] extensiones = [".css", ".razor", ".cs", ".js"];
        var separador = Path.DirectorySeparatorChar;

        return Directory
            .EnumerateFiles(raiz, "*", SearchOption.AllDirectories)
            .Where(f => extensiones.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{separador}obj{separador}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{separador}bin{separador}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{separador}wwwroot{separador}lib{separador}", StringComparison.Ordinal))
            .Select(f => (Ruta: f, Contenido: File.ReadAllText(f)))
            .ToList();
    }

    private static string Relativa(string ruta)
    {
        var raiz = RaizDelRepositorio();
        return ruta.StartsWith(raiz, StringComparison.OrdinalIgnoreCase) ? ruta[raiz.Length..].TrimStart('\\', '/') : ruta;
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}

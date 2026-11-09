using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit.Abstractions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Issue #2001: el registro de lecturas en Sesión Privilegiada solo es duradero en autocommit. La
/// guarda contra una transacción abierta está en RegistroLecturaSesionPrivilegiada, que solo ve la
/// transacción de EF (<c>Database.CurrentTransaction</c>) y la ambiente
/// (<c>Transaction.Current</c>). Una transacción abierta por SQL crudo, o con BeginTransaction
/// directamente sobre la conexión sin pasar por EF, no la ve ni el servicio ni la función de
/// PostgreSQL (con el protocolo extendido, statement_timestamp() no coincide con
/// transaction_timestamp() ni en autocommit, así que la función no puede distinguirla).
///
/// Este trinquete cierra esas dos vías en <c>src</c>:
/// <list type="bullet">
/// <item>ninguna cadena (literal, interpolada o cruda) contiene en código SQL una sentencia
/// <c>BEGIN</c> o <c>START TRANSACTION</c>; mira todas las cadenas, no solo las que se pasan en la
/// misma línea a ExecuteSqlRaw, ExecuteSqlInterpolated, SqlQuery o CommandText, para que también
/// cuente una constante que se pasa después; y las mira en Debug y en Release, para que tampoco
/// se escape la que vive bajo <c>#if DEBUG</c> o bajo su <c>#else</c> (y también sin símbolos, como
/// antes);</item>
/// <item>BeginTransaction y BeginTransactionAsync solo resuelven, según el modelo semántico de Roslyn
/// sobre el proyecto compilado en memoria, a <c>DatabaseFacade</c> de EF o a sus extensiones
/// relacionales (que fijan <c>CurrentTransaction</c>), y EnlistTransaction no se llama, salvo la lista
/// cerrada; no se mira el nombre del receptor, que una variable puede imitar;</item>
/// <item>en los <c>.razor</c>, fuera de comentarios, ni esos métodos ni cadenas (también crudas y
/// multilínea) con SQL de transacción.</item>
/// </list>
/// </summary>
public class SinTransaccionesFueraDeEfTests(ITestOutputHelper salida)
{
    /// <summary>
    /// Cadenas que pueden abrir una transacción por SQL crudo, como «ruta relativa a src:línea».
    /// Vacía: medido al crearla, ninguna lo hace.
    /// </summary>
    private static readonly HashSet<string> CadenasPermitidas = [];

    /// <summary>
    /// Ficheros que pueden abrir una transacción sobre la conexión sin EF, con su motivo. Medido al
    /// crearla: solo este uso en src.
    /// </summary>
    private static readonly Dictionary<string, string> ConexionesPermitidas = new()
    {
        ["CaeManager.Infrastructure/Persistence/Migraciones/TransicionLineaBaseCompactada.cs"] =
            "Transición de la línea base al arrancar: su propia NpgsqlConnection con la cadena del " +
            "propietario de las migraciones, sin DbContext ni Sesión Privilegiada, para reescribir " +
            "__EFMigrationsHistory bajo un candado consultivo.",
    };

    private static readonly HashSet<string> MetodosDeTransaccion = ["BeginTransaction", "BeginTransactionAsync", "EnlistTransaction"];

    [Fact]
    public void Ninguna_cadena_de_src_abre_una_transaccion_por_SQL_crudo()
    {
        var cadenasVistas = 0;
        var infractoras = new List<string>();
        foreach (var (relativa, fuente) in FicherosDeSrc())
        {
            foreach (var (linea, texto) in Cadenas(fuente))
            {
                cadenasVistas++;
                var ubicacion = $"{relativa}:{linea}";
                if (AbreTransaccion(texto) && !CadenasPermitidas.Contains(ubicacion))
                    infractoras.Add(ubicacion);
            }
        }

        cadenasVistas.Should().BeGreaterThan(1000, "el instrumento tiene que ver las cadenas de src");
        infractoras.Should().BeEmpty(
            "una transacción abierta por SQL crudo no la ve la guarda del registro de lecturas en Sesión " +
            "Privilegiada; abre la transacción con Database.BeginTransaction de EF");
    }

    [Fact]
    public void Nadie_en_src_abre_una_transaccion_sobre_la_conexion_sin_pasar_por_EF()
    {
        var src = Path.Combine(BuscarRaizDelRepositorio(), "src");
        var ficheros = FicherosDeSrc().ToList();
        var infractoras = new SortedSet<string>(StringComparer.Ordinal);
        var permitidasUsadas = new HashSet<string>();

        // Cada configuración del SDK por separado: lo que está bajo #if DEBUG solo existe en una y lo
        // que está bajo #else en la otra. El filtro y la compilación parsean con las mismas opciones.
        foreach (var (configuracion, opciones) in Configuraciones)
        {
            var deEf = 0;

            // Solo se compilan los proyectos con algún fichero que nombre uno de los métodos (en
            // sintaxis, no en comentarios); se compila el proyecto entero para que sus propios
            // tipos resuelvan.
            var porProyecto = ficheros
                .Where(f => NombraMetodoDeTransaccion(CSharpSyntaxTree.ParseText(f.Fuente, opciones)))
                .GroupBy(f => ProyectoDe(Path.Combine(src, f.Relativa)))
                .ToList();
            porProyecto.Should().NotBeEmpty("src abre transacciones de EF, y el instrumento tiene que verlas");

            foreach (var proyecto in porProyecto)
            {
                var (compilacion, arboles) = CompilacionDelProyecto(proyecto.Key, opciones);
                var relevantes = proyecto.Select(f => Path.GetFullPath(Path.Combine(src, f.Relativa))).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var arbol in arboles.Where(a => relevantes.Contains(Path.GetFullPath(a.FilePath))))
                {
                    var relativa = Path.GetRelativePath(src, arbol.FilePath).Replace('\\', '/');
                    foreach (var llamada in LlamadasDeTransaccion(compilacion, arbol))
                    {
                        if (llamada.Tipo == TipoDeLlamada.DeEf)
                        {
                            deEf++;
                            continue;
                        }

                        if (llamada.Tipo == TipoDeLlamada.FueraDeEf && ConexionesPermitidas.ContainsKey(relativa))
                            permitidasUsadas.Add(relativa);
                        else
                            infractoras.Add($"{configuracion} {relativa}:{llamada.Linea} {llamada.Tipo} {llamada.Simbolo}");
                    }
                }
            }

            salida.WriteLine($"{configuracion}: proyectos compilados: {porProyecto.Count}; llamadas de EF: {deEf}");
            deEf.Should().BeGreaterThan(5, $"el instrumento tiene que resolver las transacciones de EF de src en {configuracion}");
        }

        salida.WriteLine($"Fuera de EF en la lista cerrada: {permitidasUsadas.Count}; infractoras: {infractoras.Count}");
        infractoras.Should().BeEmpty(
            "una transacción abierta sobre la conexión no fija Database.CurrentTransaction y la guarda del " +
            "registro de lecturas en Sesión Privilegiada no la ve; ábrela con Database.BeginTransaction de EF " +
            "(y una llamada que no resuelve tampoco se da por buena)");
        permitidasUsadas.Should().BeEquivalentTo(ConexionesPermitidas.Keys,
            "una excepción de la lista cerrada que ya no se usa se quita de la lista");
    }

    /// <summary>
    /// En los <c>.razor</c> de <c>src</c>, fuera de comentarios, no aparece BeginTransaction ni
    /// EnlistTransaction, y ninguna cadena entre comillas abre una transacción por SQL. Medido al
    /// crearla: ninguna aparición en los .razor de src.
    /// </summary>
    [Fact]
    public void Ningun_razor_de_src_abre_transacciones()
    {
        var src = Path.Combine(BuscarRaizDelRepositorio(), "src");
        var razor = Directory.EnumerateFiles(src, "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        razor.Should().HaveCountGreaterThan(100, "el instrumento tiene que recorrer los .razor de src");

        var infractoras = new List<string>();
        var cadenasVistas = 0;
        foreach (var fichero in razor)
        {
            var relativa = Path.GetRelativePath(src, fichero).Replace('\\', '/');
            var (nombres, cadenas) = TransaccionesEnRazor(File.ReadAllText(fichero));
            cadenasVistas += cadenas.Total;
            infractoras.AddRange(nombres.Concat(cadenas.Infractoras).Select(linea => $"{relativa}:{linea}"));
        }

        salida.WriteLine($".razor: {razor.Count}; cadenas vistas: {cadenasVistas}; infractoras: {infractoras.Count}");
        cadenasVistas.Should().BeGreaterThan(1000, "el instrumento tiene que ver las cadenas de los .razor");
        infractoras.Should().BeEmpty("un componente no abre transacciones: ni sobre la conexión ni por SQL crudo");
    }

    private static readonly Regex NombreDeTransaccionEnRazor = new(@"\b(BeginTransaction|BeginTransactionAsync|EnlistTransaction)\b", RegexOptions.Compiled);

    /// <summary>
    /// Una cadena de C# dentro de un .razor, con sus prefijos: primero la cruda, con cualquier número
    /// de <c>$</c> y de comillas y con la gramática de C#: o de una línea (abre y cierra en ella) o
    /// multilínea (tras las comillas de apertura solo hay espacios hasta el fin de línea, y las de
    /// cierre empiezan su línea tras espacios); cierra con exactamente las mismas comillas con que
    /// abre. Luego la verbatim y por último la normal, interpoladas o no. La cruda va primero para
    /// que sus comillas no se lean como una cadena vacía seguida de otra.
    /// </summary>
    private static readonly Regex CadenaEnRazor = new(
        @"\$*(?<q>""{3,})(?!"")(?:[^\r\n]*?(?<!"")\k<q>|[ \t]*\r?\n[\s\S]*?\n[ \t]*\k<q>)(?!"")" +
        @"|(?:\$*@|@\$+)""(?:[^""]|"""")*""" +
        @"|\$*""(?:[^""\\\r\n]|\\.)*""",
        RegexOptions.Compiled);

    /// <summary>
    /// La expresión de antes de reconocer las crudas: solo normales y verbatim. Un .razor no dice
    /// dónde acaba el marcado y empieza el C#, así que unas comillas triples del marcado pueden
    /// casar con otras y tragarse un bloque <c>@code</c> entero; para que eso nunca oculte lo que
    /// ya se detectaba, sus infractoras se suman siempre a las de <see cref="CadenaEnRazor"/>.
    /// </summary>
    private static readonly Regex CadenaEnRazorAnterior = new(@"@""(?:[^""]|"""")*""|""(?:[^""\\\r\n]|\\.)*""", RegexOptions.Compiled);

    private static int LineaDe(string texto, int indice) => texto.AsSpan(0, indice).Count('\n') + 1;

    /// <summary>
    /// Las líneas de un .razor, sin sus comentarios, que nombran un método de transacción, y las de
    /// las cadenas (normales, verbatim, interpoladas o crudas, también multilínea; la línea es la de
    /// su apertura) que abren una transacción por SQL. El valor de cada cadena lo da Roslyn, que
    /// resuelve escapes, huecos y la sangría de las crudas; además se examina su texto decodificado
    /// entre los delimitadores, por si Roslyn no la reconoce. Las infractoras son la unión con las
    /// de <see cref="InfractorasEnRazorAnteriores"/>: detecta siempre un superconjunto de lo que
    /// detectaba la expresión anterior.
    /// </summary>
    internal static (List<int> Nombres, (int Total, List<int> Infractoras) Cadenas) TransaccionesEnRazor(string fuente)
    {
        var limpio = LimpiadorDeComentarios.Quitar(fuente, razor: true);

        var nombres = NombreDeTransaccionEnRazor.Matches(limpio).Select(m => LineaDe(limpio, m.Index)).ToList();
        var cadenas = CadenaEnRazor.Matches(limpio);
        var infractoras = cadenas
            .Where(m => ValoresDeCadenaEnRazor(m.Value).Any(AbreTransaccion))
            .Select(m => LineaDe(limpio, m.Index))
            .Concat(InfractorasEnRazorAnteriores(limpio))
            .Distinct()
            .Order()
            .ToList();
        return (nombres, (cadenas.Count, infractoras));
    }

    /// <summary>
    /// Lo que detectaba el analizador anterior sobre un .razor ya sin comentarios: cadenas normales
    /// y verbatim de una expresión que no conoce las crudas, decodificadas como entonces.
    /// </summary>
    internal static IEnumerable<int> InfractorasEnRazorAnteriores(string limpio) =>
        CadenaEnRazorAnterior.Matches(limpio)
            .Where(m => AbreTransaccion(m.Value.StartsWith('@')
                ? m.Value[2..^1].Replace("\"\"", "\"")
                : Regex.Unescape(m.Value[1..^1])))
            .Select(m => LineaDe(limpio, m.Index));

    private static readonly CSharpParseOptions OpcionesDeLiteral = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    /// <summary>El valor de una cadena de un .razor según Roslyn, y su texto decodificado entre los delimitadores.</summary>
    private static IEnumerable<string> ValoresDeCadenaEnRazor(string literal)
    {
        var raiz = CSharpSyntaxTree.ParseText($"class C {{ object x = {literal}\n; }}", OpcionesDeLiteral).GetRoot();
        foreach (var (_, _, texto) in CadenasDe(raiz))
            yield return texto;
        yield return TextoDecodificado(literal);
    }

    /// <summary>
    /// El texto de una cadena entre sus delimitadores, sin Roslyn: la cruda, tal cual entre sus
    /// comillas (no tiene escapes); la verbatim, con sus comillas dobles reducidas a una; la normal,
    /// con sus escapes resueltos. Los huecos de una interpolada quedan como texto.
    /// </summary>
    private static string TextoDecodificado(string literal)
    {
        var sinPrefijo = literal.TrimStart('$');
        if (sinPrefijo.StartsWith("\"\"\"", StringComparison.Ordinal))
        {
            var comillas = sinPrefijo.Length - sinPrefijo.TrimStart('"').Length;
            return sinPrefijo[comillas..^comillas];
        }

        if (sinPrefijo.StartsWith('@') || literal.StartsWith('@'))
            return sinPrefijo.TrimStart('@').TrimStart('$')[1..^1].Replace("\"\"", "\"");

        return Regex.Unescape(sinPrefijo[1..^1]);
    }

    private static IEnumerable<(string Relativa, string Fuente)> FicherosDeSrc()
    {
        var src = Path.Combine(BuscarRaizDelRepositorio(), "src");
        var separador = Path.DirectorySeparatorChar;
        var ficheros = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separador}obj{separador}", StringComparison.Ordinal)
                        && !f.Contains($"{separador}bin{separador}", StringComparison.Ordinal))
            .ToList();
        ficheros.Should().HaveCountGreaterThan(500, "el instrumento tiene que recorrer src entero");
        return ficheros.Select(f => (Path.GetRelativePath(src, f).Replace('\\', '/'), File.ReadAllText(f)));
    }

    /// <summary>
    /// Las cadenas de un fichero C#, con su línea; los huecos de una interpolada cuentan como un valor.
    /// Se parsea con las opciones de cada configuración (las mismas del análisis semántico), para que
    /// una cadena bajo <c>#if DEBUG</c> o bajo su <c>#else</c> también se examine; cada cadena sale una
    /// sola vez aunque exista en varias. Además se parsea sin símbolos, como antes, para no perder la
    /// rama que solo existe sin ellos (<c>#if !TRACE</c>, <c>#elif !NET</c>).
    /// </summary>
    internal static IEnumerable<(int Linea, string Texto)> Cadenas(string fuente)
    {
        var vistas = new HashSet<int>();
        foreach (var opciones in Configuraciones.Select(c => c.Opciones).Append(CSharpParseOptions.Default))
        {
            foreach (var cadena in CadenasDe(CSharpSyntaxTree.ParseText(fuente, opciones).GetRoot()))
            {
                if (vistas.Add(cadena.Inicio))
                    yield return (cadena.Linea, cadena.Texto);
            }
        }
    }

    private static IEnumerable<(int Inicio, int Linea, string Texto)> CadenasDe(SyntaxNode raiz)
    {
        foreach (var nodo in raiz.DescendantNodes())
        {
            string? texto = nodo switch
            {
                LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression)
                                               || l.IsKind(SyntaxKind.Utf8StringLiteralExpression) => l.Token.ValueText,
                InterpolatedStringExpressionSyntax i => string.Concat(i.Contents.Select(c => c switch
                {
                    InterpolatedStringTextSyntax t => t.TextToken.ValueText,
                    _ => " valor ",
                })),
                _ => null,
            };
            if (texto is not null)
                yield return (nodo.SpanStart, nodo.GetLocation().GetLineSpan().StartLinePosition.Line + 1, texto);
        }
    }

    internal enum TipoDeLlamada { DeEf, FueraDeEf, SinResolver }

    /// <summary>Los tipos de EF cuyo BeginTransaction fija <c>Database.CurrentTransaction</c>.</summary>
    private static readonly HashSet<string> TiposDeTransaccionDeEf =
    [
        "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade",
        "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions",
    ];

    private static bool NombraMetodoDeTransaccion(SyntaxTree arbol) =>
        arbol.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>().Any(n => MetodosDeTransaccion.Contains(n.Identifier.ValueText));

    /// <summary>
    /// Cada nombre BeginTransaction, BeginTransactionAsync o EnlistTransaction de un árbol (invocación,
    /// grupo de métodos o llamada estática), clasificado por el SÍMBOLO que resuelve el modelo
    /// semántico y no por el nombre del receptor: es de EF solo si el método pertenece a
    /// <c>DatabaseFacade</c> o a las extensiones relacionales de EF (y no es EnlistTransaction). Un
    /// nombre que no resuelve a un único método cuenta como SinResolver, que tampoco se da por bueno.
    /// </summary>
    internal static IEnumerable<(int Linea, TipoDeLlamada Tipo, string Simbolo)> LlamadasDeTransaccion(
        CSharpCompilation compilacion, SyntaxTree arbol)
    {
        var modelo = compilacion.GetSemanticModel(arbol);
        foreach (var nombre in arbol.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (!MetodosDeTransaccion.Contains(nombre.Identifier.ValueText))
                continue;

            var linea = nombre.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (modelo.GetSymbolInfo(nombre).Symbol is not IMethodSymbol metodo)
            {
                var candidatos = string.Join(", ", modelo.GetSymbolInfo(nombre).CandidateSymbols.Select(s => s.ToDisplayString()));
                yield return (linea, TipoDeLlamada.SinResolver, candidatos);
                continue;
            }

            var declarado = metodo.ReducedFrom ?? metodo;
            var tipo = declarado.ContainingType?.ToDisplayString() ?? "";
            yield return (linea,
                TiposDeTransaccionDeEf.Contains(tipo) && declarado.Name != "EnlistTransaction" ? TipoDeLlamada.DeEf : TipoDeLlamada.FueraDeEf,
                declarado.ToDisplayString());
        }
    }

    /// <summary>
    /// Las dos configuraciones: Debug (DEBUG y TRACE) y Release (TRACE, y RELEASE por si algún fuente
    /// lo usa), sobre los símbolos de net10.0. Se analizan las dos para que nada se esconda tras un
    /// <c>#if DEBUG</c> ni tras su <c>#else</c>.
    /// </summary>
    internal static readonly IReadOnlyList<(string Nombre, CSharpParseOptions Opciones)> Configuraciones =
    [
        ("Debug", OpcionesDeAnalisis("DEBUG", "TRACE")),
        ("Release", OpcionesDeAnalisis("RELEASE", "TRACE")),
    ];

    private static CSharpParseOptions OpcionesDeAnalisis(params string[] simbolosDeConfiguracion) => CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.Preview)
        .WithPreprocessorSymbols((string[])
        [
            "NET", "NETCOREAPP", "NET10_0",
            .. Enumerable.Range(5, 6).Select(v => $"NET{v}_0_OR_GREATER"),
            "NETCOREAPP1_0_OR_GREATER", "NETCOREAPP1_1_OR_GREATER", "NETCOREAPP2_0_OR_GREATER",
            "NETCOREAPP2_1_OR_GREATER", "NETCOREAPP2_2_OR_GREATER", "NETCOREAPP3_0_OR_GREATER", "NETCOREAPP3_1_OR_GREATER",
            .. simbolosDeConfiguracion,
        ]);

    /// <summary>
    /// Los usings implícitos de Microsoft.NET.Sdk (ImplicitUsings=enable en Directory.Build.props) más los
    /// <c>&lt;Using Include&gt;</c> del propio .csproj.
    /// </summary>
    private static SyntaxTree UsingsGlobales(string csproj, CSharpParseOptions opciones)
    {
        var espacios = new List<string>
        {
            "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http",
            "System.Threading", "System.Threading.Tasks",
        };
        espacios.AddRange(Regex.Matches(File.ReadAllText(csproj), @"<Using\s+Include=""(?<n>[^""]+)""\s*/>")
            .Select(m => m.Groups["n"].Value));
        return CSharpSyntaxTree.ParseText(
            string.Concat(espacios.Distinct().Select(e => $"global using global::{e};\n")), opciones, path: "UsingsGlobales.g.cs");
    }

    /// <summary>
    /// Referencias de metadatos: los ensamblados de la plataforma (TRUSTED_PLATFORM_ASSEMBLIES) y los
    /// de la carpeta de salida de este proyecto de tests, que referencia los proyectos de src y, con
    /// ellos, EF Core y Npgsql. Sin duplicados por nombre y solo ficheros con metadatos .NET.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<(string Nombre, MetadataReference Referencia)>> Referencias = new(() =>
    {
        var plataforma = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var locales = Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll");
        return plataforma.Concat(locales)
            .Where(TieneMetadatos)
            .GroupBy(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key, (MetadataReference)MetadataReference.CreateFromFile(g.First())))
            .ToList();
    });

    private static bool TieneMetadatos(string fichero)
    {
        try
        {
            using var lector = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(fichero));
            return lector.HasMetadata;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    private static string ProyectoDe(string fichero)
    {
        var directorio = new DirectoryInfo(Path.GetDirectoryName(fichero)!);
        while (directorio is not null && directorio.GetFiles("*.csproj").Length == 0)
            directorio = directorio.Parent;
        directorio.Should().NotBeNull($"{fichero} tiene que pertenecer a un proyecto");
        return directorio!.GetFiles("*.csproj").Single().FullName;
    }

    /// <summary>
    /// Compila en memoria todos los .cs de un proyecto de src (sin obj ni bin) con sus usings
    /// globales, contra las referencias de metadatos menos el ensamblado del propio proyecto, para que
    /// sus tipos se resuelvan desde el fuente. Los generadores de código no corren: lo que solo
    /// existe generado no resuelve, y una llamada de transacción que dependa de ello sale como
    /// SinResolver y hace fallar el trinquete, nunca pasar.
    /// </summary>
    private static (CSharpCompilation Compilacion, IReadOnlyList<SyntaxTree> Arboles) CompilacionDelProyecto(
        string csproj, CSharpParseOptions opciones)
    {
        var directorio = Path.GetDirectoryName(csproj)!;
        var separador = Path.DirectorySeparatorChar;
        var arboles = Directory.EnumerateFiles(directorio, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separador}obj{separador}", StringComparison.Ordinal)
                        && !f.Contains($"{separador}bin{separador}", StringComparison.Ordinal))
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), opciones, path: f))
            .ToList();
        var ensamblado = Path.GetFileNameWithoutExtension(csproj);
        var compilacion = CSharpCompilation.Create(
            ensamblado + ".AnalisisDeTransacciones",
            [.. arboles, UsingsGlobales(csproj, opciones)],
            Referencias.Value.Where(r => !string.Equals(r.Nombre, ensamblado, StringComparison.OrdinalIgnoreCase)).Select(r => r.Referencia),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: true));
        return (compilacion, arboles);
    }

    /// <summary>Compilación de un fuente suelto contra las mismas referencias, para los casos del detector.</summary>
    internal static (CSharpCompilation Compilacion, SyntaxTree Arbol) CompilacionDeEjemplo(string fuente, CSharpParseOptions? opciones = null)
    {
        var arbol = CSharpSyntaxTree.ParseText(fuente, opciones ?? Configuraciones[0].Opciones, path: "Ejemplo.cs");
        var compilacion = CSharpCompilation.Create(
            "Ejemplo",
            [arbol],
            Referencias.Value.Select(r => r.Referencia),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        return (compilacion, arbol);
    }

    private static readonly Regex InicioDeTransaccion = new(
        @"^(BEGIN(\s+(TRANSACTION|WORK|ISOLATION|READ|NOT|DEFERRABLE)\b.*)?|START\s+TRANSACTION\b.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// Si alguna sentencia del texto es <c>BEGIN</c> o <c>START TRANSACTION</c> en código, según el
    /// escáner léxico compartido: fuera de comentarios, literales, identificadores entre comillas y
    /// cadenas con dólar (los cuerpos <c>AS $$ … $$</c> de plpgsql, donde <c>BEGIN</c> abre un bloque
    /// y no una transacción, son cadenas con dólar).
    /// </summary>
    internal static bool AbreTransaccion(string sql) =>
        EscanerSql.SoloCodigo(sql).Split(';').Any(sentencia => InicioDeTransaccion.IsMatch(sentencia.Trim()));

    [Theory]
    [InlineData("BEGIN;")]
    [InlineData("begin")]
    [InlineData("  Begin ; SELECT app_registrar_actividad_soporte_por_sesion(1, 2, 3, 4)")]
    [InlineData("BEGIN TRANSACTION ISOLATION LEVEL READ COMMITTED;")]
    [InlineData("BEGIN WORK")]
    [InlineData("begin isolation level serializable")]
    [InlineData("START TRANSACTION;")]
    [InlineData("start transaction read only")]
    [InlineData("SET x.y = '1'; BEGIN; SELECT 1;")]
    [InlineData("SELECT 1; -- comentario\nBEGIN;")]
    [InlineData("SELECT 1; -- comentario\rBEGIN;")]
    [InlineData("DO $$ BEGIN PERFORM 1; END $$; BEGIN;")]
    [InlineData("SELECT '$$'; BEGIN;")]
    [InlineData("SELECT \"a$$b\"; BEGIN;")]
    [InlineData("SELECT 1 /* $$ */; BEGIN;")]
    public void El_detector_ve_una_transaccion_abierta_por_SQL(string sql) =>
        AbreTransaccion(sql).Should().BeTrue();

    [Theory]
    [InlineData("DO $$ BEGIN PERFORM 1; END $$;")]
    [InlineData("CREATE FUNCTION f() RETURNS int AS $f$ DECLARE x int; BEGIN RETURN 1; END $f$ LANGUAGE plpgsql;")]
    [InlineData("CREATE FUNCTION f() RETURNS int LANGUAGE plpgsql AS $$\nDECLARE\n  v int;\nBEGIN\n  RETURN 1;\nEND;\n$$;")]
    [InlineData("SELECT $x$ BEGIN; $x$;")]
    [InlineData("SELECT $á$; BEGIN; $á$;")]
    [InlineData("SELECT $año_1$; BEGIN; $año_1$;")]
    [InlineData("SELECT 'BEGIN;'")]
    [InlineData("SELECT E'\\'; BEGIN; '")]
    [InlineData("SELECT \"BEGIN\"")]
    [InlineData("SELECT 1; \"BEGIN\";")]
    [InlineData("-- BEGIN;\nSELECT 1")]
    [InlineData("/* BEGIN; /* START TRANSACTION; */ */ SELECT 1")]
    [InlineData("-----BEGIN CERTIFICATE-----")]
    [InlineData("Begin import")]
    [InlineData("SELECT set_config('app.contexto', @contexto, false);")]
    [InlineData("COMMIT;")]
    public void El_detector_no_confunde_bloques_plpgsql_comentarios_literales_ni_identificadores(string sql) =>
        AbreTransaccion(sql).Should().BeFalse();

    [Fact]
    public void El_detector_ve_las_cadenas_de_cualquier_forma_en_el_codigo()
    {
        const string fuente = """"
            class C
            {
                const string Inicio = "begin";
                void F(DbContext db, DbCommand cmd, int x)
                {
                    db.Database.ExecuteSqlRaw("BEGIN;");
                    db.Database.ExecuteSqlInterpolated($"START TRANSACTION; SELECT {x}");
                    cmd.CommandText = $$"""
                        BEGIN ISOLATION LEVEL SERIALIZABLE;
                        """;
                    db.Database.SqlQuery<string>($"SELECT f({x})");
                }
            }
            """";

        var cadenas = Cadenas(fuente).ToList();
        cadenas.Where(c => AbreTransaccion(c.Texto)).Select(c => c.Linea).Should().Equal(3, 6, 7, 8);
        cadenas.Should().Contain(c => c.Linea == 11 && !AbreTransaccion(c.Texto), "una consulta sin BEGIN no cuenta");
    }

    [Fact]
    public void El_detector_distingue_la_transaccion_de_EF_de_la_abierta_sobre_la_conexion()
    {
        const string fuente = """
            using System;
            using System.Data;
            using System.Data.Common;
            using System.Threading.Tasks;
            using System.Transactions;
            using Microsoft.EntityFrameworkCore;
            using Npgsql;
            using IsolationLevel = System.Data.IsolationLevel;

            class C
            {
                async Task F(DbContext dbContext, NpgsqlConnection npgsql, IDbConnection idb, Transaction t)
                {
                    await using var a = await dbContext.Database.BeginTransactionAsync();
                    using var b = dbContext.Database.BeginTransaction(IsolationLevel.ReadCommitted);
                    using var c = RelationalDatabaseFacadeExtensions.BeginTransaction(dbContext.Database, IsolationLevel.Serializable);
                    var fachada = dbContext.Database; using var d = fachada.BeginTransaction();
                    var Database = dbContext.Database.GetDbConnection(); await using var e = await Database.BeginTransactionAsync();
                    using var f = dbContext.Database.GetDbConnection().BeginTransaction();
                    await using var g = await npgsql.BeginTransactionAsync();
                    using var h = idb.BeginTransaction();
                    using var i = npgsql?.BeginTransaction();
                    npgsql.EnlistTransaction(t);
                    dbContext.Database.EnlistTransaction(t);
                    Func<IsolationLevel, NpgsqlTransaction> j = npgsql.BeginTransaction;
                    Falso.Database.BeginTransaction();
                    desconocido.BeginTransaction();
                }
            }

            static class Falso
            {
                public static class Database
                {
                    public static void BeginTransaction() { }
                }
            }
            """;

        var (compilacion, arbol) = CompilacionDeEjemplo(fuente);
        var llamadas = LlamadasDeTransaccion(compilacion, arbol).ToList();

        llamadas.Where(l => l.Tipo == TipoDeLlamada.DeEf).Select(l => l.Linea).Should().Equal(
            [14, 15, 16, 17], "la fachada Database de EF, también por variable o con la extensión llamada como estática");
        llamadas.Where(l => l.Tipo == TipoDeLlamada.FueraDeEf).Select(l => l.Linea).Should().Equal(
            [18, 19, 20, 21, 22, 23, 24, 25, 26],
            "una variable llamada Database que es la conexión, la conexión de EF, Npgsql, IDbConnection, el acceso " +
            "condicional, EnlistTransaction (también el de EF), el grupo de métodos y un tipo propio llamado Database");
        llamadas.Where(l => l.Tipo == TipoDeLlamada.SinResolver).Select(l => l.Linea).Should().Equal(
            [27], "lo que no resuelve no se da por bueno");
    }

    /// <summary>
    /// Una transacción sobre la conexión escondida tras <c>#if DEBUG</c> (o tras su <c>#else</c>) en un
    /// fichero que no nombra esos métodos en ningún otro sitio: el filtro la ve en la configuración
    /// donde existe y la compilación de esa configuración la clasifica como fuera de EF.
    /// </summary>
    [Fact]
    public void El_detector_ve_una_transaccion_escondida_tras_if_DEBUG_o_su_else()
    {
        const string fuente = """
            using System.Data.Common;
            class C
            {
                void F(DbConnection conexion)
                {
                    #if DEBUG
                    conexion.BeginTransaction();
                    #else
                    conexion.BeginTransaction(System.Data.IsolationLevel.Serializable);
                    #endif
                }
            }
            """;

        var vistas = new List<(string Configuracion, int Linea, TipoDeLlamada Tipo)>();
        foreach (var (configuracion, opciones) in Configuraciones)
        {
            var (compilacion, arbol) = CompilacionDeEjemplo(fuente, opciones);
            NombraMetodoDeTransaccion(arbol).Should().BeTrue($"el filtro de {configuracion} tiene que ver el fichero");
            vistas.AddRange(LlamadasDeTransaccion(compilacion, arbol).Select(l => (configuracion, l.Linea, l.Tipo)));
        }

        vistas.Should().Equal(("Debug", 7, TipoDeLlamada.FueraDeEf), ("Release", 9, TipoDeLlamada.FueraDeEf));

        // Sin el símbolo DEBUG (lo que hacía el filtro antes), la rama de Debug no existe para el análisis.
        var sinSimbolos = CSharpSyntaxTree.ParseText(fuente.Replace("#else", "#endif\n#if NUNCA"));
        NombraMetodoDeTransaccion(sinSimbolos).Should().BeFalse("el caso es sensible a los símbolos de preprocesador");
    }

    private const string RazorConMetodosYSql = """
            @page "/x"
            @* await Db.Database.GetDbConnection().BeginTransactionAsync(); "BEGIN;" *@
            <p title="Begin import">Begin</p>
            @code {
                // conexion.BeginTransaction(); "START TRANSACTION"
                /* conexion.EnlistTransaction(t); */
                private async Task F()
                {
                    await conexion.BeginTransactionAsync();
                    await Db.Database.ExecuteSqlRawAsync("BEGIN; SELECT 1");
                    await Db.Database.ExecuteSqlRawAsync(@"START TRANSACTION");
                    await Db.Database.ExecuteSqlRawAsync("SELECT 'BEGIN;'");
                }
            }
            """;

    [Fact]
    public void El_detector_de_razor_ve_metodos_y_SQL_de_transaccion_fuera_de_comentarios()
    {
        var (nombres, cadenas) = TransaccionesEnRazor(RazorConMetodosYSql);

        nombres.Should().Equal([9], "solo la llamada fuera de comentarios");
        cadenas.Infractoras.Should().Equal([10, 11], "las cadenas que abren transacción, normal y verbatim; no la de un literal SQL ni el marcado");
    }

    private const string RazorConCrudas = """"
            @page "/x"
            @code {
                private async Task F(int x)
                {
                    await Db.Database.ExecuteSqlRawAsync("""
                        BEGIN;
                        """);
                    await Db.Database.ExecuteSqlRawAsync($$"""
                        START TRANSACTION; SELECT {{x}}
                        """);
                    await Db.Database.ExecuteSqlRawAsync("""BEGIN ISOLATION LEVEL SERIALIZABLE""");
                    await Db.Database.ExecuteSqlRawAsync("""
                        SELECT "BEGIN;" FROM t;
                        DO $$ BEGIN PERFORM 1; END $$;
                        """);
                    await Db.Database.ExecuteSqlRawAsync(@"SELECT 1");
                }
            }
            """";

    /// <summary>
    /// Issue #2024: las cadenas crudas de C# 11 dentro de un .razor, también multilínea e
    /// interpoladas. La expresión anterior leía <c>"""</c> como una cadena vacía seguida de otra que
    /// no cerraba en la línea, así que no veía las líneas 5 y 8. Sí daba por cadena el
    /// <c>"BEGIN;"</c> de la línea 13, que es texto de la cruda abierta en la línea 12: ese falso
    /// positivo se conserva a propósito, porque las infractoras de la expresión anterior se suman
    /// siempre (ver <see cref="El_detector_de_razor_detecta_un_superconjunto_del_anterior"/>).
    /// </summary>
    [Fact]
    public void El_detector_de_razor_ve_las_cadenas_crudas_multilinea()
    {
        var (_, cadenas) = TransaccionesEnRazor(RazorConCrudas);

        cadenas.Infractoras.Should().Equal([5, 8, 11, 13],
            "las crudas que abren transacción, multilínea, interpolada y de una línea, y lo que ya veía la " +
            "expresión anterior; no la cruda cuyo BEGIN es un bloque plpgsql");
        cadenas.Total.Should().Be(6, "cada cruda es una sola cadena, más la del @page y la verbatim");
    }

    /// <summary>
    /// Unas comillas triples del marcado no abren una cruda que se trague el <c>@code</c> que hay
    /// entre ellas: la expresión anterior veía el <c>"BEGIN;"</c> de la línea 3 y la nueva también;
    /// y como tras <c>"""</c> viene <c>&lt;/p&gt;</c>, no es una apertura de cruda, así que la
    /// cruda multilínea de la línea 4 tampoco queda desemparejada.
    /// </summary>
    private const string RazorConComillasTriplesEnElMarcado = """"
            <p>"""</p>
            @code {
                void F() { Db.Database.ExecuteSqlRaw("BEGIN;"); }
                void G() { Db.Database.ExecuteSqlRaw("""
                    START TRANSACTION;
                    """); }
            }
            <p>"""</p>
            """";

    /// <summary>
    /// Lo mismo cuando las comillas del marcado sí cumplen la gramática de una cruda multilínea
    /// (nada tras las de apertura, las de cierre al empezar la línea): solo la unión con la
    /// expresión anterior lo cierra.
    /// </summary>
    private const string RazorConComillasTriplesQueParecenUnaCruda = """"
            <p>"""
            </p>
            @code {
                void F() { Db.Database.ExecuteSqlRaw("BEGIN;"); }
            }
            """</p>
            """";

    [Fact]
    public void Las_comillas_triples_del_marcado_no_ocultan_un_BEGIN_del_codigo()
    {
        TransaccionesEnRazor(RazorConComillasTriplesEnElMarcado).Cadenas.Infractoras.Should().Equal([3, 4]);
        TransaccionesEnRazor(RazorConComillasTriplesQueParecenUnaCruda).Cadenas.Infractoras.Should().Equal([4]);
    }

    /// <summary>
    /// El valor de una verbatim es <c>SELECT "x; BEGIN; y"</c>: el BEGIN es parte de un identificador
    /// entre comillas, no una sentencia. Examinar el texto sin reducir sus comillas dobles lo daba
    /// por transacción. (La forma <c>@$"…"</c> no va aquí: la expresión anterior ya la leía como dos
    /// cadenas normales y la unión conserva ese falso positivo.)
    /// </summary>
    [Fact]
    public void El_detector_de_razor_decodifica_las_comillas_dobles_de_una_verbatim()
    {
        const string fuente = """"
            @code {
                void F() { Db.Database.ExecuteSqlRaw(@"SELECT ""x; BEGIN; y"""); }
                void G() { Db.Database.ExecuteSqlRaw($@"SELECT ""x; BEGIN; y"""); }
            }
            """";

        var (_, cadenas) = TransaccionesEnRazor(fuente);

        cadenas.Total.Should().Be(2);
        cadenas.Infractoras.Should().BeEmpty("el BEGIN está dentro de un identificador entre comillas");
    }

    /// <summary>
    /// Cada caso con las líneas que detectaba el analizador de origin/main antes de esta PR, fijadas
    /// a mano: si alguien cambia la expresión anterior o su decodificación, la referencia no se mueve
    /// con el código.
    /// </summary>
    public static TheoryData<string, int[]> FuentesRazorDeLosCasos() => new()
    {
        { RazorConMetodosYSql, [10, 11] },
        { RazorConCrudas, [11, 13] },
        { RazorConComillasTriplesEnElMarcado, [3] },
        { RazorConComillasTriplesQueParecenUnaCruda, [4] },
    };

    /// <summary>
    /// La propiedad general: en cada caso, toda línea que detectaba el analizador anterior la detecta
    /// también el nuevo.
    /// </summary>
    [Theory]
    [MemberData(nameof(FuentesRazorDeLosCasos))]
    public void El_detector_de_razor_detecta_un_superconjunto_del_anterior(string fuente, int[] anteriores)
    {
        InfractorasEnRazorAnteriores(LimpiadorDeComentarios.Quitar(fuente, razor: true)).Should().Equal(
            anteriores, "la expresión anterior y su decodificación son la referencia y no cambian");
        TransaccionesEnRazor(fuente).Cadenas.Infractoras.Should().Contain(anteriores);
    }

    /// <summary>
    /// Issue #2025: una cadena bajo <c>#if DEBUG</c> solo existe con el símbolo DEBUG; la de su
    /// <c>#else</c>, solo sin él. Parseando sin símbolos (lo que hacía <see cref="Cadenas"/> antes) la
    /// de la línea 6 no se examinaba.
    /// </summary>
    [Fact]
    public void El_detector_ve_las_cadenas_escondidas_tras_if_DEBUG_o_su_else()
    {
        const string fuente = """
            class C
            {
                void F(System.Data.Common.DbCommand cmd)
                {
            #if DEBUG
                    cmd.CommandText = "BEGIN;";
                    cmd.CommandText = "SELECT 'BEGIN;'";
            #else
                    cmd.CommandText = "START TRANSACTION";
            #endif
                    cmd.CommandText = "SELECT 1";
                }
            }
            """;

        var cadenas = Cadenas(fuente).ToList();

        cadenas.Where(c => AbreTransaccion(c.Texto)).Select(c => c.Linea).Should().BeEquivalentTo(
            new[] { 6, 9 }, "la de #if DEBUG y la de su #else; no el literal SQL que contiene BEGIN");
        cadenas.Select(c => c.Linea).Should().BeEquivalentTo(
            new[] { 6, 7, 9, 11 }, "cada cadena una sola vez, aunque exista en las dos configuraciones");

        CadenasDe(CSharpSyntaxTree.ParseText(fuente).GetRoot()).Select(c => c.Linea).Should().NotContain(
            6, "el caso es sensible a los símbolos de preprocesador");
    }

    /// <summary>
    /// Una rama que solo existe sin símbolos (<c>#if !TRACE</c>, o un <c>#elif !NET</c>) no existe ni
    /// en Debug ni en Release, pero el análisis anterior, que parseaba sin símbolos, sí la veía; se
    /// sigue examinando para no perder nada de lo que ya se detectaba.
    /// </summary>
    [Fact]
    public void El_detector_ve_las_cadenas_que_solo_existen_sin_simbolos()
    {
        const string fuente = """
            class C
            {
                void F(System.Data.Common.DbCommand cmd)
                {
            #if DEBUG
                    cmd.CommandText = "SELECT 1";
            #elif !NET
                    cmd.CommandText = "BEGIN;";
            #endif
            #if !TRACE
                    cmd.CommandText = "START TRANSACTION";
            #endif
                }
            }
            """;

        Cadenas(fuente).Where(c => AbreTransaccion(c.Texto)).Select(c => c.Linea).Should().Equal(8, 11);
    }

    private static string BuscarRaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;
        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }
}

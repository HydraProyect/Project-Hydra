using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Migrations.PostgreSQL.Migrations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Un acceso medido a una de las dos tablas de Asignación (Asignación de Operación y Asignación de
/// Cartera): dónde está (<see cref="Lugar"/>, el fichero o el objeto SQL), en qué miembro, sobre qué
/// tabla, con qué forma y si el instrumento ve en él un filtro de Servicio.
/// </summary>
internal readonly record struct AccesoAAsignacion(
    string Lugar, string Miembro, string Tabla, string Forma, bool FiltraPorServicio, int Linea)
{
    /// <summary>La clave con la que la lista congelada recuerda el acceso crudo.</summary>
    public Ubicacion Clave => new(Lugar, Miembro.Length == 0 ? Tabla : $"{Miembro} · {Tabla}");
}

/// <summary>
/// Instrumento del paso M3' del ADR-013 (§ 4.6, condiciones 2 y 3): encuentra cada acceso a
/// <c>AsignacionesOperacion</c> y <c>AsignacionesCartera</c> y decide si nombra un filtro de
/// Servicio. Dos fuentes:
/// <list type="bullet">
/// <item><b>C#</b> de <c>src</c> salvo migraciones, por sintaxis (Roslyn): el <c>DbSet</c> por su
/// nombre, <c>Set&lt;AsignacionOperacion&gt;()</c> o <c>Set&lt;AsignacionCartera&gt;()</c> (también
/// por alias de <c>using</c>) y los literales con SQL que nombran la tabla. Los <c>.razor</c> se miran
/// por texto sin comentarios, y todo lo que casa en ellos cuenta como crudo.</item>
/// <item><b>SQL vigente</b> de las migraciones: el estado final de cada función
/// (<c>CREATE [OR REPLACE] FUNCTION</c> no eliminada después), de cada vista
/// (<c>CREATE [OR REPLACE] [MATERIALIZED] VIEW</c> no eliminada) y de cada política (las cláusulas
/// <c>USING</c> y <c>WITH CHECK</c> que quedan tras sus <c>ALTER POLICY</c>) cuyo cuerpo, definición o
/// expresión lee una de las dos tablas.</item>
/// </list>
///
/// <para>
/// <b>Qué cuenta como filtro de Servicio en C#.</b> En la cadena de la consulta que parte del acceso
/// (<see cref="ContextoDelAcceso"/>): una comparación <c>==</c> o <c>!=</c>, un patrón <c>is</c> o un
/// <c>Contains(…)</c> sobre la propiedad <c>.Servicio</c> de una <b>fila de esa consulta</b> (el
/// parámetro de una lambda pasada a una de sus llamadas, o una variable de rango de su consulta
/// integrada; no <c>Catalogo.Servicio</c> ni la fila de otra consulta anidada); un <b>predicado de
/// servicio</b> pasado a una de sus llamadas, que es un miembro sin acceso propio, de tipo
/// <c>Expression&lt;…&gt;</c> o <c>IQueryable&lt;…&gt;</c>, cuya declaración contiene esa comparación
/// (<c>.Where(OperacionDelServicioVigente(ahora))</c>), en el mismo tipo o cualificado por el nombre de
/// su tipo; o un <c>FromSql*</c> de la cadena cuyo SQL compara <c>"Servicio"</c>. En SQL: la
/// sentencia compara <c>"Servicio"</c>.
/// </para>
/// </summary>
internal static class AccesosAAsignaciones
{
    internal const string Operacion = "AsignacionesOperacion";
    internal const string Cartera = "AsignacionesCartera";

    private static readonly Dictionary<string, string> TablaDeEntidad = new(StringComparer.Ordinal)
    {
        ["AsignacionOperacion"] = Operacion,
        ["AsignacionCartera"] = Cartera,
    };

    private static readonly Regex TablaEnTexto = new(@"\b(?<tabla>AsignacionesOperacion|AsignacionesCartera)\b", RegexOptions.Compiled);

    /// <summary>En un <c>.razor</c>: el DbSet por su nombre o <c>Set&lt;Entidad&gt;</c>.</summary>
    private static readonly Regex AccesoEnRazor = new(
        @"\b(?<tabla>AsignacionesOperacion|AsignacionesCartera)\b|\bSet<\s*(?:[\w.]*\.)?(?<entidad>AsignacionOperacion|AsignacionCartera)\s*>",
        RegexOptions.Compiled);

    private static readonly Regex PalabraSql = new(
        @"\b(?:SELECT|UPDATE|DELETE|INSERT|FROM|JOIN|TRUNCATE|MERGE|ALTER|LOCK|COPY)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// <c>"Servicio"</c> comparado (<c>=</c>, <c>&lt;&gt;</c>, <c>!=</c>, <c>IN</c>, <c>IS</c>), no asignado por un
    /// <c>SET "Servicio" =</c> ni proyectado en un <c>RETURNING</c>.
    /// </summary>
    private static readonly Regex ServicioSql = new(
        @"(?<!\bSET\s+)""Servicio""\s*(?:=|<>|!=|\bIN\b|\bIS\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TablaSql = new(@"""(?<tabla>AsignacionesOperacion|AsignacionesCartera)""", RegexOptions.Compiled);

    // ------------------------------------------------------------------ C#

    /// <summary>Todos los accesos del C# de <c>src</c> (sin migraciones), filtrados o no.</summary>
    public static IReadOnlyList<AccesoAAsignacion> MedirSrc()
    {
        var fuentes = FuentesDeSrc.Archivos()
            .Where(a => a.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || a.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(FuentesDeSrc.Relativa, File.ReadAllText, StringComparer.Ordinal);
        return EnCSharp(fuentes);
    }

    /// <summary>
    /// El núcleo de <see cref="MedirSrc"/> sobre fuentes en memoria (ruta relativa → texto), para que
    /// el control positivo pueda alimentarlo con fuentes sintéticas.
    /// </summary>
    public static IReadOnlyList<AccesoAAsignacion> EnCSharp(IReadOnlyDictionary<string, string> fuentes)
    {
        var arboles = fuentes
            .Where(f => f.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && PuedeInteresar(f.Value))
            .ToDictionary(f => f.Key, f => CSharpSyntaxTree.ParseText(f.Value, path: f.Key).GetRoot(), StringComparer.Ordinal);

        var predicados = PredicadosDeServicio(arboles.Values);
        var resultado = new List<AccesoAAsignacion>();

        foreach (var (ruta, raiz) in arboles.OrderBy(a => a.Key, StringComparer.Ordinal))
            resultado.AddRange(AccesosDeArbol(ruta, raiz, predicados));

        foreach (var (ruta, texto) in fuentes
                     .Where(f => f.Key.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var lineas = LimpiadorDeComentarios.Quitar(texto, razor: true).Split('\n');
            for (var i = 0; i < lineas.Length; i++)
                foreach (Match m in AccesoEnRazor.Matches(lineas[i]))
                {
                    var tabla = m.Groups["tabla"].Success ? m.Groups["tabla"].Value : TablaDeEntidad[m.Groups["entidad"].Value];
                    resultado.Add(new AccesoAAsignacion(ruta, "(razor)", tabla, "razor", false, i + 1));
                }
        }

        return resultado;
    }

    private static bool PuedeInteresar(string texto) =>
        texto.Contains("Asignaciones", StringComparison.Ordinal)
        || texto.Contains("AsignacionOperacion", StringComparison.Ordinal)
        || texto.Contains("AsignacionCartera", StringComparison.Ordinal)
        || texto.Contains("Servicio", StringComparison.Ordinal);

    private static IEnumerable<AccesoAAsignacion> AccesosDeArbol(
        string ruta, SyntaxNode raiz, HashSet<(string Tipo, string Miembro)> predicados)
    {
        var alias = AliasDeEntidad(raiz);

        var accesos = Accesos(raiz, alias).ToList();
        var operaciones = accesos.Where(a => a.Tabla == Operacion && a.Forma != "SQL").Select(a => a.Nodo).ToHashSet();

        foreach (var (nodo, tabla, forma) in accesos)
        {
            var (contexto, espina) = ContextoDelAcceso(nodo);
            var tipo = NombreDeTipo(nodo);
            var filtra = forma == "SQL"
                ? ServicioSql.IsMatch(TextoDeLiteral(nodo))
                : AplicaUnPredicado(contexto, tipo, predicados, espina)
                  || FiltraSuSqlCrudo(espina)
                  || (tabla == Operacion
                      ? ComparaServicio(contexto, espina)
                      : NombraLaOperacionDeLaCartera(contexto)
                        && EspinaDeLaCartera(contexto, espina, operaciones) is var espinaCartera
                        && (ComparaServicio(contexto, espinaCartera) || AplicaUnPredicado(contexto, tipo, predicados, espinaCartera)));

            yield return new AccesoAAsignacion(
                ruta, NombreDeMiembro(nodo), tabla, forma, filtra,
                nodo.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
        }
    }

    /// <summary>Los alias de <c>using</c> que nombran una de las dos entidades: <c>using AO = …AsignacionOperacion;</c>.</summary>
    private static Dictionary<string, string> AliasDeEntidad(SyntaxNode raiz)
    {
        var alias = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var u in raiz.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            if (u.Alias is null || u.NamespaceOrType is not { } destino) continue;
            if (UltimoIdentificador(destino) is { } entidad && TablaDeEntidad.TryGetValue(entidad, out var tabla))
                alias[u.Alias.Name.Identifier.ValueText] = tabla;
        }

        return alias;
    }

    private static IEnumerable<(SyntaxNode Nodo, string Tabla, string Forma)> Accesos(
        SyntaxNode raiz, IReadOnlyDictionary<string, string> alias)
    {
        foreach (var nodo in raiz.DescendantNodes())
        {
            switch (nodo)
            {
                case GenericNameSyntax { Identifier.ValueText: "Set", TypeArgumentList.Arguments: [var argumento] }
                    when UltimoIdentificador(argumento) is { } entidad
                         && (TablaDeEntidad.TryGetValue(entidad, out var tabla) || alias.TryGetValue(entidad, out tabla)):
                    yield return (nodo, tabla, "Set<T>");
                    break;

                case IdentifierNameSyntax { Identifier.ValueText: Operacion or Cartera } nombre
                    when nombre.Parent is not UsingDirectiveSyntax:
                    yield return (nodo, nombre.Identifier.ValueText, "DbSet");
                    break;

                case ExpressionSyntax cadena
                    when cadena is InterpolatedStringExpressionSyntax || cadena.IsKind(SyntaxKind.StringLiteralExpression):
                    var texto = TextoDeLiteral(nodo);
                    if (!PalabraSql.IsMatch(texto)) break;
                    foreach (var t in TablaEnTexto.Matches(texto).Select(m => m.Groups["tabla"].Value))
                        yield return (nodo, t, "SQL");
                    break;
            }
        }
    }

    private static string TextoDeLiteral(SyntaxNode nodo) => nodo switch
    {
        LiteralExpressionSyntax l => l.Token.ValueText,
        InterpolatedStringExpressionSyntax i => string.Concat(
            i.Contents.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText)),
        _ => string.Empty,
    };

    private static string? UltimoIdentificador(SyntaxNode tipo) => tipo switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => null,
    };

    /// <summary>
    /// Dónde se busca el filtro: la <b>cadena de la consulta</b> que parte del acceso, no la sentencia
    /// entera (dos consultas en una misma sentencia no se prestan el filtro, y la cabecera de un
    /// <c>if</c> no hereda las comparaciones de su cuerpo). La cadena sube por <c>.Miembro</c>,
    /// <c>(…)</c>, <c>await</c>, conversiones, <c>?.</c>, el <c>from</c>/<c>join</c> de una consulta
    /// integrada y el paso como argumento a una llamada (<c>Ayuda(ctx.AsignacionesCartera, filtro)</c>).
    /// Si la cadena vive dentro de una lambda pasada como argumento o dentro de una consulta integrada,
    /// el contexto se amplía a la cadena exterior: la consulta interior se evalúa dentro del predicado de
    /// la exterior (<c>ops.Where(o =&gt; o.Servicio == s &amp;&amp; ctx.AsignacionesCartera.Any(c =&gt;
    /// c.AsignacionOperacionId == o.Id))</c>). Solo si la lambda es de <b>expresión</b>: una lambda con
    /// cuerpo de bloque (<c>puerta.EjecutarAsync(async () =&gt; { … })</c>) agrupa sentencias
    /// independientes, y el contexto se queda en la cadena de la sentencia, como en un método.
    ///
    /// <para>
    /// Devuelve también la <b>espina</b>: los nodos por los que ha subido la cadena (las llamadas
    /// <c>Where</c>, <c>Count</c>…, la consulta integrada) más los receptores de cada fuente
    /// <c>from</c>/<c>join</c> de una consulta integrada de la espina. Una comparación de Servicio solo
    /// cuenta si su variable es la fila de una lambda pasada a una llamada de la espina o una variable de
    /// rango de una consulta de la espina, y un predicado con nombre solo cuenta si se pasa a una llamada
    /// de la espina (o es la llamada que recibe el acceso): en
    /// <c>ops.Where(o =&gt; otros.Any(x =&gt; x.Servicio == s))</c> el Servicio es de <c>otros</c>.
    /// </para>
    /// </summary>
    private static (SyntaxNode Contexto, HashSet<SyntaxNode> Espina) ContextoDelAcceso(SyntaxNode acceso)
    {
        var espina = new HashSet<SyntaxNode>();
        var cadena = CadenaDeLaConsulta(acceso, espina);
        while (true)
        {
            var exterior = cadena.Ancestors().FirstOrDefault(a =>
                a is LambdaExpressionSyntax { Parent: ArgumentSyntax, Body: not BlockSyntax } or QueryExpressionSyntax
                    or LambdaExpressionSyntax { Body: BlockSyntax }
                    or MemberDeclarationSyntax or LocalFunctionStatementSyntax);
            if (exterior is null or MemberDeclarationSyntax or LocalFunctionStatementSyntax
                or LambdaExpressionSyntax { Body: BlockSyntax })
                break;
            cadena = CadenaDeLaConsulta(exterior, espina);
        }

        foreach (var consulta in espina.OfType<QueryExpressionSyntax>().ToList())
        {
            foreach (var clausula in consulta.DescendantNodes().Where(n => n is FromClauseSyntax or JoinClauseSyntax
                         && n.FirstAncestorOrSelf<QueryExpressionSyntax>() == consulta))
            {
                ExpressionSyntax? receptor = clausula switch
                {
                    FromClauseSyntax f => f.Expression,
                    JoinClauseSyntax j => j.InExpression,
                    _ => null,
                };
                while (receptor is not null)
                {
                    espina.Add(receptor);
                    receptor = receptor switch
                    {
                        InvocationExpressionSyntax i => i.Expression,
                        MemberAccessExpressionSyntax m => m.Expression,
                        ParenthesizedExpressionSyntax p => p.Expression,
                        AwaitExpressionSyntax a => a.Expression,
                        CastExpressionSyntax c => c.Expression,
                        ConditionalAccessExpressionSyntax c => c.Expression,
                        _ => null,
                    };
                }
            }
        }

        return (cadena, espina);
    }

    private static SyntaxNode CadenaDeLaConsulta(SyntaxNode desde, HashSet<SyntaxNode> espina)
    {
        var actual = desde;
        espina.Add(actual);
        while (actual.Parent is { } padre)
        {
            SyntaxNode? siguiente = padre switch
            {
                // El acceso empieza en el nombre (ctx.«AsignacionesOperacion», ctx.«Set<T>»), que es el
                // Name del acceso a miembro; desde ahí, la cadena sube por su receptor.
                MemberAccessExpressionSyntax ma when ma.Expression == actual || ma.Name == actual => ma,
                InvocationExpressionSyntax inv when inv.Expression == actual => inv,
                ConditionalAccessExpressionSyntax ca when ca.Expression == actual || ca.WhenNotNull == actual => ca,
                MemberBindingExpressionSyntax or ParenthesizedExpressionSyntax or AwaitExpressionSyntax
                    or CastExpressionSyntax or PostfixUnaryExpressionSyntax => padre,
                FromClauseSyntax or JoinClauseSyntax => padre.FirstAncestorOrSelf<QueryExpressionSyntax>(),
                ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax llamada } } => llamada,
                _ => null,
            };
            if (siguiente is null) break;
            actual = siguiente;
            espina.Add(actual);
        }

        return actual;
    }

    /// <summary>
    /// <c>Set&lt;AsignacionOperacion&gt;().FromSqlRaw("… WHERE \"Servicio\" = …")</c>: el SQL que la
    /// cadena pasa a un <c>FromSql*</c> compara el Servicio, así que la raíz <c>Set&lt;T&gt;</c> o el
    /// <c>DbSet</c> de esa cadena no es un acceso crudo aparte del literal (que ya se clasifica solo).
    /// </summary>
    private static bool FiltraSuSqlCrudo(HashSet<SyntaxNode> espina) =>
        espina.OfType<InvocationExpressionSyntax>().Any(i =>
            i.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var metodo }
            && metodo.StartsWith("FromSql", StringComparison.Ordinal)
            && i.ArgumentList.Arguments.Any(a =>
                (a.Expression is InterpolatedStringExpressionSyntax || a.Expression.IsKind(SyntaxKind.StringLiteralExpression))
                && ServicioSql.IsMatch(TextoDeLiteral(a.Expression))));

    /// <summary>La lambda es un argumento de una llamada de la espina: <c>.Where(o =&gt; …)</c>.</summary>
    private static bool EsLambdaDeLaEspina(LambdaExpressionSyntax lambda, HashSet<SyntaxNode> espina) =>
        lambda.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax llamada } }
        && espina.Contains(llamada);

    /// <summary>
    /// Para una Asignación de Cartera, la espina más las llamadas del contexto que encadenan sobre un
    /// acceso a <c>AsignacionesOperacion</c>: la subconsulta correlacionada
    /// (<c>c =&gt; ctx.AsignacionesOperacion.Any(o =&gt; o.Id == c.AsignacionOperacionId &amp;&amp; o.Servicio == s)</c>)
    /// y la fuente de un <c>Join</c> (<c>.Join(ctx.AsignacionesOperacion.Where(o =&gt; o.Servicio == s), …)</c>).
    /// La fila de una consulta sobre otra cosa (<c>otros.Any(x =&gt; x.Servicio == s)</c>) sigue sin contar.
    /// </summary>
    private static HashSet<SyntaxNode> EspinaDeLaCartera(SyntaxNode contexto, HashSet<SyntaxNode> espina, HashSet<SyntaxNode> operaciones)
    {
        var resultado = new HashSet<SyntaxNode>(espina);
        foreach (var llamada in contexto.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            for (SyntaxNode? receptor = llamada.Expression; receptor is not null;)
            {
                if (operaciones.Contains(receptor)) { resultado.Add(llamada); break; }
                receptor = receptor switch
                {
                    MemberAccessExpressionSyntax m when operaciones.Contains(m.Name) => m.Name,
                    MemberAccessExpressionSyntax m => m.Expression,
                    InvocationExpressionSyntax i => i.Expression,
                    ParenthesizedExpressionSyntax p => p.Expression,
                    _ => null,
                };
            }
        }

        return resultado;
    }

    /// <summary>
    /// La Asignación de Cartera no tiene Servicio: su servicio es el de su Asignación de Operación, así
    /// que la comparación de Servicio solo la filtra si la consulta la correlaciona con su operación.
    /// </summary>
    private static bool NombraLaOperacionDeLaCartera(SyntaxNode contexto) =>
        contexto.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
            .Any(m => m.Name.Identifier.ValueText is "AsignacionOperacionId" or "AsignacionOperacion");

    /// <summary>
    /// <paramref name="espina"/> nula: cualquier variable de consulta vale (para reconocer un predicado
    /// con nombre por su declaración). Con espina: solo la fila de la consulta del acceso.
    /// </summary>
    private static bool ComparaServicio(SyntaxNode contexto, HashSet<SyntaxNode>? espina) =>
        contexto.DescendantNodesAndSelf().Any(n => n switch
        {
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression)
                => EsServicioDeInstancia(b.Left, espina) || EsServicioDeInstancia(b.Right, espina),
            // «o.Servicio is ServicioCae.Outbound»: el analizador lo lee como «is Tipo» (no sabe que
            // Outbound es un valor), así que llega como expresión binaria y no como patrón.
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.IsExpression) => EsServicioDeInstancia(b.Left, espina),
            IsPatternExpressionSyntax p => EsServicioDeInstancia(p.Expression, espina),
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Contains" } } i
                => i.ArgumentList.Arguments.Any(a => EsServicioDeInstancia(a.Expression, espina)),
            _ => false,
        });

    /// <summary>
    /// <c>o.Servicio</c> o <c>c.Operacion.Servicio</c> sobre una <b>variable de la consulta</b>: un
    /// parámetro de una lambda que la rodea o una variable de rango (<c>from</c>, <c>join</c>,
    /// <c>let</c>). No <c>CatalogoIncorporacionCartera.Servicio</c> (una constante) ni
    /// <c>request.Servicio</c> (un valor que no es la fila). Con espina, la variable tiene que ser la
    /// de su declaración más cercana y pertenecer a la consulta del acceso (<see cref="ContextoDelAcceso"/>).
    /// </summary>
    private static bool EsServicioDeInstancia(ExpressionSyntax expresion, HashSet<SyntaxNode>? espina) =>
        expresion is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Servicio" } m
        && PrimerIdentificador(m.Expression) is { Length: > 0 } receptor
        && EsVariableDeConsulta(m, receptor, espina);

    private static bool EsVariableDeConsulta(SyntaxNode uso, string nombre, HashSet<SyntaxNode>? espina)
    {
        foreach (var a in uso.Ancestors())
        {
            switch (a)
            {
                case SimpleLambdaExpressionSyntax s when s.Parameter.Identifier.ValueText == nombre:
                    return espina is null || EsLambdaDeLaEspina(s, espina);
                case ParenthesizedLambdaExpressionSyntax p when p.ParameterList.Parameters.Any(x => x.Identifier.ValueText == nombre):
                    return espina is null || EsLambdaDeLaEspina(p, espina);
                case QueryExpressionSyntax q when DeclaraVariableDeRango(q, nombre):
                    return espina is null || espina.Contains(q);
            }
        }

        return false;
    }

    private static bool DeclaraVariableDeRango(QueryExpressionSyntax q, string nombre) =>
        q.FromClause.Identifier.ValueText == nombre
        || q.Body.Clauses.Any(c => c switch
        {
            FromClauseSyntax f => f.Identifier.ValueText == nombre,
            JoinClauseSyntax j => j.Identifier.ValueText == nombre,
            LetClauseSyntax l => l.Identifier.ValueText == nombre,
            _ => false,
        });

    private static string PrimerIdentificador(ExpressionSyntax expresion) => expresion switch
    {
        IdentifierNameSyntax i => i.Identifier.ValueText,
        MemberAccessExpressionSyntax m => PrimerIdentificador(m.Expression),
        ParenthesizedExpressionSyntax p => PrimerIdentificador(p.Expression),
        ThisExpressionSyntax => "this",
        _ => string.Empty,
    };

    /// <summary>
    /// Miembros sin acceso propio cuya declaración compara <c>.Servicio</c>: los predicados con nombre
    /// (<c>Expression&lt;Func&lt;AsignacionOperacion, bool&gt;&gt;</c>) que una consulta aplica sin
    /// repetir la comparación.
    /// </summary>
    private static HashSet<(string Tipo, string Miembro)> PredicadosDeServicio(IEnumerable<SyntaxNode> raices)
    {
        var predicados = new HashSet<(string, string)>();
        foreach (var raiz in raices)
        {
            foreach (var tipo in raiz.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                foreach (var miembro in tipo.Members)
                {
                    if (!DevuelveUnaConsulta(miembro)) continue;
                    if (!ComparaServicio(miembro, espina: null) || Accesos(miembro, new Dictionary<string, string>()).Any()) continue;
                    foreach (var nombre in NombresDeclarados(miembro))
                        predicados.Add((tipo.Identifier.ValueText, nombre));
                }
            }
        }

        return predicados;
    }

    /// <summary>
    /// Solo un miembro que entrega un filtro aplicable a una consulta cuenta como predicado: su tipo es
    /// <c>Expression&lt;…&gt;</c> o <c>IQueryable&lt;…&gt;</c>. Un <c>bool Vigente(x)</c> que compare
    /// <c>x.Servicio</c> no filtra ninguna consulta por estar en la misma sentencia.
    /// </summary>
    private static bool DevuelveUnaConsulta(MemberDeclarationSyntax miembro)
    {
        var tipo = miembro switch
        {
            MethodDeclarationSyntax m => m.ReturnType,
            PropertyDeclarationSyntax p => p.Type,
            BaseFieldDeclarationSyntax f => f.Declaration.Type,
            _ => null,
        };
        var texto = tipo?.ToString() ?? string.Empty;
        return texto.Contains("Expression<", StringComparison.Ordinal) || texto.Contains("IQueryable<", StringComparison.Ordinal);
    }

    private static IEnumerable<string> NombresDeclarados(MemberDeclarationSyntax miembro) => miembro switch
    {
        MethodDeclarationSyntax m => [m.Identifier.ValueText],
        PropertyDeclarationSyntax p => [p.Identifier.ValueText],
        BaseFieldDeclarationSyntax f => f.Declaration.Variables.Select(v => v.Identifier.ValueText),
        _ => [],
    };

    /// <summary>
    /// Un predicado de servicio que la consulta <b>aplica</b>: referenciado (cualificado por su tipo o,
    /// sin cualificar, desde su propio tipo) y pasado como argumento a una llamada de la espina
    /// (<c>.Where(P(ahora))</c>, <c>.Count(Otro.Predicado)</c>), o siendo él la llamada de la espina que
    /// recibe el acceso (<c>Vigentes(ctx.AsignacionesOperacion)</c>). Nombrado en otra parte del
    /// contexto (<c>.Count(o =&gt; o.Id == otros.Count(P()))</c>) no filtra esta consulta.
    /// </summary>
    private static bool AplicaUnPredicado(
        SyntaxNode contexto, string tipoActual, HashSet<(string Tipo, string Miembro)> predicados, HashSet<SyntaxNode> espina)
    {
        var tipoSimple = tipoActual.Split('.')[^1];
        foreach (var id in contexto.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var nombre = id.Identifier.ValueText;
            SyntaxNode referencia;
            if (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id)
            {
                var receptor = ma.Expression switch
                {
                    IdentifierNameSyntax i => i.Identifier.ValueText,
                    MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                    _ => null,
                };
                if (receptor is null || !predicados.Contains((receptor, nombre))) continue;
                referencia = ma;
            }
            else if (id.Parent is InvocationExpressionSyntax or ArgumentSyntax
                     && predicados.Contains((tipoSimple, nombre)))
            {
                // Sin cualificar, solo como llamada (P(ahora)) o como argumento (Where(Predicado)):
                // un local o un parámetro con el mismo nombre no es el predicado.
                referencia = id;
            }
            else
            {
                continue;
            }

            if (referencia.Parent is InvocationExpressionSyntax llamada && llamada.Expression == referencia)
            {
                if (espina.Contains(llamada)) return true;
                referencia = llamada;
            }

            if (referencia.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax receptora } }
                && espina.Contains(receptora))
                return true;
        }

        return false;
    }

    // ------------------------------------------------------------------ navegaciones del modelo de EF

    /// <summary>
    /// Las navegaciones del modelo de EF (de referencia, de colección y de salto) cuyo destino es una
    /// de las dos entidades de Asignación, o una base o derivada suya. Una navegación es la única forma
    /// de leer la tabla sin nombrarla en el C# de la consulta (<c>x.Operacion.Servicio</c>,
    /// <c>Include(x =&gt; x.Carteras)</c>) y sin que el escáner sintáctico lo vea; hoy no hay ninguna, y
    /// el trinquete exige que siga así hasta que el detector las mida.
    /// </summary>
    public static IReadOnlyList<string> NavegacionesHacia(Microsoft.EntityFrameworkCore.Metadata.IModel modelo, IReadOnlyCollection<Type> destinos) =>
        modelo.GetEntityTypes()
            .SelectMany(e => e.GetDeclaredNavigations().Cast<Microsoft.EntityFrameworkCore.Metadata.INavigationBase>()
                .Concat(e.GetDeclaredSkipNavigations()))
            .Where(n => destinos.Any(d => d.IsAssignableFrom(n.TargetEntityType.ClrType) || n.TargetEntityType.ClrType.IsAssignableFrom(d)))
            .Select(n => $"{n.DeclaringEntityType.ClrType.Name}.{n.Name} -> {n.TargetEntityType.ClrType.Name}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

    private static string NombreDeTipo(SyntaxNode nodo) =>
        string.Join('.', nodo.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.ValueText));

    /// <summary><c>Tipo.Miembro</c>; un constructor es <c>Tipo..ctor</c> y el nivel superior, <c>(nivel superior)</c>.</summary>
    private static string NombreDeMiembro(SyntaxNode nodo)
    {
        var tipo = NombreDeTipo(nodo);
        var miembro = nodo.Ancestors().FirstOrDefault(n =>
            n is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or BaseFieldDeclarationSyntax);

        var nombre = miembro switch
        {
            MethodDeclarationSyntax m => m.ExplicitInterfaceSpecifier is { } e ? $"{e.Name}.{m.Identifier.ValueText}" : m.Identifier.ValueText,
            ConstructorDeclarationSyntax => ".ctor",
            PropertyDeclarationSyntax p => p.ExplicitInterfaceSpecifier is { } e ? $"{e.Name}.{p.Identifier.ValueText}" : p.Identifier.ValueText,
            IndexerDeclarationSyntax => "this[]",
            BaseFieldDeclarationSyntax f => string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.ValueText)),
            BaseMethodDeclarationSyntax => "operator",
            _ => "(nivel superior)",
        };

        return tipo.Length == 0 ? nombre : $"{tipo}.{nombre}";
    }

    // ------------------------------------------------------------------ SQL de las migraciones

    /// <summary>El SQL de <c>Up</c> de cada migración, en orden de Id.</summary>
    public static IReadOnlyList<(string Id, string Sql)> SqlDeLasMigraciones() =>
        typeof(RegistraActividadSoportePorSesionPrivilegiada).Assembly.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => (Id: t.GetCustomAttribute<MigrationAttribute>()?.Id, Tipo: t))
            .Where(m => m.Id is not null)
            .Select(m => (Id: m.Id!, Sql: string.Join("\n", ((Migration)Activator.CreateInstance(m.Tipo)!)
                .UpOperations.OfType<SqlOperation>().Select(o => o.Sql + "\n;"))))
            .OrderBy(m => m.Id, StringComparer.Ordinal)
            .ToList();

    private const string Nombre = @"(?:""[^""]+""|[A-Za-z_]\w*)";

    private static readonly Regex CreaFuncion = new(
        @"\bCREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?<nombre>(?:" + Nombre + @"\s*\.\s*)?" + Nombre + @")\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EliminaFuncion = new(
        @"\bDROP\s+FUNCTION\s+(?:IF\s+EXISTS\s+)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DefinePolitica = new(
        @"\b(?<verbo>CREATE|ALTER|DROP)\s+POLICY\s+(?:IF\s+EXISTS\s+)?(?<nombre>" + Nombre + @")\s+ON\s+(?<tabla>(?:" + Nombre + @"\s*\.\s*)?" + Nombre + ")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CreaVista = new(
        @"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:(?:TEMP|TEMPORARY)\s+)?(?:RECURSIVE\s+)?(?:MATERIALIZED\s+)?VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<nombre>(?:" + Nombre + @"\s*\.\s*)?" + Nombre + ")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EliminaVista = new(
        @"\bDROP\s+(?:MATERIALIZED\s+)?VIEW\s+(?:IF\s+EXISTS\s+)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AlteraVista = new(
        @"\bALTER\s+(?:MATERIALIZED\s+)?VIEW\s+(?:IF\s+EXISTS\s+)?(?<nombre>(?:" + Nombre + @"\s*\.\s*)?" + Nombre + ")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Renombra = new(@"\bRENAME\s+TO\s+(?<nuevo>" + Nombre + ")", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ClausulaUsing = new(@"\bUSING\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ClausulaCheck = new(@"\bWITH\s+CHECK\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CuerpoEntreComillas = new(@"\bAS\s*'", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Parentesis = new(@"\([^()]*\)", RegexOptions.Compiled);

    /// <summary>Los accesos del SQL vigente de las migraciones del repositorio.</summary>
    public static IReadOnlyList<AccesoAAsignacion> MedirMigraciones() => EnSql(SqlDeLasMigraciones());

    /// <summary>
    /// Recorre las migraciones en orden y se queda con el <b>estado final</b> de cada función, vista y
    /// política; devuelve un acceso por cada lectura de una de las dos tablas en ese estado.
    /// <list type="bullet">
    /// <item>Función: la última <c>CREATE [OR REPLACE] FUNCTION</c> no eliminada después. Su cuerpo es la
    /// cadena con dólar <b>de su propia sentencia</b>; si no la tiene, el literal <c>AS '…'</c>; si
    /// tampoco, la sentencia entera.</item>
    /// <item>Vista: la última <c>CREATE [OR REPLACE] [MATERIALIZED] VIEW</c> no eliminada; su texto, la
    /// sentencia. <c>ALTER VIEW … RENAME TO</c> la traslada.</item>
    /// <item>Política: <c>USING</c> y <c>WITH CHECK</c> por separado, como en PostgreSQL. <c>ALTER
    /// POLICY</c> sustituye la cláusula que trae y conserva la otra; <c>RENAME TO</c> la traslada;
    /// <c>DROP POLICY</c> la retira.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<AccesoAAsignacion> EnSql(IReadOnlyList<(string Id, string Sql)> migraciones)
    {
        var funciones = new Dictionary<string, string>(StringComparer.Ordinal);
        var vistas = new Dictionary<string, string>(StringComparer.Ordinal);
        var politicas = new Dictionary<string, (string? Usando, string? Comprobando)>(StringComparer.Ordinal);

        foreach (var (_, sql) in migraciones)
        {
            var codigo = EscanerSql.SoloCodigo(sql);
            var eventos = new List<(int Indice, Action Aplicar)>();

            foreach (var m in EscanerSql.CoincidenciasEnCodigo(CreaFuncion, sql))
            {
                var nombre = Normalizar(m.Groups["nombre"].Value);
                var cuerpo = CuerpoDeFuncion(sql, m.Index, FinDeSentencia(codigo, m.Index));
                eventos.Add((m.Index, () => funciones[nombre] = cuerpo));
            }

            foreach (var m in EscanerSql.CoincidenciasEnCodigo(EliminaFuncion, sql))
            {
                var resto = sql[(m.Index + m.Length)..FinDeSentencia(codigo, m.Index)];
                foreach (var nombre in NombresEliminados(resto))
                    eventos.Add((m.Index, () => funciones.Remove(nombre)));
            }

            foreach (var m in EscanerSql.CoincidenciasEnCodigo(CreaVista, sql))
            {
                var nombre = Normalizar(m.Groups["nombre"].Value);
                var definicion = sql[(m.Index + m.Length)..FinDeSentencia(codigo, m.Index)];
                eventos.Add((m.Index, () => vistas[nombre] = definicion));
            }

            foreach (var m in EscanerSql.CoincidenciasEnCodigo(EliminaVista, sql))
            {
                var resto = sql[(m.Index + m.Length)..FinDeSentencia(codigo, m.Index)];
                foreach (var nombre in NombresEliminados(resto))
                    eventos.Add((m.Index, () => vistas.Remove(nombre)));
            }

            foreach (var m in EscanerSql.CoincidenciasEnCodigo(AlteraVista, sql))
            {
                var nombre = Normalizar(m.Groups["nombre"].Value);
                var resto = EscanerSql.SinComentarios(sql[(m.Index + m.Length)..FinDeSentencia(codigo, m.Index)]);
                if (Renombra.Match(resto) is not { Success: true } r) continue;
                var nuevo = Normalizar(EsquemaDe(nombre) + "." + r.Groups["nuevo"].Value);
                eventos.Add((m.Index, () =>
                {
                    if (vistas.Remove(nombre, out var definicion)) vistas[nuevo] = definicion;
                }));
            }

            foreach (var m in EscanerSql.CoincidenciasEnCodigo(DefinePolitica, sql))
            {
                var tabla = Normalizar(m.Groups["tabla"].Value);
                var clave = $"{m.Groups["nombre"].Value.Trim('"')} ON {tabla}";
                var tramo = sql[(m.Index + m.Length)..FinDeSentencia(codigo, m.Index)];
                var usando = ExpresionDeClausula(tramo, ClausulaUsing);
                var comprobando = ExpresionDeClausula(tramo, ClausulaCheck);
                var verbo = m.Groups["verbo"].Value.ToUpperInvariant();
                eventos.Add((m.Index, () =>
                {
                    if (verbo == "DROP") { politicas.Remove(clave); return; }
                    if (verbo == "CREATE") { politicas[clave] = (usando, comprobando); return; }
                    if (Renombra.Match(EscanerSql.SinComentarios(tramo)) is { Success: true } r)
                    {
                        if (politicas.Remove(clave, out var vieja))
                            politicas[$"{r.Groups["nuevo"].Value.Trim('"')} ON {tabla}"] = vieja;
                        return;
                    }

                    // ALTER POLICY: la cláusula que trae sustituye a la anterior; la que no trae se conserva.
                    politicas.TryGetValue(clave, out var previa);
                    politicas[clave] = (usando ?? previa.Usando, comprobando ?? previa.Comprobando);
                }
                ));
            }

            foreach (var (_, aplicar) in eventos.OrderBy(e => e.Indice))
                aplicar();
        }

        var resultado = new List<AccesoAAsignacion>();
        foreach (var (nombre, texto) in funciones.OrderBy(f => f.Key, StringComparer.Ordinal))
            resultado.AddRange(AccesosDeTexto($"SQL función {nombre}", texto, "función"));
        foreach (var (nombre, texto) in vistas.OrderBy(v => v.Key, StringComparer.Ordinal))
            resultado.AddRange(AccesosDeTexto($"SQL vista {nombre}", texto, "vista"));
        foreach (var (clave, (usando, comprobando)) in politicas.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            // Cada cláusula es su propia expresión: el Servicio de una no filtra la otra.
            foreach (var expresion in new[] { usando, comprobando }.OfType<string>())
                resultado.AddRange(AccesosDeTexto($"SQL política {clave}", expresion, "política"));
        }

        return resultado;
    }

    private static string EsquemaDe(string nombreNormalizado) =>
        nombreNormalizado[..nombreNormalizado.LastIndexOf('.')];

    /// <summary>
    /// El cuerpo de la función cuya sentencia va de <paramref name="desde"/> a <paramref name="fin"/>
    /// (el primer <c>;</c> en código, que nunca cae dentro de un literal): la cadena con dólar que empieza
    /// dentro de la sentencia, o el contenido del literal <c>AS '…'</c>, o la sentencia entera. Nunca el
    /// cuerpo de una función posterior.
    /// </summary>
    private static string CuerpoDeFuncion(string sql, int desde, int fin)
    {
        var tokens = EscanerSql.NoCodigo(sql);
        var dolar = tokens.FirstOrDefault(t => t.ConDolar && t.Inicio >= desde && t.Inicio < fin);
        if (dolar.ConDolar)
            return sql[dolar.ContenidoInicio..dolar.ContenidoFin];

        var sentencia = sql[desde..fin];
        if (EscanerSql.CoincidenciasEnCodigo(CuerpoEntreComillas, sentencia) is [var comillas, ..])
        {
            var inicio = desde + comillas.Index + comillas.Length - 1;
            var literal = tokens.FirstOrDefault(t => t.Clase == EscanerSql.Clase.Literal && t.Inicio == inicio);
            if (literal.Fin > literal.Inicio)
                return sql[literal.ContenidoInicio..literal.ContenidoFin].Replace("''", "'", StringComparison.Ordinal);
        }

        return sentencia;
    }

    /// <summary>
    /// La expresión entre paréntesis de la cláusula (<c>USING (…)</c>, <c>WITH CHECK (…)</c>) del primer
    /// nivel del tramo; <c>null</c> si el tramo no la trae. Un <c>JOIN … USING (col)</c> dentro de una
    /// subconsulta no es la cláusula: está a otra profundidad de paréntesis.
    /// </summary>
    private static string? ExpresionDeClausula(string tramo, Regex clausula)
    {
        var codigo = EscanerSql.SoloCodigo(tramo);
        var profundidad = new int[codigo.Length + 1];
        for (var i = 0; i < codigo.Length; i++)
            profundidad[i + 1] = profundidad[i] + (codigo[i] == '(' ? 1 : codigo[i] == ')' ? -1 : 0);

        var m = clausula.Matches(codigo).FirstOrDefault(x => profundidad[x.Index] == 0);
        if (m is null) return null;

        var abre = m.Index + m.Length - 1;
        for (var i = abre; i < codigo.Length; i++)
        {
            if (codigo[i] == ')' && profundidad[i + 1] == 0)
                return tramo[(abre + 1)..i];
        }

        return tramo[(abre + 1)..];
    }

    /// <summary>
    /// Un acceso por cada aparición de la tabla, clasificado por su <b>sentencia</b> (el tramo entre dos
    /// <c>;</c> en código): un <c>"Servicio"</c> comparado en otra sentencia del mismo cuerpo no lo
    /// filtra, y una lectura nueva sin servicio en un objeto ya listado sube su recuento (CRECE).
    /// </summary>
    public static IEnumerable<AccesoAAsignacion> AccesosDeTexto(string lugar, string texto, string forma)
    {
        var limpio = EscanerSql.SinComentarios(texto);
        var codigo = EscanerSql.SoloCodigo(texto);
        var inicio = 0;
        while (inicio < limpio.Length)
        {
            var fin = FinDeSentencia(codigo, inicio);
            var sentencia = limpio[inicio..fin];
            var filtra = ServicioSql.IsMatch(sentencia);
            foreach (Match m in TablaSql.Matches(sentencia))
                yield return new AccesoAAsignacion(lugar, string.Empty, m.Groups["tabla"].Value, forma, filtra, 0);
            inicio = fin + 1;
        }
    }

    private static int FinDeSentencia(string codigo, int desde)
    {
        var fin = codigo.IndexOf(';', desde);
        return fin < 0 ? codigo.Length : fin;
    }

    private static IEnumerable<string> NombresEliminados(string resto)
    {
        var sinArgumentos = resto;
        string previo;
        do { previo = sinArgumentos; sinArgumentos = Parentesis.Replace(sinArgumentos, " "); }
        while (sinArgumentos != previo);

        return sinArgumentos.Split(',')
            .Select(t => Regex.Replace(t, @"\b(?:CASCADE|RESTRICT)\b", " ", RegexOptions.IgnoreCase).Trim())
            .Where(t => t.Length > 0)
            .Select(Normalizar);
    }

    /// <summary>Sin comillas ni espacios, y con <c>public.</c> si no lleva esquema.</summary>
    private static string Normalizar(string nombre)
    {
        var partes = nombre.Split('.').Select(p => p.Trim().Trim('"')).ToArray();
        return partes.Length == 1 ? "public." + partes[0] : string.Join('.', partes);
    }
}

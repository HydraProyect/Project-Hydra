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
/// su tipo; o un <c>FromSql*</c> de la cadena propia cuyo SQL compara <c>"Servicio"</c>. La Asignación de
/// Cartera no tiene Servicio: la filtra solo esa comparación o ese predicado sobre una consulta de
/// <c>AsignacionesOperacion</c> que la correlaciona con su operación (<see cref="OperacionesCorrelacionadas"/>).
/// En SQL: la sentencia compara <c>"Servicio"</c>.
/// </para>
///
/// <para>
/// <b>Falla cerrado</b> en el SQL de las migraciones: una sentencia que el inventario no sabe seguir
/// (<c>CREATE FUNCTION</c> sin cuerpo <c>AS</c> o <c>RETURN</c> identificable, con <c>BEGIN ATOMIC</c> o con
/// el cuerpo en literales concatenados, <c>CREATE PROCEDURE</c>, <c>CREATE RULE</c>, <c>CREATE …
/// FUNCTION</c> o <c>VIEW</c> que el patrón no reconoce, <c>ALTER FUNCTION|PROCEDURE|ROUTINE|TABLE|SCHEMA
/// … RENAME TO|SET SCHEMA</c>, <c>ALTER VIEW … SET SCHEMA</c>) lanza
/// <see cref="InvalidOperationException"/> con la migración y la sentencia, en vez de ignorarse.
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
            var propia = new HashSet<SyntaxNode>();
            CadenaDeLaConsulta(nodo, propia);
            var tipo = NombreDeTipo(nodo);

            // La Asignación de Cartera no tiene Servicio: solo la filtra el Servicio (o el predicado) de
            // una consulta sobre AsignacionesOperacion que la correlaciona con su operación. El de la
            // consulta exterior que la contiene no basta: puede ser de otra cosa.
            var filtra = forma == "SQL"
                ? ServicioSql.IsMatch(TextoDeLiteral(nodo))
                : FiltraSuSqlCrudo(propia)
                  || (tabla == Operacion
                      ? AplicaUnPredicado(contexto, tipo, predicados, espina) || ComparaServicio(contexto, espina)
                      : OperacionesCorrelacionadas(contexto, propia, operaciones) is { Count: > 0 } deLaOperacion
                        && (ComparaServicio(contexto, deLaOperacion) || AplicaUnPredicado(contexto, tipo, predicados, deLaOperacion)));

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
    /// <c>Where</c>, <c>Count</c>…, la fuente <c>from</c>/<c>join</c> que contiene el acceso, la consulta
    /// integrada). Una comparación de Servicio solo cuenta si su variable es la fila de una lambda pasada
    /// a una llamada de la espina o la variable de rango de un <c>from</c>/<c>join</c> cuya fuente está en
    /// la espina (no la de un <c>join</c> con otra fuente), y un predicado con nombre solo cuenta si se
    /// pasa a una llamada de la espina (o es la llamada que recibe el acceso): en
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

            // El acceso está dentro de una cláusula (where, select…) de una consulta integrada exterior:
            // como con la lambda exterior, las filas de esa consulta son las de su predicado.
            if (exterior is QueryExpressionSyntax consulta)
                foreach (var fuente in FuentesDeRango(consulta))
                    espina.Add(fuente);

            cadena = CadenaDeLaConsulta(exterior, espina);
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
    /// cadena <b>propia</b> del acceso pasa a un <c>FromSql*</c> compara el Servicio, así que la raíz
    /// <c>Set&lt;T&gt;</c> o el <c>DbSet</c> de esa cadena no es un acceso crudo aparte del literal (que ya
    /// se clasifica solo). El <c>FromSql*</c> de una consulta exterior no filtra el acceso interior.
    /// </summary>
    private static bool FiltraSuSqlCrudo(HashSet<SyntaxNode> propia) =>
        propia.OfType<InvocationExpressionSyntax>().Any(i =>
            i.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var metodo }
            && metodo.StartsWith("FromSql", StringComparison.Ordinal)
            && i.ArgumentList.Arguments.Any(a =>
                (a.Expression is InterpolatedStringExpressionSyntax || a.Expression.IsKind(SyntaxKind.StringLiteralExpression))
                && ServicioSql.IsMatch(TextoDeLiteral(a.Expression))));

    /// <summary>
    /// Para una Asignación de Cartera: la unión de las cadenas de las consultas sobre
    /// <c>AsignacionesOperacion</c> del contexto que la <b>correlacionan</b> con su operación, sin los nodos
    /// que comparten con la cadena propia de la cartera (lo que va tras el <c>Join</c> que las une es una
    /// proyección y no cuenta); vacía si ninguna la correlaciona, y entonces la lectura es cruda. Solo tres formas correlacionan, cada una entre una
    /// fila de la cadena <b>propia</b> de la cartera (<paramref name="propia"/>) y una fila de la cadena de
    /// la operación que no sea de la propia:
    /// <list type="bullet">
    /// <item>una igualdad <c>==</c> entre <c>c.AsignacionOperacionId</c> y <c>o.Id</c>, en cualquier orden
    /// (<c>c =&gt; ctx.AsignacionesOperacion.Any(o =&gt; o.Id == c.AsignacionOperacionId &amp;&amp; o.Servicio == s)</c>,
    /// o la cartera dentro del predicado de una consulta sobre la operación);</item>
    /// <item>la clave de un <c>join</c> integrado con esos dos lados (<c>join o in ctx.AsignacionesOperacion
    /// on c.AsignacionOperacionId equals o.Id</c>; también con la cartera como lado unido);</item>
    /// <item>un <c>Join</c> o <c>GroupJoin</c> de método cuyas claves son <c>c =&gt; c.AsignacionOperacionId</c>
    /// en el lado de la cartera y <c>o =&gt; o.Id</c> en el de la operación.</item>
    /// </list>
    /// El lado de la operación tiene que ser una consulta medida sobre <c>AsignacionesOperacion</c>: una
    /// variable (<c>join o in ops</c>, <c>ops.Where(o =&gt; …)</c>) no se sabe qué recorre y no correlaciona.
    /// Una subconsulta de operaciones sin esa igualdad (<c>ctx.AsignacionesOperacion.Any(o =&gt; o.Servicio
    /// == s)</c>), una clave compuesta o cualquier otra forma tampoco.
    /// </summary>
    private static HashSet<SyntaxNode> OperacionesCorrelacionadas(
        SyntaxNode contexto, HashSet<SyntaxNode> propia, HashSet<SyntaxNode> operaciones)
    {
        var resultado = new HashSet<SyntaxNode>();
        foreach (var operacion in operaciones.Where(o => contexto.Contains(o)))
        {
            var deLaOperacion = new HashSet<SyntaxNode>();
            CadenaDeLaConsulta(operacion, deLaOperacion);
            // Solo la parte de la cadena de la operación que no es ya la de la cartera: por encima del
            // Join en que se unen, la fila es una proyección que puede arrastrar otra fuente.
            if (Correlaciona(contexto, propia, deLaOperacion)) resultado.UnionWith(deLaOperacion.Except(propia));
        }

        return resultado;
    }

    private static bool Correlaciona(SyntaxNode contexto, HashSet<SyntaxNode> propia, HashSet<SyntaxNode> deLaOperacion)
    {
        bool DeLaCartera(ExpressionSyntax e) =>
            PropiedadDeFila(e, "AsignacionOperacionId") is { } fila && FuenteDeFila(fila) is { } f && propia.Contains(f);
        bool DeLaOperacion(ExpressionSyntax e) =>
            PropiedadDeFila(e, "Id") is { } fila && FuenteDeFila(fila) is { } f && deLaOperacion.Contains(f) && !propia.Contains(f);
        bool Empareja(ExpressionSyntax a, ExpressionSyntax b) =>
            (DeLaCartera(a) && DeLaOperacion(b)) || (DeLaOperacion(a) && DeLaCartera(b));

        foreach (var nodo in contexto.DescendantNodesAndSelf())
        {
            switch (nodo)
            {
                case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.EqualsExpression) && Empareja(b.Left, b.Right):
                case JoinClauseSyntax j when Empareja(j.LeftExpression, j.RightExpression):
                    return true;

                case InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Join" or "GroupJoin" } metodo,
                    ArgumentList.Arguments: [var interior, var claveExterior, var claveInterior, ..],
                } join when propia.Contains(join) && deLaOperacion.Contains(join):
                    var carteraFuera = propia.Contains(metodo.Expression) && deLaOperacion.Contains(interior.Expression);
                    var carteraDentro = deLaOperacion.Contains(metodo.Expression) && propia.Contains(interior.Expression);
                    var (fuera, dentro) = (ClaveDeLambda(claveExterior), ClaveDeLambda(claveInterior));
                    if ((carteraFuera && fuera == "AsignacionOperacionId" && dentro == "Id")
                        || (carteraDentro && fuera == "Id" && dentro == "AsignacionOperacionId"))
                        return true;
                    break;
            }
        }

        return false;
    }

    /// <summary><c>x.Propiedad</c> sobre un identificador simple: devuelve <c>x</c>; si no, <c>null</c>.</summary>
    private static IdentifierNameSyntax? PropiedadDeFila(ExpressionSyntax expresion, string propiedad) =>
        expresion is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax fila } m
        && m.Name.Identifier.ValueText == propiedad
            ? fila
            : null;

    /// <summary>La propiedad de la clave de un <c>Join</c>: <c>c =&gt; c.AsignacionOperacionId</c> da su nombre; otra forma, <c>null</c>.</summary>
    private static string? ClaveDeLambda(ArgumentSyntax argumento) =>
        argumento is
        {
            NameColon: null,
            Expression: SimpleLambdaExpressionSyntax
            {
                ExpressionBody: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax fila } cuerpo,
            } lambda,
        }
        && fila.Identifier.ValueText == lambda.Parameter.Identifier.ValueText
            ? cuerpo.Name.Identifier.ValueText
            : null;

    /// <summary>Las fuentes de los <c>from</c> y <c>join</c> de la propia consulta (con su continuación).</summary>
    private static IEnumerable<ExpressionSyntax> FuentesDeRango(QueryExpressionSyntax consulta) =>
        consulta.DescendantNodesAndSelf()
            .Where(n => n is FromClauseSyntax or JoinClauseSyntax && n.FirstAncestorOrSelf<QueryExpressionSyntax>() == consulta)
            .Select(n => n is FromClauseSyntax f ? f.Expression : ((JoinClauseSyntax)n).InExpression);

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

    /// <summary>
    /// Sin espina, cualquier variable de consulta; con espina, solo la que es fila de una llamada o de
    /// una fuente de la espina (un <c>let</c> no recorre ninguna).
    /// </summary>
    private static bool EsVariableDeConsulta(SyntaxNode uso, string nombre, HashSet<SyntaxNode>? espina) =>
        DeclaracionDeFila(uso, nombre) is (true, var fuente)
        && (espina is null || (fuente is not null && espina.Contains(fuente)));

    /// <summary>
    /// De qué es fila la variable, por su declaración más cercana: la llamada a la que se pasa la lambda
    /// que la declara (<c>.Where(o =&gt; …)</c>) o la fuente que recorre la variable de rango; <c>null</c>
    /// si no es la fila de una consulta.
    /// </summary>
    private static SyntaxNode? FuenteDeFila(IdentifierNameSyntax variable) =>
        DeclaracionDeFila(variable, variable.Identifier.ValueText).Fuente;

    private static (bool Declarada, SyntaxNode? Fuente) DeclaracionDeFila(SyntaxNode uso, string nombre)
    {
        foreach (var a in uso.Ancestors())
        {
            switch (a)
            {
                case SimpleLambdaExpressionSyntax s when s.Parameter.Identifier.ValueText == nombre:
                    return (true, LlamadaQueRecibe(s));
                case ParenthesizedLambdaExpressionSyntax p when p.ParameterList.Parameters.Any(x => x.Identifier.ValueText == nombre):
                    return (true, LlamadaQueRecibe(p));
                case QueryExpressionSyntax q when FuenteDeLaVariable(q, nombre) is (true, var fuente):
                    return (true, fuente);
            }
        }

        return (false, null);
    }

    private static InvocationExpressionSyntax? LlamadaQueRecibe(LambdaExpressionSyntax lambda) =>
        lambda.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax llamada } } ? llamada : null;

    /// <summary>Si la consulta declara la variable y, para <c>from</c> y <c>join</c>, la fuente que recorre.</summary>
    private static (bool Declarada, ExpressionSyntax? Fuente) FuenteDeLaVariable(QueryExpressionSyntax q, string nombre)
    {
        if (q.FromClause.Identifier.ValueText == nombre) return (true, q.FromClause.Expression);
        foreach (var clausula in q.Body.Clauses)
        {
            switch (clausula)
            {
                case FromClauseSyntax f when f.Identifier.ValueText == nombre: return (true, f.Expression);
                case JoinClauseSyntax j when j.Identifier.ValueText == nombre: return (true, j.InExpression);
                case LetClauseSyntax l when l.Identifier.ValueText == nombre: return (true, null);
            }
        }

        return (false, null);
    }

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
    /// de las dos entidades de Asignación, o una base o derivada suya. Una navegación es una de las
    /// formas de leer la tabla sin nombrarla en el C# de la consulta (<c>x.Operacion.Servicio</c>,
    /// <c>Include(x =&gt; x.Carteras)</c>) que el escáner sintáctico no ve, y la única que este test
    /// cierra: hoy no hay ninguna, y el trinquete exige que siga así hasta que el detector las mida. Las
    /// demás (<c>ctx.Add(entidad)</c>, <c>Entry(…)</c>, <c>Set(typeof(…))</c>, <c>Model.FindEntityType</c>,
    /// reflexión) siguen sin medirse y están declaradas en
    /// <see cref="AccesoCrudoAAsignacionesSinServicioTests"/>.
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

    private static readonly Regex ClausulaAs = new(@"\bAS\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ClausulaRetorno = new(@"\bRETURN\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InicioAtomico = new(@"\bBEGIN\s+ATOMIC\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Toda creación de rutina, la reconozca <see cref="CreaFuncion"/> o no.</summary>
    private static readonly Regex CreaRutina = new(
        @"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:FUNCTION|PROCEDURE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Toda creación de vista, la reconozca <see cref="CreaVista"/> o no.</summary>
    private static readonly Regex CreaVistaCualquiera = new(
        @"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:(?:TEMP|TEMPORARY)\s+)?(?:RECURSIVE\s+)?(?:MATERIALIZED\s+)?VIEW\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AlteraObjeto = new(
        @"\bALTER\s+(?<clase>FUNCTION|PROCEDURE|ROUTINE|TABLE|SCHEMA|(?:MATERIALIZED\s+)?VIEW)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Una regla lee tablas sin función, vista ni política; el inventario no la sigue.</summary>
    private static readonly Regex CreaRegla = new(@"\bCREATE\s+(?:OR\s+REPLACE\s+)?RULE\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Traslada = new(@"\b(?<como>RENAME\s+TO|SET\s+SCHEMA)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Parentesis = new(@"\([^()]*\)", RegexOptions.Compiled);

    /// <summary>Los accesos del SQL vigente de las migraciones del repositorio.</summary>
    public static IReadOnlyList<AccesoAAsignacion> MedirMigraciones() => EnSql(SqlDeLasMigraciones());

    /// <summary>
    /// Recorre las migraciones en orden y se queda con el <b>estado final</b> de cada función, vista y
    /// política; devuelve un acceso por cada lectura de una de las dos tablas en ese estado.
    /// <list type="bullet">
    /// <item>Función: la última <c>CREATE [OR REPLACE] FUNCTION</c> no eliminada después. Su cuerpo es el
    /// literal (con dólar o entre comillas) que sigue a la cláusula <c>AS</c> posterior a la firma: un
    /// literal anterior, como un <c>DEFAULT $$…$$</c> de un argumento, no es el cuerpo.</item>
    /// <item>Vista: la última <c>CREATE [OR REPLACE] [MATERIALIZED] VIEW</c> no eliminada; su texto, la
    /// sentencia. <c>ALTER VIEW … RENAME TO</c> la traslada.</item>
    /// <item>Política: <c>USING</c> y <c>WITH CHECK</c> por separado, como en PostgreSQL. <c>ALTER
    /// POLICY</c> sustituye la cláusula que trae y conserva la otra; <c>RENAME TO</c> la traslada;
    /// <c>DROP POLICY</c> la retira.</item>
    /// </list>
    /// Lo que no sabe seguir lanza <see cref="InvalidOperationException"/> con la migración y la sentencia
    /// (<see cref="ExigirQueSeSabeAnalizar"/>, <see cref="CuerpoDeFuncion"/>).
    /// </summary>
    public static IReadOnlyList<AccesoAAsignacion> EnSql(IReadOnlyList<(string Id, string Sql)> migraciones)
    {
        var funciones = new Dictionary<string, string>(StringComparer.Ordinal);
        var vistas = new Dictionary<string, string>(StringComparer.Ordinal);
        var politicas = new Dictionary<string, (string? Usando, string? Comprobando)>(StringComparer.Ordinal);

        foreach (var (id, sql) in migraciones)
        {
            var codigo = EscanerSql.SoloCodigo(sql);
            var limpio = EscanerSql.SinComentarios(sql);
            var eventos = new List<(int Indice, Action Aplicar)>();

            ExigirQueSeSabeAnalizar(id, sql, codigo);

            foreach (var m in EscanerSql.CoincidenciasEnCodigo(CreaFuncion, sql))
            {
                var nombre = Normalizar(m.Groups["nombre"].Value);
                var cuerpo = CuerpoDeFuncion(id, sql, limpio, codigo, m);
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
                }
                ));
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
    /// El cuerpo de la función que crea <paramref name="creacion"/> (que acaba en el <c>(</c> de su
    /// firma): tras el <c>)</c> que cierra la firma, la primera cláusula de cuerpo en código, que es
    /// <c>AS</c> seguida de un literal (con dólar o entre comillas) o el cuerpo SQL estándar <c>RETURN
    /// expresión</c> hasta el fin de la sentencia. Un literal de la firma (<c>DEFAULT $$…$$</c>) no es el
    /// cuerpo, y la sentencia acaba en su primer <c>;</c> en código, así que nunca es el cuerpo de una
    /// función posterior. Si la firma no cierra, no hay <c>AS</c> ni <c>RETURN</c> antes del fin de la
    /// sentencia, tras el <c>AS</c> no empieza un literal o el cuerpo es <c>BEGIN ATOMIC</c> (cuyos
    /// <c>;</c> cortan la sentencia), falla cerrado.
    /// </summary>
    private static string CuerpoDeFuncion(string id, string sql, string limpio, string codigo, Match creacion)
    {
        var fin = FinDeSentencia(codigo, creacion.Index);
        var cierre = CierreDeParentesis(codigo, creacion.Index + creacion.Length - 1, fin);
        if (cierre < 0)
            throw NoAnalizable(id, sql, codigo, creacion.Index, "la firma de la función no cierra antes del fin de la sentencia");
        if (InicioAtomico.IsMatch(codigo[cierre..fin]))
            throw NoAnalizable(id, sql, codigo, creacion.Index, "cuerpo BEGIN ATOMIC: sus «;» cortan la sentencia");
        // La primera de las dos cláusulas de cuerpo tras la firma: un AS dentro de un cuerpo RETURN
        // (CAST(x AS text)) no es la cláusula AS.
        var clausula = new[] { ClausulaAs.Match(codigo, cierre), ClausulaRetorno.Match(codigo, cierre) }
            .Where(m => m.Success && m.Index < fin)
            .MinBy(m => m.Index);
        if (clausula is null)
            throw NoAnalizable(id, sql, codigo, creacion.Index, "no hay cláusula AS ni RETURN con el cuerpo tras la firma");

        // Cuerpo SQL estándar (RETURN expresión): una sola expresión hasta el fin de la sentencia.
        if (clausula.Value.Equals("RETURN", StringComparison.OrdinalIgnoreCase))
            return sql[(clausula.Index + clausula.Length)..fin];

        var inicio = clausula.Index + clausula.Length;
        while (inicio < fin && char.IsWhiteSpace(limpio[inicio])) inicio++;
        var tokens = EscanerSql.NoCodigo(sql);
        var literal = tokens.FirstOrDefault(t => t.Clase == EscanerSql.Clase.Literal && t.Inicio == inicio);
        if (literal.Fin <= literal.Inicio)
            throw NoAnalizable(id, sql, codigo, creacion.Index, "tras AS no empieza un literal con el cuerpo");

        // Dos literales seguidos separados por un salto de línea son, en PostgreSQL, uno solo: el cuerpo
        // no sería solo el primero.
        var siguiente = literal.Fin;
        while (siguiente < fin && char.IsWhiteSpace(limpio[siguiente])) siguiente++;
        if (tokens.Any(t => t.Clase == EscanerSql.Clase.Literal && t.Inicio == siguiente))
            throw NoAnalizable(id, sql, codigo, creacion.Index, "el cuerpo sigue en otro literal concatenado");

        var cuerpo = sql[literal.ContenidoInicio..literal.ContenidoFin];
        return literal.ConDolar ? cuerpo : cuerpo.Replace("''", "'", StringComparison.Ordinal);
    }

    /// <summary>El <c>)</c> en código que cierra el <c>(</c> de <paramref name="abre"/> antes de <paramref name="fin"/>; -1 si no lo hay.</summary>
    private static int CierreDeParentesis(string codigo, int abre, int fin)
    {
        var profundidad = 0;
        for (var i = abre; i < fin; i++)
        {
            if (codigo[i] == '(') profundidad++;
            else if (codigo[i] == ')' && --profundidad == 0) return i;
        }

        return -1;
    }

    /// <summary>
    /// Falla cerrado ante lo que el inventario no sabe seguir: una <c>CREATE FUNCTION</c> o <c>VIEW</c>
    /// que <see cref="CreaFuncion"/> o <see cref="CreaVista"/> no reconocen, una <c>CREATE PROCEDURE</c> o
    /// <c>CREATE RULE</c> (no se inventarían), y una sentencia que traslada un objeto sin que el estado lo
    /// siga: <c>ALTER FUNCTION|PROCEDURE|ROUTINE|TABLE|SCHEMA … RENAME TO</c> o <c>SET SCHEMA</c>, y <c>ALTER [MATERIALIZED]
    /// VIEW … SET SCHEMA</c> (el <c>RENAME TO</c> de una vista sí se sigue). Hoy ninguna migración usa
    /// ninguna de ellas en código: dentro de un <c>DO $$ … $$</c> no se ven (hueco declarado).
    /// </summary>
    private static void ExigirQueSeSabeAnalizar(string id, string sql, string codigo)
    {
        var funciones = EscanerSql.CoincidenciasEnCodigo(CreaFuncion, sql).Select(m => m.Index).ToHashSet();
        foreach (var m in EscanerSql.CoincidenciasEnCodigo(CreaRutina, sql).Where(m => !funciones.Contains(m.Index)))
            throw NoAnalizable(id, sql, codigo, m.Index, "CREATE PROCEDURE, o una CREATE FUNCTION cuyo nombre o firma no se reconoce");

        var vistas = EscanerSql.CoincidenciasEnCodigo(CreaVista, sql).Select(m => m.Index).ToHashSet();
        foreach (var m in EscanerSql.CoincidenciasEnCodigo(CreaVistaCualquiera, sql).Where(m => !vistas.Contains(m.Index)))
            throw NoAnalizable(id, sql, codigo, m.Index, "una CREATE VIEW cuyo nombre no se reconoce");

        foreach (var m in EscanerSql.CoincidenciasEnCodigo(CreaRegla, sql))
            throw NoAnalizable(id, sql, codigo, m.Index, "CREATE RULE: el inventario no sigue reglas");

        foreach (var m in EscanerSql.CoincidenciasEnCodigo(AlteraObjeto, sql))
        {
            var esVista = m.Groups["clase"].Value.EndsWith("VIEW", StringComparison.OrdinalIgnoreCase);
            foreach (var t in EscanerSql.CoincidenciasEnCodigo(Traslada, sql[m.Index..FinDeSentencia(codigo, m.Index)]))
            {
                if (esVista && t.Groups["como"].Value.StartsWith("RENAME", StringComparison.OrdinalIgnoreCase)) continue;
                throw NoAnalizable(id, sql, codigo, m.Index, $"{Regex.Replace(t.Groups["como"].Value, @"\s+", " ").ToUpperInvariant()} traslada el objeto y el inventario no lo sigue");
            }
        }
    }

    private static InvalidOperationException NoAnalizable(string id, string sql, string codigo, int desde, string motivo)
    {
        var sentencia = Regex.Replace(sql[desde..FinDeSentencia(codigo, desde)], @"\s+", " ").Trim();
        if (sentencia.Length > 240) sentencia = sentencia[..240] + " …";
        return new InvalidOperationException(
            $"Migración {id}: AccesosAAsignaciones no sabe analizar con seguridad la sentencia «{sentencia}» ({motivo}). " +
            "Falla cerrado en vez de ignorarla: modela la construcción en el detector o escribe la migración de otra forma.");
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

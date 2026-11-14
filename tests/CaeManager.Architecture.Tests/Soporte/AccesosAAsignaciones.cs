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
/// <item><b>SQL vigente</b> de las migraciones: la última definición de cada función
/// (<c>CREATE [OR REPLACE] FUNCTION</c> no eliminada después) y de cada política
/// (<c>CREATE POLICY</c>, más sus <c>ALTER POLICY</c>) cuyo cuerpo o expresión lee una de las dos
/// tablas.</item>
/// </list>
///
/// <para>
/// <b>Qué cuenta como filtro de Servicio en C#.</b> En la sentencia que contiene el acceso (o en la
/// expresión del miembro, si no hay sentencia): una comparación <c>==</c> o <c>!=</c>, un patrón
/// <c>is</c> o un <c>Contains(…)</c> sobre una propiedad <c>.Servicio</c> de una instancia (el
/// receptor empieza en minúscula: <c>o.Servicio</c>, no la constante <c>Catalogo.Servicio</c>); o una
/// referencia a un <b>predicado de servicio</b>, que es un miembro sin acceso propio cuya declaración
/// contiene esa comparación (<c>OperacionDelServicioVigente(ahora)</c>), en el mismo tipo o
/// cualificado por el nombre de su tipo. En SQL: el cuerpo o la expresión nombra <c>"Servicio"</c>.
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

        foreach (var (nodo, tabla, forma) in Accesos(raiz, alias))
        {
            var contexto = ContextoDelAcceso(nodo);
            var tipo = NombreDeTipo(nodo);
            var filtra = forma == "SQL"
                ? ServicioSql.IsMatch(TextoDeLiteral(nodo))
                : ReferenciaUnPredicado(contexto, tipo, predicados)
                  || (ComparaServicio(contexto) && (tabla == Operacion || NombraLaOperacionDeLaCartera(contexto)));

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
    /// </summary>
    private static SyntaxNode ContextoDelAcceso(SyntaxNode acceso)
    {
        var cadena = CadenaDeLaConsulta(acceso);
        while (true)
        {
            var exterior = cadena.Ancestors().FirstOrDefault(a =>
                a is LambdaExpressionSyntax { Parent: ArgumentSyntax, Body: not BlockSyntax } or QueryExpressionSyntax
                    or LambdaExpressionSyntax { Body: BlockSyntax }
                    or MemberDeclarationSyntax or LocalFunctionStatementSyntax);
            if (exterior is null or MemberDeclarationSyntax or LocalFunctionStatementSyntax
                or LambdaExpressionSyntax { Body: BlockSyntax })
                return cadena;
            cadena = CadenaDeLaConsulta(exterior);
        }
    }

    private static SyntaxNode CadenaDeLaConsulta(SyntaxNode desde)
    {
        var actual = desde;
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
        }

        return actual;
    }

    /// <summary>
    /// La Asignación de Cartera no tiene Servicio: su servicio es el de su Asignación de Operación, así
    /// que la comparación de Servicio solo la filtra si la consulta la correlaciona con su operación.
    /// </summary>
    private static bool NombraLaOperacionDeLaCartera(SyntaxNode contexto) =>
        contexto.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>()
            .Any(m => m.Name.Identifier.ValueText is "AsignacionOperacionId" or "AsignacionOperacion");

    private static bool ComparaServicio(SyntaxNode contexto) =>
        contexto.DescendantNodesAndSelf().Any(n => n switch
        {
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression)
                => EsServicioDeInstancia(b.Left) || EsServicioDeInstancia(b.Right),
            // «o.Servicio is ServicioCae.Outbound»: el analizador lo lee como «is Tipo» (no sabe que
            // Outbound es un valor), así que llega como expresión binaria y no como patrón.
            BinaryExpressionSyntax b when b.IsKind(SyntaxKind.IsExpression) => EsServicioDeInstancia(b.Left),
            IsPatternExpressionSyntax p => EsServicioDeInstancia(p.Expression),
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Contains" } } i
                => i.ArgumentList.Arguments.Any(a => EsServicioDeInstancia(a.Expression)),
            _ => false,
        });

    /// <summary>
    /// <c>o.Servicio</c> o <c>c.Operacion.Servicio</c> sobre una <b>variable de la consulta</b>: un
    /// parámetro de una lambda que la rodea o una variable de rango (<c>from</c>, <c>join</c>,
    /// <c>let</c>). No <c>CatalogoIncorporacionCartera.Servicio</c> (una constante) ni
    /// <c>request.Servicio</c> (un valor que no es la fila).
    /// </summary>
    private static bool EsServicioDeInstancia(ExpressionSyntax expresion) =>
        expresion is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Servicio" } m
        && PrimerIdentificador(m.Expression) is { Length: > 0 } receptor
        && EsVariableDeConsulta(m, receptor);

    private static bool EsVariableDeConsulta(SyntaxNode uso, string nombre) =>
        uso.Ancestors().Any(a => a switch
        {
            SimpleLambdaExpressionSyntax s => s.Parameter.Identifier.ValueText == nombre,
            ParenthesizedLambdaExpressionSyntax p => p.ParameterList.Parameters.Any(x => x.Identifier.ValueText == nombre),
            QueryExpressionSyntax q => q.FromClause.Identifier.ValueText == nombre
                                       || q.Body.Clauses.Any(c => c switch
                                       {
                                           FromClauseSyntax f => f.Identifier.ValueText == nombre,
                                           JoinClauseSyntax j => j.Identifier.ValueText == nombre,
                                           LetClauseSyntax l => l.Identifier.ValueText == nombre,
                                           _ => false,
                                       }),
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
                    if (!ComparaServicio(miembro) || Accesos(miembro, new Dictionary<string, string>()).Any()) continue;
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

    private static bool ReferenciaUnPredicado(
        SyntaxNode contexto, string tipoActual, HashSet<(string Tipo, string Miembro)> predicados)
    {
        var tipoSimple = tipoActual.Split('.')[^1];
        foreach (var id in contexto.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var nombre = id.Identifier.ValueText;
            if (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id)
            {
                var receptor = ma.Expression switch
                {
                    IdentifierNameSyntax i => i.Identifier.ValueText,
                    MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                    _ => null,
                };
                if (receptor is not null && predicados.Contains((receptor, nombre)))
                    return true;
            }
            else if (id.Parent is InvocationExpressionSyntax or ArgumentSyntax
                     && predicados.Contains((tipoSimple, nombre)))
            {
                // Sin cualificar, solo como llamada (P(ahora)) o como argumento (Where(Predicado)):
                // un local o un parámetro con el mismo nombre no es el predicado.
                return true;
            }
        }

        return false;
    }

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

    private static readonly Regex Renombra = new(@"\bRENAME\s+TO\s+(?<nuevo>" + Nombre + ")", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Parentesis = new(@"\([^()]*\)", RegexOptions.Compiled);

    /// <summary>Los accesos del SQL vigente de las migraciones del repositorio.</summary>
    public static IReadOnlyList<AccesoAAsignacion> MedirMigraciones() => EnSql(SqlDeLasMigraciones());

    /// <summary>
    /// Recorre las migraciones en orden y se queda con la definición vigente de cada función y de cada
    /// política; devuelve un acceso por objeto vigente y tabla leída.
    /// </summary>
    public static IReadOnlyList<AccesoAAsignacion> EnSql(IReadOnlyList<(string Id, string Sql)> migraciones)
    {
        var funciones = new Dictionary<string, (string Id, string Texto)>(StringComparer.Ordinal);
        var politicas = new Dictionary<string, (string Id, string Texto)>(StringComparer.Ordinal);

        foreach (var (id, sql) in migraciones)
        {
            var codigo = EscanerSql.SoloCodigo(sql);
            var eventos = new List<(int Indice, Action Aplicar)>();

            foreach (var m in EscanerSql.CoincidenciasEnCodigo(CreaFuncion, sql))
            {
                var nombre = Normalizar(m.Groups["nombre"].Value);
                string cuerpo;
                try { cuerpo = EscanerSql.PartesDeFuncion(sql, m.Index).Cuerpo; }
                catch (InvalidOperationException) { cuerpo = sql[m.Index..FinDeSentencia(codigo, m.Index)]; }
                eventos.Add((m.Index, () => funciones[nombre] = (id, cuerpo)));
            }

            foreach (var m in EscanerSql.CoincidenciasEnCodigo(EliminaFuncion, sql))
            {
                var resto = sql[(m.Index + m.Length)..FinDeSentencia(codigo, m.Index)];
                foreach (var nombre in NombresEliminados(resto))
                    eventos.Add((m.Index, () => funciones.Remove(nombre)));
            }

            foreach (var m in EscanerSql.CoincidenciasEnCodigo(DefinePolitica, sql))
            {
                var clave = $"{m.Groups["nombre"].Value.Trim('"')} ON {Normalizar(m.Groups["tabla"].Value)}";
                var expresion = sql[(m.Index + m.Length)..FinDeSentencia(codigo, m.Index)];
                var verbo = m.Groups["verbo"].Value.ToUpperInvariant();
                eventos.Add((m.Index, () =>
                {
                    if (verbo == "DROP") { politicas.Remove(clave); return; }
                    if (verbo == "CREATE") { politicas[clave] = (id, expresion); return; }
                    if (Renombra.Match(EscanerSql.SinComentarios(expresion)) is { Success: true } r)
                    {
                        if (politicas.Remove(clave, out var vieja))
                            politicas[$"{r.Groups["nuevo"].Value.Trim('"')} ON {Normalizar(m.Groups["tabla"].Value)}"] = vieja;
                        return;
                    }

                    // ALTER POLICY con USING o WITH CHECK: puede sustituir una sola de las dos
                    // expresiones, así que se acumula (sobreaproxima la lectura; nunca la pierde).
                    politicas[clave] = (id, politicas.TryGetValue(clave, out var previa) ? previa.Texto + "\n" + expresion : expresion);
                }
                ));
            }

            foreach (var (_, aplicar) in eventos.OrderBy(e => e.Indice))
                aplicar();
        }

        var resultado = new List<AccesoAAsignacion>();
        foreach (var (nombre, (_, texto)) in funciones.OrderBy(f => f.Key, StringComparer.Ordinal))
            resultado.AddRange(AccesosDeTexto($"SQL función {nombre}", texto, "función"));
        foreach (var (clave, (_, texto)) in politicas.OrderBy(p => p.Key, StringComparer.Ordinal))
            resultado.AddRange(AccesosDeTexto($"SQL política {clave}", texto, "política"));
        return resultado;
    }

    /// <summary>La definición vigente (migración y texto) de cada objeto SQL que lee las tablas: para el inventario.</summary>
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

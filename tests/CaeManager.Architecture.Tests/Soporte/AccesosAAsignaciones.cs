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

    private static readonly Regex PalabraSql = new(
        @"\b(?:SELECT|UPDATE|DELETE|INSERT|FROM|JOIN)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ServicioSql = new(@"""Servicio""", RegexOptions.Compiled);

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
                foreach (Match m in TablaEnTexto.Matches(lineas[i]))
                    resultado.Add(new AccesoAAsignacion(ruta, "(razor)", m.Groups["tabla"].Value, "razor", false, i + 1));
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
                : ComparaServicio(contexto) || ReferenciaUnPredicado(contexto, tipo, predicados);

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
    /// Dónde se busca el filtro: la sentencia que contiene el acceso; si no la hay, la expresión del
    /// miembro (<c>=&gt; …</c>) o el inicializador del campo; si tampoco, el miembro entero.
    /// </summary>
    private static SyntaxNode ContextoDelAcceso(SyntaxNode acceso) =>
        acceso.AncestorsAndSelf().FirstOrDefault(n => n is StatementSyntax and not BlockSyntax)
        ?? acceso.AncestorsAndSelf().FirstOrDefault(n => n is ArrowExpressionClauseSyntax or EqualsValueClauseSyntax)
        ?? acceso.AncestorsAndSelf().FirstOrDefault(n => n is MemberDeclarationSyntax)
        ?? acceso;

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
    /// <c>o.Servicio</c> o <c>c.Operacion.Servicio</c>, no <c>CatalogoIncorporacionCartera.Servicio</c>:
    /// una constante comparada con otra cosa no filtra la fila.
    /// </summary>
    private static bool EsServicioDeInstancia(ExpressionSyntax expresion) =>
        expresion is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Servicio" } m
        && PrimerIdentificador(m.Expression) is { Length: > 0 } receptor
        && char.IsLower(receptor[0]);

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
                    if (miembro is TypeDeclarationSyntax) continue;
                    if (!ComparaServicio(miembro) || Accesos(miembro, new Dictionary<string, string>()).Any()) continue;
                    foreach (var nombre in NombresDeclarados(miembro))
                        predicados.Add((tipo.Identifier.ValueText, nombre));
                }
            }
        }

        return predicados;
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
            else if (predicados.Contains((tipoSimple, nombre)))
            {
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
                .UpOperations.OfType<SqlOperation>().Select(o => o.Sql))))
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
                }));
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
    public static IEnumerable<AccesoAAsignacion> AccesosDeTexto(string lugar, string texto, string forma)
    {
        var limpio = EscanerSql.SinComentarios(texto);
        var filtra = ServicioSql.IsMatch(limpio);
        return TablaSql.Matches(limpio).Select(m => m.Groups["tabla"].Value).Distinct(StringComparer.Ordinal)
            .Select(t => new AccesoAAsignacion(lugar, string.Empty, t, forma, filtra, 0));
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

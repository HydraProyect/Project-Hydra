using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Decisión de producto D-P11 (2026-10-30, ajustada el 2026-11-12): el constructor público de <c>Empresa</c> deja
/// <c>EsPropia = true</c>, y todo Tenant propietario solo puede tener una Empresa propia. Todo método fuera de Domain (las
/// factorías de contraparte) y de los seeders de demostración que pueda dejar una Empresa propia activa más (construirla,
/// restaurarla de la Papelera, cambiar en bloque <c>EsPropia</c> o <c>EstaEliminado</c>) tiene que pasar por
/// <c>ILimiteEmpresaPropia.ComprobarAltaAsync</c> o <c>ComprobarGuardadoAsync</c>, y su clase tiene que guardar dentro de
/// <c>ILimiteEmpresaPropia.SerializarAsync</c>, el candado por Tenant.
/// <para>
/// Instrumento sintáctico con Roslyn, por método (revisión Codex r1 de la PR #2193, MEDIO): ve <c>new Empresa(...)</c> con
/// nombre calificado o con alias de <c>using</c>, <c>new(...)</c> con tipo destino Empresa en una declaración, una
/// asignación, un <c>return</c> o un <c>Agregar</c>/<c>Add</c> de un repositorio o conjunto de Empresas, las
/// restauraciones de una variable leída de Empresas, <c>SetProperty</c> de <c>EsPropia</c> o <c>EstaEliminado</c>, SQL que
/// escribe esas columnas de <c>"Empresas"</c>, y una asignación de <c>EsPropia</c> dentro de la clase <c>Empresa</c> fuera
/// de su constructor. Sin modelo semántico no resuelve tipos: un <c>new(...)</c> pasado a un método cualquiera, o una
/// Empresa restaurada a través de una variable con nombre y origen ajenos a Empresa, se le escapan. Que el recuento quede
/// dentro del candado lo prueban <c>CandadoEmpresaPropiaTests</c> (integración); aquí solo se exige que la clase lo use.
/// </para>
/// </summary>
public class AltaDeEmpresaPropiaSoloTrasElLimiteTests
{
    // Restaurar una Empresa desde la Papelera también puede dejar una Empresa propia activa más.
    private static readonly string[] RestauracionesConLimite =
    [
        "src/CaeManager.Application/Empresas/Commands/RestaurarEmpresa/RestaurarEmpresaCommand.cs",
        "src/CaeManager.Application/Clientes/Commands/RestaurarCliente/RestaurarClienteCommand.cs",
        "src/CaeManager.Application/Subcontratas/Commands/RestaurarSubcontrata/RestaurarSubcontrataCommand.cs",
    ];

    private static readonly string[] EscritoresConLimite =
    [
        "src/CaeManager.Application/Empresas/Commands/CrearEmpresa/CrearEmpresaCommand.cs",
        "src/CaeManager.Application/Importacion/Commands/EjecutarImportacion/EjecutarImportacionCommand.cs",
        "src/CaeManager.Application/Importacion/Commands/EjecutarImportacionCombinada/EjecutarImportacionCombinadaCommand.cs",
        "src/CaeManager.Application/Importacion/Commands/EjecutarImportacionMaestra/EjecutarImportacionMaestraCommand.cs",
    ];

    // Las factorías de Cliente empresarial y Subcontrata ponen EsPropia = false; los seeders siembran Tenants de
    // demostración sin Producto Contratado. Empresa.cs no queda exento de la regla de EsPropia fuera del constructor.
    private static readonly string[] ExentosDelLimite =
    [
        "src/CaeManager.Domain/Empresas/Empresa.cs",
        "src/CaeManager.Infrastructure/Persistence/Seed/CoberturaPantallasSeeder.cs",
        "src/CaeManager.Infrastructure/Persistence/Seed/DatosPruebaSeeder.cs",
        "src/CaeManager.Infrastructure/Persistence/Seed/EscenariosDireccionDemoSeeder.cs",
        "src/CaeManager.Infrastructure/Persistence/Seed/GestorCaeCarteraMultiTenantSeeder.cs",
        "src/CaeManager.Infrastructure/Persistence/Seed/PilotoOutboundSeeder.cs",
    ];

    // Un componente no construye ni restaura una Empresa: envía un Command. Los .razor no se analizan con Roslyn.
    private static readonly Regex EscrituraEnRazor = new(
        @"\bnew\s+(?:global::)?(?:\w+\.)*Empresa\s*\(|\bempresa\w*\s*\.\s*Restaurar\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void Solo_los_escritores_con_el_limite_y_los_exentos_escriben_una_Empresa_propia()
    {
        var quienesEscriben = EscriturasDeSrc()
            .Where(e => !e.EsPropiaEnDominio)
            .Select(e => e.Fichero)
            .Distinct()
            .ToList();

        // Igualdad, no subconjunto: si uno deja de escribir, la lista se queda vieja y se pierde el control positivo
        // del instrumento.
        quienesEscriben.Should().BeEquivalentTo(EscritoresConLimite.Concat(RestauracionesConLimite).Concat(ExentosDelLimite));
    }

    [Fact]
    public void Cada_metodo_que_escribe_una_Empresa_propia_comprueba_el_limite_y_su_clase_guarda_bajo_el_candado()
    {
        var sinGuarda = EscriturasDeSrc()
            .Where(e => !ExentosDelLimite.Contains(e.Fichero))
            .Where(e => !e.MiembroComprueba || !e.TipoSerializa)
            .Select(e => e.ToString())
            .ToList();

        sinGuarda.Should().BeEmpty("cada método que puede dejar una Empresa propia activa más pasa por ILimiteEmpresaPropia");
    }

    [Fact]
    public void Dentro_de_Empresa_solo_el_constructor_pone_EsPropia_a_algo_distinto_de_false()
    {
        EscriturasDeSrc().Where(e => e.EsPropiaEnDominio).Select(e => e.ToString())
            .Should().BeEmpty("una Empresa solo nace propia por el constructor, y ese camino lo vigila el límite");
    }

    [Fact]
    public void Ningun_componente_razor_construye_ni_restaura_una_Empresa()
    {
        var raiz = RaizDelRepositorio();
        FicherosDeSrc(raiz, "*.razor")
            .Where(f => EscrituraEnRazor.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(raiz, f).Replace('\\', '/'))
            .Should().BeEmpty();
    }

    // ---- Pruebas del propio instrumento: lo que tiene que ver y lo que no ----

    [Theory]
    [InlineData("tipo destino", "class H { void M() { Empresa e = new(\"x\"); } }")]
    [InlineData("alias de using", "using E = CaeManager.Domain.Empresas.Empresa; class H { void M() { var e = new E(\"x\"); } }")]
    [InlineData("nombre calificado", "class H { void M() { var e = new global::CaeManager.Domain.Empresas.Empresa(\"x\"); } }")]
    [InlineData("new() a un repositorio", "class H { void M(IEmpresaRepository empresaRepositorio) { empresaRepositorio.Agregar(new(\"x\")); } }")]
    [InlineData("new() devuelto", "class H { Task<Empresa> M() => Task.FromResult<Empresa>(null!); Empresa N() { return new(\"x\"); } }")]
    [InlineData("asignación a una Empresa", "class H { Empresa _e = null!; void M() { _e = new(\"x\"); } }")]
    [InlineData("restauración", "class H { async Task M(C c) { var empresa = await c.Empresas.FirstAsync(); empresa.Restaurar(); } }")]
    [InlineData("restauración con otro nombre", "class H { async Task M(C c) { var fila = await c.Empresas.IgnoreQueryFilters().FirstAsync(); fila.Restaurar(); } }")]
    [InlineData("SetProperty de EsPropia", "class H { Task M(C c) => c.Empresas.ExecuteUpdateAsync(s => s.SetProperty(e => e.EsPropia, true)); }")]
    [InlineData("SetProperty de EstaEliminado", "class H { Task M(C c) => c.Empresas.ExecuteUpdateAsync(s => s.SetProperty(e => e.EstaEliminado, false)); }")]
    [InlineData("SQL que escribe", "class H { Task M(D d) => d.ExecuteSqlRawAsync(\"UPDATE \\\"Empresas\\\" SET \\\"EsPropia\\\" = true\"); }")]
    [InlineData("SQL interpolado que restaura", "class H { Task M(D d, Guid id) => d.ExecuteSqlAsync($\"UPDATE \\\"Empresas\\\" SET \\\"EstaEliminado\\\" = false WHERE \\\"Id\\\" = {id}\"); }")]
    public void El_instrumento_ve_un_escritor_sin_guarda(string caso, string codigo)
    {
        var escrituras = EscriturasDeEmpresaPropia.Analizar("Fragmento.cs", codigo);

        escrituras.Should().ContainSingle(caso);
        escrituras[0].MiembroComprueba.Should().BeFalse(caso);
        escrituras[0].TipoSerializa.Should().BeFalse(caso);
    }

    [Fact]
    public void El_instrumento_comprueba_por_metodo_y_no_por_fichero()
    {
        const string codigo = """
            class H(ILimiteEmpresaPropia limite)
            {
                async Task<Result> Bueno() => await limite.SerializarAsync(async ct =>
                {
                    if (await limite.ComprobarAltaAsync(0, true, ct) is { } error) return Result.Fallo(error);
                    repositorio.Agregar(new Empresa("x"));
                    return Result.Exito();
                });

                void Malo() => repositorio.Agregar(new Empresa("y"));
            }
            """;

        var escrituras = EscriturasDeEmpresaPropia.Analizar("Fragmento.cs", codigo);

        escrituras.Should().HaveCount(2);
        escrituras.Single(e => e.Miembro == "Bueno").Should().Match<EscriturasDeEmpresaPropia.Escritura>(e => e.MiembroComprueba && e.TipoSerializa);
        escrituras.Single(e => e.Miembro == "Malo").MiembroComprueba.Should().BeFalse("la guarda de otro método del mismo fichero no cuenta");
    }

    [Fact]
    public void El_instrumento_ve_EsPropia_fuera_del_constructor_de_Empresa()
    {
        const string codigo = """
            class Empresa
            {
                public bool EsPropia { get; private set; }
                public Empresa(string r) { EsPropia = true; }
                public static Empresa CrearComoCliente(string r) => new Empresa(r) { EsPropia = false };
                public void HacerPropia() => EsPropia = true;
            }
            """;

        var escrituras = EscriturasDeEmpresaPropia.Analizar("Empresa.cs", codigo);

        escrituras.Where(e => e.EsPropiaEnDominio).Should().ContainSingle().Which.Miembro.Should().Be("HacerPropia");
    }

    [Theory]
    [InlineData("class H { void M() { var r = new EmpresaCliente(a, b); } }")]
    [InlineData("class H { void M() { var c = Empresa.CrearComoCliente(\"x\", null, true, null, null); } }")]
    [InlineData("class H { async Task M(C c) { var centro = await c.Centros.FirstAsync(); centro.Restaurar(); } }")]
    [InlineData("class H { Task M(D d) => d.SqlQuery<int>($\"SELECT count(*) FROM \\\"Empresas\\\" WHERE \\\"EsPropia\\\"\"); }")]
    [InlineData("class H { void M() { List<Empresa> l = new(); } }")]
    public void El_instrumento_no_ve_lo_que_no_escribe_una_Empresa_propia(string codigo)
    {
        EscriturasDeEmpresaPropia.Analizar("Fragmento.cs", codigo).Should().BeEmpty();
    }

    private static List<EscriturasDeEmpresaPropia.Escritura> EscriturasDeSrc()
    {
        var raiz = RaizDelRepositorio();
        return FicherosDeSrc(raiz, "*.cs")
            // Las migraciones crean y transforman el esquema; no son un escritor de Empresas del Tenant.
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}CaeManager.Migrations.PostgreSQL{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => EscriturasDeEmpresaPropia.Analizar(
                Path.GetRelativePath(raiz, f).Replace('\\', '/'), File.ReadAllText(f)))
            .ToList();
    }

    private static IEnumerable<string> FicherosDeSrc(string raiz, string patron) => Directory
        .EnumerateFiles(Path.Combine(raiz, "src"), patron, SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string RaizDelRepositorio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.GetFiles("*.slnx").Any() && !dir.GetFiles("*.sln").Any())
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}

/// <summary>El instrumento sintáctico de <see cref="AltaDeEmpresaPropiaSoloTrasElLimiteTests"/>.</summary>
internal static class EscriturasDeEmpresaPropia
{
    internal sealed record Escritura(
        string Fichero, int Linea, string Tipo, string Miembro, string Que,
        bool MiembroComprueba, bool TipoSerializa, bool EsPropiaEnDominio)
    {
        public override string ToString() => $"{Fichero}:{Linea} {Tipo}.{Miembro} {Que}";
    }

    private static readonly string[] Comprobaciones = ["ComprobarAltaAsync", "ComprobarGuardadoAsync"];
    private static readonly string[] Altas = ["Agregar", "Add", "AddAsync", "AddRange", "Attach", "Update"];

    internal static List<Escritura> Analizar(string fichero, string codigo)
    {
        var raiz = CSharpSyntaxTree.ParseText(codigo).GetRoot();
        var nombres = NombresDeEmpresa(raiz);
        var declaradas = DeclaradasComoEmpresa(raiz, nombres);
        var leidas = LeidasDeEmpresas(raiz);
        var resultado = new List<Escritura>();

        foreach (var nodo in raiz.DescendantNodes())
        {
            var que = nodo switch
            {
                ObjectCreationExpressionSyntax c when nombres.Contains(NombreSimple(c.Type)) => "construye una Empresa",
                ImplicitObjectCreationExpressionSyntax c when DestinoEsEmpresa(c, nombres, declaradas) => "construye una Empresa con new(...)",
                InvocationExpressionSyntax i when EsRestauracion(i, declaradas, leidas) => "restaura una Empresa",
                InvocationExpressionSyntax i when EsSetPropertyDelLimite(i) => "cambia EsPropia o EstaEliminado en bloque",
                LiteralExpressionSyntax or InterpolatedStringExpressionSyntax when EsSqlQueEscribe(nodo) => "escribe EsPropia o EstaEliminado de Empresas por SQL",
                _ => null,
            };
            var esPropiaEnDominio = que is null && nodo is AssignmentExpressionSyntax a && PoneEsPropiaFueraDelConstructor(a);
            if (esPropiaEnDominio) que = "pone EsPropia fuera del constructor de Empresa";
            if (que is null) continue;

            var miembro = MiembroDe(nodo);
            var tipo = nodo.Ancestors().OfType<TypeDeclarationSyntax>().LastOrDefault();
            resultado.Add(new Escritura(
                fichero,
                nodo.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                tipo?.Identifier.ValueText ?? "(global)",
                NombreDeMiembro(miembro),
                que,
                MiembroComprueba: miembro is not null && Invoca(miembro, Comprobaciones),
                TipoSerializa: tipo is not null && Invoca(tipo, ["SerializarAsync"]),
                EsPropiaEnDominio: esPropiaEnDominio));
        }

        return resultado;
    }

    /// <summary>«Empresa» y los alias de <c>using</c> que la nombran.</summary>
    private static HashSet<string> NombresDeEmpresa(SyntaxNode raiz)
    {
        var nombres = new HashSet<string>(StringComparer.Ordinal) { "Empresa" };
        foreach (var u in raiz.DescendantNodes().OfType<UsingDirectiveSyntax>())
            if (u.Alias is { } alias && u.NamespaceOrType is TypeSyntax t && NombreSimple(t) == "Empresa")
                nombres.Add(alias.Name.Identifier.ValueText);
        return nombres;
    }

    /// <summary>Variables, parámetros, campos y propiedades declarados con tipo Empresa.</summary>
    private static HashSet<string> DeclaradasComoEmpresa(SyntaxNode raiz, HashSet<string> nombres)
    {
        var declaradas = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nodo in raiz.DescendantNodes())
        {
            switch (nodo)
            {
                case VariableDeclarationSyntax d when EsTipoEmpresa(d.Type, nombres):
                    foreach (var v in d.Variables) declaradas.Add(v.Identifier.ValueText);
                    break;
                case ParameterSyntax { Type: { } t } p when EsTipoEmpresa(t, nombres):
                    declaradas.Add(p.Identifier.ValueText);
                    break;
                case PropertyDeclarationSyntax p when EsTipoEmpresa(p.Type, nombres):
                    declaradas.Add(p.Identifier.ValueText);
                    break;
                case ForEachStatementSyntax f when EsTipoEmpresa(f.Type, nombres):
                    declaradas.Add(f.Identifier.ValueText);
                    break;
            }
        }
        return declaradas;
    }

    /// <summary>
    /// Variables cuyo valor sale de algo que nombra Empresas (<c>var x = await contexto.Empresas...</c>,
    /// <c>var x = await empresaRepositorio...</c>, <c>foreach (var x in empresas)</c>).
    /// </summary>
    private static HashSet<string> LeidasDeEmpresas(SyntaxNode raiz)
    {
        var leidas = new HashSet<string>(StringComparer.Ordinal);
        foreach (var nodo in raiz.DescendantNodes())
        {
            switch (nodo)
            {
                case VariableDeclaratorSyntax { Initializer.Value: { } valor } v when NombraEmpresas(valor):
                    leidas.Add(v.Identifier.ValueText);
                    break;
                case ForEachStatementSyntax f when NombraEmpresas(f.Expression):
                    leidas.Add(f.Identifier.ValueText);
                    break;
            }
        }
        return leidas;
    }

    /// <summary>
    /// El conjunto <c>Empresas</c> de un contexto, o un repositorio de Empresas (<c>empresaRepositorio</c>, <c>repositorio</c>
    /// no: es demasiado genérico). No basta con que aparezca «Empresa»: <c>v.EmpresaId</c> no lee una Empresa.
    /// </summary>
    private static bool NombraEmpresas(SyntaxNode expresion) => expresion.DescendantNodesAndSelf().OfType<SimpleNameSyntax>()
        .Select(n => n.Identifier.ValueText)
        .Any(n => n == "Empresas"
                  || (n.StartsWith("empresa", StringComparison.OrdinalIgnoreCase)
                      && (n.EndsWith("Repositorio", StringComparison.Ordinal) || n.EndsWith("Repository", StringComparison.Ordinal))));

    private static bool DestinoEsEmpresa(ImplicitObjectCreationExpressionSyntax c, HashSet<string> nombres, HashSet<string> declaradas)
    {
        switch (c.Parent)
        {
            case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax d } }:
                return EsTipoEmpresa(d.Type, nombres);
            case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax p }:
                return EsTipoEmpresa(p.Type, nombres);
            case EqualsValueClauseSyntax { Parent: ParameterSyntax { Type: { } t } }:
                return EsTipoEmpresa(t, nombres);
            case AssignmentExpressionSyntax a when a.Right == c:
                return a.Left switch
                {
                    IdentifierNameSyntax izquierda => declaradas.Contains(izquierda.Identifier.ValueText),
                    MemberAccessExpressionSyntax acceso => declaradas.Contains(acceso.Name.Identifier.ValueText),
                    _ => false,
                };
            case ReturnStatementSyntax or ArrowExpressionClauseSyntax:
                return TipoDevuelto(c) is { } devuelto && EsTipoEmpresa(devuelto, nombres);
            case ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax i } }:
                return i.Expression is MemberAccessExpressionSyntax m
                       && Altas.Contains(m.Name.Identifier.ValueText)
                       && NombraEmpresas(m.Expression);
            default:
                return false;
        }
    }

    private static TypeSyntax? TipoDevuelto(SyntaxNode nodo)
    {
        foreach (var ancestro in nodo.Ancestors())
        {
            switch (ancestro)
            {
                case AnonymousFunctionExpressionSyntax: return null;
                case LocalFunctionStatementSyntax f: return f.ReturnType;
                case MethodDeclarationSyntax m: return m.ReturnType;
                case PropertyDeclarationSyntax p: return p.Type;
            }
        }
        return null;
    }

    private static bool EsTipoEmpresa(TypeSyntax tipo, HashSet<string> nombres) => tipo switch
    {
        NullableTypeSyntax n => EsTipoEmpresa(n.ElementType, nombres),
        GenericNameSyntax { Identifier.ValueText: "Task" or "ValueTask", TypeArgumentList.Arguments.Count: 1 } g
            => EsTipoEmpresa(g.TypeArgumentList.Arguments[0], nombres),
        QualifiedNameSyntax { Right: GenericNameSyntax g } => EsTipoEmpresa(g, nombres),
        _ => nombres.Contains(NombreSimple(tipo)),
    };

    private static bool EsRestauracion(InvocationExpressionSyntax i, HashSet<string> declaradas, HashSet<string> leidas)
    {
        if (i.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Restaurar" } m
            || i.ArgumentList.Arguments.Count != 0)
            return false;

        return m.Expression switch
        {
            IdentifierNameSyntax id => declaradas.Contains(id.Identifier.ValueText) || leidas.Contains(id.Identifier.ValueText)
                                       || id.Identifier.ValueText.Contains("empresa", StringComparison.OrdinalIgnoreCase),
            MemberAccessExpressionSyntax interior => interior.Name.Identifier.ValueText.Contains("Empresa", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool EsSetPropertyDelLimite(InvocationExpressionSyntax i) =>
        i.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "SetProperty" }
        && i.ArgumentList.Arguments.FirstOrDefault()?.Expression is LambdaExpressionSyntax { Body: MemberAccessExpressionSyntax cuerpo }
        && cuerpo.Name.Identifier.ValueText is "EsPropia" or "EstaEliminado";

    private static bool EsSqlQueEscribe(SyntaxNode nodo)
    {
        // Un literal dentro de una cadena interpolada ya cuenta con la cadena entera.
        if (nodo is LiteralExpressionSyntax && nodo.Ancestors().OfType<InterpolatedStringExpressionSyntax>().Any())
            return false;
        var texto = nodo switch
        {
            LiteralExpressionSyntax l => l.Token.ValueText,
            InterpolatedStringExpressionSyntax s => string.Concat(
                s.Contents.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText)),
            _ => string.Empty,
        };
        return texto.Contains("\"Empresas\"", StringComparison.Ordinal)
               && (texto.Contains("\"EsPropia\"", StringComparison.Ordinal) || texto.Contains("\"EstaEliminado\"", StringComparison.Ordinal))
               && (texto.Contains("UPDATE", StringComparison.OrdinalIgnoreCase) || texto.Contains("INSERT", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Dentro de la clase Empresa, <c>EsPropia = </c> algo que no es <c>false</c>, fuera de un constructor.</summary>
    private static bool PoneEsPropiaFueraDelConstructor(AssignmentExpressionSyntax a) =>
        a.Left is IdentifierNameSyntax { Identifier.ValueText: "EsPropia" }
        && !a.Right.IsKind(SyntaxKind.FalseLiteralExpression)
        && a.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText == "Empresa"
        && !a.Ancestors().OfType<ConstructorDeclarationSyntax>().Any();

    /// <summary>El miembro que contiene el nodo; una lambda o una función local cuentan como parte del método que las declara.</summary>
    private static MemberDeclarationSyntax? MiembroDe(SyntaxNode nodo) => nodo.Ancestors().OfType<MemberDeclarationSyntax>()
        .FirstOrDefault(m => m is not BaseTypeDeclarationSyntax and not BaseNamespaceDeclarationSyntax);

    private static string NombreDeMiembro(MemberDeclarationSyntax? miembro) => miembro switch
    {
        MethodDeclarationSyntax m => m.Identifier.ValueText,
        ConstructorDeclarationSyntax c => c.Identifier.ValueText,
        PropertyDeclarationSyntax p => p.Identifier.ValueText,
        FieldDeclarationSyntax f => string.Join(",", f.Declaration.Variables.Select(v => v.Identifier.ValueText)),
        null => "(sin miembro)",
        _ => miembro.Kind().ToString(),
    };

    private static bool Invoca(SyntaxNode ambito, string[] nombres) => ambito.DescendantNodes().OfType<InvocationExpressionSyntax>()
        .Any(i => i.Expression switch
        {
            MemberAccessExpressionSyntax m => nombres.Contains(m.Name.Identifier.ValueText),
            SimpleNameSyntax s => nombres.Contains(s.Identifier.ValueText),
            _ => false,
        });

    private static string NombreSimple(TypeSyntax tipo) => tipo switch
    {
        IdentifierNameSyntax i => i.Identifier.ValueText,
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        _ => string.Empty,
    };
}

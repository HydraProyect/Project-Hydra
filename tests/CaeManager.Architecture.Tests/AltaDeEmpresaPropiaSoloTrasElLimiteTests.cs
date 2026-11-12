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
/// <c>ILimiteEmpresaPropia.ComprobarAltaAsync</c> o <c>ComprobarGuardadoAsync</c>, y la comprobación que decide y el
/// guardado tienen que ir dentro del delegado pasado a <c>ILimiteEmpresaPropia.SerializarAsync</c>, el candado por Tenant.
/// <para>
/// Instrumento sintáctico con Roslyn, por método (revisión Codex r1 de la PR #2193, MEDIO): ve <c>new Empresa(...)</c> con
/// nombre calificado o con alias de <c>using</c>, <c>new(...)</c> con tipo destino Empresa en una declaración, una
/// asignación, un <c>return</c> o un <c>Agregar</c>/<c>Add</c> de un repositorio o conjunto de Empresas, las
/// restauraciones de una variable leída de Empresas, <c>SetProperty</c> de <c>EsPropia</c> o <c>EstaEliminado</c>, SQL que
/// escribe esas columnas de <c>"Empresas"</c>, y una asignación de <c>EsPropia</c> dentro de la clase <c>Empresa</c> fuera
/// de su constructor. Sin modelo semántico no resuelve tipos: un <c>new(...)</c> pasado a un método cualquiera, o una
/// Empresa restaurada a través de una variable con nombre y origen ajenos a Empresa, se le escapan.
/// </para>
/// <para>
/// El candado (revisión Codex r2 de la PR #2193, MEDIO 2): cuenta solo un <c>SerializarAsync</c> cuyo receptor está
/// declarado en el tipo como <c>ILimiteEmpresaPropia</c>, y solo si lo que alcanza su delegado (él mismo y las funciones
/// locales y métodos del tipo a los que llama por su nombre) comprueba el límite con ese receptor y guarda. La escritura
/// tiene que estar en ese alcance, o en el método que contiene el delegado sin ningún guardado fuera de él salvo en la
/// rama <c>if (n == 0)</c> del recuento final (ver <c>CandadosDelTipo</c>). No mira el orden entre comprobación y
/// guardado, ni otros llamantes sin candado de un método alcanzado. Que el guardado ocurra con el candado tomado lo
/// prueban además <c>CandadoEmpresaPropiaTests</c> (integración, en CI).
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
    public void Cada_metodo_que_escribe_una_Empresa_propia_comprueba_el_limite_y_guarda_dentro_del_candado()
    {
        var sinGuarda = EscriturasDeSrc()
            .Where(e => !ExentosDelLimite.Contains(e.Fichero))
            .Where(e => !e.MiembroComprueba || !e.BajoElCandado)
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
        escrituras[0].BajoElCandado.Should().BeFalse(caso);
    }

    [Fact]
    public void El_instrumento_comprueba_por_metodo_y_no_por_fichero()
    {
        const string codigo = """
            class H(ILimiteEmpresaPropia limite, IUnitOfWork unitOfWork)
            {
                async Task<Result> Bueno() => await limite.SerializarAsync(async ct =>
                {
                    if (await limite.ComprobarAltaAsync(0, true, ct) is { } error) return Result.Fallo(error);
                    repositorio.Agregar(new Empresa("x"));
                    await unitOfWork.SaveChangesAsync(ct);
                    return Result.Exito();
                });

                void Malo() => repositorio.Agregar(new Empresa("y"));
            }
            """;

        var escrituras = EscriturasDeEmpresaPropia.Analizar("Fragmento.cs", codigo);

        escrituras.Should().HaveCount(2);
        escrituras.Single(e => e.Miembro == "Bueno").Should().Match<EscriturasDeEmpresaPropia.Escritura>(e => e.MiembroComprueba && e.BajoElCandado);
        escrituras.Single(e => e.Miembro == "Malo").Should().Match<EscriturasDeEmpresaPropia.Escritura>(
            e => !e.MiembroComprueba && !e.BajoElCandado, "la guarda de otro método del mismo fichero no cuenta");
    }

    // Revisión Codex r2 de la PR #2193, MEDIO 2: lo que tiene que ir dentro del delegado de SerializarAsync.
    [Theory]
    [InlineData("comprobación antes del candado y guardado después", """
        class H(ILimiteEmpresaPropia limite, IUnitOfWork unitOfWork)
        {
            async Task<Result> M(CancellationToken ct)
            {
                if (await limite.ComprobarAltaAsync(0, true, ct) is { } error) return Result.Fallo(error);
                repositorio.Agregar(new Empresa("x"));
                await limite.SerializarAsync(c => Task.FromResult(Result.Exito()), ct);
                await unitOfWork.SaveChangesAsync(ct);
                return Result.Exito();
            }
        }
        """)]
    [InlineData("comprobación dentro y guardado después", """
        class H(ILimiteEmpresaPropia limite, IUnitOfWork unitOfWork)
        {
            async Task<Result> M(CancellationToken ct)
            {
                repositorio.Agregar(new Empresa("x"));
                await limite.SerializarAsync(async c => await limite.ComprobarAltaAsync(0, true, c) is { } e ? Result.Fallo(e) : Result.Exito(), ct);
                await unitOfWork.SaveChangesAsync(ct);
                return Result.Exito();
            }
        }
        """)]
    [InlineData("guardado dentro y otro guardado fuera", """
        class H(ILimiteEmpresaPropia limite, IUnitOfWork unitOfWork)
        {
            async Task<Result> M(int nuevas, CancellationToken ct)
            {
                repositorio.Agregar(new Empresa("x"));
                await unitOfWork.SaveChangesAsync(ct);
                return await limite.SerializarAsync(async c =>
                {
                    if (await limite.ComprobarGuardadoAsync(nuevas, c) is { } e) return Result.Fallo(e);
                    await unitOfWork.SaveChangesAsync(c);
                    return Result.Exito();
                }, ct);
            }
        }
        """)]
    [InlineData("guardado fuera en una función local", """
        class H(ILimiteEmpresaPropia limite, IUnitOfWork unitOfWork)
        {
            async Task<Result> M(int nuevas, CancellationToken ct)
            {
                repositorio.Agregar(new Empresa("x"));
                await Guardar(ct);
                return await limite.SerializarAsync(async c =>
                    await limite.ComprobarGuardadoAsync(nuevas, c) is { } e ? Result.Fallo(e) : await Guardar(c), ct);

                async Task<Result> Guardar(CancellationToken t) { await unitOfWork.SaveChangesAsync(t); return Result.Exito(); }
            }
        }
        """)]
    [InlineData("SerializarAsync homónimo de otro tipo", """
        class H(ILimiteEmpresaPropia limite, IOtroCandado otro, IUnitOfWork unitOfWork)
        {
            Task<Result> M() => otro.SerializarAsync(async ct =>
            {
                if (await limite.ComprobarAltaAsync(0, true, ct) is { } error) return Result.Fallo(error);
                repositorio.Agregar(new Empresa("x"));
                await unitOfWork.SaveChangesAsync(ct);
                return Result.Exito();
            });
        }
        """)]
    [InlineData("comprobación homónima de otro tipo dentro del candado", """
        class H(ILimiteEmpresaPropia limite, IOtroLimite otro, IUnitOfWork unitOfWork)
        {
            Task<Result> M() => limite.SerializarAsync(async ct =>
            {
                if (await otro.ComprobarAltaAsync(0, true, ct) is { } error) return Result.Fallo(error);
                repositorio.Agregar(new Empresa("x"));
                await unitOfWork.SaveChangesAsync(ct);
                return Result.Exito();
            });
        }
        """)]
    [InlineData("guardado fuera aunque la rama de cero sea de otro recuento", """
        class H(ILimiteEmpresaPropia limite, IUnitOfWork unitOfWork)
        {
            async Task<Result> M(int nuevas, int otras, CancellationToken ct)
            {
                repositorio.Agregar(new Empresa("x"));
                if (otras == 0) await unitOfWork.SaveChangesAsync(ct);
                return await limite.SerializarAsync(async c =>
                {
                    if (await limite.ComprobarGuardadoAsync(nuevas, c) is { } e) return Result.Fallo(e);
                    await unitOfWork.SaveChangesAsync(c);
                    return Result.Exito();
                }, ct);
            }
        }
        """)]
    public void El_instrumento_exige_comprobacion_y_guardado_dentro_del_candado_del_limite(string caso, string codigo)
    {
        EscriturasDeEmpresaPropia.Analizar("Fragmento.cs", codigo)
            .Should().ContainSingle(caso).Which.BajoElCandado.Should().BeFalse(caso);
    }

    // Las tres formas de los escritores reales: todo dentro del delegado (alta y restauraciones), preparar fila a fila y
    // guardar bajo el candado con la rama sin Empresas propias nuevas (importaciones), y escribir en un método al que el
    // delegado llama (importación maestra).
    [Theory]
    [InlineData("rama de cero con guardado directo", """
        class H(ILimiteEmpresaPropia limiteEmpresaPropia, IOperacionImportacionRepository operaciones)
        {
            async Task<Result> M(CancellationToken ct)
            {
                var empresasCreadas = 0;
                if (await limiteEmpresaPropia.ComprobarAltaAsync(empresasCreadas, true, ct) is null) { repositorio.Agregar(new Empresa("x")); empresasCreadas++; }
                if (empresasCreadas == 0)
                {
                    if (!await operaciones.GuardarSiOperacionNuevaAsync(ct)) return Result.Exito();
                }
                else
                {
                    var guardado = await limiteEmpresaPropia.SerializarAsync(async c =>
                    {
                        if (await limiteEmpresaPropia.ComprobarGuardadoAsync(empresasCreadas, c) is { } e) return Result.Fallo(e);
                        await operaciones.GuardarSiOperacionNuevaAsync(c);
                        return Result.Exito();
                    }, ct);
                }
                return Result.Exito();
            }
        }
        """)]
    [InlineData("rama de cero con función local", """
        class H(ILimiteEmpresaPropia limiteEmpresaPropia, IOperacionImportacionRepository operaciones)
        {
            async Task<Result> M(CancellationToken ct)
            {
                var empresasCreadas = 0;
                if (await limiteEmpresaPropia.ComprobarAltaAsync(empresasCreadas, true, ct) is null) { repositorio.Agregar(new Empresa("x")); empresasCreadas++; }
                return await GuardarAsync(ct);

                async Task<Result> GuardarAsync(CancellationToken t)
                {
                    if (empresasCreadas == 0)
                        return await GuardarOperacionAsync(t);
                    return await limiteEmpresaPropia.SerializarAsync(async c =>
                        await limiteEmpresaPropia.ComprobarGuardadoAsync(empresasCreadas, c) is { } e ? Result.Fallo(e) : await GuardarOperacionAsync(c), t);
                }

                async Task<Result> GuardarOperacionAsync(CancellationToken t) =>
                    await operaciones.GuardarSiOperacionNuevaAsync(t) ? Result.Exito() : Result.Fallo(null!);
            }
        }
        """)]
    [InlineData("escritura en un método al que llama el delegado", """
        class H(ILimiteEmpresaPropia limiteEmpresaPropia, IOperacionImportacionRepository operaciones)
        {
            Task<Result> Confirmar(CancellationToken ct) => limiteEmpresaPropia.SerializarAsync(async c => await this.Procesar(c), ct);

            async Task<Result> Procesar(CancellationToken ct)
            {
                if (await limiteEmpresaPropia.ComprobarAltaAsync(0, true, ct) is { } e) return Result.Fallo(e);
                repositorio.Agregar(new Empresa("x"));
                await operaciones.GuardarSiOperacionNuevaAsync(ct);
                return Result.Exito();
            }
        }
        """)]
    public void El_instrumento_acepta_las_formas_de_los_escritores_con_el_candado(string caso, string codigo)
    {
        EscriturasDeEmpresaPropia.Analizar("Fragmento.cs", codigo)
            .Should().ContainSingle(caso).Which.Should().Match<EscriturasDeEmpresaPropia.Escritura>(e => e.MiembroComprueba && e.BajoElCandado, caso);
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
        bool MiembroComprueba, bool BajoElCandado, bool EsPropiaEnDominio)
    {
        public override string ToString() => $"{Fichero}:{Linea} {Tipo}.{Miembro} {Que}";
    }

    private const string TipoDelLimite = "ILimiteEmpresaPropia";
    private static readonly string[] Comprobaciones = ["ComprobarAltaAsync", "ComprobarGuardadoAsync"];
    private static readonly string[] Altas = ["Agregar", "Add", "AddAsync", "AddRange", "Attach", "Update"];
    private static readonly string[] Guardados = ["SaveChangesAsync", "SaveChanges", "GuardarCambiosAsync", "GuardarSiOperacionNuevaAsync"];

    internal static List<Escritura> Analizar(string fichero, string codigo)
    {
        var raiz = CSharpSyntaxTree.ParseText(codigo).GetRoot();
        var nombres = NombresDeEmpresa(raiz);
        var declaradas = DeclaradasComoEmpresa(raiz, nombres);
        var leidas = LeidasDeEmpresas(raiz);
        var candados = new Dictionary<TypeDeclarationSyntax, CandadosDelTipo>();
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
            if (tipo is not null && !candados.ContainsKey(tipo)) candados[tipo] = new CandadosDelTipo(tipo);
            resultado.Add(new Escritura(
                fichero,
                nodo.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                tipo?.Identifier.ValueText ?? "(global)",
                NombreDeMiembro(miembro),
                que,
                MiembroComprueba: miembro is not null && Invoca(miembro, Comprobaciones),
                BajoElCandado: tipo is not null && candados[tipo].Cubre(nodo, miembro),
                EsPropiaEnDominio: esPropiaEnDominio));
        }

        return resultado;
    }

    /// <summary>
    /// Los delegados que un tipo pasa a <c>SerializarAsync</c> de un receptor declarado en él como
    /// <c>ILimiteEmpresaPropia</c> (campo, propiedad o parámetro, también del constructor primario), y lo que alcanzan.
    /// Revisión Codex r2 de la PR #2193, MEDIO 2: no basta con que la clase invoque algo llamado <c>SerializarAsync</c>;
    /// la comprobación que decide y el guardado tienen que ir dentro de ese delegado. «Dentro» incluye las funciones
    /// locales y los métodos del mismo tipo que el delegado invoca por su nombre, de forma transitiva (la importación
    /// maestra recuenta y guarda en un método al que llama desde el candado). Sin modelo semántico: los métodos se
    /// resuelven por nombre (todas sus sobrecargas), y un guardado a través de otro tipo no se ve.
    /// </summary>
    private sealed class CandadosDelTipo
    {
        private readonly HashSet<string> receptores;
        private readonly ILookup<string, SyntaxNode> funciones;
        private readonly List<(AnonymousFunctionExpressionSyntax Delegado, HashSet<SyntaxNode> Alcance)> validos = [];
        private readonly HashSet<string> recuentosFinales = new(StringComparer.Ordinal);

        public CandadosDelTipo(TypeDeclarationSyntax tipo)
        {
            receptores = ReceptoresDelLimite(tipo);
            funciones = tipo.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Select(m => (Nombre: m.Identifier.ValueText, Nodo: (SyntaxNode)m))
                .Concat(tipo.DescendantNodes().OfType<LocalFunctionStatementSyntax>()
                    .Select(f => (Nombre: f.Identifier.ValueText, Nodo: (SyntaxNode)f)))
                .ToLookup(x => x.Nombre, x => x.Nodo, StringComparer.Ordinal);

            foreach (var serializa in tipo.DescendantNodes().OfType<InvocationExpressionSyntax>()
                         .Where(i => EsDelLimite(i, ["SerializarAsync"])))
            {
                if (serializa.ArgumentList.Arguments.FirstOrDefault()?.Expression is not AnonymousFunctionExpressionSyntax delegado)
                    continue;
                var alcance = Alcance(delegado);
                var invocaciones = alcance.SelectMany(r => r.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()).ToList();
                var comprobaciones = invocaciones.Where(i => EsDelLimite(i, Comprobaciones)).ToList();
                if (comprobaciones.Count == 0 || !invocaciones.Any(EsGuardado))
                    continue;
                validos.Add((delegado, alcance));
                foreach (var c in comprobaciones)
                    if (c.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ComprobarGuardadoAsync" }
                        && c.ArgumentList.Arguments.FirstOrDefault()?.Expression is IdentifierNameSyntax recuento)
                        recuentosFinales.Add(recuento.Identifier.ValueText);
            }
        }

        /// <summary>
        /// La escritura queda bajo el candado si está en lo que alcanza un delegado válido, o si su método lo contiene y
        /// prepara antes en memoria (las importaciones construyen fila a fila y recuentan al final, ya bajo el candado)
        /// sin guardar nada fuera de él. Un guardado fuera solo vale en la rama <c>if (n == 0)</c>, con <c>n</c> el
        /// recuento que el delegado pasa a <c>ComprobarGuardadoAsync</c>: sin Empresas propias nuevas no hay candado.
        /// </summary>
        public bool Cubre(SyntaxNode escritura, MemberDeclarationSyntax? miembro)
        {
            if (validos.Any(v => escritura.AncestorsAndSelf().Any(v.Alcance.Contains)))
                return true;
            return miembro is not null
                   && validos.Any(v => v.Delegado.Ancestors().Contains(miembro))
                   && !GuardaFueraDelCandado(miembro, [miembro]);
        }

        private HashSet<SyntaxNode> Alcance(AnonymousFunctionExpressionSyntax delegado)
        {
            var alcance = new HashSet<SyntaxNode> { delegado };
            var pendientes = new Queue<SyntaxNode>(alcance);
            while (pendientes.TryDequeue(out var raiz))
                foreach (var llamada in raiz.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    foreach (var funcion in Llamadas(llamada))
                        if (alcance.Add(funcion))
                            pendientes.Enqueue(funcion);
            return alcance;
        }

        private bool GuardaFueraDelCandado(SyntaxNode raiz, HashSet<SyntaxNode> visitadas)
        {
            foreach (var llamada in raiz.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var ancestros = llamada.Ancestors().TakeWhile(a => a != raiz).ToList();
                // Lo de dentro de un delegado válido va bajo el candado; una función local se recorre solo si se la llama.
                if (ancestros.Any(a => validos.Any(v => v.Delegado == a) || a is LocalFunctionStatementSyntax)
                    || EnLaRamaSinPropiasNuevas(llamada, raiz))
                    continue;
                if (EsGuardado(llamada))
                    return true;
                foreach (var funcion in Llamadas(llamada))
                    if (visitadas.Add(funcion) && GuardaFueraDelCandado(funcion, visitadas))
                        return true;
            }
            return false;
        }

        private bool EnLaRamaSinPropiasNuevas(SyntaxNode nodo, SyntaxNode raiz)
        {
            SyntaxNode hijo = nodo;
            foreach (var ancestro in nodo.Ancestors().TakeWhile(a => a != raiz))
            {
                if (ancestro is IfStatementSyntax si && si.Statement == hijo
                    && si.Condition is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.EqualsExpression } igual
                    && (EsRecuentoACero(igual.Left, igual.Right) || EsRecuentoACero(igual.Right, igual.Left)))
                    return true;
                hijo = ancestro;
            }
            return false;
        }

        private bool EsRecuentoACero(ExpressionSyntax recuento, ExpressionSyntax cero) =>
            recuento is IdentifierNameSyntax id && recuentosFinales.Contains(id.Identifier.ValueText)
            && cero is LiteralExpressionSyntax { Token.ValueText: "0" };

        /// <summary>Las funciones locales y los métodos del tipo a los que llama <paramref name="llamada"/> por su nombre.</summary>
        private IEnumerable<SyntaxNode> Llamadas(InvocationExpressionSyntax llamada) => llamada.Expression switch
        {
            IdentifierNameSyntax n => funciones[n.Identifier.ValueText],
            GenericNameSyntax g => funciones[g.Identifier.ValueText],
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } m => funciones[m.Name.Identifier.ValueText],
            _ => [],
        };

        private bool EsDelLimite(InvocationExpressionSyntax llamada, string[] metodos) =>
            llamada.Expression is MemberAccessExpressionSyntax m
            && metodos.Contains(m.Name.Identifier.ValueText)
            && m.Expression switch
            {
                IdentifierNameSyntax r => receptores.Contains(r.Identifier.ValueText),
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } r => receptores.Contains(r.Name.Identifier.ValueText),
                _ => false,
            };

        private static bool EsGuardado(InvocationExpressionSyntax llamada) =>
            llamada.Expression is MemberAccessExpressionSyntax m && Guardados.Contains(m.Name.Identifier.ValueText);

        private static HashSet<string> ReceptoresDelLimite(TypeDeclarationSyntax tipo)
        {
            var receptores = new HashSet<string>(StringComparer.Ordinal);
            foreach (var nodo in tipo.DescendantNodesAndSelf())
            {
                switch (nodo)
                {
                    case ParameterSyntax { Type: { } t } p when NombreSimple(t) == TipoDelLimite:
                        receptores.Add(p.Identifier.ValueText);
                        break;
                    case FieldDeclarationSyntax f when NombreSimple(f.Declaration.Type) == TipoDelLimite:
                        foreach (var v in f.Declaration.Variables) receptores.Add(v.Identifier.ValueText);
                        break;
                    case PropertyDeclarationSyntax p when NombreSimple(p.Type) == TipoDelLimite:
                        receptores.Add(p.Identifier.ValueText);
                        break;
                }
            }
            return receptores;
        }
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

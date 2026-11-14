using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// #2009, revisión Codex r3 de la PR #2213 (hallazgo b): nunca sale un correo de acuse de la aceptación contractual
/// que no esté guardado. El acuse es una bandeja de salida de solo inserción (<c>AcuseAceptacionContractualTenant</c>):
/// la fila Preparado se guarda antes del correo, y el resultado (Enviado o NoConfirmado) después.
/// <para>
/// Inventario de escritores: solo <c>EnvioAcuseAceptacionContractual</c> construye filas de acuse y solo el repositorio
/// las añade al contexto. Igualdad, no subconjunto: un escritor nuevo, o uno que deje de serlo, rompe el test y obliga a
/// revisar la regla. Dentro de <c>EnviarAsync</c>, el instrumento exige que el primer guardado vaya antes del primer
/// envío de correo, que antes de ese guardado solo se añada la fase Preparado (como argumento de <c>AgregarFase</c> o
/// <c>AgregarAcuse</c>), y que antes de ese guardado el método salga (<c>return</c> o <c>throw</c>) con una transacción
/// abierta o con cambios pendientes, cualquiera de las dos.
/// </para>
/// <para>
/// Instrumento sintáctico con Roslyn, sin modelo semántico: reconoce <c>new AcuseAceptacionContractualTenant(...)</c>
/// (también calificado), <c>new(...)</c> pasado a <c>AgregarAcuse</c>, las llamadas a <c>AgregarAcuse</c>, un
/// <c>Add</c>/<c>AddRange</c>/<c>Attach</c> sobre <c>AcusesAceptacionContractualTenant</c> y SQL que inserta en esa
/// tabla. El orden se mide por posición en el texto del método, que basta porque <c>EnviarAsync</c> no tiene bucles ni
/// saltos hacia atrás entre esos pasos. Un correo enviado a través de otro tipo con otro nombre no se ve; la integración
/// (<c>AceptacionContractualTenantBajoRlsTests</c>, en CI) prueba que el repositorio ve la transacción abierta.
/// </para>
/// </summary>
public class AcuseAceptacionContractualSeGuardaAntesDelCorreoTests
{
    private const string Servicio = "src/CaeManager.Application/Cumplimiento/AceptacionContractual/AcuseAceptacionContractual.cs";

    private static readonly string[] EscritoresDelAcuse =
    [
        Servicio,
        "src/CaeManager.Infrastructure/Persistence/Repositories/AceptacionContractualTenantRepository.cs",
    ];

    [Fact]
    public void Solo_el_envio_del_acuse_y_su_repositorio_escriben_filas_de_acuse()
    {
        var raiz = RaizDelRepositorio();
        var escritores = FicherosDeSrc(raiz)
            .Select(f => (Fichero: Path.GetRelativePath(raiz, f).Replace('\\', '/'), Codigo: File.ReadAllText(f)))
            .Where(f => InstrumentoAcuse.Escrituras(f.Codigo).Count > 0)
            .Select(f => f.Fichero)
            .ToList();

        escritores.Should().BeEquivalentTo(EscritoresDelAcuse);
    }

    [Fact]
    public void El_envio_del_acuse_guarda_la_preparacion_antes_de_enviar_el_correo()
    {
        var codigo = File.ReadAllText(Path.Combine(RaizDelRepositorio(), Servicio.Replace('/', Path.DirectorySeparatorChar)));

        InstrumentoAcuse.Infracciones(codigo, "EnviarAsync").Should().BeEmpty();
    }

    // ---- Pruebas del propio instrumento ----

    [Theory]
    [InlineData("construcción", "class H { void M() { var a = new AcuseAceptacionContractualTenant(x, y, z, f, t); } }")]
    [InlineData("construcción calificada", "class H { void M() { var a = new CaeManager.Domain.Cumplimiento.AcuseAceptacionContractualTenant(x, y, z, f, t); } }")]
    [InlineData("AgregarAcuse con new()", "class H { void M(IAceptacionContractualTenantRepository r) { r.AgregarAcuse(new(x, y, z, f, t)); } }")]
    [InlineData("Add al conjunto", "class H { void M(C c, object a) { c.AcusesAceptacionContractualTenant.Add(a); } }")]
    [InlineData("SQL que inserta", "class H { Task M(D d) => d.ExecuteSqlRawAsync(\"INSERT INTO \\\"AcusesAceptacionContractualTenant\\\" VALUES (1)\"); }")]
    public void El_instrumento_ve_un_escritor_del_acuse(string caso, string codigo)
    {
        InstrumentoAcuse.Escrituras(codigo).Should().NotBeEmpty(caso);
    }

    [Theory]
    [InlineData("class H { Task M(C c) => c.AcusesAceptacionContractualTenant.AsNoTracking().ToListAsync(); }")]
    [InlineData("class H { Task M(D d) => d.ExecuteSqlRawAsync(\"DELETE FROM \\\"AcusesAceptacionContractualTenant\\\"\"); }")]
    [InlineData("interface I { void AgregarAcuse(AcuseAceptacionContractualTenant acuse); }")]
    public void El_instrumento_no_ve_lo_que_no_escribe_un_acuse(string codigo)
    {
        InstrumentoAcuse.Escrituras(codigo).Should().BeEmpty();
    }

    [Theory]
    [InlineData("correo antes del guardado", """
        class H
        {
            async Task<Result> EnviarAsync(CancellationToken ct)
            {
                if (repositorio.HayTransaccionAbierta || repositorio.HayCambiosPendientes) return Result.Fallo(e);
                var envio = await correo.EnviarAsync(a, b, c, d);
                AgregarFase(documentos, u, id, FaseAcuseAceptacionContractual.Preparado);
                await unitOfWork.SaveChangesAsync(ct);
                return envio;
            }
        }
        """)]
    [InlineData("sin guardado", """
        class H
        {
            async Task<Result> EnviarAsync(CancellationToken ct)
            {
                if (repositorio.HayTransaccionAbierta || repositorio.HayCambiosPendientes) return Result.Fallo(e);
                AgregarFase(documentos, u, id, FaseAcuseAceptacionContractual.Preparado);
                return await correo.EnviarAsync(a, b, c, d);
            }
        }
        """)]
    [InlineData("se guarda el resultado y no la preparación", """
        class H
        {
            async Task<Result> EnviarAsync(CancellationToken ct)
            {
                if (repositorio.HayTransaccionAbierta || repositorio.HayCambiosPendientes) return Result.Fallo(e);
                AgregarFase(documentos, u, id, FaseAcuseAceptacionContractual.Enviado);
                await unitOfWork.SaveChangesAsync(ct);
                return await correo.EnviarAsync(a, b, c, d);
            }
        }
        """)]
    [InlineData("sin negativa ante una transacción abierta", """
        class H
        {
            async Task<Result> EnviarAsync(CancellationToken ct)
            {
                AgregarFase(documentos, u, id, FaseAcuseAceptacionContractual.Preparado);
                await unitOfWork.SaveChangesAsync(ct);
                return await correo.EnviarAsync(a, b, c, d);
            }
        }
        """)]
    // Revisión Codex r4 de la PR #2213, BAJO: nombrar Preparado no basta si no se añade, y la negativa tiene que salir.
    [InlineData("Preparado nombrado pero no añadido", """
        class H
        {
            async Task<Result> EnviarAsync(CancellationToken ct)
            {
                if (repositorio.HayTransaccionAbierta || repositorio.HayCambiosPendientes) return Result.Fallo(e);
                var fase = FaseAcuseAceptacionContractual.Preparado;
                await unitOfWork.SaveChangesAsync(ct);
                return await correo.EnviarAsync(a, b, c, d);
            }
        }
        """)]
    [InlineData("negativa que no sale", """
        class H
        {
            async Task<Result> EnviarAsync(CancellationToken ct)
            {
                if (repositorio.HayTransaccionAbierta || repositorio.HayCambiosPendientes) logger.LogWarning("x");
                AgregarFase(documentos, u, id, FaseAcuseAceptacionContractual.Preparado);
                await unitOfWork.SaveChangesAsync(ct);
                return await correo.EnviarAsync(a, b, c, d);
            }
        }
        """)]
    [InlineData("negativa solo si coinciden las dos", """
        class H
        {
            async Task<Result> EnviarAsync(CancellationToken ct)
            {
                if (repositorio.HayTransaccionAbierta && repositorio.HayCambiosPendientes) return Result.Fallo(e);
                AgregarFase(documentos, u, id, FaseAcuseAceptacionContractual.Preparado);
                await unitOfWork.SaveChangesAsync(ct);
                return await correo.EnviarAsync(a, b, c, d);
            }
        }
        """)]
    public void El_instrumento_ve_un_envio_que_no_guarda_antes(string caso, string codigo)
    {
        InstrumentoAcuse.Infracciones(codigo, "EnviarAsync").Should().NotBeEmpty(caso);
    }

    [Fact]
    public void El_instrumento_acepta_la_forma_correcta()
    {
        const string codigo = """
            class H
            {
                async Task<Result> EnviarAsync(CancellationToken ct)
                {
                    if (repositorio.HayTransaccionAbierta || repositorio.HayCambiosPendientes) return Result.Fallo(e);
                    AgregarFase(documentos, u, id, FaseAcuseAceptacionContractual.Preparado);
                    await unitOfWork.SaveChangesAsync(ct);
                    var envio = await correo.EnviarAsync(a, b, c, d);
                    AgregarFase(documentos, u, id, envio.EsFallido ? FaseAcuseAceptacionContractual.NoConfirmado : FaseAcuseAceptacionContractual.Enviado);
                    await unitOfWork.SaveChangesAsync(CancellationToken.None);
                    return envio;
                }
            }
            """;

        InstrumentoAcuse.Infracciones(codigo, "EnviarAsync").Should().BeEmpty();
    }

    private static IEnumerable<string> FicherosDeSrc(string raiz) => Directory
        .EnumerateFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    // Las migraciones crean el esquema; no escriben acuses.
                    && !f.Contains($"{Path.DirectorySeparatorChar}CaeManager.Migrations.PostgreSQL{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string RaizDelRepositorio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.GetFiles("*.slnx").Any() && !dir.GetFiles("*.sln").Any())
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}

/// <summary>El instrumento sintáctico de <see cref="AcuseAceptacionContractualSeGuardaAntesDelCorreoTests"/>.</summary>
internal static class InstrumentoAcuse
{
    private const string Entidad = "AcuseAceptacionContractualTenant";
    private const string Tabla = "AcusesAceptacionContractualTenant";
    private static readonly string[] Altas = ["Add", "AddAsync", "AddRange", "AddRangeAsync", "Attach", "Update"];
    private static readonly string[] Guardados = ["SaveChangesAsync", "SaveChanges", "GuardarCambiosAsync"];

    internal static List<SyntaxNode> Escrituras(string codigo)
    {
        var raiz = CSharpSyntaxTree.ParseText(codigo).GetRoot();
        return raiz.DescendantNodes().Where(nodo => nodo switch
        {
            ObjectCreationExpressionSyntax c => NombreSimple(c.Type) == Entidad,
            InvocationExpressionSyntax i when Nombre(i) == "AgregarAcuse" => true,
            InvocationExpressionSyntax i => Altas.Contains(Nombre(i))
                && i.Expression is MemberAccessExpressionSyntax m && NombreFinal(m.Expression) == Tabla,
            LiteralExpressionSyntax l => l.Token.ValueText.Contains($"\"{Tabla}\"", StringComparison.Ordinal)
                && l.Token.ValueText.Contains("INSERT", StringComparison.OrdinalIgnoreCase),
            _ => false,
        }).ToList();
    }

    /// <summary>Lo que falta en <paramref name="metodo"/> para que el correo salga solo con la preparación guardada.</summary>
    internal static List<string> Infracciones(string codigo, string metodo)
    {
        var raiz = CSharpSyntaxTree.ParseText(codigo).GetRoot();
        var cuerpo = raiz.DescendantNodes().OfType<MethodDeclarationSyntax>().SingleOrDefault(m => m.Identifier.ValueText == metodo && (m.Body is not null || m.ExpressionBody is not null));
        if (cuerpo is null)
            return [$"no hay ningún método {metodo}"];

        var llamadas = cuerpo.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        var guardado = llamadas.FirstOrDefault(i => Guardados.Contains(Nombre(i)));
        var correo = llamadas.FirstOrDefault(i => Nombre(i) == "EnviarAsync"
            && i.Expression is MemberAccessExpressionSyntax m && NombreFinal(m.Expression) == "correo");
        var infracciones = new List<string>();
        if (correo is null)
            infracciones.Add("no envía el correo con correo.EnviarAsync: el instrumento ya no ve el envío");
        if (guardado is null)
        {
            infracciones.Add("no guarda nada");
            return infracciones;
        }

        if (correo is not null && correo.SpanStart < guardado.SpanStart)
            infracciones.Add("el correo sale antes del primer guardado");

        // Antes del primer guardado solo se nombra la fase Preparado, y como argumento directo de la llamada que añade
        // filas de acuse (AgregarFase o AgregarAcuse): mencionarla en otro sitio no la añade.
        var fasesAntes = cuerpo.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.SpanStart < guardado.SpanStart && NombreFinal(m.Expression) == "FaseAcuseAceptacionContractual")
            .ToList();
        var preparadoAnadido = fasesAntes.Any(m => m.Name.Identifier.ValueText == "Preparado"
            && m.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax alta } }
            && Nombre(alta) is "AgregarFase" or "AgregarAcuse");
        if (!preparadoAnadido || fasesAntes.Any(m => m.Name.Identifier.ValueText != "Preparado"))
            infracciones.Add($"antes del primer guardado no se añade solo Preparado (se nombra [{string.Join(", ", fasesAntes.Select(m => m.Name.Identifier.ValueText))}])");

        // La negativa sale del método: un if que no devuelve deja seguir hasta el guardado.
        var negativa = cuerpo.DescendantNodes().OfType<IfStatementSyntax>().FirstOrDefault(s =>
            s.Condition.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(m => m.Name.Identifier.ValueText == "HayTransaccionAbierta")
            && s.Condition.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(m => m.Name.Identifier.ValueText == "HayCambiosPendientes")
            && s.Condition is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalOrExpression }
            && s.Statement is ReturnStatementSyntax or ThrowStatementSyntax);
        if (negativa is null || negativa.SpanStart > guardado.SpanStart)
            infracciones.Add("no sale del método con una transacción abierta o cambios pendientes antes del primer guardado");

        return infracciones;
    }

    private static string Nombre(InvocationExpressionSyntax i) => i.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax b => b.Name.Identifier.ValueText,
        SimpleNameSyntax s => s.Identifier.ValueText,
        _ => string.Empty,
    };

    private static string NombreFinal(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax i => i.Identifier.ValueText,
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    private static string NombreSimple(TypeSyntax tipo) => tipo switch
    {
        IdentifierNameSyntax i => i.Identifier.ValueText,
        QualifiedNameSyntax q => q.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax a => a.Name.Identifier.ValueText,
        _ => string.Empty,
    };
}

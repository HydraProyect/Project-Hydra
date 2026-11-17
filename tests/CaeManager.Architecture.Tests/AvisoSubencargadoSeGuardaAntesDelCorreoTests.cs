using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// #2010, D-7: nunca sale un correo de aviso de un subencargado que no esté guardado, y el aviso solo lo escriben su
/// Command y su repositorio. Mismo criterio que <see cref="AcuseAceptacionContractualSeGuardaAntesDelCorreoTests"/>:
/// bandeja de salida de solo inserción, Preparado guardado antes del correo y el resultado después.
/// <para>
/// Inventario de escritores por igualdad: <c>new AvisoCambioSubencargado(...)</c> (también calificado), las llamadas a
/// <c>AgregarAviso</c>, un <c>Add</c>/<c>AddRange</c>/<c>Attach</c>/<c>Update</c> sobre <c>AvisosCambioSubencargado</c>
/// y SQL que inserta en esa tabla. Dentro de <c>EnviarAsync</c> del Command: el primer guardado va antes de
/// <c>correo.EnviarAsync</c>, antes de él solo se nombra la fase Preparado, y el correo sale con
/// <c>CancellationToken.None</c>. En <c>Handle</c>: la negativa por transacción abierta o cambios pendientes sale del
/// método antes del primer envío. Instrumento sintáctico, sin modelo semántico; el orden se mide por posición en el
/// texto, que basta porque esos pasos no tienen saltos hacia atrás entre sí.
/// </para>
/// </summary>
public class AvisoSubencargadoSeGuardaAntesDelCorreoTests
{
    private const string Command = "src/CaeManager.Application/Cumplimiento/Subencargados/AvisarCambioSubencargadoCommand.cs";
    private const string Repositorio = "src/CaeManager.Infrastructure/Persistence/Repositories/SubencargadoRepository.cs";
    private const string Entidad = "AvisoCambioSubencargado";
    private const string Tabla = "AvisosCambioSubencargado";
    private static readonly string[] Altas = ["Add", "AddAsync", "AddRange", "AddRangeAsync", "Attach", "Update"];
    private static readonly string[] Guardados = ["SaveChangesAsync", "SaveChanges", "GuardarCambiosAsync"];

    [Fact]
    public void Solo_el_Command_del_aviso_y_su_repositorio_escriben_avisos()
    {
        var raiz = RaizDelRepositorio();
        var escritores = Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or "CaeManager.Migrations.PostgreSQL"))
            .Select(f => (Fichero: Path.GetRelativePath(raiz, f).Replace('\\', '/'), Codigo: File.ReadAllText(f)))
            .Where(f => Escrituras(f.Codigo).Count > 0)
            .Select(f => f.Fichero)
            .ToList();

        escritores.Should().BeEquivalentTo([Command, Repositorio]);
    }

    [Fact]
    public void El_aviso_guarda_la_preparacion_antes_de_enviar_el_correo()
    {
        var codigo = File.ReadAllText(Path.Combine(RaizDelRepositorio(), Command.Replace('/', Path.DirectorySeparatorChar)));

        Infracciones(codigo).Should().BeEmpty();
    }

    [Theory]
    [InlineData("correo antes del guardado", """
        class H {
          async Task Handle() { if (r.HayTransaccionAbierta || r.HayCambiosPendientes) return; await EnviarAsync(); }
          async Task EnviarAsync() {
            await correo.EnviarAsync(a, b, c, d, cancellationToken: CancellationToken.None);
            r.AgregarAviso(new AvisoCambioSubencargado(s, e, u, FaseAvisoCambioSubencargado.Preparado, t, x));
            await u.SaveChangesAsync(ct);
          }
        }
        """)]
    [InlineData("Enviado antes del guardado", """
        class H {
          async Task Handle() { if (r.HayTransaccionAbierta || r.HayCambiosPendientes) return; await EnviarAsync(); }
          async Task EnviarAsync() {
            r.AgregarAviso(new AvisoCambioSubencargado(s, e, u, FaseAvisoCambioSubencargado.Enviado, t, x));
            await u.SaveChangesAsync(ct);
            await correo.EnviarAsync(a, b, c, d, cancellationToken: CancellationToken.None);
          }
        }
        """)]
    [InlineData("sin negativa", """
        class H {
          async Task Handle() { await EnviarAsync(); }
          async Task EnviarAsync() {
            r.AgregarAviso(new AvisoCambioSubencargado(s, e, u, FaseAvisoCambioSubencargado.Preparado, t, x));
            await u.SaveChangesAsync(ct);
            await correo.EnviarAsync(a, b, c, d, cancellationToken: CancellationToken.None);
          }
        }
        """)]
    [InlineData("correo cancelable", """
        class H {
          async Task Handle() { if (r.HayTransaccionAbierta || r.HayCambiosPendientes) return; await EnviarAsync(); }
          async Task EnviarAsync() {
            r.AgregarAviso(new AvisoCambioSubencargado(s, e, u, FaseAvisoCambioSubencargado.Preparado, t, x));
            await u.SaveChangesAsync(ct);
            await correo.EnviarAsync(a, b, c, d, cancellationToken: ct);
          }
        }
        """)]
    public void El_instrumento_ve_cada_infraccion(string caso, string codigo)
    {
        Infracciones(codigo).Should().NotBeEmpty(caso);
    }

    [Fact]
    public void El_instrumento_acepta_la_forma_correcta_y_ve_las_escrituras()
    {
        const string codigo = """
            class H {
              async Task Handle() { if (r.HayTransaccionAbierta || r.HayCambiosPendientes) return; await EnviarAsync(); }
              async Task EnviarAsync() {
                r.AgregarAviso(new AvisoCambioSubencargado(s, e, u, FaseAvisoCambioSubencargado.Preparado, t, x));
                await u.SaveChangesAsync(ct);
                await correo.EnviarAsync(a, b, c, d, cancellationToken: CancellationToken.None);
                r.AgregarAviso(new AvisoCambioSubencargado(s, e, u, FaseAvisoCambioSubencargado.Enviado, t, x));
                await u.SaveChangesAsync(CancellationToken.None);
              }
            }
            """;

        Infracciones(codigo).Should().BeEmpty();
        Escrituras("class H { void M(C c, object a) { c.AvisosCambioSubencargado.Add(a); } }").Should().NotBeEmpty();
        Escrituras("class H { Task M(D d) => d.ExecuteSqlRawAsync(\"INSERT INTO \\\"AvisosCambioSubencargado\\\" VALUES (1)\"); }").Should().NotBeEmpty();
        Escrituras("class H { Task M(C c) => c.AvisosCambioSubencargado.AsNoTracking().AnyAsync(); }").Should().BeEmpty();
    }

    private static List<SyntaxNode> Escrituras(string codigo) =>
        CSharpSyntaxTree.ParseText(codigo).GetRoot().DescendantNodes().Where(nodo => nodo switch
        {
            ObjectCreationExpressionSyntax c => NombreSimple(c.Type) == Entidad,
            InvocationExpressionSyntax i when Nombre(i) == "AgregarAviso" => true,
            InvocationExpressionSyntax i => Altas.Contains(Nombre(i))
                && i.Expression is MemberAccessExpressionSyntax m && NombreFinal(m.Expression) == Tabla,
            LiteralExpressionSyntax l => l.Token.ValueText.Contains($"\"{Tabla}\"", StringComparison.Ordinal)
                && l.Token.ValueText.Contains("INSERT", StringComparison.OrdinalIgnoreCase),
            _ => false,
        }).ToList();

    private static List<string> Infracciones(string codigo)
    {
        var raiz = CSharpSyntaxTree.ParseText(codigo).GetRoot();
        var metodos = raiz.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();
        var envio = metodos.SingleOrDefault(m => m.Identifier.ValueText == "EnviarAsync");
        var handle = metodos.SingleOrDefault(m => m.Identifier.ValueText == "Handle");
        if (envio is null || handle is null)
            return ["no están Handle y EnviarAsync: el instrumento ya no ve el envío"];

        var infracciones = new List<string>();
        var llamadas = envio.DescendantNodes().OfType<InvocationExpressionSyntax>().ToList();
        var guardado = llamadas.FirstOrDefault(i => Guardados.Contains(Nombre(i)));
        var correo = llamadas.FirstOrDefault(i => Nombre(i) == "EnviarAsync"
            && i.Expression is MemberAccessExpressionSyntax m && NombreFinal(m.Expression) == "correo");
        if (guardado is null || correo is null)
            return ["EnviarAsync no guarda o no envía con correo.EnviarAsync"];

        if (correo.SpanStart < guardado.SpanStart)
            infracciones.Add("el correo sale antes del primer guardado");

        var fasesAntes = envio.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
            .Where(m => m.SpanStart < guardado.SpanStart && NombreFinal(m.Expression) == "FaseAvisoCambioSubencargado")
            .Select(m => m.Name.Identifier.ValueText).ToList();
        if (!fasesAntes.SequenceEqual(["Preparado"]))
            infracciones.Add($"antes del primer guardado no se nombra solo Preparado ([{string.Join(", ", fasesAntes)}])");

        if (!correo.ArgumentList.Arguments.Any(a => a.NameColon?.Name.Identifier.ValueText == "cancellationToken"
                                                     && a.Expression.ToString() == "CancellationToken.None"))
            infracciones.Add("el correo se puede cancelar a medias con la preparación ya guardada");

        var primerEnvio = handle.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(i => Nombre(i) == "EnviarAsync");
        var negativa = handle.DescendantNodes().OfType<IfStatementSyntax>().FirstOrDefault(s =>
            s.Condition is BinaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalOrExpression }
            && s.Condition.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(m => m.Name.Identifier.ValueText == "HayTransaccionAbierta")
            && s.Condition.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>().Any(m => m.Name.Identifier.ValueText == "HayCambiosPendientes")
            && s.Statement is ReturnStatementSyntax or ThrowStatementSyntax);
        if (primerEnvio is null || negativa is null || negativa.SpanStart > primerEnvio.SpanStart)
            infracciones.Add("Handle no sale con una transacción abierta o cambios pendientes antes del primer envío");

        return infracciones;
    }

    private static string Nombre(InvocationExpressionSyntax i) => i.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
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
        _ => string.Empty,
    };

    private static string RaizDelRepositorio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.GetFiles("*.slnx").Any() && !dir.GetFiles("*.sln").Any())
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}

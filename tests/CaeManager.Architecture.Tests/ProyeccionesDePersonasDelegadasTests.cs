using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

public class ProyeccionesDePersonasDelegadasTests
{
    [Theory]
    [InlineData("EsCuentaActivaConRolAsync", "new { u.Id, u.LockoutEnd }")]
    [InlineData("ObtenerCuentasActivasConRolAsync", "new { u.Id, u.LockoutEnd }")]
    [InlineData("ObtenerAsync", "new { u.TenantId, u.LockoutEnd, u.CoordinadorUsuarioId }")]
    public void Directorio_proyecta_los_campos_de_cuenta(string metodo, string proyeccion)
    {
        var cuerpo = Metodo("src/CaeManager.Infrastructure/Autorizacion/DirectorioUsuariosTenant.cs", metodo);
        cuerpo.Should().Contain(proyeccion);
        cuerpo.Should().NotContain("select u)");
        cuerpo.Should().NotContain("FindByIdAsync");
        cuerpo.Should().NotContain("FirstOrDefaultAsync(u =>");
    }

    [Fact]
    public void Lectura_de_personas_delegadas_proyecta_y_no_busca_cuentas_individualmente()
    {
        var lectura = Metodo("src/CaeManager.Infrastructure/Autorizacion/LecturaPersonasDelegadas.cs", "ObtenerAsync");
        lectura.Should().Contain(".Select(u => new").And.Contain("u.NombreCompleto").And.Contain("u.Email");
        lectura.Should().NotContain("FindByIdAsync").And.NotContain("select u)").And.NotContain("PasswordHash");
        var pagina = Metodo("src/CaeManager.Web/Features/Delegaciones/Pages/Delegaciones.razor.cs", "CargarNombresDeOperadoresAsync");
        pagina.Should().Contain("LecturaPersonas.ObtenerAsync").And.NotContain("FindByIdAsync").And.NotContain("GetRolesAsync");
    }

    /// <summary>
    /// Acepta cualquier identificador y devuelve el rol de Identity: fuera de la página que lo pinta sería el rol de
    /// origen con otro nombre, y el rol de origen no autoriza (<see cref="RolDeOrigenFueraDeAutorizacionTests"/>).
    /// Comparte el contexto scoped sin puerta propia: la única consumidora la llama dentro de <c>PuertaAccesoDatos</c>.
    /// </summary>
    [Fact]
    public void La_lectura_de_personas_delegadas_solo_tiene_los_consumidores_congelados()
    {
        var raiz = Raiz();
        var separador = Path.DirectorySeparatorChar;

        var consumidores = Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.*", SearchOption.AllDirectories)
            .Where(a => a.EndsWith(".cs", StringComparison.Ordinal) || a.EndsWith(".razor", StringComparison.Ordinal))
            .Where(a => !a.Contains($"{separador}obj{separador}") && !a.Contains($"{separador}bin{separador}"))
            .Where(a => LimpiadorDeComentarios.Quitar(File.ReadAllText(a), razor: a.EndsWith(".razor", StringComparison.Ordinal))
                .Contains("ILecturaPersonasDelegadas", StringComparison.Ordinal))
            .Select(a => Path.GetRelativePath(raiz, a).Replace(separador, '/'));

        consumidores.Should().BeEquivalentTo(
            "src/CaeManager.Infrastructure/Autorizacion/LecturaPersonasDelegadas.cs",
            "src/CaeManager.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs",
            "src/CaeManager.Web/Features/Delegaciones/Pages/Delegaciones.razor.cs");
    }

    private static string Raiz()
    {
        var raiz = new DirectoryInfo(AppContext.BaseDirectory);
        while (raiz is not null && !File.Exists(Path.Combine(raiz.FullName, "CaeManager.slnx"))) raiz = raiz.Parent;
        raiz.Should().NotBeNull();
        return raiz!.FullName;
    }

    private static string Metodo(string ruta, string nombre)
    {
        var arbol = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(Raiz(), ruta)));
        return arbol.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == nombre && (m.Body is not null || m.ExpressionBody is not null)).ToString();
    }
}

using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>El contexto de una petición de la extensión se decide antes que todo lo que depende de él.</b> Si
/// <c>UseContextoExtension</c> se moviera después del ajuste de rol, el rol efectivo no vería la selección de la
/// extensión; si se quitara, una petición con token volvería a leer la cookie de selección. Se vigila el texto de
/// <c>Program.cs</c>, como el resto de pruebas del pipeline: el comportamiento de cada pieza lo cubren
/// <c>ContextoExtensionMiddlewareTests</c> y <c>ExtensionTenantBeneficiarioBajoRlsTests</c>.
/// </summary>
public class ContextoExtensionEnElPipelineTests
{
    [Fact]
    public void Va_tras_la_autenticacion_y_antes_de_todo_lo_que_lee_identidad_o_seleccion()
    {
        var programa = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        var orden = new[]
        {
            "app.UseAuthentication();",
            "app.UseContextoExtension();",
            "app.UseSesionPrivilegiadaSinRolDeNegocio();",
            "app.UseTenantBeneficiarioPorDefecto();",
            "app.UseRolEfectivoDelWorkspace();",
            "app.UseAuthorization();",
            "app.UseRevalidacionClienteActivo();",
        }.Select(linea => programa.IndexOf(linea, StringComparison.Ordinal)).ToList();

        orden.Should().NotContain(-1, "control positivo: cada pieza del pipeline se encuentra en Program.cs");
        orden.Should().BeInAscendingOrder();
    }

    [Fact]
    public void El_evaluador_de_politicas_no_repite_la_autenticacion_de_la_extension()
    {
        var programa = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        programa.Should().Contain("IPolicyEvaluator, CaeManager.Web.Services.EvaluadorPoliticaExtension>()");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}

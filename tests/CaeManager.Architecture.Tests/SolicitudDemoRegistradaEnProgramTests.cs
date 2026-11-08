using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>Program.cs</c> cablea el endpoint público del formulario de demo de
/// talveg.es. Su comportamiento lo prueba <c>SolicitudDemoEndpointsTests</c> en
/// Web.Tests sobre un espejo mínimo del pipeline; este trinquete vigila que
/// <c>Program.cs</c> registre los servicios, monte la ruta y llame a
/// <c>UseCors</c> antes de autenticar (el preflight del navegador no lleva
/// cookie) y antes de <c>UseRateLimiter</c>, porque el proyecto no tiene un
/// <c>WebApplicationFactory</c> que lo observe.
/// </summary>
public class SolicitudDemoRegistradaEnProgramTests
{
    [Fact]
    public void Program_registra_y_monta_el_endpoint_de_demo_con_cors_antes_de_autenticar()
    {
        var program = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        program.Should().Contain("builder.Services.AddSolicitudDemo(builder.Configuration);");
        program.Should().Contain("app.MapSolicitudDemoEndpoints();");

        var cors = program.IndexOf("app.UseCors();", StringComparison.Ordinal);
        cors.Should().BeGreaterThan(0, "sin UseCors el navegador no recibe permiso y el formulario falla siempre");
        cors.Should().BeLessThan(program.IndexOf("app.UseAuthentication();", StringComparison.Ordinal));
        cors.Should().BeLessThan(program.IndexOf("app.UseRateLimiter();", StringComparison.Ordinal),
            "el preflight no debe gastar la cuota de la IP");
        cors.Should().BeLessThan(program.IndexOf("app.UseAuthorization();", StringComparison.Ordinal));
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
            " — este test necesita el árbol fuente del repositorio.");
    }
}

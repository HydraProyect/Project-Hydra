using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>Restaura en Application el límite que ya exponen las 4 páginas de importación (Administrador).</summary>
public class EjecutarImportacionCommandAutorizacionTests
{
    [Fact]
    public async Task Administrador_puede_ejecutar_una_importacion()
    {
        var escenario = new EscenarioImportacion();

        var resultado = await escenario.Handler("Administrador").Handle(
            new EjecutarImportacionCommand(EscenarioImportacion.Plan()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("DireccionCae")]
    [InlineData(null)]
    public async Task Otros_roles_no_pueden_ejecutar_una_importacion(string? rol)
    {
        var escenario = new EscenarioImportacion();

        var resultado = await escenario.Handler(rol).Handle(
            new EjecutarImportacionCommand(EscenarioImportacion.Plan()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Importacion.SoloAdministrador");
    }
}

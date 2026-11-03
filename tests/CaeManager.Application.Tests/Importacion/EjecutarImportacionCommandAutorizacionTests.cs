using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Importacion;

/// <summary>Restaura en Application el límite que ya exponen las 4 páginas de importación (Administrador).</summary>
public class EjecutarImportacionCommandAutorizacionTests
{
    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    public async Task Administrador_Direccion_y_Coordinador_pueden_ejecutar_una_importacion(string rol)
    {
        var escenario = new EscenarioImportacion();

        var resultado = await escenario.Handler(rol).Handle(
            new EjecutarImportacionCommand(EscenarioImportacion.Plan()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Otros_roles_no_pueden_ejecutar_una_importacion(string? rol)
    {
        var escenario = new EscenarioImportacion();

        var resultado = await escenario.Handler(rol).Handle(
            new EjecutarImportacionCommand(EscenarioImportacion.Plan()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Importacion.SinAutoridadCargaMasiva");
    }
}

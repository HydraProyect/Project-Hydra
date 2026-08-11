using CaeManager.Application.Comunicaciones.Commands.DescartarSugerenciaVisita;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

public class DescartarSugerenciaVisitaCorreoCommandHandlerTests
{
    [Fact]
    public async Task Marca_la_sugerencia_como_resuelta()
    {
        var sugerencia = new SugerenciaVisitaCorreo(Guid.NewGuid(), null, null, null, "Resumen", 90, 0, 0);
        var repositorio = new SugerenciaVisitaCorreoRepositorioFalso();
        repositorio.Agregar(sugerencia);
        var handler = new DescartarSugerenciaVisitaCorreoCommandHandler(repositorio, new CurrentUserServiceFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new DescartarSugerenciaVisitaCorreoCommand(sugerencia.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        sugerencia.Resuelta.Should().BeTrue();
        sugerencia.Resolucion.Should().Be(ResolucionSugerencia.Descartada);
    }

    [Fact]
    public async Task Falla_si_la_sugerencia_no_existe()
    {
        var handler = new DescartarSugerenciaVisitaCorreoCommandHandler(new SugerenciaVisitaCorreoRepositorioFalso(), new CurrentUserServiceFalso(), new UnitOfWorkFalso());

        var resultado = await handler.Handle(new DescartarSugerenciaVisitaCorreoCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("SugerenciaVisitaCorreo.NoEncontrada");
    }
}

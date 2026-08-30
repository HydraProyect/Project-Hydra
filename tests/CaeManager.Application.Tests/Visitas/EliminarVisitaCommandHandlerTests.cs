using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Visitas.Commands.EliminarVisita;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

public class EliminarVisitaCommandHandlerTests
{
    private static Visita CrearVisita() =>
        new(Guid.NewGuid(), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), null);

    [Fact]
    public async Task Marca_la_visita_como_eliminada()
    {
        var visita = CrearVisita();
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(visita);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarVisitaCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarVisitaCommand(visita.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        visita.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Falla_cuando_la_visita_no_existe()
    {
        var repositorio = new VisitaRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarVisitaCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarVisitaCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
    }

    [Fact]
    public async Task Falla_sin_borrar_cuando_no_hay_identidad_resuelta()
    {
        // Auditoría Módulo 5, hallazgo crítico 7/9: sin ICurrentUserService
        // resuelto, el borrado se aborta — nunca se atribuye a Guid.Empty.
        var visita = CrearVisita();
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(visita);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarVisitaCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(usuarioId: null));

        var resultado = await handler.Handle(new EliminarVisitaCommand(visita.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Visita.SinIdentidad");
        visita.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}

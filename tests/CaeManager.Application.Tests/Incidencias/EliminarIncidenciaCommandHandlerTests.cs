using CaeManager.Application.Incidencias.Commands.EliminarIncidencia;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Incidencias;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Incidencias;

public class EliminarIncidenciaCommandHandlerTests
{
    private static Incidencia CrearIncidencia() =>
        new(Guid.NewGuid(), null, TipoIncidencia.Accidente, GravedadIncidencia.Leve, new DateOnly(2026, 1, 1), "Caída en almacén.");

    [Fact]
    public async Task Marca_la_incidencia_como_eliminada()
    {
        var incidencia = CrearIncidencia();
        var repositorio = new IncidenciaRepositorioFalso();
        repositorio.Agregar(incidencia);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarIncidenciaCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarIncidenciaCommand(incidencia.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        incidencia.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Falla_cuando_la_incidencia_no_existe()
    {
        var repositorio = new IncidenciaRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarIncidenciaCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarIncidenciaCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Incidencia.NoEncontrada");
    }

    [Fact]
    public async Task Falla_sin_borrar_cuando_no_hay_identidad_resuelta()
    {
        // Auditoría Módulo 5, hallazgo crítico 7/9: sin ICurrentUserService
        // resuelto, el borrado se aborta — nunca se atribuye a Guid.Empty.
        var incidencia = CrearIncidencia();
        var repositorio = new IncidenciaRepositorioFalso();
        repositorio.Agregar(incidencia);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarIncidenciaCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(usuarioId: null));

        var resultado = await handler.Handle(new EliminarIncidenciaCommand(incidencia.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Incidencia.SinIdentidad");
        incidencia.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}

using CaeManager.Application.Gestiones.Commands.EliminarGestion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Gestiones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Gestiones;

public class EliminarGestionCommandHandlerTests
{
    private static Gestion CrearGestion() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task Marca_la_gestion_como_eliminada()
    {
        var gestion = CrearGestion();
        var repositorio = new GestionRepositorioFalso();
        repositorio.Agregar(gestion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarGestionCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarGestionCommand(gestion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        gestion.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Falla_cuando_la_gestion_no_existe()
    {
        var repositorio = new GestionRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarGestionCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarGestionCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Gestion.NoEncontrada");
    }

    [Fact]
    public async Task Falla_cuando_el_centro_de_la_gestion_esta_fuera_de_la_cartera()
    {
        var gestion = CrearGestion();
        var repositorio = new GestionRepositorioFalso();
        repositorio.Agregar(gestion);
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [Guid.NewGuid()]);
        var handler = new EliminarGestionCommandHandler(repositorio, alcance, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarGestionCommand(gestion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Gestion.NoEncontrada");
        gestion.EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public async Task Falla_sin_borrar_cuando_no_hay_identidad_resuelta()
    {
        // Auditoría Módulo 5, hallazgo crítico 7/9: sin ICurrentUserService
        // resuelto, el borrado se aborta — nunca se atribuye a Guid.Empty.
        var gestion = CrearGestion();
        var repositorio = new GestionRepositorioFalso();
        repositorio.Agregar(gestion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarGestionCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(usuarioId: null));

        var resultado = await handler.Handle(new EliminarGestionCommand(gestion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Gestion.SinIdentidad");
        gestion.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}

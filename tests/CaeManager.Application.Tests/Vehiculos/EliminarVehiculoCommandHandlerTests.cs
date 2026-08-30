using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Vehiculos.Commands.EliminarVehiculo;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Vehiculos;

public class EliminarVehiculoCommandHandlerTests
{
    private static Vehiculo CrearVehiculo() =>
        Vehiculo.DeEmpresa(Guid.NewGuid(), "Furgón 1", "Transit", "1234ABC");

    [Fact]
    public async Task Marca_el_vehiculo_como_eliminado()
    {
        var vehiculo = CrearVehiculo();
        var repositorio = new VehiculoRepositorioFalso();
        repositorio.Agregar(vehiculo);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarVehiculoCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarVehiculoCommand(vehiculo.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        vehiculo.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Falla_cuando_el_vehiculo_no_existe()
    {
        var repositorio = new VehiculoRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarVehiculoCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarVehiculoCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Vehiculo.NoEncontrado");
    }

    [Fact]
    public async Task Falla_sin_borrar_cuando_no_hay_identidad_resuelta()
    {
        // Auditoría Módulo 5, hallazgo crítico 7/9: sin ICurrentUserService
        // resuelto, el borrado se aborta — nunca se atribuye a Guid.Empty.
        var vehiculo = CrearVehiculo();
        var repositorio = new VehiculoRepositorioFalso();
        repositorio.Agregar(vehiculo);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarVehiculoCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(usuarioId: null));

        var resultado = await handler.Handle(new EliminarVehiculoCommand(vehiculo.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Vehiculo.SinIdentidad");
        vehiculo.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}

using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Vehiculos.Commands.EliminarVehiculos;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Vehiculos;

public class EliminarVehiculosCommandHandlerTests
{
    private static Vehiculo CrearVehiculo() =>
        Vehiculo.DeEmpresa(Guid.NewGuid(), "Furgón 1", "Transit", Guid.NewGuid().ToString("N")[..8]);

    [Fact]
    public async Task Marca_todos_los_vehiculos_del_lote_como_eliminados()
    {
        var uno = CrearVehiculo();
        var dos = CrearVehiculo();
        var repositorio = new VehiculoRepositorioFalso();
        repositorio.Agregar(uno);
        repositorio.Agregar(dos);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarVehiculosCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarVehiculosCommand([uno.Id, dos.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(2);
        uno.EstaEliminado.Should().BeTrue();
        dos.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Reporta_error_parcial_cuando_un_vehiculo_ya_no_existe()
    {
        var existente = CrearVehiculo();
        var repositorio = new VehiculoRepositorioFalso();
        repositorio.Agregar(existente);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarVehiculosCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarVehiculosCommand([existente.Id, Guid.NewGuid()]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(1);
        resultado.Valor.Errores.Should().ContainSingle();
    }

    [Fact]
    public async Task No_borra_ninguno_del_lote_sin_identidad_resuelta()
    {
        var vehiculo = CrearVehiculo();
        var repositorio = new VehiculoRepositorioFalso();
        repositorio.Agregar(vehiculo);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarVehiculosCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(usuarioId: null));

        var resultado = await handler.Handle(new EliminarVehiculosCommand([vehiculo.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Vehiculo.SinIdentidad");
        vehiculo.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}

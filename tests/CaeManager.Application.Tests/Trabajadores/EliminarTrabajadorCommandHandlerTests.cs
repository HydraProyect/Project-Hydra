using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Trabajadores.Commands.EliminarTrabajador;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Trabajadores;

public class EliminarTrabajadorCommandHandlerTests
{
    private static Trabajador CrearTrabajador(string dni) => Trabajador.DeEmpresa(
        Guid.NewGuid(), "Manuel", "Moreno Domínguez", dni, new DateOnly(1985, 4, 12), null, null, null);

    [Fact]
    public async Task Marca_el_trabajador_como_eliminado()
    {
        var trabajador = CrearTrabajador("12345678Z");
        var repositorio = new TrabajadorRepositorioFalso();
        repositorio.Agregar(trabajador);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarTrabajadorCommandHandler(repositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(new EliminarTrabajadorCommand(trabajador.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        trabajador.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Falla_cuando_el_trabajador_no_existe()
    {
        var repositorio = new TrabajadorRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarTrabajadorCommandHandler(repositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(new EliminarTrabajadorCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Trabajador.NoEncontrado");
    }

    [Fact]
    public async Task Falla_cuando_el_trabajador_esta_fuera_de_la_cartera()
    {
        var trabajador = CrearTrabajador("12345678Z");
        var repositorio = new TrabajadorRepositorioFalso();
        repositorio.Agregar(trabajador);
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: []);
        var handler = new EliminarTrabajadorCommandHandler(repositorio, alcance, unitOfWork);

        var resultado = await handler.Handle(new EliminarTrabajadorCommand(trabajador.Id, Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Trabajador.NoEncontrado");
        trabajador.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}

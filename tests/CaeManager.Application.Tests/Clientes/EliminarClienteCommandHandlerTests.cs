using CaeManager.Application.Clientes.Commands.EliminarCliente;
using CaeManager.Domain.Empresas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Clientes;

public class EliminarClienteCommandHandlerTests
{
    [Fact]
    public async Task Marca_el_cliente_como_eliminado_cuando_no_tiene_centros_activos()
    {
        var cliente = Empresa.CrearComoCliente("Bebidas del Norte S.A. (Planta El Prat)", "B12345674", true, null, null);
        var repositorio = new EmpresaRepositorioFalso { TieneCentrosActivos = false };
        repositorio.Agregar(cliente);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarClienteCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarClienteCommand(cliente.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        cliente.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Falla_cuando_el_cliente_esta_fuera_de_la_cartera()
    {
        var cliente = Empresa.CrearComoCliente("Bebidas del Norte S.A. (Planta El Prat)", "B12345674", true, null, null);
        var repositorio = new EmpresaRepositorioFalso { TieneCentrosActivos = false };
        repositorio.Agregar(cliente);
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [Guid.NewGuid()]);
        var handler = new EliminarClienteCommandHandler(repositorio, alcance, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarClienteCommand(cliente.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Cliente.NoEncontrado");
        cliente.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Falla_cuando_el_cliente_tiene_centros_activos()
    {
        var cliente = Empresa.CrearComoCliente("Bebidas del Norte S.A. (Planta El Prat)", "B12345674", true, null, null);
        var repositorio = new EmpresaRepositorioFalso { TieneCentrosActivos = true };
        repositorio.Agregar(cliente);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarClienteCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarClienteCommand(cliente.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Cliente.TieneCentrosActivos");
        cliente.EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public async Task Falla_cuando_el_cliente_no_existe()
    {
        var repositorio = new EmpresaRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarClienteCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarClienteCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Cliente.NoEncontrado");
    }

    [Fact]
    public async Task Falla_sin_borrar_cuando_no_hay_identidad_resuelta()
    {
        // Auditoría Módulo 5, hallazgo crítico 7/9: sin ICurrentUserService
        // resuelto, el borrado se aborta — nunca se atribuye a Guid.Empty.
        var cliente = Empresa.CrearComoCliente("Bebidas del Norte S.A. (Planta El Prat)", "B12345674", true, null, null);
        var repositorio = new EmpresaRepositorioFalso { TieneCentrosActivos = false };
        repositorio.Agregar(cliente);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarClienteCommandHandler(
            repositorio, new AlcanceDatosServiceFalso(), unitOfWork, new CurrentUserServiceFalso(usuarioId: null));

        var resultado = await handler.Handle(new EliminarClienteCommand(cliente.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Cliente.SinIdentidad");
        cliente.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}

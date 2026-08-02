using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Domain.Clientes;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Clientes;

public class EliminarClientesCommandHandlerTests
{
    [Fact]
    public async Task Elimina_todos_los_clientes_sin_centros_activos()
    {
        var uno = new Cliente("Uno S.A.", "B12345674", false);
        var dos = new Cliente("Dos S.A.", "B12345674", false);
        var repositorio = new ClienteRepositorioFalso { TieneCentrosActivos = false };
        repositorio.Agregar(uno);
        repositorio.Agregar(dos);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarClientesCommandHandler(repositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(new EliminarClientesCommand([uno.Id, dos.Id], Guid.NewGuid()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(2);
        resultado.Valor.Errores.Should().BeEmpty();
        uno.EstaEliminado.Should().BeTrue();
        dos.EstaEliminado.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(1, "una única transacción para todo el lote");
    }

    [Fact]
    public async Task Reporta_exito_parcial_cuando_alguno_tiene_centros_activos()
    {
        var sinCentros = new Cliente("Sin centros", "B12345674", false);
        var conCentros = new Cliente("Con centros", "B12345674", false);
        var repositorio = new ClienteRepositorioFalso();
        repositorio.Agregar(sinCentros);
        repositorio.Agregar(conCentros);
        repositorio.IdsConCentrosActivos.Add(conCentros.Id);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarClientesCommandHandler(repositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new EliminarClientesCommand([sinCentros.Id, conCentros.Id], Guid.NewGuid()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(1);
        resultado.Valor.Errores.Should().ContainSingle();
        sinCentros.EstaEliminado.Should().BeTrue();
        conCentros.EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public async Task Reporta_error_por_cada_id_inexistente()
    {
        var repositorio = new ClienteRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarClientesCommandHandler(repositorio, new AlcanceDatosServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(new EliminarClientesCommand([Guid.NewGuid()], Guid.NewGuid()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(0);
        resultado.Valor.Errores.Should().ContainSingle();
    }

    [Fact]
    public async Task No_elimina_un_cliente_fuera_de_la_cartera_aunque_venga_en_el_lote()
    {
        // Blindaje contra un lote construido a mano (fuera de la UI, que ya
        // filtra por cartera) con el Id de un cliente ajeno dentro del mismo tenant.
        var propio = new Cliente("Propio S.A.", "B12345674", false);
        var ajeno = new Cliente("Ajeno S.A.", "B12345674", false);
        var repositorio = new ClienteRepositorioFalso { TieneCentrosActivos = false };
        repositorio.Agregar(propio);
        repositorio.Agregar(ajeno);
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [propio.Id]);
        var handler = new EliminarClientesCommandHandler(repositorio, alcance, unitOfWork);

        var resultado = await handler.Handle(new EliminarClientesCommand([propio.Id, ajeno.Id], Guid.NewGuid()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(1);
        resultado.Valor.Errores.Should().ContainSingle();
        propio.EstaEliminado.Should().BeTrue();
        ajeno.EstaEliminado.Should().BeFalse();
    }
}

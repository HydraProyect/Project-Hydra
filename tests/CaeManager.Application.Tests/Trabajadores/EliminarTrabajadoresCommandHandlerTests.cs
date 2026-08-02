using CaeManager.Application.Trabajadores.Commands.EliminarTrabajadores;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Trabajadores;

public class EliminarTrabajadoresCommandHandlerTests
{
    private static Trabajador CrearTrabajador(string dni) => Trabajador.DeEmpresa(
        Guid.NewGuid(), "Manuel", "Moreno Domínguez", dni, new DateOnly(1985, 4, 12), null, null, null);

    [Fact]
    public async Task Elimina_todos_los_trabajadores_existentes()
    {
        var uno = CrearTrabajador("12345678Z");
        var dos = CrearTrabajador("87654321X");
        var repositorio = new TrabajadorRepositorioFalso();
        repositorio.Agregar(uno);
        repositorio.Agregar(dos);
        var unitOfWork = new Clientes.UnitOfWorkFalso();
        var handler = new EliminarTrabajadoresCommandHandler(repositorio, unitOfWork);

        var resultado = await handler.Handle(new EliminarTrabajadoresCommand([uno.Id, dos.Id], Guid.NewGuid()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(2);
        resultado.Valor.Errores.Should().BeEmpty();
        uno.EstaEliminado.Should().BeTrue();
        dos.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Reporta_error_por_cada_id_inexistente_sin_fallar_el_resto()
    {
        var existente = CrearTrabajador("12345678Z");
        var repositorio = new TrabajadorRepositorioFalso();
        repositorio.Agregar(existente);
        var unitOfWork = new Clientes.UnitOfWorkFalso();
        var handler = new EliminarTrabajadoresCommandHandler(repositorio, unitOfWork);

        var resultado = await handler.Handle(
            new EliminarTrabajadoresCommand([existente.Id, Guid.NewGuid()], Guid.NewGuid()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(1);
        resultado.Valor.Errores.Should().ContainSingle();
    }
}

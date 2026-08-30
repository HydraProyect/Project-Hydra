using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Visitas.Commands.EliminarVisitas;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

public class EliminarVisitasCommandHandlerTests
{
    private static Visita CrearVisita() =>
        new(Guid.NewGuid(), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), null);

    [Fact]
    public async Task Marca_todas_las_visitas_del_lote_como_eliminadas()
    {
        var uno = CrearVisita();
        var dos = CrearVisita();
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(uno);
        repositorio.Agregar(dos);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarVisitasCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarVisitasCommand([uno.Id, dos.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(2);
        uno.EstaEliminado.Should().BeTrue();
        dos.EstaEliminado.Should().BeTrue();
    }

    [Fact]
    public async Task Reporta_error_parcial_cuando_una_visita_ya_no_existe()
    {
        var existente = CrearVisita();
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(existente);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarVisitasCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()));

        var resultado = await handler.Handle(new EliminarVisitasCommand([existente.Id, Guid.NewGuid()]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(1);
        resultado.Valor.Errores.Should().ContainSingle();
    }

    [Fact]
    public async Task No_borra_ninguna_del_lote_sin_identidad_resuelta()
    {
        // Se comprueba antes del bucle a propósito: un éxito parcial sin autor
        // no es un éxito parcial, y dejar la mitad del lote borrada sin poder
        // decir quién lo hizo es peor que no borrar nada.
        var visita = CrearVisita();
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(visita);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new EliminarVisitasCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(usuarioId: null));

        var resultado = await handler.Handle(new EliminarVisitasCommand([visita.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Visita.SinIdentidad");
        visita.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }
}

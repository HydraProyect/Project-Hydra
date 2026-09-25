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
        var handler = new EliminarVisitasCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()), new AlcanceDatosServiceFalso());

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
        var handler = new EliminarVisitasCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(Guid.NewGuid()), new AlcanceDatosServiceFalso());

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
        var handler = new EliminarVisitasCommandHandler(repositorio, unitOfWork, new CurrentUserServiceFalso(usuarioId: null), new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new EliminarVisitasCommand([visita.Id]), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Visita.SinIdentidad");
        visita.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Alcance de gestión en lote: la Visita cuyo Centro está fuera de la Asignación de
    /// Cartera cuenta como una que ya no existía; la de dentro se borra.
    /// </summary>
    [Fact]
    public async Task Solo_borra_del_lote_las_visitas_dentro_del_alcance_de_gestion()
    {
        var dentro = CrearVisita();
        var fuera = CrearVisita();
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(dentro);
        repositorio.Agregar(fuera);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [dentro.CentroId]);
        var handler = new EliminarVisitasCommandHandler(repositorio, new UnitOfWorkFalso(), new CurrentUserServiceFalso(Guid.NewGuid()), alcance);

        var resultado = await handler.Handle(new EliminarVisitasCommand([dentro.Id, fuera.Id]), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Eliminados.Should().Be(1);
        resultado.Valor.Errores.Should().ContainSingle();
        dentro.EstaEliminado.Should().BeTrue();
        fuera.EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public async Task Con_alcance_de_gestion_vacio_no_borra_nada_del_lote()
    {
        var visita = CrearVisita();
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(visita);
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [visita.CentroId], centroIdsParaGestion: []);
        var handler = new EliminarVisitasCommandHandler(repositorio, new UnitOfWorkFalso(), new CurrentUserServiceFalso(Guid.NewGuid()), alcance);

        var resultado = await handler.Handle(new EliminarVisitasCommand([visita.Id]), CancellationToken.None);

        resultado.Valor.Eliminados.Should().Be(0);
        visita.EstaEliminado.Should().BeFalse();
    }
}

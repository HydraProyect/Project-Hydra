using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Visitas.Commands.CancelarVisita;
using CaeManager.Application.Visitas.Commands.CancelarVisitas;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// FS-11: cancelar una Visita la deja en estado Cancelada (reversible), con el
/// mismo alcance de gestión que tenía el borrado al que sustituye.
/// </summary>
public class CancelarVisitaCommandHandlerTests
{
    private static Visita CrearVisita() =>
        new(Guid.NewGuid(), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), null);

    [Fact]
    public async Task Cancela_la_visita_con_su_motivo_sin_borrarla()
    {
        var visita = CrearVisita();
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(visita);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CancelarVisitaCommandHandler(repositorio, unitOfWork, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new CancelarVisitaCommand(visita.Id, "Aplazada por el cliente"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        visita.EstaCancelada.Should().BeTrue();
        visita.MotivoCancelacion.Should().Be("Aplazada por el cliente");
        visita.EstaEliminado.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Falla_cuando_la_visita_no_existe()
    {
        var handler = new CancelarVisitaCommandHandler(new VisitaRepositorioFalso(), new UnitOfWorkFalso(), new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new CancelarVisitaCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
    }

    [Fact]
    public async Task Una_visita_ya_cancelada_no_se_vuelve_a_cancelar()
    {
        var visita = CrearVisita();
        visita.Cancelar(DateTime.UtcNow, "primero");
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(visita);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = new CancelarVisitaCommandHandler(repositorio, unitOfWork, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new CancelarVisitaCommand(visita.Id, "segundo"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.YaCancelada");
        visita.MotivoCancelacion.Should().Be("primero");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Alcance de gestión: una Visita cuyo Centro está fuera de la Asignación de Cartera
    /// responde igual que una inexistente y no se toca.
    /// </summary>
    [Fact]
    public async Task No_cancela_una_visita_cuyo_Centro_esta_fuera_del_alcance_de_gestion()
    {
        var visita = CrearVisita();
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(visita);
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [Guid.NewGuid()]);
        var handler = new CancelarVisitaCommandHandler(repositorio, unitOfWork, alcance);

        var resultado = await handler.Handle(new CancelarVisitaCommand(visita.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
        visita.EstaCancelada.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>Alcance de GESTIÓN, no de lectura: ver el Centro no basta para cancelar sus Visitas.</summary>
    [Fact]
    public async Task No_cancela_con_alcance_de_lectura_sin_alcance_de_gestion()
    {
        var visita = CrearVisita();
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(visita);
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [visita.CentroId], centroIdsParaGestion: []);
        var handler = new CancelarVisitaCommandHandler(repositorio, new UnitOfWorkFalso(), alcance);

        var resultado = await handler.Handle(new CancelarVisitaCommand(visita.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
        visita.EstaCancelada.Should().BeFalse();
    }

    [Fact]
    public async Task El_lote_cancela_lo_que_puede_y_devuelve_solo_esos_ids()
    {
        var dentro = CrearVisita();
        var yaCancelada = CrearVisita();
        yaCancelada.Cancelar(DateTime.UtcNow, null);
        var fuera = CrearVisita();
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(dentro);
        repositorio.Agregar(yaCancelada);
        repositorio.Agregar(fuera);
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [dentro.CentroId, yaCancelada.CentroId]);
        var handler = new CancelarVisitasCommandHandler(repositorio, new UnitOfWorkFalso(), alcance);

        var resultado = await handler.Handle(
            new CancelarVisitasCommand([dentro.Id, yaCancelada.Id, fuera.Id, Guid.NewGuid()], "Obra aplazada"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Canceladas.Should().Be(1);
        resultado.Valor.IdsCanceladas.Should().Equal([dentro.Id]);
        resultado.Valor.Errores.Should().HaveCount(3);
        dentro.EstaCancelada.Should().BeTrue();
        dentro.MotivoCancelacion.Should().Be("Obra aplazada");
        fuera.EstaCancelada.Should().BeFalse("fuera de la Asignación de Cartera no se toca");
    }
}

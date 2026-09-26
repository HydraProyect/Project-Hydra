using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Visitas.Commands.EditarVisita;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// Alcance de gestión al EDITAR una Visita. Solo cubre la barrera, que se decide antes de tocar
/// los Trabajadores: por eso el repositorio de la unión, el contexto de Trabajadores y el
/// evaluador van a null — si el handler pasara la barrera, el test reventaría en vez de dar un
/// falso verde. El camino feliz, con Trabajadores reales, se prueba en Integration
/// (<c>EditarCancelarVisitaAlcanceCarteraBajoRlsTests</c>).
/// </summary>
public class EditarVisitaCommandHandlerTests
{
    private static readonly DateOnly FechaOriginal = new(2026, 1, 1);

    private static Visita CrearVisita() => new(Guid.NewGuid(), FechaOriginal, FechaOriginal.AddDays(1), null);

    private static EditarVisitaCommandHandler CrearHandler(VisitaRepositorioFalso repositorio, UnitOfWorkFalso unitOfWork, AlcanceDatosServiceFalso alcance) =>
        new(repositorio, null!, null!, null!, unitOfWork, NullLogger<EditarVisitaCommandHandler>.Instance, alcance);

    private static EditarVisitaCommand Comando(Guid visitaId, Guid version = default) =>
        new(visitaId, FechaOriginal.AddDays(7), FechaOriginal.AddDays(8), [Guid.NewGuid()], "editada", version);

    [Fact]
    public async Task No_edita_una_visita_cuyo_Centro_esta_fuera_del_alcance_de_gestion()
    {
        var visita = CrearVisita();
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(visita);
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [Guid.NewGuid()]);

        var resultado = await CrearHandler(repositorio, unitOfWork, alcance).Handle(Comando(visita.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada",
            "fuera de cartera se responde igual que si no existiera, sin revelar qué hay fuera del alcance");
        visita.FechaInicio.Should().Be(FechaOriginal);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// El alcance se comprueba antes que la concurrencia: una versión desfasada sobre una Visita
    /// fuera de alcance no puede responder «otra persona la modificó», que confirmaría que existe.
    /// </summary>
    [Fact]
    public async Task Fuera_de_alcance_no_revela_el_conflicto_de_version()
    {
        var visita = CrearVisita();
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(visita);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [Guid.NewGuid()]);

        var resultado = await CrearHandler(repositorio, new UnitOfWorkFalso(), alcance)
            .Handle(Comando(visita.Id, version: Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
    }

    /// <summary>
    /// Alcance de GESTIÓN, no de lectura: ver el Centro (p. ej. un usuario de portal sobre su
    /// propio Cliente empresarial) no basta para editar sus Visitas.
    /// </summary>
    [Fact]
    public async Task No_edita_con_alcance_de_lectura_sin_alcance_de_gestion()
    {
        var visita = CrearVisita();
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(visita);
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [visita.CentroId], centroIdsParaGestion: []);

        var resultado = await CrearHandler(repositorio, new UnitOfWorkFalso(), alcance).Handle(Comando(visita.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
        visita.FechaInicio.Should().Be(FechaOriginal);
    }
}

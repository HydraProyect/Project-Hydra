using CaeManager.Application.Gestiones.Commands.CompletarGestion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Gestiones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Gestiones;

public class CompletarGestionCommandHandlerTests
{
    private static readonly DateTime Ahora = new(2026, 10, 17, 9, 30, 0, DateTimeKind.Utc);

    private static Gestion CrearGestion() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

    private static CompletarGestionCommandHandler CrearHandler(
        GestionRepositorioFalso repositorio, UnitOfWorkFalso unitOfWork, AlcanceDatosServiceFalso? alcance = null) =>
        new(repositorio, alcance ?? new AlcanceDatosServiceFalso(), unitOfWork, new RelojFijo(Ahora));

    [Fact]
    public async Task Completar_registra_el_instante_del_reloj_inyectado()
    {
        var gestion = CrearGestion();
        var repositorio = new GestionRepositorioFalso();
        repositorio.Agregar(gestion);
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await CrearHandler(repositorio, unitOfWork)
            .Handle(new CompletarGestionCommand(gestion.Id, Completada: true), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        gestion.Estado.Should().Be(EstadoGestion.Completada);
        gestion.CompletadaEnUtc.Should().Be(Ahora);
        unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Reabrir_limpia_el_instante_de_completado()
    {
        var gestion = CrearGestion();
        gestion.Completar(Ahora.AddDays(-3));
        var repositorio = new GestionRepositorioFalso();
        repositorio.Agregar(gestion);
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await CrearHandler(repositorio, unitOfWork)
            .Handle(new CompletarGestionCommand(gestion.Id, Completada: false), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        gestion.Estado.Should().Be(EstadoGestion.Pendiente);
        gestion.CompletadaEnUtc.Should().BeNull();
    }

    [Fact]
    public async Task Fuera_del_alcance_no_completa_ni_registra_instante()
    {
        var gestion = CrearGestion();
        var repositorio = new GestionRepositorioFalso();
        repositorio.Agregar(gestion);
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [Guid.NewGuid()]);

        var resultado = await CrearHandler(repositorio, unitOfWork, alcance)
            .Handle(new CompletarGestionCommand(gestion.Id, Completada: true), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Gestion.NoEncontrada");
        gestion.Estado.Should().Be(EstadoGestion.Pendiente);
        gestion.CompletadaEnUtc.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    private sealed class RelojFijo(DateTime ahoraUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahoraUtc);
    }
}

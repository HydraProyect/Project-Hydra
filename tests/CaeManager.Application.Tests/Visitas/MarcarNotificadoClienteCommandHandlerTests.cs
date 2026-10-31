using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Visitas.Commands.MarcarNotificadoCliente;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

public class MarcarNotificadoClienteCommandHandlerTests
{
    private static Visita CrearVisita() =>
        new(Guid.NewGuid(), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), null);

    private static VisitaRepositorioFalso Repositorio(Visita visita)
    {
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(visita);
        return repositorio;
    }

    [Fact]
    public async Task Marca_una_visita_dentro_del_alcance_de_gestion()
    {
        var visita = CrearVisita();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [visita.CentroId]);

        var resultado = await new MarcarNotificadoClienteCommandHandler(Repositorio(visita), new UnitOfWorkFalso(), alcance)
            .Handle(new MarcarNotificadoClienteCommand(visita.Id, true), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        visita.NotificadoCliente.Should().BeTrue();
    }

    /// <summary>
    /// Antes solo se comprobaba que la Visita existiera en el Tenant: un Gestor CAE
    /// podía cambiar la marca de una Visita fuera de su Asignación de Cartera.
    /// </summary>
    [Fact]
    public async Task No_marca_una_visita_fuera_del_alcance_de_gestion()
    {
        var visita = CrearVisita();
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [Guid.NewGuid()]);

        var resultado = await new MarcarNotificadoClienteCommandHandler(Repositorio(visita), unitOfWork, alcance)
            .Handle(new MarcarNotificadoClienteCommand(visita.Id, true), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
        visita.NotificadoCliente.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task No_marca_una_visita_cancelada()
    {
        var visita = CrearVisita();
        visita.Cancelar(DateTime.UtcNow, null);
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await new MarcarNotificadoClienteCommandHandler(Repositorio(visita), unitOfWork, new AlcanceDatosServiceFalso())
            .Handle(new MarcarNotificadoClienteCommand(visita.Id, true), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.Cancelada");
        visita.NotificadoCliente.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Otra persona modificó la Visita desde que este Gestor CAE la vio: la marca no se
    /// escribe y el choque sale como <c>Concurrencia.Conflicto</c>, no como éxito silencioso.
    /// </summary>
    [Fact]
    public async Task Rechaza_la_marca_si_la_visita_cambio_desde_que_se_vio()
    {
        var visita = CrearVisita();
        var versionVista = Guid.NewGuid();
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await new MarcarNotificadoClienteCommandHandler(Repositorio(visita), unitOfWork, new AlcanceDatosServiceFalso())
            .Handle(new MarcarNotificadoClienteCommand(visita.Id, true, versionVista), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        visita.NotificadoCliente.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Marca_si_la_version_vista_es_la_actual()
    {
        var visita = CrearVisita();

        var resultado = await new MarcarNotificadoClienteCommandHandler(Repositorio(visita), new UnitOfWorkFalso(), new AlcanceDatosServiceFalso())
            .Handle(new MarcarNotificadoClienteCommand(visita.Id, true, visita.Version), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        visita.NotificadoCliente.Should().BeTrue();
    }
}

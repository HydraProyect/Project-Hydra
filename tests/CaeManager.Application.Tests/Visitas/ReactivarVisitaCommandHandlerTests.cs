using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Application.Visitas.Commands.CancelarVisita;
using CaeManager.Application.Visitas.Commands.ReactivarVisita;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// FS-11: reactivar una Visita cancelada exige exactamente la misma autorización
/// que cancelarla (decisión de la coordinadora, 2026-09-26): el rol lo filtra
/// <c>AutorizacionEscrituraBehavior</c> para cualquier ICommand, y el alcance de
/// GESTIÓN lo comprueba <c>AutorizacionCancelacionVisita</c> en los dos handlers.
/// </summary>
public class ReactivarVisitaCommandHandlerTests
{
    private static readonly ILogger<ReactivarVisitaCommandHandler> Log = NullLogger<ReactivarVisitaCommandHandler>.Instance;

    private sealed class EvaluadorQueAnota : IEvaluadorExpedienteVisitaService
    {
        public List<Guid> Evaluadas { get; } = [];

        public Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default)
        {
            Evaluadas.Add(visitaId);
            return Task.FromResult(false);
        }

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static Visita VisitaActiva() =>
        new(Guid.NewGuid(), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), null);

    private static Visita VisitaCancelada()
    {
        var visita = VisitaActiva();
        visita.Cancelar(DateTime.UtcNow, "aplazada");
        return visita;
    }

    private static VisitaRepositorioFalso Repositorio(Visita visita)
    {
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(visita);
        return repositorio;
    }

    /// <summary>
    /// Los alcances de GESTIÓN de cada rol que puede cancelar: todo el Tenant
    /// (Administrador, Dirección CAE) o una cartera que incluye el Centro
    /// (Coordinador CAE, Gestor CAE). Se construyen sobre el Centro de la Visita.
    /// </summary>
    public static TheoryData<string> AlcancesQuePuedenCancelar => ["TodoElTenant", "CarteraConElCentro"];

    private static AlcanceDatosServiceFalso Alcance(string alcance, Guid centroId) => alcance switch
    {
        "TodoElTenant" => new AlcanceDatosServiceFalso(),
        "CarteraConElCentro" => new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [centroId]),
        "CarteraSinElCentro" => new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [Guid.NewGuid()]),
        "SoloLecturaDelCentro" => new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [centroId], centroIdsParaGestion: []),
        _ => throw new ArgumentOutOfRangeException(nameof(alcance)),
    };

    /// <summary>Simetría: con cada alcance que permite cancelar, también se reactiva.</summary>
    [Theory]
    [MemberData(nameof(AlcancesQuePuedenCancelar))]
    public async Task Quien_puede_cancelar_puede_reactivar(string alcance)
    {
        var visita = VisitaActiva();
        var repositorio = Repositorio(visita);
        var alcanceDatos = Alcance(alcance, visita.CentroId);

        var cancelacion = await new CancelarVisitaCommandHandler(repositorio, new UnitOfWorkFalso(), alcanceDatos)
            .Handle(new CancelarVisitaCommand(visita.Id), CancellationToken.None);
        var unitOfWork = new UnitOfWorkFalso();
        var evaluador = new EvaluadorQueAnota();
        var reactivacion = await new ReactivarVisitaCommandHandler(repositorio, unitOfWork, alcanceDatos, evaluador, Log)
            .Handle(new ReactivarVisitaCommand(visita.Id, "Se retoma"), CancellationToken.None);

        cancelacion.EsExitoso.Should().BeTrue("control positivo: este alcance cancela");
        reactivacion.EsExitoso.Should().BeTrue();
        visita.EstaCancelada.Should().BeFalse();
        visita.MotivoReactivacion.Should().Be("Se retoma");
        visita.ReactivadaEnUtc.Should().NotBeNull();
        unitOfWork.VecesGuardado.Should().Be(1);
        evaluador.Evaluadas.Should().Equal([visita.Id],
            "mientras estaba cancelada el evaluador la saltaba: al reactivarla se evalúa su expediente");
    }

    /// <summary>
    /// Y el negativo, simétrico también: sin alcance de GESTIÓN sobre el Centro
    /// (Gestor CAE fuera de su cartera, o quien solo lo ve) no se cancela ni se
    /// reactiva, y se responde igual que «no existe».
    /// </summary>
    [Theory]
    [InlineData("CarteraSinElCentro")]
    [InlineData("SoloLecturaDelCentro")]
    public async Task Sin_alcance_de_gestion_no_se_cancela_ni_se_reactiva(string alcance)
    {
        var activa = VisitaActiva();
        var cancelada = VisitaCancelada();
        var cancelacion = await new CancelarVisitaCommandHandler(Repositorio(activa), new UnitOfWorkFalso(), Alcance(alcance, activa.CentroId))
            .Handle(new CancelarVisitaCommand(activa.Id), CancellationToken.None);
        var unitOfWork = new UnitOfWorkFalso();
        var reactivacion = await new ReactivarVisitaCommandHandler(Repositorio(cancelada), unitOfWork, Alcance(alcance, cancelada.CentroId), new EvaluadorQueAnota(), Log)
            .Handle(new ReactivarVisitaCommand(cancelada.Id), CancellationToken.None);

        cancelacion.Error.Codigo.Should().Be("Visita.NoEncontrada");
        reactivacion.Error.Codigo.Should().Be("Visita.NoEncontrada");
        activa.EstaCancelada.Should().BeFalse();
        cancelada.EstaCancelada.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// La mitad de rol de la simetría: los dos son ICommand, así que
    /// AutorizacionEscrituraBehavior les aplica la misma lista de roles.
    /// </summary>
    [Fact]
    public void Cancelar_y_reactivar_pasan_por_el_mismo_filtro_de_rol()
    {
        typeof(CaeManager.Application.Common.ICommandBase).IsAssignableFrom(typeof(CancelarVisitaCommand)).Should().BeTrue();
        typeof(CaeManager.Application.Common.ICommandBase).IsAssignableFrom(typeof(ReactivarVisitaCommand)).Should().BeTrue();
    }

    [Fact]
    public async Task Una_visita_no_cancelada_no_se_reactiva()
    {
        var visita = VisitaActiva();
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await new ReactivarVisitaCommandHandler(Repositorio(visita), unitOfWork, new AlcanceDatosServiceFalso(), new EvaluadorQueAnota(), Log)
            .Handle(new ReactivarVisitaCommand(visita.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.NoCancelada");
        visita.ReactivadaEnUtc.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Falla_cuando_la_visita_no_existe()
    {
        var resultado = await new ReactivarVisitaCommandHandler(new VisitaRepositorioFalso(), new UnitOfWorkFalso(), new AlcanceDatosServiceFalso(), new EvaluadorQueAnota(), Log)
            .Handle(new ReactivarVisitaCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
    }
}

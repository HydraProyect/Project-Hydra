using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Application.Visitas.Commands.ReabrirExpedienteVisita;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// D-4 (#2005, contrato del 2026-11-15, opción A + (i)): solo el Gestor CAE con Asignación de Cartera sobre el Centro
/// reabre el expediente sellado de una Visita; reabrir retira el sello y reevalúa al momento con la regla común.
/// </summary>
public class ReabrirExpedienteVisitaCommandHandlerTests
{
    private static readonly DateTime Solicitud = new(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Sello = new(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc);

    private sealed class Usuario(string? rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <summary>Simula la regla común: si el expediente sigue completo, lo vuelve a sellar con la atribución que toca ahora.</summary>
    private sealed class EvaluadorQueResella(Visita visita, bool sigueCompleto) : IEvaluadorExpedienteVisitaService
    {
        public int Evaluaciones { get; private set; }

        public Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default)
        {
            Evaluaciones++;
            if (visitaId != visita.Id || !sigueCompleto)
                return Task.FromResult(false);
            return Task.FromResult(visita.MarcarExpedienteCompleto(DateTime.UtcNow,
                new ResultadoAntelacion(30m, 10m, 20m, TramoAntelacion.Estandar, AtribucionUrgencia.SinUrgencia)));
        }

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EvaluadorQueCancela : IEvaluadorExpedienteVisitaService
    {
        public Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(false);
        }

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EvaluadorConConflicto : IEvaluadorExpedienteVisitaService
    {
        public Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default) =>
            throw new Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException("otra persona la modificó");

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EvaluadorQueFalla : IEvaluadorExpedienteVisitaService
    {
        public Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("caída del evaluador");

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static DateOnly DentroDe(int dias) => DiaDeNegocio.Hoy().AddDays(dias);

    private static Visita VisitaFutura() => new(Guid.NewGuid(), DentroDe(10), DentroDe(11), null);

    private static Visita VisitaSellada(Visita? visita = null)
    {
        visita ??= VisitaFutura();
        visita.RegistrarOrigenSolicitud(Guid.NewGuid(), Solicitud);
        visita.MarcarExpedienteCompleto(Sello,
            new ResultadoAntelacion(30m, 24m, 6m, TramoAntelacion.Urgente, AtribucionUrgencia.SolicitudTardiaCliente));
        return visita;
    }

    private static VisitaRepositorioFalso Repositorio(Visita visita)
    {
        var repositorio = new VisitaRepositorioFalso();
        repositorio.Agregar(visita);
        return repositorio;
    }

    private static AlcanceDatosServiceFalso CarteraCon(Guid centroId) =>
        new(tieneAccesoTotal: false, centroIdsVisibles: [centroId]);

    private static ReabrirExpedienteVisitaCommandHandler Handler(
        Visita visita, UnitOfWorkFalso unitOfWork, IAlcanceDatosService alcance, string? rol, IEvaluadorExpedienteVisitaService evaluador,
        TransaccionDeComandoFalsa? transaccion = null) =>
        new(Repositorio(visita), unitOfWork, transaccion ?? new TransaccionDeComandoFalsa(), alcance, new Usuario(rol), evaluador,
            NullLogger<ReabrirExpedienteVisitaCommandHandler>.Instance);

    [Fact]
    public void El_rol_que_reabre_es_el_de_Gestor_CAE()
    {
        AutorizacionReaperturaExpediente.RolQueReabre.Should().Be("GestorCae");
    }

    [Fact]
    public async Task El_Gestor_CAE_con_el_Centro_en_cartera_reabre_y_si_sigue_completo_se_vuelve_a_sellar_con_atribucion_nueva()
    {
        var visita = VisitaSellada();
        var unitOfWork = new UnitOfWorkFalso();
        var evaluador = new EvaluadorQueResella(visita, sigueCompleto: true);
        var transaccion = new TransaccionDeComandoFalsa();

        var resultado = await Handler(visita, unitOfWork, CarteraCon(visita.CentroId), "GestorCae", evaluador, transaccion)
            .Handle(new ReabrirExpedienteVisitaCommand(visita.Id, visita.Version, "Certificado aceptado por error"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(1, "la reapertura se guarda antes de reevaluar; el resello lo guarda el evaluador (aquí, un doble en memoria)");
        transaccion.Confirmadas.Should().Be(1, "Codex r3 (#2266): reapertura y resello, en una sola transacción");
        evaluador.Evaluaciones.Should().Be(1, "opción A: se reevalúa al momento");
        visita.FechaHoraExpedienteCompletoUtc.Should().NotBe(Sello).And.NotBeNull();
        visita.Tramo.Should().Be(TramoAntelacion.Estandar);
        visita.Atribucion.Should().Be(AtribucionUrgencia.SinUrgencia);
        visita.MotivoReaperturaExpediente.Should().Be("Certificado aceptado por error");
        visita.ExpedienteReabiertoEnUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Si_ya_no_esta_completo_queda_sin_sello()
    {
        var visita = VisitaSellada();
        var evaluador = new EvaluadorQueResella(visita, sigueCompleto: false);

        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await Handler(visita, unitOfWork, CarteraCon(visita.CentroId), "GestorCae", evaluador)
            .Handle(new ReabrirExpedienteVisitaCommand(visita.Id, visita.Version, "Falta un certificado"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        evaluador.Evaluaciones.Should().Be(1);
        unitOfWork.VecesGuardado.Should().Be(1, "la reapertura la guarda el handler, y sin resello no hay más guardados");
        visita.FechaHoraExpedienteCompletoUtc.Should().BeNull();
        visita.Tramo.Should().BeNull();
    }

    /// <summary>
    /// Codex r2 y r3 (#2266): si la reevaluación falla, la orden falla y la transacción deshace también la reapertura ya
    /// guardada; la Visita no puede quedar sin sello por un fallo transitorio. Que el rollback deshaga de verdad lo
    /// prueba la integración.
    /// </summary>
    [Fact]
    public async Task Si_la_reevaluacion_falla_se_deshace_la_reapertura()
    {
        var visita = VisitaSellada();
        var transaccion = new TransaccionDeComandoFalsa();

        var resultado = await Handler(visita, new UnitOfWorkFalso(), CarteraCon(visita.CentroId), "GestorCae", new EvaluadorQueFalla(), transaccion)
            .Handle(new ReabrirExpedienteVisitaCommand(visita.Id, visita.Version, "motivo"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.ReaperturaNoEvaluada");
        transaccion.Deshechas.Should().Be(1);
        transaccion.Confirmadas.Should().Be(0);
    }

    /// <summary>revisor-puente (#2266): un conflicto en el guardado del evaluador llega a ConcurrenciaBehavior, no se traga.</summary>
    [Fact]
    public async Task Un_conflicto_de_concurrencia_al_resellar_se_propaga()
    {
        var visita = VisitaSellada();
        var transaccion = new TransaccionDeComandoFalsa();

        var reabrir = () => Handler(visita, new UnitOfWorkFalso(), CarteraCon(visita.CentroId), "GestorCae", new EvaluadorConConflicto(), transaccion)
            .Handle(new ReabrirExpedienteVisitaCommand(visita.Id, visita.Version, "motivo"), CancellationToken.None);

        await reabrir.Should().ThrowAsync<Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException>();
        transaccion.Deshechas.Should().Be(1);
        transaccion.Confirmadas.Should().Be(0);
    }

    [Fact]
    public async Task Si_la_reevaluacion_se_cancela_no_se_guarda_y_la_cancelacion_se_propaga()
    {
        var visita = VisitaSellada();
        var transaccion = new TransaccionDeComandoFalsa();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var reabrir = () => Handler(visita, new UnitOfWorkFalso(), CarteraCon(visita.CentroId), "GestorCae", new EvaluadorQueCancela(), transaccion)
            .Handle(new ReabrirExpedienteVisitaCommand(visita.Id, visita.Version, "motivo"), cts.Token);

        await reabrir.Should().ThrowAsync<OperationCanceledException>();
        transaccion.Deshechas.Should().Be(1);
        transaccion.Confirmadas.Should().Be(0);
    }

    /// <summary>
    /// revisor-puente (#2266): una Visita que ya empezó o pasó no se reabre; resellarla con «ahora» daría tramo Exprés
    /// y una atribución al Cliente empresarial que no describe lo que pasó.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task Una_visita_que_ya_empezo_no_se_reabre(int inicioDentroDeDias)
    {
        var visita = VisitaSellada(new Visita(Guid.NewGuid(), DentroDe(inicioDentroDeDias), DentroDe(inicioDentroDeDias + 5), null));
        var unitOfWork = new UnitOfWorkFalso();
        var evaluador = new EvaluadorQueResella(visita, sigueCompleto: true);

        var resultado = await Handler(visita, unitOfWork, CarteraCon(visita.CentroId), "GestorCae", evaluador)
            .Handle(new ReabrirExpedienteVisitaCommand(visita.Id, visita.Version, "motivo"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.ReaperturaFueraDePlazo");
        visita.FechaHoraExpedienteCompletoUtc.Should().Be(Sello);
        unitOfWork.VecesGuardado.Should().Be(0);
        evaluador.Evaluaciones.Should().Be(0);
    }

    [Fact]
    public async Task Una_visita_que_empieza_manana_si_se_reabre()
    {
        var visita = VisitaSellada(new Visita(Guid.NewGuid(), DentroDe(1), DentroDe(2), null));

        var resultado = await Handler(visita, new UnitOfWorkFalso(), CarteraCon(visita.CentroId), "GestorCae", new EvaluadorQueResella(visita, false))
            .Handle(new ReabrirExpedienteVisitaCommand(visita.Id, visita.Version, "motivo"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("control del límite: antes del día de la visita se reabre");
    }

    /// <summary>Gestor CAE sin el Centro en su cartera: se responde igual que «no existe» y el sello no se toca.</summary>
    [Fact]
    public async Task El_Gestor_CAE_fuera_de_cartera_no_reabre()
    {
        var visita = VisitaSellada();
        var unitOfWork = new UnitOfWorkFalso();
        var evaluador = new EvaluadorQueResella(visita, sigueCompleto: true);

        var resultado = await Handler(visita, unitOfWork, CarteraCon(Guid.NewGuid()), "GestorCae", evaluador)
            .Handle(new ReabrirExpedienteVisitaCommand(visita.Id, visita.Version, "motivo"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
        visita.FechaHoraExpedienteCompletoUtc.Should().Be(Sello);
        unitOfWork.VecesGuardado.Should().Be(0);
        evaluador.Evaluaciones.Should().Be(0);
    }

    /// <summary>
    /// Gestor CAE que ve el Centro pero no lo gestiona (solo lectura): tampoco reabre.
    /// </summary>
    [Fact]
    public async Task El_Gestor_CAE_con_el_Centro_solo_en_lectura_no_reabre()
    {
        var visita = VisitaSellada();
        var unitOfWork = new UnitOfWorkFalso();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [visita.CentroId], centroIdsParaGestion: []);

        var resultado = await Handler(visita, unitOfWork, alcance, "GestorCae", new EvaluadorQueResella(visita, true))
            .Handle(new ReabrirExpedienteVisitaCommand(visita.Id, visita.Version, "motivo"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
        visita.FechaHoraExpedienteCompletoUtc.Should().Be(Sello);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// El Administrador del Tenant propietario y Dirección CAE tienen alcance «sin restricción», no una Asignación de
    /// Cartera; el Coordinador CAE puede tener el Centro en su alcance, pero no es Gestor CAE. Ninguno reabre.
    /// Un usuario sin rol efectivo en el Tenant (Soporte TALVEG sin Sesión Privilegiada) tampoco.
    /// </summary>
    /// <remarks>
    /// Cada rol se prueba dos veces: con su alcance real («todo el Tenant» para Administrador, Dirección CAE y Consulta) y
    /// con una cartera que sí incluye el Centro. La segunda aísla la comprobación de rol (revisor-puente, #2266): sin ella,
    /// la de «cartera no nula» taparía su ausencia.
    /// </remarks>
    [Theory]
    [InlineData("Administrador", true)]
    [InlineData("Administrador", false)]
    [InlineData("DireccionCae", true)]
    [InlineData("DireccionCae", false)]
    [InlineData("CoordinadorCae", false)]
    [InlineData("Consulta", true)]
    [InlineData("Consulta", false)]
    [InlineData(null, false)]
    public async Task Quien_no_es_Gestor_CAE_no_reabre_aunque_alcance_el_Centro(string? rol, bool todoElTenant)
    {
        var visita = VisitaSellada();
        var unitOfWork = new UnitOfWorkFalso();
        var evaluador = new EvaluadorQueResella(visita, sigueCompleto: true);
        IAlcanceDatosService alcance = todoElTenant ? new AlcanceDatosServiceFalso() : CarteraCon(visita.CentroId);

        var resultado = await Handler(visita, unitOfWork, alcance, rol, evaluador)
            .Handle(new ReabrirExpedienteVisitaCommand(visita.Id, visita.Version, "motivo"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.ReaperturaSoloGestorCae");
        visita.FechaHoraExpedienteCompletoUtc.Should().Be(Sello);
        visita.ExpedienteReabiertoEnUtc.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(0);
        evaluador.Evaluaciones.Should().Be(0);
    }

    /// <summary>
    /// Defensa en profundidad: un Gestor CAE cuyo alcance saliera «sin restricción» (lo que hoy no ocurre) no reabre:
    /// sin restricción no es una Asignación de Cartera.
    /// </summary>
    [Fact]
    public async Task Un_Gestor_CAE_con_alcance_sin_restriccion_no_reabre()
    {
        var visita = VisitaSellada();
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await Handler(visita, unitOfWork, new AlcanceDatosServiceFalso(), "GestorCae", new EvaluadorQueResella(visita, true))
            .Handle(new ReabrirExpedienteVisitaCommand(visita.Id, visita.Version, "motivo"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.ReaperturaSoloGestorCae");
        visita.FechaHoraExpedienteCompletoUtc.Should().Be(Sello);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_una_version_vieja_no_se_reabre()
    {
        var visita = VisitaSellada();
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await Handler(visita, unitOfWork, CarteraCon(visita.CentroId), "GestorCae", new EvaluadorQueResella(visita, true))
            .Handle(new ReabrirExpedienteVisitaCommand(visita.Id, Guid.NewGuid(), "motivo"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        visita.FechaHoraExpedienteCompletoUtc.Should().Be(Sello);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Sin_motivo_no_se_reabre(string motivo)
    {
        var visita = VisitaSellada();
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await Handler(visita, unitOfWork, CarteraCon(visita.CentroId), "GestorCae", new EvaluadorQueResella(visita, true))
            .Handle(new ReabrirExpedienteVisitaCommand(visita.Id, visita.Version, motivo), CancellationToken.None);

        resultado.EsExitoso.Should().BeFalse();
        visita.FechaHoraExpedienteCompletoUtc.Should().Be(Sello);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_version_no_se_reabre()
    {
        var visita = VisitaSellada();
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await Handler(visita, unitOfWork, CarteraCon(visita.CentroId), "GestorCae", new EvaluadorQueResella(visita, true))
            .Handle(new ReabrirExpedienteVisitaCommand(visita.Id, Guid.Empty, "motivo"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.VersionRequerida");
        visita.FechaHoraExpedienteCompletoUtc.Should().Be(Sello);
    }

    [Fact]
    public async Task Una_visita_sin_sello_no_se_reabre()
    {
        var visita = new Visita(Guid.NewGuid(), new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 11), null);
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await Handler(visita, unitOfWork, CarteraCon(visita.CentroId), "GestorCae", new EvaluadorQueResella(visita, true))
            .Handle(new ReabrirExpedienteVisitaCommand(visita.Id, visita.Version, "motivo"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.ExpedienteNoSellado");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_visita_cancelada_no_se_reabre()
    {
        var visita = VisitaSellada();
        visita.Cancelar(DateTime.UtcNow, "aplazada");
        var unitOfWork = new UnitOfWorkFalso();

        var resultado = await Handler(visita, unitOfWork, CarteraCon(visita.CentroId), "GestorCae", new EvaluadorQueResella(visita, true))
            .Handle(new ReabrirExpedienteVisitaCommand(visita.Id, visita.Version, "motivo"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Visita.Cancelada");
        visita.FechaHoraExpedienteCompletoUtc.Should().Be(Sello);
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void El_validador_exige_motivo_y_version()
    {
        var validador = new ReabrirExpedienteVisitaCommandValidator();

        validador.Validate(new ReabrirExpedienteVisitaCommand(Guid.NewGuid(), Guid.NewGuid(), "motivo")).IsValid.Should().BeTrue();
        validador.Validate(new ReabrirExpedienteVisitaCommand(Guid.NewGuid(), Guid.NewGuid(), " ")).IsValid.Should().BeFalse();
        validador.Validate(new ReabrirExpedienteVisitaCommand(Guid.NewGuid(), Guid.Empty, "motivo")).IsValid.Should().BeFalse();
        validador.Validate(new ReabrirExpedienteVisitaCommand(Guid.NewGuid(), Guid.NewGuid(), new string('x', Visita.LongitudMaximaMotivo + 1)))
            .IsValid.Should().BeFalse();
    }
}

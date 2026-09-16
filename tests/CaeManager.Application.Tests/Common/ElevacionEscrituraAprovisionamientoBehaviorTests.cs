using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Common;

public class ElevacionEscrituraAprovisionamientoBehaviorTests
{
    private record FalsoComandoDeAprovisionamiento : IComandoDeAprovisionamiento, IRequest<string>;
    private record FalsoComandoSinMarcador : IRequest<string>;

    private static SesionPrivilegiadaActiva SesionCon(CapacidadPrivilegio capacidad, Guid tenantObjetivoId) =>
        new(Guid.NewGuid(), Guid.NewGuid(), tenantObjetivoId, capacidad, null);

    [Fact]
    public async Task Un_comando_sin_marcador_nunca_eleva_ni_consulta_la_sesion()
    {
        var elevacion = new ElevacionEscrituraPrivilegiadaFalsa();
        var behavior = new ElevacionEscrituraAprovisionamientoBehavior<FalsoComandoSinMarcador, string>(
            new SesionPrivilegiadaActualFalsa(SesionCon(CapacidadPrivilegio.Aprovisionamiento, Guid.NewGuid())),
            new TenantActualFalso(null), elevacion);

        var resultado = await behavior.Handle(
            new FalsoComandoSinMarcador(), _ => Task.FromResult("ok"), CancellationToken.None);

        resultado.Should().Be("ok");
        elevacion.SeElevo.Should().BeFalse();
    }

    [Fact]
    public async Task Un_comando_marcado_sin_sesion_privilegiada_no_eleva()
    {
        var elevacion = new ElevacionEscrituraPrivilegiadaFalsa();
        var behavior = new ElevacionEscrituraAprovisionamientoBehavior<FalsoComandoDeAprovisionamiento, string>(
            new SesionPrivilegiadaActualFalsa(null), new TenantActualFalso(null), elevacion);

        var resultado = await behavior.Handle(
            new FalsoComandoDeAprovisionamiento(), _ => Task.FromResult("ok"), CancellationToken.None);

        resultado.Should().Be("ok");
        elevacion.SeElevo.Should().BeFalse();
    }

    [Fact]
    public async Task Un_comando_marcado_con_sesion_de_Aprovisionamiento_fuera_de_tenant_no_eleva()
    {
        var elevacion = new ElevacionEscrituraPrivilegiadaFalsa();
        var tenantSesion = Guid.NewGuid();
        var behavior = new ElevacionEscrituraAprovisionamientoBehavior<FalsoComandoDeAprovisionamiento, string>(
            new SesionPrivilegiadaActualFalsa(SesionCon(CapacidadPrivilegio.Aprovisionamiento, tenantSesion)),
            new TenantActualFalso(Guid.NewGuid()), elevacion);

        var resultado = await behavior.Handle(
            new FalsoComandoDeAprovisionamiento(), _ => Task.FromResult("ok"), CancellationToken.None);

        resultado.Should().Be("ok");
        elevacion.SeElevo.Should().BeFalse();
    }

    [Fact]
    public async Task Una_sesion_sin_camino_de_escritura_no_eleva_aunque_el_comando_este_marcado()
    {
        var elevacion = new ElevacionEscrituraPrivilegiadaFalsa();
        var tenant = Guid.NewGuid();
        var behavior = new ElevacionEscrituraAprovisionamientoBehavior<FalsoComandoDeAprovisionamiento, string>(
            new SesionPrivilegiadaActualFalsa(SesionCon(CapacidadPrivilegio.BreakGlass, tenant)),
            new TenantActualFalso(tenant), elevacion);

        var resultado = await behavior.Handle(
            new FalsoComandoDeAprovisionamiento(), _ => Task.FromResult("ok"), CancellationToken.None);

        resultado.Should().Be("ok");
        elevacion.SeElevo.Should().BeFalse();
    }

    /// <summary>
    /// Hallazgo de composición completa (Codex, revisión previa a este PR):
    /// <c>AmbitoEscrituraPrivilegiada.Establecer</c> tiene que llamarse
    /// SÍNCRONAMENTE dentro de <c>Handle</c>, no a través de un método
    /// <c>async</c> de <see cref="IElevacionEscrituraPrivilegiada"/> — ese
    /// método puede completar de forma síncrona (conexión cerrada, el caso
    /// más común) y en ese caso la mutación del <c>AsyncLocal</c> no
    /// sobrevive de vuelta en el llamador. Este test comprueba justo eso:
    /// que el ámbito está REALMENTE abierto (vía <c>AmbitoEscrituraPrivilegiada.Actual</c>,
    /// no solo un booleano del doble) mientras corre el handler, y cerrado
    /// después — con un doble cuya implementación de
    /// <see cref="IElevacionEscrituraPrivilegiada"/> es <c>async</c> y no
    /// hace nada más (como <c>ElevacionEscrituraPrivilegiadaInerte</c>), para
    /// que una regresión que vuelva a delegar la apertura del ámbito a un
    /// método async ajeno no pueda dar falso verde aquí.
    /// </summary>
    [Fact]
    public async Task Comando_marcado_mas_sesion_de_Aprovisionamiento_sobre_el_mismo_tenant_eleva_y_cierra()
    {
        var elevacion = new ElevacionEscrituraPrivilegiadaFalsa();
        var tenant = Guid.NewGuid();
        var sesion = SesionCon(CapacidadPrivilegio.Aprovisionamiento, tenant);
        var behavior = new ElevacionEscrituraAprovisionamientoBehavior<FalsoComandoDeAprovisionamiento, string>(
            new SesionPrivilegiadaActualFalsa(sesion), new TenantActualFalso(tenant), elevacion);

        (Guid SesionId, Guid TenantObjetivoId)? ambitoDentroDelHandler = null;
        var resultado = await behavior.Handle(new FalsoComandoDeAprovisionamiento(), _ =>
        {
            ambitoDentroDelHandler = AmbitoEscrituraPrivilegiada.Actual;
            return Task.FromResult("ok");
        }, CancellationToken.None);

        resultado.Should().Be("ok");
        elevacion.SeElevo.Should().BeTrue();
        elevacion.SeDevolvio.Should().BeTrue();
        ambitoDentroDelHandler.Should().Be((sesion.SesionId, sesion.TenantObjetivoId),
            "el ámbito tiene que estar abierto MIENTRAS corre el handler, visible vía el AsyncLocal real");
        AmbitoEscrituraPrivilegiada.Actual.Should().BeNull("el ámbito se cierra al salir de next");
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class SesionPrivilegiadaActualFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);
    }

    /// <summary>
    /// Async a propósito, como la implementación real: no vale un doble
    /// síncrono aquí, precisamente porque el hallazgo que blinda este archivo
    /// era específico de métodos <c>async</c> que completan sin suspenderse.
    /// </summary>
    private sealed class ElevacionEscrituraPrivilegiadaFalsa : IElevacionEscrituraPrivilegiada
    {
        public bool SeElevo { get; private set; }
        public bool SeDevolvio { get; private set; }

        public async Task ElevarSiConexionAbiertaAsync(CancellationToken cancellationToken = default)
        {
            SeElevo = true;
            await Task.CompletedTask;
        }

        public async Task DevolverSiConexionAbiertaAsync(CancellationToken cancellationToken = default)
        {
            SeDevolvio = true;
            await Task.CompletedTask;
        }
    }
}

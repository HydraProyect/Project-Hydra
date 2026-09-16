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
        elevacion.SeEstablecio.Should().BeFalse();
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
        elevacion.SeEstablecio.Should().BeFalse();
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
        elevacion.SeEstablecio.Should().BeFalse();
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
        elevacion.SeEstablecio.Should().BeFalse();
    }

    [Fact]
    public async Task Comando_marcado_mas_sesion_de_Aprovisionamiento_sobre_el_mismo_tenant_eleva_y_cierra()
    {
        var elevacion = new ElevacionEscrituraPrivilegiadaFalsa();
        var tenant = Guid.NewGuid();
        var sesion = SesionCon(CapacidadPrivilegio.Aprovisionamiento, tenant);
        var behavior = new ElevacionEscrituraAprovisionamientoBehavior<FalsoComandoDeAprovisionamiento, string>(
            new SesionPrivilegiadaActualFalsa(sesion), new TenantActualFalso(tenant), elevacion);

        var siEstabaElevadoDentroDelHandler = false;
        var resultado = await behavior.Handle(new FalsoComandoDeAprovisionamiento(), _ =>
        {
            siEstabaElevadoDentroDelHandler = elevacion.SeEstablecio && !elevacion.SeCerro;
            return Task.FromResult("ok");
        }, CancellationToken.None);

        resultado.Should().Be("ok");
        elevacion.SeEstablecio.Should().BeTrue();
        elevacion.SesionIdRecibida.Should().Be(sesion.SesionId);
        elevacion.TenantObjetivoIdRecibido.Should().Be(sesion.TenantObjetivoId);
        siEstabaElevadoDentroDelHandler.Should().BeTrue("el ámbito tiene que estar abierto MIENTRAS corre el handler");
        elevacion.SeCerro.Should().BeTrue("el ámbito se cierra al salir de next, vía el using del propio behavior");
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

    private sealed class ElevacionEscrituraPrivilegiadaFalsa : IElevacionEscrituraPrivilegiada
    {
        public bool SeEstablecio { get; private set; }
        public bool SeCerro { get; private set; }
        public Guid? SesionIdRecibida { get; private set; }
        public Guid? TenantObjetivoIdRecibido { get; private set; }

        public Task<IAsyncDisposable> EstablecerAsync(
            Guid sesionId, Guid tenantObjetivoId, CancellationToken cancellationToken = default)
        {
            SeEstablecio = true;
            SesionIdRecibida = sesionId;
            TenantObjetivoIdRecibido = tenantObjetivoId;
            return Task.FromResult<IAsyncDisposable>(new Cierre(this));
        }

        private sealed class Cierre(ElevacionEscrituraPrivilegiadaFalsa duenio) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                duenio.SeCerro = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}

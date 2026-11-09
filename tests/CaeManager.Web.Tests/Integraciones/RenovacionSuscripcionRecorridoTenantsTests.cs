using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Integraciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TenantDelCiclo = CaeManager.Infrastructure.Integraciones.RenovacionSuscripcionWebhookHostedService.TenantDelCiclo;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// Reparto del ciclo diario de <see cref="RenovacionSuscripcionWebhookHostedService"/> entre Tenants propietarios: los
/// activos verifican identidad y renuevan; los no activos solo pasan por el barrido de la guarda transitoria, para que una
/// reclamación heredada suya no deje en pausa para siempre el alta de buzones compartidos en toda la plataforma.
/// </summary>
public class RenovacionSuscripcionRecorridoTenantsTests
{
    private sealed class Registro
    {
        public List<Guid> CicloActivo { get; } = [];
        public List<Guid> Barrido { get; } = [];
        public HashSet<Guid> QueLanzan { get; } = [];

        public Task Ciclo(Guid tenantId, CancellationToken _)
        {
            CicloActivo.Add(tenantId);
            return QueLanzan.Contains(tenantId) ? Task.FromException(new InvalidOperationException("fallo simulado")) : Task.CompletedTask;
        }

        public Task Barrer(Guid tenantId, CancellationToken _)
        {
            Barrido.Add(tenantId);
            return QueLanzan.Contains(tenantId) ? Task.FromException(new InvalidOperationException("fallo simulado")) : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TenantNoActivo_SoloPasaPorElBarrido_SinVerificacionNiRenovacion()
    {
        var activo = Guid.NewGuid();
        var suspendido = Guid.NewGuid();
        var registro = new Registro();

        await RenovacionSuscripcionWebhookHostedService.RecorrerTenantsAsync(
            [new TenantDelCiclo(suspendido, Activo: false), new TenantDelCiclo(activo, Activo: true)],
            registro.Ciclo, registro.Barrer, NullLogger.Instance, CancellationToken.None);

        registro.CicloActivo.Should().Equal(activo);
        registro.Barrido.Should().Equal(suspendido);
    }

    [Fact]
    public async Task LosActivosVanPrimero_ParaQueElBarridoNoRetraseNingunaRenovacion()
    {
        var activo = Guid.NewGuid();
        var suspendido = Guid.NewGuid();
        var orden = new List<Guid>();

        await RenovacionSuscripcionWebhookHostedService.RecorrerTenantsAsync(
            [new TenantDelCiclo(suspendido, Activo: false), new TenantDelCiclo(activo, Activo: true)],
            (id, _) => { orden.Add(id); return Task.CompletedTask; },
            (id, _) => { orden.Add(id); return Task.CompletedTask; },
            NullLogger.Instance, CancellationToken.None);

        orden.Should().Equal(activo, suspendido);
    }

    [Fact]
    public async Task UnTenantQueFalla_NoDejaSinRecorrerALosSiguientes()
    {
        var activoRoto = Guid.NewGuid();
        var activoSano = Guid.NewGuid();
        var suspendidoRoto = Guid.NewGuid();
        var suspendidoSano = Guid.NewGuid();
        var registro = new Registro();
        registro.QueLanzan.UnionWith([activoRoto, suspendidoRoto]);

        await RenovacionSuscripcionWebhookHostedService.RecorrerTenantsAsync(
            [
                new TenantDelCiclo(activoRoto, Activo: true),
                new TenantDelCiclo(suspendidoRoto, Activo: false),
                new TenantDelCiclo(activoSano, Activo: true),
                new TenantDelCiclo(suspendidoSano, Activo: false),
            ],
            registro.Ciclo, registro.Barrer, NullLogger.Instance, CancellationToken.None);

        registro.CicloActivo.Should().Equal(activoRoto, activoSano);
        registro.Barrido.Should().Equal(suspendidoRoto, suspendidoSano);
    }

    // Ronda 11, B4: el cableado que hace terminal la reclamación de un Tenant no activo.

    [Theory]
    [InlineData(EstadoTenant.Activo, true)]
    [InlineData(EstadoTenant.Suspendido, false)]
    public void SoloUnTenantActivo_VerificaYRenueva(EstadoTenant estado, bool activo)
    {
        var id = Guid.NewGuid();

        RenovacionSuscripcionWebhookHostedService.DelTenant(id, estado).Should().Be(new TenantDelCiclo(id, activo));
    }

    [Fact]
    public void CualquierEstadoQueNoSeaActivo_VaAlBarrido()
    {
        foreach (var estado in Enum.GetValues<EstadoTenant>().Where(e => e != EstadoTenant.Activo))
            RenovacionSuscripcionWebhookHostedService.DelTenant(Guid.NewGuid(), estado).Activo.Should().BeFalse(estado.ToString());
    }

    [Fact]
    public async Task ElBarridoDeUnTenantNoActivo_TrataSusReclamacionesComoTerminales()
    {
        var pedidos = new List<bool>();

        await RenovacionSuscripcionWebhookHostedService.BarrerTenantNoActivoAsync(
            (tenantActivo, _) => { pedidos.Add(tenantActivo); return Task.CompletedTask; }, CancellationToken.None);

        pedidos.Should().Equal(false);
    }

    [Fact]
    public async Task LaCancelacionPedida_SePropaga()
    {
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();
        var registro = new Registro();

        var recorrer = () => RenovacionSuscripcionWebhookHostedService.RecorrerTenantsAsync(
            [new TenantDelCiclo(Guid.NewGuid(), Activo: false)],
            registro.Ciclo, registro.Barrer, NullLogger.Instance, cancelacion.Token);

        await recorrer.Should().ThrowAsync<OperationCanceledException>();
        registro.Barrido.Should().BeEmpty();
    }
}

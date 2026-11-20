using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comercial;

/// <summary>
/// D3: <see cref="OutboundCerradoTenantTecService"/> responde «cerrado» solo para el Tenant beneficiario marcado como
/// participante TEC que no tiene la línea Outbound vigente con el inicio alcanzado. El estado de la línea lo da
/// <see cref="IEstadoLineaOutbound"/>, que prueba <c>EstadoLineaOutboundTests</c>; aquí se fija para recorrer sus cuatro
/// valores, incluido <see cref="EstadoLineaOutbound.Programada"/> (Vigente con el inicio en el futuro), que ninguna vía
/// escribe hoy.
/// </summary>
public class OutboundCerradoTenantTecServiceTests
{
    private readonly TenantsQueryContextFalso _tenants = new();

    private Tenant Alta(bool marcado)
    {
        var tenant = new Tenant("Tenant de prueba");
        if (marcado)
            tenant.MarcarComoParticipanteTec();
        _tenants.ListaTenants.Add(tenant);
        return tenant;
    }

    private OutboundCerradoTenantTecService Servicio(EstadoLineaFijo estado) => new(_tenants, estado);

    [Theory]
    [InlineData(EstadoLineaOutbound.SinLinea, true)]
    [InlineData(EstadoLineaOutbound.Programada, true)]
    [InlineData(EstadoLineaOutbound.Finalizada, true)]
    [InlineData(EstadoLineaOutbound.Vigente, false)]
    public async Task El_Tenant_marcado_solo_abre_con_la_linea_Outbound_vigente(EstadoLineaOutbound estado, bool cerrado)
    {
        var tenant = Alta(marcado: true);
        var linea = new EstadoLineaFijo(estado);

        (await Servicio(linea).EstaCerradoAsync(tenant.Id)).Should().Be(cerrado);
        linea.Preguntados.Should().Equal(tenant.Id);
    }

    [Theory]
    [InlineData(EstadoLineaOutbound.SinLinea)]
    [InlineData(EstadoLineaOutbound.Programada)]
    [InlineData(EstadoLineaOutbound.Finalizada)]
    [InlineData(EstadoLineaOutbound.Vigente)]
    public async Task El_Tenant_sin_marcar_nunca_queda_cerrado(EstadoLineaOutbound estado)
    {
        var tenant = Alta(marcado: false);
        var linea = new EstadoLineaFijo(estado);

        (await Servicio(linea).EstaCerradoAsync(tenant.Id)).Should().BeFalse("D3 solo restringe a los Tenants marcados (F-2)");
        linea.Preguntados.Should().BeEmpty("sin marcador no hace falta leer la línea");
    }

    [Fact]
    public async Task Un_Tenant_que_no_aparece_cuenta_como_cerrado()
    {
        Alta(marcado: false);

        (await Servicio(new EstadoLineaFijo(EstadoLineaOutbound.Vigente)).EstaCerradoAsync(Guid.NewGuid()))
            .Should().BeTrue("falla cerrado");
    }

    [Fact]
    public async Task La_linea_vigente_de_otro_Tenant_no_abre()
    {
        var marcado = Alta(marcado: true);
        var otro = Alta(marcado: false);
        var linea = new EstadoLineaFijo(EstadoLineaOutbound.SinLinea, vigenteSoloPara: otro.Id);

        (await Servicio(linea).EstaCerradoAsync(marcado.Id)).Should().BeTrue();
        // Pregunta por el Tenant beneficiario, no por otro.
        linea.Preguntados.Should().Equal(marcado.Id);
    }

    [Fact]
    public void Marcar_es_idempotente()
    {
        var tenant = new Tenant("Tenant de prueba");
        tenant.EsParticipanteTec.Should().BeFalse("el valor por defecto es sin marcar");

        tenant.MarcarComoParticipanteTec();
        tenant.MarcarComoParticipanteTec();

        tenant.EsParticipanteTec.Should().BeTrue();
    }

    private sealed class EstadoLineaFijo(EstadoLineaOutbound estado, Guid? vigenteSoloPara = null) : IEstadoLineaOutbound
    {
        public List<Guid> Preguntados { get; } = [];

        public Task<EstadoLineaOutbound> ObtenerEstadoLineaOutboundAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            Preguntados.Add(tenantId);
            return Task.FromResult(vigenteSoloPara == tenantId ? EstadoLineaOutbound.Vigente : estado);
        }

        public async Task<bool> TieneLineaOutboundVigenteAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
            await ObtenerEstadoLineaOutboundAsync(tenantId, cancellationToken) == EstadoLineaOutbound.Vigente;
    }
}

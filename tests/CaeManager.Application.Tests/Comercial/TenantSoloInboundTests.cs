using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Application.Tests.Comercial;

/// <summary>
/// «Inbound sin Outbound» (decisión del 2026-11-18): <see cref="TenantSoloInboundService"/> dice «solo Inbound» con la
/// línea Inbound vigente y ninguna línea Outbound, y <see cref="CriterioDeOutbound"/> solo lo aplica con el papel de Tenant
/// de origen. El contexto falso devuelve todas las filas, de todos los Tenants y también las eliminadas, para que el filtro
/// que se prueba sea el del servicio.
/// </summary>
public class TenantSoloInboundTests
{
    private static readonly DateTime Ahora = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.NewGuid();

    private readonly List<ProductoContratado> _filas = [];
    private int _lecturas;

    private TenantSoloInboundService Servicio() => new(new ContextoFalso(_filas, () => _lecturas++), new RelojFijo(Ahora));

    private Task<bool> EsSoloInboundAsync(Guid? tenant = null) => Servicio().EsSoloInboundAsync(tenant ?? Tenant);

    private ProductoContratado Contratar(LineaProductoContratado linea, Guid? tenant = null, DateTime? desde = null)
    {
        var producto = ProductoContratado.Contratar(linea, "cus_PagadorTalveg", TramoTrabajadoresActivos.De1A10,
            ModalidadSuscripcionAlta.Anual, desde ?? Ahora.AddDays(-30), ahoraUtc: Ahora);
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(producto, tenant ?? Tenant);
        _filas.Add(producto);
        return producto;
    }

    [Fact]
    public async Task Solo_la_linea_Inbound_vigente_es_solo_Inbound()
    {
        Contratar(LineaProductoContratado.Inbound);

        (await EsSoloInboundAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task Sin_ninguna_linea_no_es_solo_Inbound_el_Outbound_historico_conserva_Outbound()
    {
        (await EsSoloInboundAsync()).Should().BeFalse("no hay backfill de la línea Outbound: exigirla quitaría Outbound");
    }

    [Fact]
    public async Task Con_las_dos_lineas_no_es_solo_Inbound()
    {
        Contratar(LineaProductoContratado.Inbound);
        Contratar(LineaProductoContratado.OutboundGestionPropia);

        (await EsSoloInboundAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Una_linea_Outbound_finalizada_sigue_siendo_una_linea_Outbound()
    {
        Contratar(LineaProductoContratado.Inbound);
        Contratar(LineaProductoContratado.OutboundGestionPropia).Finalizar(Ahora.AddDays(-1), ahoraUtc: Ahora);

        (await EsSoloInboundAsync()).Should().BeFalse("«ninguna línea Outbound» es que no hay fila; ante la duda, se conserva");
    }

    [Fact]
    public async Task Una_linea_Outbound_eliminada_no_cuenta()
    {
        Contratar(LineaProductoContratado.Inbound);
        Contratar(LineaProductoContratado.OutboundGestionPropia).MarcarComoEliminado(Guid.NewGuid());

        (await EsSoloInboundAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task La_linea_Inbound_finalizada_o_eliminada_no_es_vigente()
    {
        Contratar(LineaProductoContratado.Inbound).Finalizar(Ahora.AddDays(-1), ahoraUtc: Ahora);
        Contratar(LineaProductoContratado.Inbound).MarcarComoEliminado(Guid.NewGuid());

        (await EsSoloInboundAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Las_lineas_de_otro_Tenant_no_cuentan()
    {
        Contratar(LineaProductoContratado.Inbound, tenant: Guid.NewGuid());
        Contratar(LineaProductoContratado.Inbound);
        Contratar(LineaProductoContratado.OutboundGestionPropia, tenant: Guid.NewGuid());

        (await EsSoloInboundAsync()).Should().BeTrue();
        (await EsSoloInboundAsync(Guid.NewGuid())).Should().BeFalse();
    }

    private CriterioDeOutbound Criterio(Guid? tenantActivo) => new(new ServiceCollection()
        .AddSingleton<ITenantActual>(new TenantFijo(tenantActivo))
        .AddSingleton<ITenantSoloInbound>(Servicio())
        .BuildServiceProvider());

    [Theory]
    [InlineData(PapelActivo.TenantDeOrigen, false)]
    [InlineData(PapelActivo.Outbound, true)]
    [InlineData(PapelActivo.SinPapel, true)]
    [InlineData(PapelActivo.Inbound, false)]
    [InlineData(PapelActivo.OperacionQueYaNoAutoriza, false)]
    public async Task El_criterio_solo_cierra_Outbound_al_papel_de_Tenant_de_origen(PapelActivo papel, bool admite)
    {
        Contratar(LineaProductoContratado.Inbound);

        (await Criterio(Tenant).AdmiteOutboundAsync(papel)).Should().Be(admite);
        (await Criterio(Tenant).CierraPorSoloInboundAsync(papel)).Should().Be(papel == PapelActivo.TenantDeOrigen);
    }

    [Fact]
    public async Task Con_otro_papel_el_criterio_no_lee_el_Producto_Contratado()
    {
        Contratar(LineaProductoContratado.Inbound);

        await Criterio(Tenant).AdmiteOutboundAsync(PapelActivo.Outbound);

        _lecturas.Should().Be(0);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    public async Task El_Tenant_de_origen_que_no_es_solo_Inbound_conserva_Outbound(bool inbound, bool outbound, bool admite)
    {
        if (inbound) Contratar(LineaProductoContratado.Inbound);
        if (outbound) Contratar(LineaProductoContratado.OutboundGestionPropia);

        (await Criterio(Tenant).AdmiteOutboundAsync(PapelActivo.TenantDeOrigen)).Should().Be(admite);
    }

    [Fact]
    public async Task Sin_Tenant_activo_el_criterio_no_cierra_nada()
    {
        Contratar(LineaProductoContratado.Inbound);

        (await Criterio(null).AdmiteOutboundAsync(PapelActivo.TenantDeOrigen)).Should().BeTrue();
    }

    private sealed class TenantFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId { get; } = tenantId;
    }

    private sealed class ContextoFalso(List<ProductoContratado> filas, Action alLeer) : ILineaOutboundQueryContext
    {
        public IQueryable<ProductoContratado> ProductosContratados
        {
            get
            {
                alLeer();
                return new TestAsyncQueryable<ProductoContratado>(filas.AsQueryable());
            }
        }
    }

    private sealed class RelojFijo(DateTime ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahora);
    }
}

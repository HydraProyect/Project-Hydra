using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using FluentAssertions;

namespace CaeManager.Application.Tests.Comercial;

/// <summary>
/// <see cref="EstadoLineaOutboundService"/>: el estado de la línea Outbound del Producto Contratado de un Tenant
/// beneficiario. El contexto de lectura falso devuelve todas las filas, de todos los Tenants y también las eliminadas, para
/// que el filtro que se prueba sea el del servicio y no el global de EF ni la RLS. <see cref="EstadoLineaOutbound.Programada"/>
/// no tiene test: <c>ProductoContratado.Contratar</c> no admite un inicio futuro, y ninguna vía la escribe.
/// </summary>
public class EstadoLineaOutboundTests
{
    private static readonly DateTime Ahora = new(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid OtroTenant = Guid.NewGuid();

    private readonly List<ProductoContratado> _filas = [];

    private EstadoLineaOutboundService Servicio() => new(new ContextoFalso(_filas), new RelojFijo(Ahora));

    private Task<EstadoLineaOutbound> LeerAsync(Guid? tenant = null) =>
        Servicio().ObtenerEstadoLineaOutboundAsync(tenant ?? Tenant, CancellationToken.None);

    private ProductoContratado Contratar(
        Guid tenant, LineaProductoContratado linea = LineaProductoContratado.OutboundGestionPropia, DateTime? desde = null)
    {
        var producto = ProductoContratado.Contratar(linea, "cus_PagadorTalveg", TramoTrabajadoresActivos.De1A10,
            ModalidadSuscripcionAlta.Anual, desde ?? Ahora.AddDays(-30), ahoraUtc: Ahora);
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(producto, tenant);
        _filas.Add(producto);
        return producto;
    }

    [Fact]
    public async Task Sin_Producto_Contratado_es_SinLinea()
    {
        (await LeerAsync()).Should().Be(EstadoLineaOutbound.SinLinea);
    }

    [Fact]
    public async Task Con_la_linea_Outbound_vigente_es_Vigente()
    {
        Contratar(Tenant);

        (await LeerAsync()).Should().Be(EstadoLineaOutbound.Vigente);
        (await Servicio().TieneLineaOutboundVigenteAsync(Tenant)).Should().BeTrue();
    }

    [Fact]
    public async Task La_linea_Outbound_que_empieza_justo_ahora_es_Vigente()
    {
        Contratar(Tenant, desde: Ahora);

        (await LeerAsync()).Should().Be(EstadoLineaOutbound.Vigente, "el inicio es inclusivo, como en EstaVigenteEn");
    }

    [Fact]
    public async Task Con_la_linea_Outbound_finalizada_es_Finalizada()
    {
        Contratar(Tenant).Finalizar(Ahora.AddDays(-1), ahoraUtc: Ahora);

        (await LeerAsync()).Should().Be(EstadoLineaOutbound.Finalizada);
        (await Servicio().TieneLineaOutboundVigenteAsync(Tenant)).Should().BeFalse();
    }

    [Fact]
    public async Task Una_finalizada_y_otra_vigente_dan_Vigente()
    {
        Contratar(Tenant, desde: Ahora.AddDays(-60)).Finalizar(Ahora.AddDays(-2), ahoraUtc: Ahora);
        Contratar(Tenant, desde: Ahora.AddDays(-2));

        (await LeerAsync()).Should().Be(EstadoLineaOutbound.Vigente);
    }

    [Fact]
    public async Task La_unica_linea_Outbound_eliminada_es_SinLinea()
    {
        Contratar(Tenant).MarcarComoEliminado(Guid.NewGuid());

        (await LeerAsync()).Should().Be(EstadoLineaOutbound.SinLinea, "las filas eliminadas no cuentan");
        (await Servicio().TieneLineaOutboundVigenteAsync(Tenant)).Should().BeFalse();
    }

    [Fact]
    public async Task Una_vigente_eliminada_junto_a_una_finalizada_es_Finalizada()
    {
        Contratar(Tenant, desde: Ahora.AddDays(-60)).Finalizar(Ahora.AddDays(-2), ahoraUtc: Ahora);
        Contratar(Tenant, desde: Ahora.AddDays(-2)).MarcarComoEliminado(Guid.NewGuid());

        (await LeerAsync()).Should().Be(EstadoLineaOutbound.Finalizada);
    }

    [Fact]
    public async Task La_linea_Inbound_vigente_no_cuenta_como_Outbound()
    {
        Contratar(Tenant, LineaProductoContratado.Inbound);

        (await LeerAsync()).Should().Be(EstadoLineaOutbound.SinLinea);
    }

    [Fact]
    public async Task La_linea_Outbound_de_otro_Tenant_no_cuenta()
    {
        Contratar(OtroTenant);

        (await LeerAsync()).Should().Be(EstadoLineaOutbound.SinLinea, "cuenta el Tenant beneficiario");
        (await LeerAsync(OtroTenant)).Should().Be(EstadoLineaOutbound.Vigente, "control: el otro Tenant sí la tiene");
    }

    private sealed class ContextoFalso(List<ProductoContratado> filas) : ILineaOutboundQueryContext
    {
        public IQueryable<ProductoContratado> ProductosContratados => new TestAsyncQueryable<ProductoContratado>(filas.AsQueryable());
    }

    private sealed class RelojFijo(DateTime ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahora);
    }
}

using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Comercial;
using CaeManager.Application.Inbound.Comercial.Queries.LineaInboundVigente;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using FluentAssertions;

namespace CaeManager.Application.Tests.Inbound.Comercial;

/// <summary>
/// <see cref="LineaInboundVigenteQuery"/>: el Tenant activo, como Tenant beneficiario, tiene la línea Inbound vigente solo
/// con un Producto Contratado de línea Inbound, Vigente, no eliminado, con el inicio ya alcanzado y suyo. Es el criterio del
/// gate DN-7 de la base (<c>app_inbound_tenant_contratista_del_contrato</c>). El contexto de lectura falso devuelve todas
/// las filas, de todos los Tenants y también las eliminadas, para que el filtro que se prueba sea el de la Query y no el
/// global de EF ni la RLS; la lectura real bajo <c>cae_app_runtime</c> la prueba la integración, en CI.
/// </summary>
public class LineaInboundVigenteQueryTests
{
    private static readonly DateTime Ahora = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid TenantActivo = Guid.NewGuid();
    private static readonly Guid OtroTenant = Guid.NewGuid();

    private readonly List<ProductoContratado> _filas = [];

    private Task<bool> LeerAsync(Guid? tenantActivo = null, bool sinTenant = false) =>
        new LineaInboundVigenteQueryHandler(
                new ContextoFalso(_filas), new TenantFijo(sinTenant ? null : tenantActivo ?? TenantActivo), new RelojFijo(Ahora))
            .Handle(new LineaInboundVigenteQuery(), CancellationToken.None);

    private ProductoContratado Contratar(
        Guid tenant, LineaProductoContratado linea = LineaProductoContratado.Inbound, DateTime? desde = null)
    {
        var inicio = desde ?? Ahora.AddDays(-30);
        var producto = ProductoContratado.Contratar(linea, "cus_PagadorTalveg", TramoTrabajadoresActivos.De1A10,
            ModalidadSuscripcionAlta.Anual, inicio, ahoraUtc: inicio > Ahora ? inicio : Ahora);
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(producto, tenant);
        _filas.Add(producto);
        return producto;
    }

    [Fact]
    public async Task Un_Tenant_sin_Producto_Contratado_no_tiene_la_linea_Inbound()
    {
        (await LeerAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Un_Tenant_con_la_linea_Inbound_vigente_la_tiene()
    {
        Contratar(TenantActivo);

        (await LeerAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task La_linea_Inbound_dada_de_baja_no_cuenta()
    {
        // Baja o corrección (DarDeBajaLineaInboundCommand, CorregirLineaInboundCommand): la fila queda Finalizado con su
        // fin en el pasado. No hay un estado «caducado» aparte: la vigencia que terminó es Finalizado.
        Contratar(TenantActivo).Finalizar(Ahora.AddDays(-1), ahoraUtc: Ahora);

        (await LeerAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task La_baja_y_una_contratacion_posterior_vigente_dan_la_linea_Inbound()
    {
        // La corrección deja la fila errónea como histórico Finalizado y contrata otra: cuenta la vigente.
        Contratar(TenantActivo, desde: Ahora.AddDays(-60)).Finalizar(Ahora.AddDays(-2), ahoraUtc: Ahora);
        Contratar(TenantActivo, desde: Ahora.AddDays(-2));

        (await LeerAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task La_linea_Inbound_vigente_de_otro_Tenant_no_cuenta_para_el_Tenant_activo()
    {
        Contratar(OtroTenant);

        (await LeerAsync()).Should().BeFalse("cuenta el Tenant beneficiario, no cualquier Tenant con la línea");
        (await LeerAsync(tenantActivo: OtroTenant)).Should().BeTrue("control: el otro Tenant sí la tiene");
    }

    [Fact]
    public async Task Sin_Tenant_activo_falla_cerrado()
    {
        Contratar(TenantActivo);

        (await LeerAsync(sinTenant: true)).Should().BeFalse();
    }

    [Fact]
    public async Task Una_linea_que_no_es_Inbound_no_cuenta()
    {
        Contratar(TenantActivo, LineaProductoContratado.OutboundGestionPropia);

        (await LeerAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task La_linea_Inbound_eliminada_no_cuenta()
    {
        Contratar(TenantActivo).MarcarComoEliminado(Guid.NewGuid());

        (await LeerAsync()).Should().BeFalse("el gate de la base exige NOT EstaEliminado");
    }

    [Fact]
    public async Task La_linea_Inbound_cuyo_inicio_no_ha_llegado_no_cuenta()
    {
        // Desde la issue #2259 la política de INSERT no admite un inicio posterior al now() de la base: esta fila solo la
        // escriben el propietario de la base o un alta anterior a esa migración.
        Contratar(TenantActivo, desde: Ahora.AddMinutes(1));

        (await LeerAsync()).Should().BeFalse("el gate de la base exige VigenteDesdeUtc <= ahora");
    }

    [Fact]
    public async Task La_linea_Inbound_que_empieza_justo_ahora_cuenta()
    {
        Contratar(TenantActivo, desde: Ahora);

        (await LeerAsync()).Should().BeTrue("el inicio es inclusivo, como en ProductoContratado.EstaVigenteEn");
    }

    private sealed class ContextoFalso(List<ProductoContratado> filas) : ILineaInboundQueryContext
    {
        public IQueryable<ProductoContratado> ProductosContratados => new TestAsyncQueryable<ProductoContratado>(filas.AsQueryable());
    }

    private sealed class TenantFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class RelojFijo(DateTime ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahora);
    }
}

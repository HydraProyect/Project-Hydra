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
/// «Inbound sin Outbound», opción A' (DECISION_LOG e496f33): <see cref="TenantSoloInboundService"/> dice «solo Inbound»
/// con las tres condiciones (línea Inbound vigente, su declaración «solo Inbound» marcada y ninguna línea Outbound), y
/// <see cref="CriterioDeOutbound"/> solo lo aplica con el papel de Tenant de origen. El contexto falso devuelve todas las filas, de todos los Tenants y también las eliminadas, para que el filtro
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

    /// <summary>
    /// Contrata una fila del Tenant. La línea Inbound lleva por defecto la declaración «solo Inbound» marcada, para que cada
    /// test cambie una sola de las tres condiciones de la opción A'; <paramref name="declara"/> = false es el Tenant
    /// histórico. <paramref name="contratadaEn"/> permite un inicio posterior al reloj del servicio (línea programada).
    /// </summary>
    private ProductoContratado Contratar(
        LineaProductoContratado linea, Guid? tenant = null, DateTime? desde = null, bool? declara = null,
        DateTime? contratadaEn = null)
    {
        var producto = ProductoContratado.Contratar(linea, "cus_PagadorTalveg", TramoTrabajadoresActivos.De1A10,
            ModalidadSuscripcionAlta.Anual, desde ?? Ahora.AddDays(-30), ahoraUtc: contratadaEn ?? Ahora,
            declaraSoloInbound: declara ?? linea == LineaProductoContratado.Inbound);
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(producto, tenant ?? Tenant);
        _filas.Add(producto);
        return producto;
    }

    [Fact]
    public async Task La_linea_Inbound_vigente_con_la_declaracion_y_sin_Outbound_es_solo_Inbound()
    {
        Contratar(LineaProductoContratado.Inbound, declara: true);

        (await EsSoloInboundAsync()).Should().BeTrue();
    }

    /// <summary>
    /// El defecto que corrige la opción A' (DECISION_LOG e496f33): el Tenant histórico que operaba Outbound sin fila de la
    /// línea Outbound y contrató Inbound sin la declaración NO es solo Inbound, y conserva Outbound con el papel de Tenant
    /// de origen.
    /// </summary>
    [Fact]
    public async Task El_Tenant_historico_con_Inbound_sin_declaracion_y_sin_fila_Outbound_conserva_Outbound()
    {
        Contratar(LineaProductoContratado.Inbound, declara: false);

        (await EsSoloInboundAsync()).Should().BeFalse("sin la declaración, la falta de fila Outbound no cierra Outbound");
        (await Criterio(Tenant).AdmiteOutboundAsync(PapelActivo.TenantDeOrigen)).Should().BeTrue();
        (await Criterio(Tenant).CierraPorSoloInboundAsync(PapelActivo.TenantDeOrigen)).Should().BeFalse();
    }

    [Fact]
    public async Task Sin_ninguna_linea_no_es_solo_Inbound_el_Outbound_historico_conserva_Outbound()
    {
        (await EsSoloInboundAsync()).Should().BeFalse("no hay backfill de la línea Outbound: exigirla quitaría Outbound");
    }

    [Fact]
    public async Task Con_la_declaracion_y_una_linea_Outbound_no_es_solo_Inbound()
    {
        Contratar(LineaProductoContratado.Inbound, declara: true);
        Contratar(LineaProductoContratado.OutboundGestionPropia);

        (await EsSoloInboundAsync()).Should().BeFalse();
        (await Criterio(Tenant).AdmiteOutboundAsync(PapelActivo.TenantDeOrigen)).Should().BeTrue();
    }

    [Fact]
    public async Task La_declaracion_de_una_fila_Inbound_finalizada_no_vale_para_la_vigente()
    {
        Contratar(LineaProductoContratado.Inbound, declara: true).Finalizar(Ahora.AddDays(-1), ahoraUtc: Ahora);
        Contratar(LineaProductoContratado.Inbound, desde: Ahora.AddDays(-1), declara: false);

        (await EsSoloInboundAsync()).Should().BeFalse("la declaración cuenta en la misma fila de la línea Inbound vigente");
    }

    [Fact]
    public async Task La_declaracion_sin_linea_Inbound_vigente_no_es_solo_Inbound()
    {
        Contratar(LineaProductoContratado.Inbound, declara: true).Finalizar(Ahora.AddDays(-1), ahoraUtc: Ahora);

        (await EsSoloInboundAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Una_linea_Inbound_declarada_cuyo_inicio_no_ha_llegado_no_es_vigente()
    {
        Contratar(LineaProductoContratado.Inbound, desde: Ahora.AddDays(1), declara: true, contratadaEn: Ahora.AddDays(2));

        (await EsSoloInboundAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Una_linea_Outbound_finalizada_sigue_siendo_una_linea_Outbound()
    {
        Contratar(LineaProductoContratado.Inbound);
        Contratar(LineaProductoContratado.OutboundGestionPropia).Finalizar(Ahora.AddDays(-1), ahoraUtc: Ahora);

        (await EsSoloInboundAsync()).Should().BeFalse("«ninguna línea Outbound» es que no hay fila; ante la duda, se conserva");
    }

    /// <summary>
    /// Una línea Outbound programada (su inicio aún no ha llegado) también es una línea Outbound: el Tenant no es solo
    /// Inbound. Mata el mutante que exigiría a la línea Outbound el inicio ya alcanzado, como a la Inbound. Como
    /// <c>ProductoContratado.Contratar</c> rechaza un inicio futuro, la fila solo llega así con un desfase de reloj
    /// entre quien la escribe y quien la lee, o escrita fuera del dominio: el test la fabrica con un <c>ahoraUtc</c> posterior.
    /// </summary>
    [Fact]
    public async Task Una_linea_Outbound_programada_sigue_siendo_una_linea_Outbound()
    {
        Contratar(LineaProductoContratado.Inbound, declara: true);
        Contratar(LineaProductoContratado.OutboundGestionPropia, desde: Ahora.AddDays(1), contratadaEn: Ahora.AddDays(2));

        (await EsSoloInboundAsync()).Should().BeFalse("la línea Outbound cuenta aunque su inicio no haya llegado");
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

    /// <summary>
    /// BAJA 4 de la revisión de Outbound: la respuesta se memoiza por Tenant pedido y Tenant activo con la misma vida que el
    /// alcance (caducidad y descarte en cada Command), en vez de leer ProductosContratados en cada comando y en cada
    /// petición de Comunicaciones.
    /// </summary>
    private (CriterioDeOutbound Criterio, RelojFijo Reloj, TenantMutable Activo) CriterioMemoizado(
        TimeSpan? caducidad, Action? alLeer = null)
    {
        var reloj = new RelojFijo(Ahora);
        var activo = new TenantMutable { TenantId = Tenant };
        var criterio = new CriterioDeOutbound(new ServiceCollection()
            .AddSingleton<ITenantActual>(activo)
            .AddSingleton<TimeProvider>(reloj)
            .AddSingleton<ITenantSoloInbound>(new TenantSoloInboundService(
                new ContextoFalso(_filas, () => { _lecturas++; alLeer?.Invoke(); }), reloj))
            .BuildServiceProvider(), caducidad);
        return (criterio, reloj, activo);
    }

    [Fact]
    public async Task La_respuesta_se_memoiza_hasta_la_caducidad_o_el_descarte()
    {
        Contratar(LineaProductoContratado.Inbound);
        var (criterio, reloj, activo) = CriterioMemoizado(TimeSpan.FromSeconds(30));

        (await criterio.CierraPorSoloInboundAsync(PapelActivo.TenantDeOrigen)).Should().BeTrue();
        var outbound = Contratar(LineaProductoContratado.OutboundGestionPropia);
        (await criterio.CierraPorSoloInboundAsync(PapelActivo.TenantDeOrigen)).Should().BeTrue("dentro de la caducidad sirve lo memoizado");
        _lecturas.Should().Be(1);

        reloj.Avanzar(TimeSpan.FromSeconds(31));
        (await criterio.CierraPorSoloInboundAsync(PapelActivo.TenantDeOrigen)).Should().BeFalse("caducada, vuelve a leer");
        _lecturas.Should().Be(2);

        _filas.Remove(outbound);
        criterio.Invalidar();
        (await criterio.CierraPorSoloInboundAsync(PapelActivo.TenantDeOrigen)).Should().BeTrue("un Command descarta lo memoizado");
        _lecturas.Should().Be(3);

        activo.TenantId = Guid.NewGuid();
        (await criterio.CierraPorSoloInboundAsync(PapelActivo.TenantDeOrigen)).Should().BeFalse(
            "con otro Tenant activo es otra clave: no hereda lo de Tenant");
        _lecturas.Should().Be(4);
    }

    [Fact]
    public async Task Una_lectura_que_empezo_antes_de_un_descarte_no_se_guarda()
    {
        Contratar(LineaProductoContratado.Inbound);
        CriterioDeOutbound? criterio = null;
        criterio = CriterioMemoizado(null, () => { if (_lecturas == 1) criterio!.Invalidar(); }).Criterio;

        await criterio.CierraPorSoloInboundAsync(PapelActivo.TenantDeOrigen);
        await criterio.CierraPorSoloInboundAsync(PapelActivo.TenantDeOrigen);

        _lecturas.Should().Be(2);
    }

    [Theory]
    [InlineData(3600, 60)]
    [InlineData(0, 1)]
    public async Task La_caducidad_se_acota_al_rango_del_alcance(int segundosPedidos, int segundosEfectivos)
    {
        Contratar(LineaProductoContratado.Inbound);
        var (criterio, reloj, _) = CriterioMemoizado(TimeSpan.FromSeconds(segundosPedidos));

        await criterio.CierraPorSoloInboundAsync(PapelActivo.TenantDeOrigen);
        reloj.Avanzar(TimeSpan.FromSeconds(segundosEfectivos) - TimeSpan.FromMilliseconds(1));
        await criterio.CierraPorSoloInboundAsync(PapelActivo.TenantDeOrigen);
        _lecturas.Should().Be(1);

        reloj.Avanzar(TimeSpan.FromMilliseconds(1));
        await criterio.CierraPorSoloInboundAsync(PapelActivo.TenantDeOrigen);
        _lecturas.Should().Be(2);
    }

    private sealed class TenantFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId { get; } = tenantId;
    }

    private sealed class TenantMutable : ITenantActual
    {
        public Guid? TenantId { get; set; }
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
        private long _marca = 1_000;

        public override DateTimeOffset GetUtcNow() => new(ahora);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _marca;
        public void Avanzar(TimeSpan tiempo) => _marca += tiempo.Ticks;
    }
}

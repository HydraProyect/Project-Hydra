using CaeManager.Application.Common;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Dashboard.TarifasOperadorCae;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Dashboard;
using CaeManager.Domain.Operaciones;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>
/// La cuota del Operador CAE por Empresa gestionada y su ingreso por gestión (decisión de Chris, 2026-10-20): la
/// cifra de extranjeros sale de ContarExtranjerosCobrablesDelMesQuery, no de la medición, y los importes no se calculan
/// con precios escritos en la pantalla.
/// </summary>
public class FacturacionOperadorCaeTests
{
    private static readonly Guid Ebro = Guid.NewGuid();
    private static readonly Guid Berriz = Guid.NewGuid();
    private static readonly ConceptoTarifaOperadorCae Ext = ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero;

    private static LineaFacturacionCuadroDto Linea(ConceptoTarifaOperadorCae c, int unidades, decimal precio, bool activo = true, string moneda = "EUR") =>
        new(c, c.ToString(), moneda, unidades, activo ? unidades * precio : 0m, precio, activo);

    private static FilaFacturacionOperadorCaeDto Fila(int extranjeros, int? gestiones, params LineaFacturacionCuadroDto[] lineas) =>
        CalculosFacturacionOperadorCae.Fila(Ebro, "Ebro", extranjeros, 2, 1, gestiones, [], lineas, []);

    [Fact]
    public void El_importe_de_extranjeros_usa_la_cifra_del_conteo_y_el_precio_de_la_tarifa()
    {
        // La medición contó 14 Visitas, pero la cifra cobrable (el conteo del Excel) es 12: manda el conteo.
        var fila = Fila(12, 100,
            Linea(Ext, 14, 40m), Linea(ConceptoTarifaOperadorCae.CentroNuevo, 2, 25m), Linea(ConceptoTarifaOperadorCae.ObraNueva, 1, 120m));

        fila.Extranjero.Unidades.Should().Be(12);
        fila.Extranjero.Importe.Should().Be(480m);
        fila.Centro.Importe.Should().Be(50m);
        fila.Obra.Importe.Should().Be(120m);
        fila.Cuota.Should().Be(650m);
        fila.SinTarifa.Should().BeFalse();
    }

    [Fact]
    public void Sumar_las_lineas_de_un_solo_Tenant_conserva_el_precio_y_el_cobro_y_con_varios_no_inventa_un_precio()
    {
        var uno = ObtenerSerieCuadroDireccionQueryHandler.SumarLineas([Linea(Ext, 3, 40m), Linea(ConceptoTarifaOperadorCae.CentroNuevo, 2, 25m, activo: false)]);
        uno.Single(l => l.Concepto == Ext).PrecioUnitario.Should().Be(40m);
        uno.Single(l => l.Concepto == ConceptoTarifaOperadorCae.CentroNuevo).CobroActivo.Should().BeFalse();

        var varios = ObtenerSerieCuadroDireccionQueryHandler.SumarLineas([Linea(Ext, 3, 40m), Linea(Ext, 1, 30m)]);
        varios.Should().ContainSingle().Which.PrecioUnitario.Should().BeNull();
    }

    [Fact]
    public void El_ingreso_por_gestion_separa_las_normales_de_las_de_extranjero_y_da_la_relacion()
    {
        var fila = Fila(10, 110, Linea(Ext, 10, 40m), Linea(ConceptoTarifaOperadorCae.CentroNuevo, 2, 25m));

        // Cuota 450; importe de extranjeros 400; 110 gestiones: 10 de extranjero y 100 normales.
        fila.Cuota.Should().Be(450m);
        fila.GestionesNormales.Should().Be(100);
        fila.Ingreso.PorGestion.Should().BeApproximately(450m / 110m, 0.0001m);
        fila.Ingreso.PorGestionNormal.Should().Be(0.5m);
        fila.Ingreso.PorGestionExtranjero.Should().Be(40m);
        fila.Ingreso.Relacion.Should().Be(80m);
    }

    [Fact]
    public void Sin_tarifa_las_unidades_se_cuentan_pero_no_hay_cuota_ni_ingreso_ni_cero()
    {
        var fila = Fila(4, 64);

        fila.SinTarifa.Should().BeTrue();
        fila.Extranjero.Unidades.Should().Be(4);
        fila.Cuota.Should().BeNull();
        fila.Ingreso.Should().Be(new IngresoPorGestionOperadorCaeDto(null, null, null, null));
    }

    [Fact]
    public void Sin_gestiones_o_sin_medicion_de_gestiones_el_ingreso_es_sin_dato()
    {
        Fila(0, 0, Linea(Ext, 0, 40m)).Ingreso.PorGestion.Should().BeNull();
        Fila(3, null, Linea(Ext, 3, 40m)).Ingreso.PorGestion.Should().BeNull("un cierre anterior no guardó las gestiones: no se inventan");
    }

    [Fact]
    public void Con_el_cobro_desactivado_el_concepto_se_cuenta_y_no_se_cobra()
    {
        var fila = Fila(5, 50, Linea(Ext, 5, 40m), Linea(ConceptoTarifaOperadorCae.CentroNuevo, 3, 25m, activo: false));

        fila.Centro.Unidades.Should().Be(3);
        fila.Centro.CobroActivo.Should().BeFalse();
        fila.Centro.Importe.Should().Be(0m);
        fila.Cuota.Should().Be(200m);
    }

    [Fact]
    public void Sin_ingreso_normal_o_sin_extranjeros_la_relacion_es_sin_dato()
    {
        Fila(10, 10, Linea(Ext, 10, 40m)).Ingreso.PorGestionNormal.Should().BeNull("todas las gestiones son de extranjero");
        Fila(10, 10, Linea(Ext, 10, 40m)).Ingreso.Relacion.Should().BeNull();
        Fila(0, 50, Linea(Ext, 0, 40m), Linea(ConceptoTarifaOperadorCae.CentroNuevo, 2, 25m)).Ingreso.PorGestionExtranjero.Should().BeNull();
    }

    [Fact]
    public void El_total_solo_suma_las_Empresas_con_tarifa_en_la_moneda_principal()
    {
        var a = Fila(10, 110, Linea(Ext, 10, 40m));
        var b = CalculosFacturacionOperadorCae.Fila(Berriz, "Berriz", 5, 0, 0, 55, [], [Linea(Ext, 5, 10m, moneda: "USD")], []);
        var c = CalculosFacturacionOperadorCae.Fila(Guid.NewGuid(), "Sin", 3, 1, 0, 30, [], [], []);

        var total = CalculosFacturacionOperadorCae.Total([a, b, c]);

        total.Moneda.Should().Be("EUR");
        total.Cuota.Should().Be(400m, "la Empresa en USD y la que no tiene tarifa no se mezclan en la cuota");
        total.EmpresasConTarifa.Should().Be(1);
        total.Extranjeros.Should().Be(18, "las unidades se cuentan de todas las Empresas");
    }

    // ------------------------------------------------ la consulta

    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly DateTime Enero = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static AsignacionOperacion Externa(Guid beneficiario, Guid operador, DateTime desde, DateTime? hasta = null) =>
        AsignacionOperacion.Externa(beneficiario, operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, hasta, desde);

    private static (ObtenerFacturacionOperadorCaeQueryHandler Handler, MediadorFalso Mediator) Armar(
        ConteoExtranjerosCobrablesDelMesDto conteo, SerieCuadroDireccionDto serie, IReadOnlyList<TarifaOperadorCaeDto>? tarifas,
        string rol = "DireccionCae", Guid? origen = null, IReadOnlyList<AsignacionOperacion>? asignaciones = null)
    {
        var mediator = new MediadorFalso(conteo, serie, tarifas);
        var handler = new ObtenerFacturacionOperadorCaeQueryHandler(
            mediator, new UsuarioFalso(rol, origen ?? Operador),
            new OperacionesFalsas(asignaciones ?? [Externa(Ebro, Operador, Enero)]), new RelojFijo());
        return (handler, mediator);
    }

    private sealed class UsuarioFalso(string rol, Guid origen) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(origen);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class OperacionesFalsas(IReadOnlyList<AsignacionOperacion> asignaciones) : IOperacionesQueryContext
    {
        public IQueryable<AsignacionOperacion> AsignacionesOperacion => new TestAsyncQueryable<AsignacionOperacion>(asignaciones.AsQueryable());
        public IQueryable<AsignacionCartera> AsignacionesCartera => new TestAsyncQueryable<AsignacionCartera>(new List<AsignacionCartera>().AsQueryable());
    }

    private sealed class MediadorFalso(
        ConteoExtranjerosCobrablesDelMesDto conteo, SerieCuadroDireccionDto serie, IReadOnlyList<TarifaOperadorCaeDto>? tarifas) : IMediator
    {
        public List<Type> Pedidas { get; } = [];
        public ContarExtranjerosCobrablesDelMesQuery? Conteo { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Pedidas.Add(request.GetType());
            if (request is ContarExtranjerosCobrablesDelMesQuery c)
                Conteo = c;
            object? respuesta = request switch
            {
                ContarExtranjerosCobrablesDelMesQuery => conteo,
                ObtenerSerieCuadroDireccionQuery => serie,
                ObtenerTarifasOperadorCaeQuery => tarifas,
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return Task.FromResult((TResponse)respuesta!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
    private sealed class RelojFijo : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    }

    private static CifraMensual Cifra(int valor) => new(valor, EstadoCifraMensual.Parcial);

    private static SerieCuadroDireccionDto Serie(IReadOnlyList<LineaFacturacionCuadroDto>? lineas, params Guid[] otrosTenants)
    {
        var cifras = new CifrasCuadroDireccionDto(
            Cifra(10), Cifra(0), Cifra(0), Cifra(3), Cifra(2), Cifra(0), Cifra(0),
            new SolicitudesDelMesDto(Cifra(0), []), new CumplimientoActualDto(0, 0, 0, 0, 0, 0, 0, 0, true, DateTime.UtcNow),
            Cifra(100), null, Cifra(1), [new CentrosNuevosPorClienteDto(Ebro, Guid.NewGuid(), "Papelera", 2)]);
        var punto = new PuntoSerieCuadroDto(2026, 10, EstadoCifraMensual.Parcial, cifras, lineas);
        return new SerieCuadroDireccionDto(
            [punto],
            [new SerieTenantCuadroDto(Ebro, "Ebro", [punto]), .. otrosTenants.Select(t => new SerieTenantCuadroDto(t, "Otro", [punto]))], 0);
    }

    [Fact]
    public async Task La_consulta_toma_los_extranjeros_del_conteo_y_los_precios_de_la_tarifa()
    {
        var conteo = new ConteoExtranjerosCobrablesDelMesDto(
            2026, 10, [new ConteoExtranjerosCobrablesTenantDto(Ebro, "Ebro", 7, 5)], 0);
        var tarifas = new[] { new TarifaOperadorCaeDto(Ebro, Ext, 40m, "EUR", true, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) };
        var (handler, _) = Armar(conteo, Serie([Linea(Ext, 9, 40m)]), tarifas);

        var dto = await handler.Handle(new ObtenerFacturacionOperadorCaeQuery(2026, 10), CancellationToken.None);

        dto.PuedeFijarTarifas.Should().BeTrue();
        var fila = dto.Filas.Should().ContainSingle().Subject;
        fila.Extranjero.Unidades.Should().Be(7, "las asignaciones del conteo, no las 9 de la medición");
        fila.Cuota.Should().Be(280m);
        fila.TarifaVigente.Should().ContainSingle().Which.PrecioUnitario.Should().Be(40m);
        dto.CentrosPorCliente.Should().ContainSingle().Which.Should().Be(new CentrosNuevosPorClienteTotalDto("Papelera", 2));
    }

    [Fact]
    public async Task Quien_no_gestiona_las_tarifas_ve_las_unidades_pero_ni_importes_ni_el_derecho_a_fijarlas()
    {
        var conteo = new ConteoExtranjerosCobrablesDelMesDto(2026, 10, [new ConteoExtranjerosCobrablesTenantDto(Ebro, "Ebro", 7, 5)], 0);
        var (handler, _) = Armar(conteo, Serie(null), null, rol: "CoordinadorCae");

        var dto = await handler.Handle(new ObtenerFacturacionOperadorCaeQuery(2026, 10), CancellationToken.None);

        dto.PuedeFijarTarifas.Should().BeFalse();
        var fila = dto.Filas.Single();
        fila.Extranjero.Unidades.Should().Be(7);
        fila.Cuota.Should().BeNull();
        fila.SinTarifa.Should().BeTrue();
        fila.TarifaVigente.Should().BeEmpty();
    }

    private static readonly ConteoExtranjerosCobrablesDelMesDto ConteoVacio = new(2026, 10, [], 0);

    [Fact]
    public async Task Solo_salen_las_Empresas_gestionadas_por_una_Asignacion_externa_y_nunca_el_Tenant_propio_del_Operador()
    {
        var otroOperador = Guid.NewGuid();
        var sinAsignacion = Guid.NewGuid();
        var delOtroOperador = Guid.NewGuid();
        var (handler, mediator) = Armar(
            ConteoVacio, Serie([], Operador, sinAsignacion, delOtroOperador), null,
            asignaciones:
            [
                Externa(Ebro, Operador, Enero),
                // El Operador sobre sí mismo (Asignación interna o propietario == operador) no es una Empresa gestionada.
                AsignacionOperacion.Interna(Operador, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()), Enero, null, Enero),
                Externa(delOtroOperador, otroOperador, Enero),
            ]);

        var dto = await handler.Handle(new ObtenerFacturacionOperadorCaeQuery(2026, 10), CancellationToken.None);

        dto.Filas.Select(f => f.TenantId).Should().Equal(Ebro);
        mediator.Conteo!.TramosPorTenant!.Keys.Should().BeEquivalentTo([Ebro], "el conteo de extranjeros se acota a las mismas Empresas");
    }

    [Fact]
    public async Task Quien_no_es_de_un_Operador_CAE_no_ve_ninguna_Empresa()
    {
        var (handler, _) = Armar(ConteoVacio, Serie([], Operador), null, origen: Guid.NewGuid());

        var dto = await handler.Handle(new ObtenerFacturacionOperadorCaeQuery(2026, 10), CancellationToken.None);

        dto.Filas.Should().BeEmpty();
        dto.Total.Extranjeros.Should().Be(0);
    }

    [Fact]
    public async Task Una_Asignacion_cerrada_antes_del_mes_o_que_empieza_despues_no_da_fila()
    {
        var cerrada = Guid.NewGuid();
        var futura = Guid.NewGuid();
        var (handler, _) = Armar(
            ConteoVacio, Serie([], cerrada, futura), null,
            asignaciones:
            [
                Externa(cerrada, Operador, Enero, hasta: new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc)),
                Externa(futura, Operador, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)),
            ]);

        var dto = await handler.Handle(new ObtenerFacturacionOperadorCaeQuery(2026, 10), CancellationToken.None);

        dto.Filas.Should().BeEmpty("sin vigencia dentro de octubre el Operador no gestiona ni cobra a esa Empresa");
    }

    [Fact]
    public async Task Los_tramos_de_una_Asignacion_que_empieza_a_mitad_de_mes_se_pasan_al_conteo_de_extranjeros()
    {
        var desde = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        var (handler, mediator) = Armar(ConteoVacio, Serie([]), null, asignaciones: [Externa(Ebro, Operador, desde)]);

        await handler.Handle(new ObtenerFacturacionOperadorCaeQuery(2026, 10), CancellationToken.None);

        mediator.Conteo!.TramosPorTenant![Ebro].Should().Equal(new TramoDeVigencia(desde, new MesCuadroDireccion(2026, 10).FinUtc));
    }

    // ------------------------------------------------ el conteo acotado a la vigencia

    [Fact]
    public void Una_Visita_cuenta_si_su_dia_de_inicio_cae_en_un_tramo_con_el_dia_de_alta_dentro_y_el_de_cierre_fuera()
    {
        var tramo = new TramoDeVigencia(new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));

        ContarExtranjerosCobrablesDelMesQueryHandler.EnAlgunTramo(new DateOnly(2026, 10, 9), [tramo]).Should().BeFalse();
        ContarExtranjerosCobrablesDelMesQueryHandler.EnAlgunTramo(new DateOnly(2026, 10, 10), [tramo]).Should().BeTrue("el día en que empieza cuenta");
        ContarExtranjerosCobrablesDelMesQueryHandler.EnAlgunTramo(new DateOnly(2026, 10, 19), [tramo]).Should().BeTrue();
        ContarExtranjerosCobrablesDelMesQueryHandler.EnAlgunTramo(new DateOnly(2026, 10, 20), [tramo]).Should().BeFalse("el día en que se cierra no cuenta");
    }

    [Fact]
    public async Task El_conteo_con_tramos_descarta_las_asignaciones_fuera_de_vigencia_y_los_Tenants_sin_ella()
    {
        var sinVigencia = Guid.NewGuid();
        var excel = new ExtranjerosDelMesDto(2026, 10,
        [
            FilaExcel(Ebro, new DateOnly(2026, 10, 5)),
            FilaExcel(Ebro, new DateOnly(2026, 10, 12)),
            FilaExcel(Ebro, new DateOnly(2026, 10, 14)),
            FilaExcel(sinVigencia, new DateOnly(2026, 10, 12)),
        ], 0);
        var handler = new ContarExtranjerosCobrablesDelMesQueryHandler(new MediadorDeExcel(excel));
        var tramos = new Dictionary<Guid, IReadOnlyList<TramoDeVigencia>>
        {
            [Ebro] = [new TramoDeVigencia(new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc))],
        };

        var acotado = await handler.Handle(new ContarExtranjerosCobrablesDelMesQuery(2026, 10, tramos), CancellationToken.None);
        var completo = await handler.Handle(new ContarExtranjerosCobrablesDelMesQuery(2026, 10), CancellationToken.None);

        acotado.PorTenant.Should().ContainSingle().Which.Asignaciones.Should().Be(2, "la del día 5 es anterior a la Asignación");
        acotado.PorTenant.Single().TrabajadoresDistintos.Should().Be(2);
        completo.PorTenant.Sum(t => t.Asignaciones).Should().Be(4, "sin tramos es el conteo del Excel entero");
    }

    private static FilaExtranjeroDelMesDto FilaExcel(Guid tenant, DateOnly ingreso) =>
        new(tenant, "T", ingreso, null, null, null, null, "Centro", ingreso, ingreso.AddDays(1), "Nombre", null, null, Guid.NewGuid());

    private sealed class MediadorDeExcel(ExtranjerosDelMesDto excel) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is ObtenerExtranjerosDelMesQuery
                ? Task.FromResult((TResponse)(object)excel)
                : throw new NotSupportedException(request.GetType().Name);

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    [Fact]
    public async Task Un_mes_futuro_sale_vacio_con_su_motivo_y_sin_pedir_la_serie()
    {
        var conteo = new ConteoExtranjerosCobrablesDelMesDto(2027, 1, [], 0, CuadroDireccionSinCifras.MesFuturo);
        var (handler, mediator) = Armar(conteo, Serie(null), null);

        var dto = await handler.Handle(new ObtenerFacturacionOperadorCaeQuery(2027, 1), CancellationToken.None);

        dto.SinCifras.Should().Be(CuadroDireccionSinCifras.MesFuturo);
        dto.Filas.Should().BeEmpty();
        mediator.Pedidas.Should().NotContain(typeof(ObtenerSerieCuadroDireccionQuery));
    }
}

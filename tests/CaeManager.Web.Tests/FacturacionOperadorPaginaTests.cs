using System.Globalization;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Dashboard.TarifasOperadorCae;
using CaeManager.Domain.Common;
using CaeManager.Domain.Dashboard;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using FacturacionPagina = CaeManager.Web.Features.Facturacion.Pages.Facturacion;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Facturación» (cuota del Operador CAE a cada Empresa gestionada) contra su maqueta. Lo que SÍ observa: qué consultas y
/// comandos llegan al mediador y con qué parámetros (el doble responde según ellos) y qué se pinta con lo que vuelve.
/// Lo que NO observa: el cálculo de importes (<c>ValoracionOperadorCae</c>, ya hecho en las líneas que devuelve el doble),
/// la autorización de la ruta y de los comandos (la tienen sus propios tests) y el aspecto (CSS).
/// </summary>
public class FacturacionOperadorPaginaTests : BunitContext
{
    private static readonly Guid Ebro = Guid.Parse("e0000000-0000-0000-0000-000000000001");
    private static readonly Guid Rubén = Guid.Parse("e0000000-0000-0000-0000-000000000002");
    private static readonly DateTime Ahora = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Desde = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public FacturacionOperadorPaginaTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class RelojFijo(DateTime ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahora, TimeSpan.Zero);
    }

    private sealed class MediadorControlado(Func<object, object?> responder) : IMediator
    {
        public List<object> Enviados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return Task.FromResult((TResponse)responder(request)!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Enviados.Add(request!);
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return Task.FromResult<object?>(null);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private static CifraMensual C(int valor) => new(valor, EstadoCifraMensual.Parcial);

    private static CifrasCuadroDireccionDto Cifras(int centros, int obras, int gestiones) => new(
        C(0), C(0), C(0), C(0), C(centros), C(0), C(0), new SolicitudesDelMesDto(C(0), []),
        new CumplimientoActualDto(0, 0, 0, 0, 0, 0, 0, 0, true, Ahora), Gestiones: C(gestiones), ObrasNuevas: C(obras));

    private static LineaFacturacionCuadroDto Linea(ConceptoTarifaOperadorCae c, int unidades, decimal importe) =>
        new(c, ValoracionOperadorCae.NombreDe(c), "EUR", unidades, importe);

    private static TarifaOperadorCaeDto Tarifa(ConceptoTarifaOperadorCae c, decimal precio, bool activa = true) =>
        new(Ebro, c, precio, "EUR", activa, Desde);

    private sealed class Escenario
    {
        public IReadOnlyList<TarifaOperadorCaeDto>? Tarifas { get; set; } =
        [
            Tarifa(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 40m),
            Tarifa(ConceptoTarifaOperadorCae.CentroNuevo, 25m),
            Tarifa(ConceptoTarifaOperadorCae.ObraNueva, 120m, activa: false),
        ];

        public Exception? ErrorEnSerie { get; set; }
        public Func<FijarTarifaOperadorCaeCommand, Result> AlFijar { get; set; } = _ => Result.Exito();

        /// <summary>Las líneas con las que la serie valoró a Ebro: 14 × 40 = 560, 2 × 25 = 50 y una Obra sin cobrar.</summary>
        public IReadOnlyList<LineaFacturacionCuadroDto> LineasDeEbro { get; set; } =
        [
            Linea(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 14, 560m),
            Linea(ConceptoTarifaOperadorCae.CentroNuevo, 2, 50m),
            Linea(ConceptoTarifaOperadorCae.ObraNueva, 1, 0m),
        ];

        /// <summary>Las filas del Excel «Exportar extranjeros del mes» de Ebro.</summary>
        public int AsignacionesEbro { get; set; } = 14;

        /// <summary>Las Empresas que el Operador CAE opera en el mes (Asignación de Operación externa en vigor).</summary>
        public IReadOnlyList<Guid> Operadas { get; set; } = [Ebro, Rubén];

        public object? Responder(object peticion)
        {
            switch (peticion)
            {
                case ObtenerEmpresasGestionadasDelMesQuery:
                    return new EmpresasGestionadasDelMesDto(true, Operadas.ToDictionary(id => id, id => (IReadOnlyList<TramoDeVigencia>)[new TramoDeVigencia(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc))]));
                case ObtenerSerieCuadroDireccionQuery q:
                    if (ErrorEnSerie is not null)
                        throw ErrorEnSerie;
                    // Ebro tiene tarifa; Rubén no. Sin tarifas (Coordinador CAE) la serie no lleva facturación.
                    var conTarifas = Tarifas is not null;
                    return new SerieCuadroDireccionDto([],
                    [
                        new SerieTenantCuadroDto(Ebro, "Obras y Estructuras del Ebro S.L.",
                            [new PuntoSerieCuadroDto(q.Anyo, q.Mes, EstadoCifraMensual.Parcial, Cifras(2, 1, 212), conTarifas ? LineasDeEbro : null)]),
                        new SerieTenantCuadroDto(Rubén, "Rubén Salas Quintana",
                            [new PuntoSerieCuadroDto(q.Anyo, q.Mes, EstadoCifraMensual.Parcial, Cifras(1, 0, 18), conTarifas ? [] : null)]),
                    ], 1);
                case ContarExtranjerosCobrablesDelMesQuery q:
                    return new ConteoExtranjerosCobrablesDelMesDto(q.Anyo, q.Mes, [new ConteoExtranjerosCobrablesTenantDto(Ebro, "Ebro", AsignacionesEbro, 12)], 0);
                case ObtenerTarifasOperadorCaeQuery:
                    return Tarifas;
                case FijarTarifaOperadorCaeCommand c:
                    var resultado = AlFijar(c);
                    // Lo que se guarda queda guardado: la siguiente lectura de las tarifas ya lo trae.
                    if (!resultado.EsFallido)
                        Tarifas = [.. Tarifas ?? [], new TarifaOperadorCaeDto(c.PropietarioTenantId, c.Concepto, c.PrecioUnitario, c.MonedaIso, c.Activa, Ahora)];
                    return resultado;
                default:
                    throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.");
            }
        }
    }

    private (IRenderedComponent<FacturacionPagina> Cut, MediadorControlado Mediador) Renderizar(Escenario escenario)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");

        var mediador = new MediadorControlado(escenario.Responder);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddSingleton<TimeProvider>(new RelojFijo(Ahora));
        Services.AddLocalization();
        Services.AddScoped<ToastService>();

        return (Render<FacturacionPagina>(), mediador);
    }

    private static string Texto(IElement e) => e.TextContent.Trim();

    private static IReadOnlyList<string> Filas(IRenderedComponent<FacturacionPagina> cut) =>
        cut.FindAll("[role=option]").Select(Texto).ToList();

    [Fact]
    public void Pide_la_serie_con_facturacion_los_extranjeros_y_las_tarifas_del_mes_en_curso()
    {
        var (_, mediador) = Renderizar(new Escenario());

        mediador.Enviados.OfType<ObtenerSerieCuadroDireccionQuery>().Should().ContainSingle()
            .Which.Should().Be(new ObtenerSerieCuadroDireccionQuery(2026, 10, 1, ConFacturacion: true));
        mediador.Enviados.OfType<ContarExtranjerosCobrablesDelMesQuery>().Should().ContainSingle()
            .Which.Should().Match<ContarExtranjerosCobrablesDelMesQuery>(q => q.Anyo == 2026 && q.Mes == 10
                && q.TramosPorTenant != null && q.TramosPorTenant.Count == 2 && q.TramosPorTenant.ContainsKey(Ebro) && q.TramosPorTenant.ContainsKey(Rubén));
        mediador.Enviados.OfType<ObtenerTarifasOperadorCaeQuery>().Should().ContainSingle();
        mediador.Enviados.OfType<ObtenerEmpresasGestionadasDelMesQuery>().Should().ContainSingle()
            .Which.Should().Be(new ObtenerEmpresasGestionadasDelMesQuery(2026, 10));
    }

    [Fact]
    public void Solo_salen_las_Empresas_que_el_Operador_opera_en_el_mes_aunque_el_Cuadro_alcance_mas()
    {
        var (cut, _) = Renderizar(new Escenario { Operadas = [Ebro] });

        var filas = Filas(cut);
        filas.Should().ContainSingle().Which.Should().Contain("Obras y Estructuras del Ebro S.L.");
        cut.Markup.Should().NotContain("Rubén Salas Quintana");
    }

    [Fact]
    public void El_total_dice_que_es_parcial_cuando_faltan_Empresas_que_no_se_alcanzan_por_completo()
    {
        var (cut, _) = Renderizar(new Escenario());

        cut.Find(".fct-total-parcial").TextContent.Should().Contain("Total parcial").And.Contain("1 empresa que no alcanzas por completo");
    }

    [Fact]
    public void Con_la_cuota_a_cero_porque_ningun_concepto_se_cobra_la_pagina_enseña_cero_euros()
    {
        var escenario = new Escenario
        {
            Tarifas =
            [
                Tarifa(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 40m, activa: false),
                Tarifa(ConceptoTarifaOperadorCae.CentroNuevo, 25m, activa: false),
                Tarifa(ConceptoTarifaOperadorCae.ObraNueva, 120m, activa: false),
            ],
            LineasDeEbro =
            [
                Linea(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 14, 0m),
                Linea(ConceptoTarifaOperadorCae.CentroNuevo, 2, 0m),
                Linea(ConceptoTarifaOperadorCae.ObraNueva, 1, 0m),
            ],
        };

        var (cut, _) = Renderizar(escenario);

        Filas(cut)[0].Should().Contain("0 €");
        cut.Find(".fct-pie-cuota.fct-cuota-total").TextContent.Should().Contain("0 €");
    }

    [Fact]
    public void Con_linea_las_unidades_son_las_de_la_linea_y_el_Excel_de_extranjeros_se_enseña_aparte()
    {
        var escenario = new Escenario { AsignacionesEbro = 16 };

        var (cut, _) = Renderizar(escenario);

        var detalle = Texto(cut.Find(".fct-conceptos"));
        detalle.Should().Contain("Extranjeros gestionados").And.Contain("14").And.Contain("40,00 € / extranjero").And.Contain("560 €");
        Texto(cut.Find(".fct-complemento")).Should().Contain("Excel: 16 filas");
    }

    [Fact]
    public void Pinta_cada_Empresa_gestionada_con_sus_unidades_y_su_cuota_y_el_total_del_mes()
    {
        var (cut, _) = Renderizar(new Escenario());

        var filas = Filas(cut);
        filas.Should().HaveCount(2);
        filas[0].Should().Contain("Obras y Estructuras del Ebro S.L.").And.Contain("14 extranjeros · 2 Centros nuevos · 1 Obra nueva").And.Contain("610 €");
        filas[1].Should().Contain("Rubén Salas Quintana").And.Contain("Sin tarifa");
        cut.Find(".fct-total").TextContent.Should().Contain("610 €");
    }

    [Fact]
    public void El_detalle_enseña_el_concepto_desactivado_con_sus_unidades_y_el_Cliente_nuevo_no_aparece()
    {
        var (cut, _) = Renderizar(new Escenario());

        var detalle = Texto(cut.Find(".fct-conceptos"));
        detalle.Should().Contain("Extranjeros gestionados").And.Contain("40,00 € / extranjero").And.Contain("560 €");
        detalle.Should().Contain("Centros nuevos").And.Contain("25,00 € / Centro");
        detalle.Should().Contain("Obras nuevas").And.Contain("No se cobra");
        detalle.Should().NotContain("Cliente empresarial nuevo");
        Texto(cut.Find(".fct-metricas")).Should().Contain("198 normales + 14 de extranjero");
    }

    [Fact]
    public void El_detalle_enseña_la_cuenta_de_Costo_por_gestion_y_de_Costo_por_extranjero_como_la_maqueta()
    {
        var (cut, _) = Renderizar(new Escenario());

        // (610 − 560) ÷ 198 gestiones normales y 560 ÷ 14 de extranjero: las mismas cifras que el modelo ya divide, sin recalcular.
        Texto(cut.Find("[data-metrica=costo-gestion]")).Should().Be("0,25 € (50 € ÷ 198 gestiones normales)");
        Texto(cut.Find("[data-metrica=costo-extranjero]")).Should().Be("40,00 € (560 € ÷ 14 de extranjero)");
    }

    [Fact]
    public void Sin_importes_a_la_vista_los_costos_no_enseñan_ninguna_cuenta()
    {
        var (cut, _) = Renderizar(new Escenario { Tarifas = null });

        Texto(cut.Find("[data-metrica=costo-gestion]")).Should().Be("Sin dato");
        Texto(cut.Find("[data-metrica=costo-extranjero]")).Should().Be("Sin dato");
    }

    [Fact]
    public async Task Una_Empresa_sin_tarifa_enseña_Sin_dato_en_los_costos_y_sin_cuenta()
    {
        var (cut, _) = Renderizar(new Escenario());

        await cut.FindAll("[role=option]")[1].ClickAsync(new MouseEventArgs());

        Texto(cut.Find("[data-metrica=costo-gestion]")).Should().Be("Sin dato");
        Texto(cut.Find("[data-metrica=costo-extranjero]")).Should().Be("Sin dato");
    }

    [Fact]
    public async Task Elegir_una_Empresa_sin_tarifa_dice_que_no_hay_importe_y_ofrece_fijarla()
    {
        var (cut, _) = Renderizar(new Escenario());

        await cut.FindAll("[role=option]")[1].ClickAsync(new MouseEventArgs());

        cut.Find(".fct-aviso").TextContent.Should().Contain("Sin tarifa: las unidades se cuentan, pero no hay importe.");
        cut.FindAll(".fct-aviso button").Should().ContainSingle(b => Texto(b) == "Fijar tarifa");
    }

    [Fact]
    public async Task Elegir_otro_mes_vuelve_a_pedir_las_cifras_de_ese_mes_y_el_futuro_no_se_puede_elegir()
    {
        var (cut, mediador) = Renderizar(new Escenario());

        var chips = cut.FindAll(".fct-mes");
        chips.Should().HaveCount(5);
        chips[4].HasAttribute("disabled").Should().BeTrue("noviembre todavía no tiene datos");

        await chips[2].ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<ObtenerSerieCuadroDireccionQuery>().Last().Should().Be(new ObtenerSerieCuadroDireccionQuery(2026, 9, 1, ConFacturacion: true));
        mediador.Enviados.OfType<ContarExtranjerosCobrablesDelMesQuery>().Last().Should().Match<ContarExtranjerosCobrablesDelMesQuery>(q => q.Anyo == 2026 && q.Mes == 9);
    }

    [Fact]
    public void La_pestana_por_Cliente_empresarial_sale_desactivada_como_proximamente()
    {
        var (cut, _) = Renderizar(new Escenario());

        var pestana = cut.FindAll("[role=tab]").Single(t => Texto(t).Contains("Por Cliente empresarial"));
        pestana.HasAttribute("disabled").Should().BeTrue();
        Texto(pestana).Should().Contain("Próximamente");
    }

    [Fact]
    public void Sin_acceso_a_las_tarifas_no_hay_importes_ni_boton_de_tarifas()
    {
        var (cut, _) = Renderizar(new Escenario { Tarifas = null });

        Filas(cut)[0].Should().Contain("14 extranjeros").And.NotContain("€");
        cut.FindAll(".fct-total").Should().BeEmpty();
        cut.FindAll(".fct-detalle-cabecera button").Should().BeEmpty();
        cut.Find(".fct-aviso").TextContent.Should().Contain("Los importes y las tarifas los ven Administrador del Tenant propietario y Dirección CAE.");
        cut.Markup.Should().NotContain("Coordinación CAE").And.NotContain("Administración y");
    }

    [Fact]
    public void Sin_acceso_a_las_tarifas_el_Coordinador_CAE_ve_las_unidades_y_el_conteo_del_Excel_y_el_Excel_de_la_cuota_no_enseña_importes()
    {
        var (cut, _) = Renderizar(new Escenario { Tarifas = null, AsignacionesEbro = 16 });

        // Sin línea de facturación las unidades de extranjeros son las del conteo del Excel de extranjeros.
        Filas(cut)[0].Should().Contain("16 extranjeros · 2 Centros nuevos · 1 Obra nueva");
        cut.FindAll(".fct-complemento").Should().BeEmpty();
    }

    [Fact]
    public void Un_rol_que_el_Cuadro_no_admite_ve_un_aviso_honesto_y_no_un_error()
    {
        var (cut, _) = Renderizar(new Escenario { ErrorEnSerie = new UnauthorizedAccessException() });

        cut.Markup.Should().Contain("Facturación no está disponible para tu rol");
        cut.Markup.Should().Contain("La ven Dirección CAE, Coordinación CAE y Administración del Operador CAE.");
        cut.FindAll("[role=option]").Should().BeEmpty();
    }

    [Fact]
    public async Task Fijar_la_tarifa_de_una_Empresa_sin_ella_guarda_los_tres_conceptos_con_la_misma_moneda()
    {
        var escenario = new Escenario();
        var (cut, mediador) = Renderizar(escenario);
        await cut.FindAll("[role=option]")[1].ClickAsync(new MouseEventArgs());

        await cut.Find(".fct-aviso button").ClickAsync(new MouseEventArgs());
        cut.FindAll(".fct-precio input").Should().HaveCount(3);
        // Cada cambio repinta el drawer y los manejadores en línea cambian de identidad: se busca de nuevo antes de cada gesto.
        await cut.FindAll(".fct-precio input")[0].ChangeAsync(new ChangeEventArgs { Value = "36" });
        await cut.FindAll("input[role=switch]")[1].ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".fct-precio input")[1].ChangeAsync(new ChangeEventArgs { Value = "25" });
        await cut.FindAll("button").Single(b => Texto(b) == "Guardar tarifas").ClickAsync(new MouseEventArgs());

        var enviados = mediador.Enviados.OfType<FijarTarifaOperadorCaeCommand>().ToList();
        enviados.Should().Equal(
            new FijarTarifaOperadorCaeCommand(Rubén, ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 36m, "EUR", true),
            new FijarTarifaOperadorCaeCommand(Rubén, ConceptoTarifaOperadorCae.CentroNuevo, 25m, "EUR", true),
            new FijarTarifaOperadorCaeCommand(Rubén, ConceptoTarifaOperadorCae.ObraNueva, 0m, "EUR", false));
    }

    [Fact]
    public async Task Un_fallo_del_comando_se_muestra_en_el_drawer_y_no_lo_cierra()
    {
        var escenario = new Escenario
        {
            AlFijar = _ => Result.Fallo(Error.Crear("TarifaOperadorCae.EmpresaNoGestionada", "Tu organización no gestiona hoy a esta empresa, así que no puedes fijarle una tarifa.")),
        };
        var (cut, _) = Renderizar(escenario);
        await cut.FindAll("[role=option]")[1].ClickAsync(new MouseEventArgs());
        await cut.Find(".fct-aviso button").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".fct-precio input")[0].ChangeAsync(new ChangeEventArgs { Value = "36" });

        await cut.FindAll("button").Single(b => Texto(b) == "Guardar tarifas").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("no gestiona hoy a esta empresa");
        cut.FindAll("button").Should().Contain(b => Texto(b) == "Guardar tarifas");
    }

    // ------------------------------------------------------------------ drawer: editar, moneda, validación y fallo a medias

    private static async Task AbrirTarifasDeEbroAsync(IRenderedComponent<FacturacionPagina> cut) =>
        await cut.Find(".fct-detalle-cabecera button").ClickAsync(new MouseEventArgs());

    private static async Task GuardarAsync(IRenderedComponent<FacturacionPagina> cut) =>
        await cut.FindAll("button").Single(b => Texto(b) == "Guardar tarifas").ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task Editar_una_tarifa_existente_solo_envia_el_concepto_que_cambio_y_el_toast_dice_cual()
    {
        var (cut, mediador) = Renderizar(new Escenario());
        await AbrirTarifasDeEbroAsync(cut);

        cut.FindAll(".fct-precio input")[0].GetAttribute("value").Should().Be("40");
        await cut.FindAll(".fct-precio input")[0].ChangeAsync(new ChangeEventArgs { Value = "42.5" });
        await GuardarAsync(cut);

        mediador.Enviados.OfType<FijarTarifaOperadorCaeCommand>().Should().Equal(
            new FijarTarifaOperadorCaeCommand(Ebro, ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 42.5m, "EUR", true));
        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Subject.Mensaje;
        aviso.Should().Contain("trabajador extranjero gestionado").And.Contain("meses sin cerrar").And.Contain("cerrados conservan");
        aviso.Should().NotContain("desde octubre", "la versión vale desde ahora y recalcula los meses sin cerrar, no solo el actual");
    }

    [Fact]
    public async Task Cambiar_la_moneda_avisa_de_que_los_importes_no_se_convierten_y_guarda_los_tres_conceptos_en_la_nueva()
    {
        var (cut, mediador) = Renderizar(new Escenario());
        await AbrirTarifasDeEbroAsync(cut);
        cut.FindAll(".fct-aviso-moneda").Should().BeEmpty("mientras no cambie la moneda no hay nada que avisar");

        await cut.Find("select").ChangeAsync(new ChangeEventArgs { Value = "USD" });

        cut.Find(".fct-aviso-moneda").TextContent.Should().Contain("EUR").And.Contain("USD").And.Contain("no se convierten");
        await GuardarAsync(cut);

        mediador.Enviados.OfType<FijarTarifaOperadorCaeCommand>().Should().Equal(
            new FijarTarifaOperadorCaeCommand(Ebro, ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 40m, "USD", true),
            new FijarTarifaOperadorCaeCommand(Ebro, ConceptoTarifaOperadorCae.CentroNuevo, 25m, "USD", true),
            new FijarTarifaOperadorCaeCommand(Ebro, ConceptoTarifaOperadorCae.ObraNueva, 120m, "USD", false));
    }

    [Theory]
    [InlineData("", "Indica el precio.")]
    [InlineData("   ", "Indica el precio.")]
    [InlineData("abc", "Precio no válido")]
    [InlineData("36.255", "Precio no válido")]
    [InlineData("-1", "Precio no válido")]
    [InlineData("1000000", "Precio no válido")]
    public async Task Un_precio_en_blanco_o_no_valido_no_envia_nada_y_nunca_se_guarda_como_cero(string escrito, string mensaje)
    {
        var (cut, mediador) = Renderizar(new Escenario());
        await cut.FindAll("[role=option]")[1].ClickAsync(new MouseEventArgs());
        await cut.Find(".fct-aviso button").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".fct-precio input")[0].ChangeAsync(new ChangeEventArgs { Value = "36" });
        await cut.FindAll("input[role=switch]")[1].ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".fct-precio input")[1].ChangeAsync(new ChangeEventArgs { Value = escrito });

        await GuardarAsync(cut);

        mediador.Enviados.OfType<FijarTarifaOperadorCaeCommand>().Should().BeEmpty("se valida todo antes de enviar nada");
        cut.Find(".fct-error-precio").TextContent.Should().Contain(mensaje);
        cut.Markup.Should().Contain("no se ha guardado ninguna tarifa");
        cut.FindAll("button").Should().Contain(b => Texto(b) == "Guardar tarifas", "el drawer sigue abierto");
    }

    [Fact]
    public async Task Un_precio_con_coma_decimal_vale_como_decimal_y_no_como_miles()
    {
        var (cut, mediador) = Renderizar(new Escenario());
        await AbrirTarifasDeEbroAsync(cut);

        await cut.FindAll(".fct-precio input")[0].ChangeAsync(new ChangeEventArgs { Value = "36,5" });
        await GuardarAsync(cut);

        mediador.Enviados.OfType<FijarTarifaOperadorCaeCommand>().Should().ContainSingle()
            .Which.PrecioUnitario.Should().Be(36.5m);
    }

    [Fact]
    public async Task Si_un_comando_falla_a_mitad_se_recargan_las_tarifas_se_dice_cuales_se_guardaron_y_reintentar_solo_envia_lo_que_falta()
    {
        var llamadas = 0;
        var escenario = new Escenario
        {
            AlFijar = _ => ++llamadas == 2
                ? Result.Fallo(Error.Crear("TarifaOperadorCae.EmpresaNoGestionada", "Tu organización no gestiona hoy a esta empresa."))
                : Result.Exito(),
        };
        var (cut, mediador) = Renderizar(escenario);
        await cut.FindAll("[role=option]")[1].ClickAsync(new MouseEventArgs());
        await cut.Find(".fct-aviso button").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".fct-precio input")[0].ChangeAsync(new ChangeEventArgs { Value = "36" });
        await cut.FindAll("input[role=switch]")[1].ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".fct-precio input")[1].ChangeAsync(new ChangeEventArgs { Value = "25" });

        await GuardarAsync(cut);

        mediador.Enviados.OfType<FijarTarifaOperadorCaeCommand>().Select(c => c.Concepto).Should().Equal(
            ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, ConceptoTarifaOperadorCae.CentroNuevo);
        mediador.Enviados.OfType<ObtenerTarifasOperadorCaeQuery>().Should().HaveCount(2, "tras el fallo se recargan las tarifas");
        cut.Markup.Should().Contain("Se guardaron trabajador extranjero gestionado")
            .And.Contain("pero no centros nuevos, obras nuevas")
            .And.Contain("no gestiona hoy a esta empresa");
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty("no hay toast de éxito si faltó algo");

        // Reintentar: la primera ya está guardada y no se vuelve a enviar.
        await GuardarAsync(cut);

        mediador.Enviados.OfType<FijarTarifaOperadorCaeCommand>().Select(c => c.Concepto).Skip(2).Should().Equal(
            ConceptoTarifaOperadorCae.CentroNuevo, ConceptoTarifaOperadorCae.ObraNueva);
    }
}

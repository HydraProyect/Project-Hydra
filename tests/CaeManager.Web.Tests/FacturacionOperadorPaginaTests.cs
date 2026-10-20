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

        public object? Responder(object peticion)
        {
            switch (peticion)
            {
                case ObtenerSerieCuadroDireccionQuery q:
                    if (ErrorEnSerie is not null)
                        throw ErrorEnSerie;
                    // Ebro tiene tarifa; Rubén no. Sin tarifas (Coordinador CAE) la serie no lleva facturación.
                    var conTarifas = Tarifas is not null;
                    return new SerieCuadroDireccionDto([],
                    [
                        new SerieTenantCuadroDto(Ebro, "Obras y Estructuras del Ebro S.L.",
                            [new PuntoSerieCuadroDto(q.Anyo, q.Mes, EstadoCifraMensual.Parcial, Cifras(2, 1, 212),
                                conTarifas ? [Linea(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, 14, 560m), Linea(ConceptoTarifaOperadorCae.CentroNuevo, 2, 50m), Linea(ConceptoTarifaOperadorCae.ObraNueva, 1, 0m)] : null)]),
                        new SerieTenantCuadroDto(Rubén, "Rubén Salas Quintana",
                            [new PuntoSerieCuadroDto(q.Anyo, q.Mes, EstadoCifraMensual.Parcial, Cifras(1, 0, 18), conTarifas ? [] : null)]),
                    ], 1);
                case ContarExtranjerosCobrablesDelMesQuery q:
                    return new ConteoExtranjerosCobrablesDelMesDto(q.Anyo, q.Mes, [new ConteoExtranjerosCobrablesTenantDto(Ebro, "Ebro", 14, 12)], 0);
                case ObtenerTarifasOperadorCaeQuery:
                    return Tarifas;
                case FijarTarifaOperadorCaeCommand c:
                    return AlFijar(c);
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
            .Which.Should().Be(new ContarExtranjerosCobrablesDelMesQuery(2026, 10));
        mediador.Enviados.OfType<ObtenerTarifasOperadorCaeQuery>().Should().ContainSingle();
    }

    [Fact]
    public void Pinta_cada_Empresa_gestionada_con_sus_unidades_y_su_cuota_y_el_total_del_mes()
    {
        var (cut, _) = Renderizar(new Escenario());

        var filas = Filas(cut);
        filas.Should().HaveCount(2);
        filas[0].Should().Contain("Obras y Estructuras del Ebro S.L.").And.Contain("14 extranjeros · 2 Centros nuevos · 1 Obras nuevas").And.Contain("610 €");
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
        mediador.Enviados.OfType<ContarExtranjerosCobrablesDelMesQuery>().Last().Should().Be(new ContarExtranjerosCobrablesDelMesQuery(2026, 9));
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
        cut.Find(".fct-aviso").TextContent.Should().Contain("Los importes y las tarifas los ven Administración y Dirección CAE.");
    }

    [Fact]
    public void Un_rol_que_el_Cuadro_no_admite_ve_un_aviso_y_no_un_error()
    {
        var (cut, _) = Renderizar(new Escenario { ErrorEnSerie = new UnauthorizedAccessException() });

        cut.Markup.Should().Contain("Tu rol aún no puede leer las cifras del mes");
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
}

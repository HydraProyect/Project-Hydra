using System.Globalization;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Facturacion.Commands.ActualizarTarifaCliente;
using CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion;
using CaeManager.Application.Facturacion.Queries.ObtenerTarifasCliente;
using CaeManager.Domain.Common;
using CaeManager.Domain.Facturacion;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using FacturacionPagina = CaeManager.Web.Features.Facturacion.Pages.Facturacion;

namespace CaeManager.Web.Tests;

/// <summary>
/// Facturación contra su mockup Gen 2 («Facturacion TALVEG.dc.html»). La
/// confirmación al eliminar una tarifa la sigue probando
/// <see cref="ConfirmacionAccionesDestructivasTests"/>; esto cubre lo que el
/// rediseño añadió o corrigió.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> qué consultas y comandos llegan al mediador
/// y con qué parámetros —el doble responde según ellos, así que una pantalla
/// que no los enviara recibiría otra cosa—, qué se pinta con lo que vuelve, y
/// qué pasa cuando las respuestas llegan fuera de orden (mediador controlado
/// por <see cref="TaskCompletionSource{TResult}"/>).
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> el cálculo de unidades ni de importes, que es de
/// <c>ObtenerResumenFacturacionQueryHandler</c> (Application); la autorización
/// de la ruta y de los comandos; el aspecto (CSS); ni si dos consultas en vuelo
/// a la vez caben en el mismo DbContext del circuito, que aquí no existe.
/// </para>
/// </summary>
public class FacturacionGen2Tests : BunitContext
{
    /// <summary><see cref="Pestanas"/> mueve el foco por JS y <see cref="Modal"/> importa dialogo-foco.js.</summary>
    public FacturacionGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid ClienteA = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid ClienteB = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");
    private static readonly DateTime Hoy = DateTime.Today;

    /// <summary>Un mes que nunca es el actual, para distinguir el resumen pedido del estimado automático.</summary>
    private static readonly int MesCalculado = Hoy.Month == 3 ? 4 : 3;
    private static readonly int OtroMes = MesCalculado + 1;

    private static readonly string[] Meses =
        ["Enero", "Febrero", "Marzo", "Abril", "Mayo", "Junio", "Julio", "Agosto", "Septiembre", "Octubre", "Noviembre", "Diciembre"];

    // ---------------------------------------------------------------- dobles

    private sealed class MediadorControlado(Func<object, Task<object?>> responder) : IMediator
    {
        public List<object> Enviados { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return (TResponse)(await responder(request))!;
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

    /// <summary>
    /// Datos que ve el mediador. Las tarifas se devuelven <b>por el ClienteId de
    /// la consulta</b> y el resumen lo decide una función de la consulta entera:
    /// un doble que ignorase los parámetros dejaría en verde una pantalla que no
    /// los envía.
    /// </summary>
    private sealed class Escenario
    {
        public List<ClienteSelectorDto> Clientes { get; } =
            [new(ClienteA, "Refrielectric S.L."), new(ClienteB, "Montajes Ebro S.L.")];

        public Dictionary<Guid, List<TarifaClienteDto>> Tarifas { get; } = new() { [ClienteA] = [], [ClienteB] = [] };

        public Func<ObtenerResumenFacturacionQuery, ResumenFacturacionDto?> Resumen { get; set; } = _ => null;

        public Func<ActualizarTarifaClienteCommand, Result> AlActualizar { get; set; } = _ => Result.Exito();

        /// <summary>Si devuelve una tarea, esa petición se resuelve cuando el test lo diga.</summary>
        public Func<object, Task<object?>?> Interceptar { get; set; } = _ => null;

        public Task<object?> Responder(object peticion) =>
            Interceptar(peticion) ?? Task.FromResult<object?>(peticion switch
            {
                ObtenerClientesParaSelectorQuery => Clientes,
                ObtenerTarifasClienteQuery q => Tarifas.TryGetValue(q.ClienteId, out var lista) ? lista.ToList() : new List<TarifaClienteDto>(),
                ObtenerResumenFacturacionQuery q => Resumen(q),
                ActualizarTarifaClienteCommand c => AlActualizar(c),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
            });
    }

    private static TarifaClienteDto Tarifa(Guid clienteId, ConceptoFacturable concepto, string nombre, decimal precio,
        string moneda = "EUR", Guid? version = null) =>
        new(Guid.NewGuid(), clienteId, concepto, nombre, precio, moneda, version ?? Guid.NewGuid());

    private static LineaFacturacionDto Linea(ConceptoFacturable concepto, string nombre, int unidades, decimal precio, string moneda = "EUR") =>
        new(concepto, nombre, unidades, precio, moneda, unidades * precio);

    /// <summary>Como el handler: un total por cada moneda presente en las líneas.</summary>
    private static ResumenFacturacionDto Resumen(string cliente, params LineaFacturacionDto[] lineas) =>
        new(cliente, ResumenFacturacionDto.CalcularTotalesPorMoneda(lineas), lineas.ToList());

    /// <summary>84×3,50 + 1×45 + 6×12 + 148×1,80 + 3×60 = 857,40 EUR, las cifras del mockup.</summary>
    private static ResumenFacturacionDto ResumenDelMockup() => Resumen("Refrielectric S.L.",
        Linea(ConceptoFacturable.TrabajadorActivo, "Trabajador activo", 84, 3.50m),
        Linea(ConceptoFacturable.AltaCentro, "Alta de centro", 1, 45m),
        Linea(ConceptoFacturable.VisitaTrabajadorExtranjero, "Visita de trabajador extranjero", 6, 12m),
        Linea(ConceptoFacturable.DocumentoGestionado, "Documento gestionado", 148, 1.80m),
        Linea(ConceptoFacturable.TecnicoAsignadoProyecto, "Técnico asignado a proyecto", 3, 60m));

    private static ResumenFacturacionDto ResumenEnDosMonedas() => Resumen("Refrielectric S.L.",
        Linea(ConceptoFacturable.TrabajadorActivo, "Trabajador activo", 10, 10m, "EUR"),
        Linea(ConceptoFacturable.AltaCentro, "Alta de centro", 5, 10m, "USD"));

    // ---------------------------------------------------------------- arnés

    private (IRenderedComponent<FacturacionPagina> Cut, MediadorControlado Mediador) Renderizar(Escenario escenario)
    {
        // La aplicación fija es-ES en Program.cs; aquí se fija en el flujo del
        // propio test, que es donde renderiza bUnit.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");

        var mediador = new MediadorControlado(escenario.Responder);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();

        return (Render<FacturacionPagina>(), mediador);
    }

    private static Task ElegirCliente(IRenderedComponent<FacturacionPagina> cut, Guid clienteId) =>
        cut.Find("#sel-cliente").ChangeAsync(new ChangeEventArgs { Value = clienteId.ToString() });

    private static Task IrAResumen(IRenderedComponent<FacturacionPagina> cut) =>
        cut.FindAll("button[role=tab]").Single(b => b.TextContent.Trim() == "Resumen mensual").ClickAsync(new MouseEventArgs());

    private static Task ElegirMes(IRenderedComponent<FacturacionPagina> cut, int mes) =>
        cut.Find(".campo-mes select").ChangeAsync(new ChangeEventArgs { Value = mes.ToString(CultureInfo.InvariantCulture) });

    private static Task Calcular(IRenderedComponent<FacturacionPagina> cut) =>
        cut.FindAll(".filtros-resumen button").Single(b => b.TextContent.Trim() == "Calcular").ClickAsync(new MouseEventArgs());

    private static string Estimado(IRenderedComponent<FacturacionPagina> cut) =>
        cut.Find(".dato-estimado .dato-cliente-valor").TextContent.Trim();

    private static string ConceptosTarificados(IRenderedComponent<FacturacionPagina> cut) =>
        cut.Find(".dato-conceptos .dato-cliente-valor").TextContent.Trim();

    private static IReadOnlyList<IElement> FilasTarifas(IRenderedComponent<FacturacionPagina> cut) =>
        cut.FindAll("table.tabla-facturacion tbody tr");

    private static string Texto(IElement elemento) => elemento.TextContent.Trim();

    // ---------------------------------------------------------------- sin cliente

    [Fact]
    public void Sin_cliente_elegido_pide_elegir_uno_y_no_consulta_tarifas()
    {
        var (cut, mediador) = Renderizar(new Escenario());

        cut.Find(".estado-vacio h3").TextContent.Should().Be("Elige un cliente para empezar",
            "antes no se pintaba nada bajo el selector: «aún no has elegido» no se distinguía de «no hay resultados»");
        cut.FindAll("[role=tablist]").Should().BeEmpty();
        mediador.Enviados.OfType<ObtenerTarifasClienteQuery>().Should().BeEmpty();
    }

    [Fact]
    public void Sin_clientes_disponibles_lo_dice_en_vez_de_pintar_un_selector_vacio()
    {
        var escenario = new Escenario();
        escenario.Clientes.Clear();

        var (cut, _) = Renderizar(escenario);

        cut.FindAll("#sel-cliente").Should().BeEmpty();
        cut.Find(".estado-vacio h3").TextContent.Should().Be("No hay clientes disponibles");
    }

    // ---------------------------------------------------------------- franja del cliente

    [Fact]
    public async Task Elegir_un_cliente_pinta_sus_tarifas_y_cuantos_conceptos_tienen_tarifa()
    {
        var escenario = new Escenario();
        escenario.Tarifas[ClienteA] =
        [
            Tarifa(ClienteA, ConceptoFacturable.TrabajadorActivo, "Trabajador activo", 3.50m),
            Tarifa(ClienteA, ConceptoFacturable.AltaCentro, "Alta de centro", 45m),
        ];
        escenario.Tarifas[ClienteB] = [Tarifa(ClienteB, ConceptoFacturable.DocumentoGestionado, "Documento gestionado", 1.80m)];
        var (cut, mediador) = Renderizar(escenario);

        await ElegirCliente(cut, ClienteA);

        mediador.Enviados.OfType<ObtenerTarifasClienteQuery>().Should().ContainSingle()
            .Which.ClienteId.Should().Be(ClienteA);
        FilasTarifas(cut).Select(f => Texto(f.QuerySelector(".concepto-nombre")!))
            .Should().Equal("Trabajador activo", "Alta de centro");
        ConceptosTarificados(cut).Should().Be("2 de 7", "hay siete ConceptoFacturable y este cliente tiene tarifa para dos");
    }

    [Fact]
    public async Task El_estimado_es_el_resumen_del_mes_en_curso_de_ese_cliente()
    {
        var escenario = new Escenario
        {
            Resumen = q => q.ClienteId == ClienteA && q.Anyo == Hoy.Year && q.Mes == Hoy.Month ? ResumenDelMockup() : null
        };
        var (cut, _) = Renderizar(escenario);

        await ElegirCliente(cut, ClienteA);

        Estimado(cut).Should().Be("857,40 EUR");
    }

    [Fact]
    public async Task El_estimado_con_varias_monedas_da_un_total_por_moneda_y_no_la_suma_a_ciegas()
    {
        var escenario = new Escenario { Resumen = q => q.ClienteId == ClienteA ? ResumenEnDosMonedas() : null };
        var (cut, _) = Renderizar(escenario);

        await ElegirCliente(cut, ClienteA);

        Estimado(cut).Should().Be("100,00 EUR · 50,00 USD",
            "sumar euros y dólares en una sola cifra (150) no significa nada");
    }

    [Fact]
    public async Task Sin_tarifas_el_estimado_no_inventa_cero_euros()
    {
        // El handler, sin tarifas, devuelve la lista de totales vacía.
        var escenario = new Escenario { Resumen = q => q.ClienteId == ClienteA ? new ResumenFacturacionDto("Refrielectric S.L.", [], []) : null };
        var (cut, _) = Renderizar(escenario);

        await ElegirCliente(cut, ClienteA);

        Estimado(cut).Should().Be("—");
        cut.Find(".dato-estimado .dato-cliente-pista").TextContent.Trim().Should().Be("Sin tarifas configuradas");
    }

    // ---------------------------------------------------------------- carreras

    [Fact]
    public async Task Las_tarifas_del_cliente_anterior_que_llegan_tarde_se_descartan()
    {
        var tarifasA = new TaskCompletionSource<object?>();
        var tarifasB = new TaskCompletionSource<object?>();
        var escenario = new Escenario
        {
            Interceptar = p => p switch
            {
                ObtenerTarifasClienteQuery q when q.ClienteId == ClienteA => tarifasA.Task,
                ObtenerTarifasClienteQuery q when q.ClienteId == ClienteB => tarifasB.Task,
                _ => null
            }
        };
        var (cut, _) = Renderizar(escenario);

        var eleccionA = ElegirCliente(cut, ClienteA);
        var eleccionB = ElegirCliente(cut, ClienteB);

        // B responde primero; A, que ya no es el cliente elegido, responde después.
        await cut.InvokeAsync(() => tarifasB.SetResult(new List<TarifaClienteDto>
        {
            Tarifa(ClienteB, ConceptoFacturable.DocumentoGestionado, "Documento gestionado", 1.80m)
        }));
        await eleccionB;
        await cut.InvokeAsync(() => tarifasA.SetResult(new List<TarifaClienteDto>
        {
            Tarifa(ClienteA, ConceptoFacturable.TrabajadorActivo, "Trabajador activo", 3.50m),
            Tarifa(ClienteA, ConceptoFacturable.AltaCentro, "Alta de centro", 45m)
        }));
        await eleccionA;

        FilasTarifas(cut).Select(f => Texto(f.QuerySelector(".concepto-nombre")!))
            .Should().Equal(["Documento gestionado"], "el cliente elegido es B: las tarifas de A llegaron tarde y no son suyas");
        ConceptosTarificados(cut).Should().Be("1 de 7");
    }

    [Fact]
    public async Task El_estimado_del_cliente_anterior_que_llega_tarde_se_descarta()
    {
        var estimadoA = new TaskCompletionSource<object?>();
        var escenario = new Escenario
        {
            Resumen = q => q.ClienteId == ClienteB && q.Anyo == Hoy.Year && q.Mes == Hoy.Month
                ? Resumen("Montajes Ebro S.L.", Linea(ConceptoFacturable.AltaCentro, "Alta de centro", 1, 10m))
                : null,
            Interceptar = p => p is ObtenerResumenFacturacionQuery q && q.ClienteId == ClienteA ? estimadoA.Task : null
        };
        var (cut, _) = Renderizar(escenario);

        // Las tarifas de A vuelven en el acto; su estimado se queda en vuelo.
        var eleccionA = ElegirCliente(cut, ClienteA);
        await ElegirCliente(cut, ClienteB);
        Estimado(cut).Should().Be("10,00 EUR");

        await cut.InvokeAsync(() => estimadoA.SetResult(ResumenDelMockup()));
        await eleccionA;

        Estimado(cut).Should().Be("10,00 EUR", "el estimado de Refrielectric llegó tarde y el cliente elegido es Montajes Ebro");
    }

    [Fact]
    public async Task Un_resumen_pedido_para_el_cliente_anterior_no_aparece_bajo_el_nuevo()
    {
        var resumenA = new TaskCompletionSource<object?>();
        var escenario = new Escenario
        {
            Interceptar = p => p is ObtenerResumenFacturacionQuery q && q.ClienteId == ClienteA && q.Mes == MesCalculado
                ? resumenA.Task
                : null
        };
        var (cut, _) = Renderizar(escenario);
        await ElegirCliente(cut, ClienteA);
        await IrAResumen(cut);
        await ElegirMes(cut, MesCalculado);

        var calculo = Calcular(cut);
        await ElegirCliente(cut, ClienteB);
        await IrAResumen(cut);
        await cut.InvokeAsync(() => resumenA.SetResult(ResumenDelMockup()));
        await calculo;

        cut.FindAll(".tarjeta-titulo").Should().BeEmpty(
            "ese resumen se pidió para Refrielectric y el cliente elegido ahora es Montajes Ebro");
        cut.Markup.Should().NotContain("857,40");
    }

    // ---------------------------------------------------------------- resumen mensual

    [Fact]
    public async Task El_titulo_y_la_exportacion_son_del_periodo_calculado_no_del_filtro()
    {
        var escenario = new Escenario
        {
            Resumen = q => q.ClienteId == ClienteA
                ? Resumen("Refrielectric S.L.", Linea(ConceptoFacturable.TrabajadorActivo, "Trabajador activo", 10, 3.50m))
                : null
        };
        var (cut, mediador) = Renderizar(escenario);
        await ElegirCliente(cut, ClienteA);
        await IrAResumen(cut);
        await ElegirMes(cut, MesCalculado);
        await Calcular(cut);

        // Cambiar el filtro sin volver a calcular.
        await ElegirMes(cut, OtroMes);

        mediador.Enviados.OfType<ObtenerResumenFacturacionQuery>().Last()
            .Should().Be(new ObtenerResumenFacturacionQuery(ClienteA, Hoy.Year, MesCalculado));
        cut.Find(".tarjeta-titulo").TextContent.Trim()
            .Should().Be($"Resumen — Refrielectric S.L. — {Meses[MesCalculado - 1]} {Hoy.Year}",
                "en pantalla están los datos del mes calculado, no los del mes que marca ahora el filtro");
        cut.Find("a.enlace-exportar").GetAttribute("href")
            .Should().Be($"/facturacion/resumen.xlsx?clienteId={ClienteA}&anyo={Hoy.Year}&mes={MesCalculado}");
    }

    [Fact]
    public async Task El_resumen_con_varias_monedas_da_un_total_por_moneda_y_no_pinta_el_reparto()
    {
        var escenario = new Escenario { Resumen = q => q.ClienteId == ClienteA ? ResumenEnDosMonedas() : null };
        var (cut, _) = Renderizar(escenario);
        await ElegirCliente(cut, ClienteA);
        await IrAResumen(cut);

        await Calcular(cut);

        cut.FindAll("tfoot tr.fila-total").Select(f => string.Join(" | ", f.QuerySelectorAll("td").Select(Texto)))
            .Should().Equal("Total estimado en EUR | 100,00 EUR", "Total estimado en USD | 50,00 USD");
        cut.FindAll(".reparto-importe").Should().BeEmpty("comparar barras de euros con barras de dólares no dice nada");
        cut.FindAll(".nota-varias-monedas").Should().ContainSingle();
        cut.Markup.Should().NotContain("150,00");
    }

    [Fact]
    public async Task El_reparto_del_importe_es_proporcional_al_subtotal_mayor()
    {
        var escenario = new Escenario
        {
            Resumen = q => q.ClienteId == ClienteA
                ? Resumen("Refrielectric S.L.",
                    Linea(ConceptoFacturable.TrabajadorActivo, "Trabajador activo", 10, 29.40m),
                    Linea(ConceptoFacturable.AltaCentro, "Alta de centro", 10, 14.70m),
                    Linea(ConceptoFacturable.DocumentoGestionado, "Documento gestionado", 0, 1.80m))
                : null
        };
        var (cut, _) = Renderizar(escenario);
        await ElegirCliente(cut, ClienteA);
        await IrAResumen(cut);

        await Calcular(cut);

        cut.FindAll(".reparto-importe-barra").Select(b => b.GetAttribute("style"))
            .Should().Equal("width:100%", "width:50%", "width:0%");
        cut.FindAll(".reparto-importe-valor").Select(Texto)
            .Should().Equal("294,00 EUR", "147,00 EUR", "0,00 EUR");
    }

    [Fact]
    public async Task Si_el_resumen_revienta_avisa_y_reintentar_lo_recupera()
    {
        var llamadas = 0;
        var escenario = new Escenario
        {
            Resumen = q =>
            {
                if (q.ClienteId != ClienteA || q.Mes != MesCalculado) return null;
                if (++llamadas == 1) throw new InvalidOperationException("Base de datos caída (simulada).");
                return Resumen("Refrielectric S.L.", Linea(ConceptoFacturable.TrabajadorActivo, "Trabajador activo", 10, 3.50m));
            }
        };
        var (cut, _) = Renderizar(escenario);
        await ElegirCliente(cut, ClienteA);
        await IrAResumen(cut);
        await ElegirMes(cut, MesCalculado);

        await Calcular(cut);

        cut.Find(".aviso-error").TextContent.Should().Contain("No pudimos calcular el resumen");

        await cut.Find(".aviso-error button").ClickAsync(new MouseEventArgs());

        llamadas.Should().Be(2);
        cut.FindAll(".aviso-error").Should().BeEmpty();
        cut.Find("tfoot tr.fila-total").TextContent.Should().Contain("35,00 EUR");
    }

    [Fact]
    public async Task Un_cliente_que_el_resumen_no_encuentra_se_dice()
    {
        var (cut, _) = Renderizar(new Escenario { Resumen = _ => null });
        await ElegirCliente(cut, ClienteA);
        await IrAResumen(cut);

        await Calcular(cut);

        cut.Find("[role=tabpanel] .estado-vacio h3").TextContent.Should().Be("No encontramos este cliente");
    }

    // ---------------------------------------------------------------- tarifas

    [Fact]
    public async Task Editar_envia_la_tarifa_con_su_version_y_recarga_precio_y_estimado()
    {
        var version = Guid.Parse("99999999-0000-0000-0000-000000000009");
        var tarifa = Tarifa(ClienteA, ConceptoFacturable.TrabajadorActivo, "Trabajador activo", 3.50m, version: version);
        var escenario = new Escenario();
        escenario.Tarifas[ClienteA] = [tarifa];
        // El estimado se calcula con las tarifas que haya en ese momento: si la
        // pantalla no lo vuelve a pedir tras guardar, se queda con el precio viejo.
        escenario.Resumen = q => q.ClienteId == ClienteA && q.Anyo == Hoy.Year && q.Mes == Hoy.Month
            ? Resumen("Refrielectric S.L.", escenario.Tarifas[ClienteA]
                .Select(t => Linea(t.Concepto, t.ConceptoNombre, 10, t.PrecioUnitario, t.MonedaIso)).ToArray())
            : null;
        escenario.AlActualizar = c =>
        {
            escenario.Tarifas[ClienteA] = [tarifa with { PrecioUnitario = c.PrecioUnitario, MonedaIso = c.MonedaIso, Version = Guid.NewGuid() }];
            return Result.Exito();
        };
        var (cut, mediador) = Renderizar(escenario);
        await ElegirCliente(cut, ClienteA);
        Estimado(cut).Should().Be("35,00 EUR");

        await cut.FindAll("td button.enlace-accion").Single(b => b.TextContent.Trim() == "Editar").ClickAsync(new MouseEventArgs());
        await cut.Find("tr.fila-edicion input[type=number]").ChangeAsync(new ChangeEventArgs { Value = "4.25" });
        await cut.FindAll("tr.fila-edicion button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<ActualizarTarifaClienteCommand>().Should().ContainSingle()
            .Which.Should().Be(new ActualizarTarifaClienteCommand(tarifa.Id, 4.25m, "EUR", version));
        Texto(FilasTarifas(cut).Single().QuerySelectorAll("td")[1]).Should().Be("4,25");
        Estimado(cut).Should().Be("42,50 EUR", "el estimado se recalcula con la tarifa nueva");
    }

    [Fact]
    public async Task Un_error_de_validacion_al_editar_se_ve_junto_al_precio()
    {
        var escenario = new Escenario
        {
            AlActualizar = _ => throw new ValidationException([new ValidationFailure("PrecioUnitario", "El precio no puede ser negativo.")])
        };
        escenario.Tarifas[ClienteA] = [Tarifa(ClienteA, ConceptoFacturable.TrabajadorActivo, "Trabajador activo", 3.50m)];
        var (cut, _) = Renderizar(escenario);
        await ElegirCliente(cut, ClienteA);

        await cut.FindAll("td button.enlace-accion").Single(b => b.TextContent.Trim() == "Editar").ClickAsync(new MouseEventArgs());
        await cut.Find("tr.fila-edicion input[type=number]").ChangeAsync(new ChangeEventArgs { Value = "-1" });
        await cut.FindAll("tr.fila-edicion button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        cut.Find("tr.fila-edicion td.col-numero .mensaje-error-campo").TextContent.Trim()
            .Should().Be("El precio no puede ser negativo.",
                "antes el error llegaba con la clave PrecioUnitario y la vista solo leía \"precio\": no se veía nada");
    }

    [Fact]
    public async Task El_desplegable_de_nueva_tarifa_nombra_los_siete_conceptos_en_castellano()
    {
        var (cut, _) = Renderizar(new Escenario());
        await ElegirCliente(cut, ClienteA);

        await cut.FindAll("button").Single(b => b.TextContent.Contains("Añadir tarifa")).ClickAsync(new MouseEventArgs());

        var opciones = cut.FindAll(".campo-concepto select option").Select(Texto).ToList();
        opciones.Should().HaveCount(7);
        opciones.Should().NotIntersectWith(Enum.GetNames<ConceptoFacturable>(),
            "antes tres conceptos salían con el nombre del enum, p. ej. «GestionProyectoRealizada»");
        opciones.Should().Contain(["Técnico asignado a proyecto", "Gestión de proyecto realizada", "Día de proyecto abierto"]);
    }

    [Fact]
    public async Task Con_los_siete_conceptos_tarificados_explica_por_que_no_se_puede_anadir_y_que_cuenta_cada_uno()
    {
        var escenario = new Escenario();
        escenario.Tarifas[ClienteA] = Enum.GetValues<ConceptoFacturable>()
            .Select(c => Tarifa(ClienteA, c, c.ToString(), 1m))
            .ToList();
        var (cut, _) = Renderizar(escenario);

        await ElegirCliente(cut, ClienteA);

        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Añadir tarifa"));
        cut.FindAll(".nota-conceptos-completos").Should().ContainSingle(
            "antes el botón desaparecía sin explicar que ya no quedan conceptos libres");
        var unidades = cut.FindAll(".concepto-unidad").Select(Texto).ToList();
        unidades.Should().HaveCount(7).And.OnlyHaveUniqueItems().And.NotContain(string.Empty);
    }

    [Fact]
    public async Task El_precio_de_la_tarifa_sale_una_vez_como_cifra_y_la_moneda_en_su_columna()
    {
        var escenario = new Escenario();
        escenario.Tarifas[ClienteA] = [Tarifa(ClienteA, ConceptoFacturable.TrabajadorActivo, "Trabajador activo", 3.50m)];
        var (cut, _) = Renderizar(escenario);

        await ElegirCliente(cut, ClienteA);

        var celdas = FilasTarifas(cut).Single().QuerySelectorAll("td");
        Texto(celdas[1]).Should().Be("3,50", "la moneda tiene su propia columna: antes salía dos veces por fila");
        celdas[1].ClassList.Should().Contain(["col-numero", "cifra"]);
        Texto(celdas[2]).Should().Be("EUR");
    }
}

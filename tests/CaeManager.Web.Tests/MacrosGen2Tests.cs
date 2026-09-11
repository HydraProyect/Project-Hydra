using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.CrearMacro;
using CaeManager.Application.Comunicaciones.Commands.EditarMacro;
using CaeManager.Application.Comunicaciones.Commands.EliminarMacro;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMacros;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones.Pages;
using FluentAssertions;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// Macros contra su mockup Gen 2 («Macros TALVEG.dc.html»). Los estados vacíos
/// y la semántica del filtro que ENSANCHA los sigue probando
/// <see cref="MacrosVacioPorFiltroTests"/>; esto cubre lo que el rediseño
/// añadió o corrigió.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> qué consultas y comandos llegan al mediador y
/// con qué parámetros —el doble responde según ellos: con cliente, las
/// genéricas más las suyas; sin cliente, solo las genéricas; y la edición falla
/// por conflicto si la versión que llega no es la vigente—, qué se pinta con lo
/// que vuelve, y qué pasa cuando las respuestas llegan fuera de orden (mediador
/// controlado por <see cref="TaskCompletionSource{TResult}"/>).
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> el alcance de cartera ni la autorización (de
/// <c>ObtenerMacrosQueryHandler</c> y los handlers, en Application); que la
/// bandeja ofrezca de verdad estas macros; ni el aspecto (CSS).
/// </para>
/// </summary>
public class MacrosGen2Tests : BunitContext
{
    /// <summary><see cref="Drawer"/> y <see cref="Modal"/> importan dialogo-foco.js, y <see cref="BotonCopiar"/> clipboard.js.</summary>
    public MacrosGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly ClienteSelectorDto ClienteA = new(Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001"), "Refrielectric S.A.");
    private static readonly ClienteSelectorDto ClienteB = new(Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002"), "Montajes Ebro S.L.");
    private static readonly Guid UsuarioActual = Guid.Parse("c3c3c3c3-0000-0000-0000-000000000003");

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

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(UsuarioActual);
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <summary>
    /// Datos que ve el mediador. <c>ObtenerMacrosQuery</c> responde <b>según su
    /// ClienteId</b>, como el handler real; la edición compara la versión que
    /// llega con la vigente, como <c>ConcurrenciaOptimista.Verificar</c>; y
    /// eliminar quita la macro de verdad. Un doble que ignorase los parámetros
    /// dejaría en verde una pantalla que no los envía.
    /// </summary>
    private sealed class Escenario
    {
        public List<ClienteSelectorDto> Clientes { get; } = [ClienteA, ClienteB];

        public List<MacroListaDto> Macros { get; } = [];

        public Func<CrearMacroCommand, Result<Guid>> AlCrear { get; set; } = _ => Result.Exito(Guid.NewGuid());

        /// <summary>Si devuelve una tarea, esa petición se resuelve cuando el test lo diga.</summary>
        public Func<object, Task<object?>?> Interceptar { get; set; } = _ => null;

        public Task<object?> Responder(object peticion) =>
            Interceptar(peticion) ?? Task.FromResult<object?>(peticion switch
            {
                ObtenerClientesParaSelectorQuery => Clientes.ToList(),
                ObtenerMacrosQuery q => Visibles(q.ClienteId),
                CrearMacroCommand c => AlCrear(c),
                EditarMacroCommand c => Editar(c),
                EliminarMacroCommand c => Eliminar(c),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
            });

        public List<MacroListaDto> Visibles(Guid? clienteId) =>
            Macros.Where(m => m.ClienteId is null || (clienteId is not null && m.ClienteId == clienteId)).ToList();

        private Result Editar(EditarMacroCommand c)
        {
            var indice = Macros.FindIndex(m => m.Id == c.Id);
            if (indice < 0)
                return Result.Fallo(Error.Crear("MacroRespuesta.NoEncontrada", "No encontramos esta macro."));
            if (Macros[indice].Version != c.Version)
                return Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "Otra persona modificó esta macro mientras lo editabas."));

            Macros[indice] = Macros[indice] with { Titulo = c.Titulo, CuerpoHtml = c.CuerpoHtml, Version = Guid.NewGuid() };
            return Result.Exito();
        }

        private Result Eliminar(EliminarMacroCommand c) =>
            Macros.RemoveAll(m => m.Id == c.Id) > 0
                ? Result.Exito()
                : Result.Fallo(Error.Crear("MacroRespuesta.NoEncontrada", "No encontramos esta macro."));
    }

    private static MacroListaDto Macro(string titulo, ClienteSelectorDto? cliente = null, string? cuerpo = null) =>
        new(Guid.NewGuid(), cliente?.Id, cliente?.RazonSocial, titulo, cuerpo ?? $"<p>{titulo}</p>", Guid.NewGuid());

    // ---------------------------------------------------------------- arnés

    private (IRenderedComponent<Macros> Cut, MediadorControlado Mediador) Renderizar(Escenario escenario)
    {
        var mediador = new MediadorControlado(escenario.Responder);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddSingleton<ILogger<Macros>>(_ => NullLogger<Macros>.Instance);

        // El módulo está congelado por defecto: sin esto la página navega a
        // /not-found y el test observaría una pantalla que no es.
        Services.AddSingleton<IOptions<ComunicacionesOptions>>(
            Options.Create(new ComunicacionesOptions { Activo = true }));

        return (Render<Macros>(), mediador);
    }

    private static Task ElegirCliente(IRenderedComponent<Macros> cut, Guid? clienteId) =>
        cut.Find(".macros-filtros select").ChangeAsync(new ChangeEventArgs { Value = clienteId?.ToString() ?? string.Empty });

    private static IReadOnlyList<IElement> Filas(IRenderedComponent<Macros> cut) => cut.FindAll("table.tabla-macros tbody tr");

    private static IReadOnlyList<string> TitulosFilas(IRenderedComponent<Macros> cut) =>
        Filas(cut).Select(f => f.QuerySelector(".macro-titulo")!.TextContent.Trim()).ToList();

    private static IElement Boton(IRenderedComponent<Macros> cut, string selector, string texto) =>
        cut.FindAll(selector).Single(b => b.TextContent.Trim() == texto);

    private static Task Pulsar(IRenderedComponent<Macros> cut, string selector, string texto) =>
        Boton(cut, selector, texto).ClickAsync(new MouseEventArgs());

    private static Task AbrirNuevaMacro(IRenderedComponent<Macros> cut) => Pulsar(cut, ".acciones-cabecera button", "+ Nueva macro");

    private static Task EditarFila(IRenderedComponent<Macros> cut, string titulo) =>
        Filas(cut).Single(f => f.QuerySelector(".macro-titulo")!.TextContent.Trim() == titulo)
            .QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Editar").ClickAsync(new MouseEventArgs());

    private static Task EliminarFila(IRenderedComponent<Macros> cut, string titulo) =>
        Filas(cut).Single(f => f.QuerySelector(".macro-titulo")!.TextContent.Trim() == titulo)
            .QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

    /// <summary>CampoTexto notifica tras su debounce: InputAsync espera a que el valor llegue a la página.</summary>
    private static async Task RellenarFormulario(IRenderedComponent<Macros> cut, string titulo, string cuerpo)
    {
        await cut.Find(".drawer-cuerpo input.campo-input").InputAsync(new ChangeEventArgs { Value = titulo });
        await cut.Find(".drawer-cuerpo textarea").InputAsync(new ChangeEventArgs { Value = cuerpo });
    }

    private static Task Guardar(IRenderedComponent<Macros> cut) => Pulsar(cut, ".drawer-pie button", "Guardar");

    // ---------------------------------------------------------------- filtro y recuento

    [Fact]
    public async Task Con_cliente_elegido_la_consulta_lleva_ese_cliente_y_el_recuento_suma_genericas_y_suyas()
    {
        var escenario = new Escenario();
        escenario.Macros.AddRange(
        [
            Macro("Acuse de recibo"),
            Macro("Documento ilegible"),
            Macro("Requisitos de acceso — Centro Norte", ClienteA),
            Macro("Rechazo del portal", ClienteB),
        ]);
        var (cut, mediador) = Renderizar(escenario);

        cut.Find(".macros-filtro-resumen").TextContent.Trim()
            .Should().Be("2 macros genéricas. Elige un cliente para ver también las suyas.");

        await ElegirCliente(cut, ClienteA.Id);

        mediador.Enviados.OfType<ObtenerMacrosQuery>().Last().ClienteId.Should().Be(ClienteA.Id);
        TitulosFilas(cut).Should().BeEquivalentTo(["Acuse de recibo", "Documento ilegible", "Requisitos de acceso — Centro Norte"],
            "con cliente se ven las genéricas MÁS las suyas, y nunca las de otro cliente");
        cut.Find(".macros-filtro-resumen").TextContent.Trim()
            .Should().Be("2 genéricas + 1 de Refrielectric S.A.", "el filtro SUMA, y la frase lo dice");
        cut.Find(".paginador-texto").TextContent.Trim()
            .Should().Be("Página 1 de 1 — 3 macro(s): 2 genérica(s) · 1 de Refrielectric S.A.");
    }

    [Fact]
    public async Task La_fila_generica_lleva_el_badge_y_la_de_cliente_su_razon_social()
    {
        var escenario = new Escenario();
        escenario.Macros.AddRange([Macro("Acuse de recibo"), Macro("Aviso de visita", ClienteA)]);

        // Sin cliente la consulta no trae la de ClienteA: se elige para verlas juntas.
        var (cut, _) = Renderizar(escenario);
        await ElegirCliente(cut, ClienteA.Id);

        var generica = Filas(cut).Single(f => f.TextContent.Contains("Acuse de recibo"));
        generica.QuerySelector(".badge")!.TextContent.Trim().Should().Be("Genérica");

        var propia = Filas(cut).Single(f => f.TextContent.Contains("Aviso de visita"));
        propia.QuerySelector(".badge").Should().BeNull("una macro con cliente no es genérica");
        propia.QuerySelector(".macro-cliente")!.TextContent.Trim().Should().Be("Refrielectric S.A.");
    }

    [Fact]
    public void El_resumen_de_la_fila_es_el_contenido_sin_etiquetas_y_recortado()
    {
        var largo = string.Join(" ", Enumerable.Repeat("documentación", 20));
        var escenario = new Escenario();
        escenario.Macros.AddRange(
        [
            Macro("Con etiquetas", cuerpo: "<p>Buenos&nbsp;días,</p>\n\n<p>Confirmamos la <b>recepción</b>.</p>"),
            Macro("Largo", cuerpo: largo),
        ]);
        var (cut, _) = Renderizar(escenario);

        string Resumen(string titulo) => Filas(cut)
            .Single(f => f.QuerySelector(".macro-titulo")!.TextContent.Trim() == titulo)
            .QuerySelector(".macro-resumen")!.TextContent;

        Resumen("Con etiquetas").Should().Be("Buenos días, Confirmamos la recepción.",
            "CuerpoHtml puede traer etiquetas (el seeder las pone): la fila enseña texto corrido, no marcado");
        // 96 caracteres, como el recorte del mockup, más la elipsis.
        Resumen("Largo").Should().HaveLength(96 + 1).And.EndWith("…");
    }

    // ---------------------------------------------------------------- carreras

    [Fact]
    public async Task La_lista_del_cliente_anterior_que_llega_tarde_se_descarta()
    {
        var escenario = new Escenario();
        escenario.Macros.AddRange([Macro("Solo de A", ClienteA), Macro("Solo de B", ClienteB)]);
        var listaA = new TaskCompletionSource<object?>();
        var listaB = new TaskCompletionSource<object?>();
        escenario.Interceptar = p => p switch
        {
            ObtenerMacrosQuery q when q.ClienteId == ClienteA.Id => listaA.Task,
            ObtenerMacrosQuery q when q.ClienteId == ClienteB.Id => listaB.Task,
            _ => null
        };
        var (cut, _) = Renderizar(escenario);

        var eleccionA = ElegirCliente(cut, ClienteA.Id);
        var eleccionB = ElegirCliente(cut, ClienteB.Id);

        // B responde primero; A, que ya no es el cliente elegido, responde después.
        await cut.InvokeAsync(() => listaB.SetResult(escenario.Visibles(ClienteB.Id)));
        await eleccionB;
        await cut.InvokeAsync(() => listaA.SetResult(escenario.Visibles(ClienteA.Id)));
        await eleccionA;

        TitulosFilas(cut).Should().Equal(["Solo de B"], "el cliente elegido es B: la lista de A llegó tarde y no es suya");
        cut.Find(".macros-filtro-resumen").TextContent.Trim().Should().Be("0 genéricas + 1 de Montajes Ebro S.L.");
    }

    // ---------------------------------------------------------------- paginación

    [Fact]
    public async Task La_paginacion_en_memoria_reparte_las_filas_y_vuelve_a_la_primera_al_cambiar_de_cliente()
    {
        var escenario = new Escenario();
        escenario.Macros.AddRange(Enumerable.Range(1, 25).Select(i => Macro($"Genérica {i:00}")));
        var (cut, _) = Renderizar(escenario);

        Filas(cut).Should().HaveCount(20);
        cut.Find(".paginador-texto").TextContent.Trim().Should().Be("Página 1 de 2 — 25 macro(s) genérica(s)");

        await Pulsar(cut, ".paginador button", "Siguiente");

        TitulosFilas(cut).Should().Equal(Enumerable.Range(21, 5).Select(i => $"Genérica {i:00}"),
            "la segunda página son las cinco que no cabían en la primera");
        cut.Find(".paginador-texto").TextContent.Trim().Should().StartWith("Página 2 de 2");

        await ElegirCliente(cut, ClienteA.Id);

        cut.Find(".paginador-texto").TextContent.Trim().Should().StartWith("Página 1 de 2",
            "otra lista empieza por su primera página");

        await cut.Find(".paginador-tamano-select").ChangeAsync(new ChangeEventArgs { Value = "50" });

        Filas(cut).Should().HaveCount(25);
    }

    // ---------------------------------------------------------------- crear

    [Fact]
    public async Task La_macro_nueva_nace_del_cliente_elegido_en_el_filtro()
    {
        var escenario = new Escenario();
        var (cut, mediador) = Renderizar(escenario);
        await ElegirCliente(cut, ClienteA.Id);

        await AbrirNuevaMacro(cut);
        await RellenarFormulario(cut, "Aviso de visita", "La visita es el lunes.");
        await Guardar(cut);

        var comando = mediador.Enviados.OfType<CrearMacroCommand>().Should().ContainSingle().Subject;
        comando.Should().Be(new CrearMacroCommand("Aviso de visita", "La visita es el lunes.", ClienteA.Id),
            "con Refrielectric elegido en el filtro, la macro nueva es suya salvo que se cambie a genérica");
    }

    [Fact]
    public async Task Un_doble_clic_en_guardar_envia_un_solo_alta()
    {
        var escenario = new Escenario();
        var alta = new TaskCompletionSource<object?>();
        escenario.Interceptar = p => p is CrearMacroCommand ? alta.Task : null;
        var (cut, mediador) = Renderizar(escenario);
        await AbrirNuevaMacro(cut);
        await RellenarFormulario(cut, "Acuse de recibo", "Recibido, gracias.");

        // Ninguno de los dos clics se espera antes de comprobar: sin guarda, el
        // segundo también quedaría retenido en «alta» y el test se colgaría en
        // vez de caer por el motivo que mide.
        var primero = Guardar(cut);
        var segundo = Guardar(cut);

        mediador.Enviados.OfType<CrearMacroCommand>().Should().ContainSingle(
            "el segundo clic llega mientras el primero espera al servidor: sin guarda saldrían dos altas iguales");

        await cut.InvokeAsync(() => alta.SetResult(Result.Exito(Guid.NewGuid())));
        await primero;
        await segundo;

        mediador.Enviados.OfType<CrearMacroCommand>().Should().ContainSingle("el segundo clic se descartó, no quedó en cola");
    }

    [Fact]
    public async Task Un_error_de_validacion_marca_el_campo_y_no_cierra_el_formulario()
    {
        var escenario = new Escenario
        {
            AlCrear = _ => throw new ValidationException([new ValidationFailure(nameof(CrearMacroCommand.Titulo), "El título no puede superar 150 caracteres.")])
        };
        var (cut, _) = Renderizar(escenario);
        await AbrirNuevaMacro(cut);
        await RellenarFormulario(cut, new string('x', 151), "Cuerpo");

        await Guardar(cut);

        cut.Find(".drawer-cuerpo .campo-mensaje-error").TextContent.Trim().Should().Be("El título no puede superar 150 caracteres.");
        cut.Find(".drawer-cuerpo .alerta-formulario").TextContent.Trim().Should().Be("Revisa los campos marcados.");
        cut.FindAll(".drawer-panel").Should().ContainSingle("el formulario sigue abierto con lo escrito");
    }

    // ---------------------------------------------------------------- conflicto

    [Fact]
    public async Task Tras_un_conflicto_cargar_la_version_actual_permite_guardar_sobre_ella()
    {
        var escenario = new Escenario();
        var original = Macro("Acuse de recibo");
        escenario.Macros.Add(original);
        var (cut, mediador) = Renderizar(escenario);

        await EditarFila(cut, "Acuse de recibo");

        // Otra persona guarda mientras el formulario está abierto.
        var deOtraPersona = original with { Titulo = "Acuse de recibo (revisado)", CuerpoHtml = "Recibido.", Version = Guid.NewGuid() };
        escenario.Macros[0] = deOtraPersona;

        await RellenarFormulario(cut, "Mi título", "Mi cuerpo");
        await Guardar(cut);

        mediador.Enviados.OfType<EditarMacroCommand>().Should().ContainSingle().Which.Version.Should().Be(original.Version);
        cut.Find(".macros-conflicto .macros-conflicto-titulo").TextContent.Trim()
            .Should().Be("Otra persona editó esta macro mientras la tenías abierta");
        Boton(cut, ".drawer-pie button", "Guardar").HasAttribute("disabled").Should().BeTrue(
            "guardar otra vez con la versión vieja volvería a chocar");

        await Pulsar(cut, ".macros-conflicto button", "Cargar la versión actual");

        cut.FindAll(".macros-conflicto").Should().BeEmpty();
        cut.Find(".drawer-cuerpo input.campo-input").GetAttribute("value").Should().Be("Acuse de recibo (revisado)");

        await Guardar(cut);

        var ultimo = mediador.Enviados.OfType<EditarMacroCommand>().Last();
        ultimo.Version.Should().Be(deOtraPersona.Version, "se guarda sobre la versión que se acaba de cargar, no sobre la vieja");
        mediador.Enviados.OfType<EditarMacroCommand>().Should().HaveCount(2);
        cut.FindAll(".drawer-panel").Should().BeEmpty("el segundo guardado entra y el formulario se cierra");
    }

    // ---------------------------------------------------------------- eliminar

    [Fact]
    public async Task Eliminar_pide_confirmacion_no_promete_recuperarla_y_borra_la_macro_elegida()
    {
        var escenario = new Escenario();
        var queda = Macro("Acuse de recibo");
        var borrada = Macro("Documento ilegible");
        escenario.Macros.AddRange([queda, borrada]);
        var (cut, mediador) = Renderizar(escenario);

        await EliminarFila(cut, "Documento ilegible");

        mediador.Enviados.OfType<EliminarMacroCommand>().Should().BeEmpty("eliminar pasa por la confirmación");
        cut.Markup.Should().Contain("¿Eliminar la macro «Documento ilegible»?");
        cut.Markup.Should().Contain("no se puede deshacer desde la aplicación");
        cut.Markup.Should().NotContain("recuperarla",
            "Auditoría no tiene restauración para macros: prometerla era falso");

        await cut.Find("button.boton-destructivo").ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<EliminarMacroCommand>().Should().ContainSingle()
            .Which.Should().Be(new EliminarMacroCommand(borrada.Id, UsuarioActual));
        TitulosFilas(cut).Should().Equal(["Acuse de recibo"]);
    }
}

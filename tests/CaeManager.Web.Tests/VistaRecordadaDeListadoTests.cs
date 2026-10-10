using Bunit;
using CaeManager.Application.Configuracion.Commands.GuardarVistaRecordada;
using CaeManager.Application.Configuracion.Commands.OlvidarVistaRecordada;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Domain.Common;
using CaeManager.Web.Components;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La pieza compartida de la vista recordada (<see cref="VistaRecordadaDeListado"/>) con su enlace
/// «Restablecer vista» en <see cref="BarraFiltros"/>, sin ninguna página: cuándo restaura, cuándo manda
/// la URL, cuándo escribe (una vez por ráfaga, con el reloj en la mano), cuándo olvida, qué hace con lo
/// pendiente al salir y que ningún fallo llega al usuario. Lo que cada listado hace con la vista que
/// recibe se prueba en <c>VistaRecordadaEnListadosTests</c>.
/// </summary>
public class VistaRecordadaDeListadoTests : BunitContext
{
    private static readonly IReadOnlyList<string> ListaBlanca = ["cliente", "q", "estado", "agrupar", "orden"];

    private readonly MediadorDeVista _mediador = new();
    private readonly RelojManual _reloj = new();
    private readonly ConexionVistaRecordada _conexion = new();
    private readonly List<IReadOnlyDictionary<string, string?>> _aplicadas = [];
    private int _navegaciones;

    public VistaRecordadaDeListadoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton<TimeProvider>(_reloj);
        Navegacion.LocationChanged += (_, _) => _navegaciones++;
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private ToastService Toasts => Services.GetRequiredService<ToastService>();

    // ------------------------------------------------------------ dobles

    private sealed class MediadorDeVista : IMediator
    {
        public List<object> Enviadas { get; } = [];
        public string? Recordada { get; set; }
        public Func<Task<string?>>? Lectura { get; set; }
        public Func<Result> Escritura { get; set; } = Result.Exito;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return request switch
            {
                ObtenerVistaRecordadaQuery => (Task<TResponse>)(object)(Lectura?.Invoke() ?? Task.FromResult(Recordada)),
                GuardarVistaRecordadaCommand or OlvidarVistaRecordadaCommand => Task.FromResult((TResponse)(object)Escritura()),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    /// <summary>Un reloj que solo avanza cuando el test lo dice: el rebote se mide sin esperas reales.</summary>
    private sealed class RelojManual : TimeProvider
    {
        private readonly List<Temporizador> _temporizadores = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var temporizador = new Temporizador(callback, state, dueTime);
            lock (_temporizadores)
                _temporizadores.Add(temporizador);
            return temporizador;
        }

        public void Avanzar(TimeSpan cuanto)
        {
            Temporizador[] vivos;
            lock (_temporizadores)
                vivos = [.. _temporizadores];
            foreach (var temporizador in vivos)
                temporizador.Avanzar(cuanto);
        }

        private sealed class Temporizador(TimerCallback callback, object? state, TimeSpan vence) : ITimer
        {
            private TimeSpan _queda = vence;
            private bool _terminado;

            public void Avanzar(TimeSpan cuanto)
            {
                if (_terminado)
                    return;

                _queda -= cuanto;
                if (_queda > TimeSpan.Zero)
                    return;

                _terminado = true;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _queda = dueTime;
                return true;
            }

            public void Dispose() => _terminado = true;

            public ValueTask DisposeAsync()
            {
                _terminado = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Lo que hace un listado: la barra con el enlace y, junto a ella, la pieza.</summary>
    private sealed class Listado : ComponentBase
    {
        [Parameter] public ConexionVistaRecordada Conexion { get; set; } = default!;
        [Parameter] public IReadOnlyList<string> Contexto { get; set; } = [];
        [Parameter] public bool PiezaMontada { get; set; } = true;
        [Parameter] public Func<IReadOnlyDictionary<string, string?>, Task> AlAplicar { get; set; } = default!;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<BarraFiltros>(0);
            builder.AddComponentParameter(1, nameof(BarraFiltros.Pastillas), (RenderFragment)(_ => { }));
            builder.AddComponentParameter(2, nameof(BarraFiltros.VistaRecordada), Conexion);
            builder.CloseComponent();

            if (!PiezaMontada)
                return;

            builder.OpenComponent<VistaRecordadaDeListado>(10);
            builder.AddComponentParameter(11, nameof(VistaRecordadaDeListado.Conexion), Conexion);
            builder.AddComponentParameter(12, nameof(VistaRecordadaDeListado.Pantalla), "Vehiculos");
            builder.AddComponentParameter(13, nameof(VistaRecordadaDeListado.ParametrosDeVista), ListaBlanca);
            builder.AddComponentParameter(14, nameof(VistaRecordadaDeListado.ParametrosDeContexto), Contexto);
            builder.AddComponentParameter(15, nameof(VistaRecordadaDeListado.OnAplicar),
                EventCallback.Factory.Create<IReadOnlyDictionary<string, string?>>(this, AlAplicar));
            builder.AddComponentParameter(16, nameof(VistaRecordadaDeListado.AlCambiar), EventCallback.Factory.Create(this, StateHasChanged));
            builder.CloseComponent();
        }
    }

    // ------------------------------------------------------------ ayudantes

    /// <summary>La página aplica como aplican los listados: toda la vista a la URL, en una navegación.</summary>
    private Task AplicarComoUnListado(IReadOnlyDictionary<string, string?> vista)
    {
        _aplicadas.Add(vista);
        Navegacion.ActualizarFiltrosEnUrl(vista);
        return Task.CompletedTask;
    }

    private IRenderedComponent<Listado> Montar(
        string ruta, IReadOnlyList<string>? contexto = null, Func<IReadOnlyDictionary<string, string?>, Task>? alAplicar = null)
    {
        Navegacion.NavigateTo(ruta);
        _navegaciones = 0;
        return Render<Listado>(p => p
            .Add(l => l.Conexion, _conexion)
            .Add(l => l.Contexto, contexto ?? [])
            .Add(l => l.AlAplicar, alAplicar ?? AplicarComoUnListado));
    }

    private string Consulta => new Uri(Navegacion.Uri).Query;

    private IReadOnlyList<object> Escrituras =>
        _mediador.Enviadas.Where(e => e is GuardarVistaRecordadaCommand or OlvidarVistaRecordadaCommand).ToList();

    /// <summary>Vence el rebote y deja correr lo que haya quedado en cola.</summary>
    private async Task VencerElReboteAsync(IRenderedComponent<Listado> cut)
    {
        _reloj.Avanzar(VistaRecordadaDeListado.Rebote);
        await cut.InvokeAsync(() => { });
    }

    private static AngleSharp.Dom.IElement? Restablecer(IRenderedComponent<Listado> cut) =>
        cut.FindAll("button.restablecer-vista-barra").SingleOrDefault();

    // ------------------------------------------------------------ restaurar

    [Fact]
    public async Task Sin_parametros_restaura_la_vista_recordada_en_una_navegacion_y_no_escribe()
    {
        _mediador.Recordada = """{"estado":"Vencido","orden":"matricula-desc","intruso":"x"}""";

        var cut = Montar("/vehiculos");

        var vista = _aplicadas.Should().ContainSingle("se restaura una vez").Subject;
        vista.Keys.Should().BeEquivalentTo(ListaBlanca, "la página recibe la vista entera: un valor por parámetro de la lista blanca, y nada de fuera");
        vista["estado"].Should().Be("Vencido");
        vista["orden"].Should().Be("matricula-desc");
        vista["q"].Should().BeNull("lo que la vista recordada no trae se quita");
        _navegaciones.Should().Be(1, "restaurar escribe la URL en una sola navegación");
        Consulta.Should().Be("?estado=Vencido&orden=matricula-desc");

        await VencerElReboteAsync(cut);
        Escrituras.Should().BeEmpty("restaurar no es un cambio del usuario");
    }

    [Theory]
    [InlineData("/vehiculos?estado=Vigente")]
    [InlineData("/vehiculos?accion=crear")]
    [InlineData("/vehiculos?VehiculoId=5f0c0e0a-0000-0000-0000-000000000001")]
    public async Task Con_cualquier_parametro_manda_la_url_y_no_se_restaura_ni_se_lee(string ruta)
    {
        _mediador.Recordada = """{"estado":"Vencido"}""";

        var cut = Montar(ruta);
        await VencerElReboteAsync(cut);

        _aplicadas.Should().BeEmpty();
        _navegaciones.Should().Be(0);
        _mediador.Enviadas.Should().BeEmpty("con la URL mandando no hace falta ni leer lo recordado, y llegar no es cambiar la vista");
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("[1,2]")]
    [InlineData("""{"intruso":"x"}""")]
    [InlineData("""{"estado":7}""")]
    public async Task Una_vista_recordada_ilegible_o_sin_nada_de_la_lista_blanca_se_ignora(string recordada)
    {
        _mediador.Recordada = recordada;

        var cut = Montar("/vehiculos");
        await VencerElReboteAsync(cut);

        _aplicadas.Should().BeEmpty();
        _navegaciones.Should().Be(0);
        Escrituras.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_leer_la_vista_recordada_falla_la_pagina_sigue_sin_ella()
    {
        _mediador.Lectura = () => throw new InvalidOperationException("sin base");

        var cut = Montar("/vehiculos");
        await VencerElReboteAsync(cut);

        _aplicadas.Should().BeEmpty();
        Toasts.Mensajes.Should().BeEmpty("que no se pueda recordar la vista no se le cuenta al usuario");
    }

    [Fact]
    public async Task Si_el_usuario_cambia_la_vista_mientras_se_lee_lo_recordado_no_se_le_pisa()
    {
        var lectura = new TaskCompletionSource<string?>();
        _mediador.Lectura = () => lectura.Task;
        var cut = Montar("/vehiculos");

        Navegacion.NavigateTo("/vehiculos?q=grua");
        await cut.InvokeAsync(() => lectura.SetResult("""{"estado":"Vencido"}"""));

        _aplicadas.Should().BeEmpty("el usuario ya actuó");
        Consulta.Should().Be("?q=grua");
    }

    [Fact]
    public async Task Al_remontarse_la_pieza_en_la_misma_visita_no_vuelve_a_restaurar()
    {
        _mediador.Recordada = """{"estado":"Vencido"}""";
        var cut = Montar("/vehiculos", alAplicar: v => { _aplicadas.Add(v); return Task.CompletedTask; });
        _aplicadas.Should().ContainSingle();

        cut.Render(p => p.Add(l => l.PiezaMontada, false));
        cut.Render(p => p.Add(l => l.PiezaMontada, true));

        _aplicadas.Should().ContainSingle("un «Quitar filtros» seguido de un remontaje no puede devolver lo recordado");
        _mediador.Enviadas.OfType<ObtenerVistaRecordadaQuery>().Should().ContainSingle();
    }

    // ------------------------------------------------------------ recordar

    [Fact]
    public async Task Una_rafaga_de_cambios_es_una_sola_escritura_tras_el_rebote_con_la_ultima_vista()
    {
        var cut = Montar("/vehiculos");

        Navegacion.NavigateTo("/vehiculos?estado=Vencido");
        Navegacion.NavigateTo("/vehiculos?estado=Vencido&q=g");
        _reloj.Avanzar(VistaRecordadaDeListado.Rebote - TimeSpan.FromMilliseconds(1));
        Navegacion.NavigateTo("/vehiculos?estado=Vencido&q=grua&orden=matricula&accion=crear");
        _reloj.Avanzar(VistaRecordadaDeListado.Rebote - TimeSpan.FromMilliseconds(1));
        await cut.InvokeAsync(() => { });

        Escrituras.Should().BeEmpty("mientras siguen llegando cambios no se escribe");

        await VencerElReboteAsync(cut);

        var guardado = Escrituras.Should().ContainSingle("una escritura por ráfaga").Which.Should().BeOfType<GuardarVistaRecordadaCommand>().Subject;
        guardado.Pantalla.Should().Be("Vehiculos");
        guardado.ValoresJson.Should().Be("""{"q":"grua","estado":"Vencido","orden":"matricula"}""",
            "se guarda la lista blanca presente en la URL, en el orden de la lista, y nada más");

        await VencerElReboteAsync(cut);
        Escrituras.Should().ContainSingle();
    }

    [Fact]
    public async Task Volver_a_la_vista_de_inicio_olvida_lo_recordado_en_vez_de_guardar_una_vista_vacia()
    {
        var cut = Montar("/vehiculos?estado=Vencido");

        Navegacion.NavigateTo("/vehiculos");
        await VencerElReboteAsync(cut);

        Escrituras.Should().ContainSingle().Which.Should().BeEquivalentTo(new OlvidarVistaRecordadaCommand("Vehiculos"));
    }

    [Fact]
    public async Task Si_la_vista_no_cambia_no_se_escribe()
    {
        _mediador.Recordada = """{"estado":"Vencido"}""";
        var cut = Montar("/vehiculos");

        // Ida y vuelta dentro del rebote, y un parámetro que no es de vista.
        Navegacion.NavigateTo("/vehiculos?estado=Vigente");
        Navegacion.NavigateTo("/vehiculos?estado=Vencido");
        Navegacion.NavigateTo("/vehiculos?estado=Vencido&accion=crear");
        await VencerElReboteAsync(cut);

        Escrituras.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_la_pagina_descarta_un_valor_recordado_que_ya_no_vale_lo_recordado_se_corrige_una_vez()
    {
        _mediador.Recordada = """{"estado":"YaNoExiste","q":"grua"}""";
        var cut = Montar("/vehiculos", alAplicar: vista =>
        {
            // Como una página: el estado no pasa su validación de la URL y se queda fuera.
            Navegacion.ActualizarFiltrosEnUrl(new Dictionary<string, string?> { ["q"] = vista["q"], ["estado"] = null });
            return Task.CompletedTask;
        });

        await VencerElReboteAsync(cut);

        Escrituras.Should().ContainSingle().Which.Should().BeEquivalentTo(new GuardarVistaRecordadaCommand("Vehiculos", """{"q":"grua"}"""));
    }

    [Fact]
    public async Task Que_el_guardado_se_rechace_o_lance_no_llega_al_usuario()
    {
        var cut = Montar("/vehiculos");

        // Bajo una Sesión Privilegiada el comando de autoservicio se rechaza.
        _mediador.Escritura = () => Result.Fallo(Error.Crear("Autoservicio.SesionPrivilegiada", "No disponible en una Sesión Privilegiada."));
        Navegacion.NavigateTo("/vehiculos?estado=Vencido");
        await VencerElReboteAsync(cut);

        _mediador.Escritura = () => throw new InvalidOperationException("sin base");
        // Tras un await, el despachador puede seguir cerrando el trabajo anterior: una navegación suelta se
        // encolaría y el reloj avanzaría antes de que existiera su temporizador (rojo intermitente bajo carga,
        // medido 2026-10-10). Dentro del despachador y esperada, el cambio de dirección ya ocurrió al seguir.
        await cut.InvokeAsync(() => Navegacion.NavigateTo("/vehiculos?estado=Vigente"));
        await VencerElReboteAsync(cut);

        Escrituras.Should().HaveCount(2, "se intentó las dos veces");
        Toasts.Mensajes.Should().BeEmpty("ni aviso ni excepción: recordar la vista es una comodidad");
    }

    // ------------------------------------------------------------ al salir

    [Fact]
    public async Task Al_salir_a_otra_pantalla_lo_pendiente_se_escribe_en_el_acto()
    {
        var cut = Montar("/vehiculos");
        Navegacion.NavigateTo("/vehiculos?estado=Vencido");

        Navegacion.NavigateTo("/centros?estado=Bloqueo");
        await cut.InvokeAsync(() => { });

        Escrituras.Should().ContainSingle("sin esperar al rebote").Which.Should()
            .BeEquivalentTo(new GuardarVistaRecordadaCommand("Vehiculos", """{"estado":"Vencido"}"""), "la vista es la de esta pantalla, no la de la siguiente");

        await VencerElReboteAsync(cut);
        Escrituras.Should().ContainSingle("el temporizador ya no escribe otra vez");
    }

    [Fact]
    public async Task Si_la_pieza_se_retira_sin_salir_de_la_pantalla_el_temporizador_no_escribe()
    {
        var cut = Montar("/vehiculos");
        Navegacion.NavigateTo("/vehiculos?estado=Vencido");

        // Se cierra la pestaña o cae el circuito: la pieza se retira y nadie la vuelve a montar.
        cut.Render(p => p.Add(l => l.PiezaMontada, false));
        await VencerElReboteAsync(cut);

        Escrituras.Should().BeEmpty("no queda ninguna tarea viva detrás de un componente retirado");
    }

    [Fact]
    public async Task Si_la_pieza_se_remonta_en_la_misma_visita_retoma_lo_pendiente()
    {
        var cut = Montar("/vehiculos");
        Navegacion.NavigateTo("/vehiculos?estado=Vencido");

        // La rama que la contiene se repinta (una carga, un cambio de pestaña) y vuelve.
        cut.Render(p => p.Add(l => l.PiezaMontada, false));
        cut.Render(p => p.Add(l => l.PiezaMontada, true));
        Escrituras.Should().BeEmpty("retomar no es escribir antes de tiempo");

        await VencerElReboteAsync(cut);

        Escrituras.Should().ContainSingle().Which.Should().BeEquivalentTo(new GuardarVistaRecordadaCommand("Vehiculos", """{"estado":"Vencido"}"""));
    }

    // ------------------------------------------------------------ «Restablecer vista»

    [Theory]
    [InlineData("/vehiculos", false)]
    [InlineData("/vehiculos?accion=crear", false)]
    [InlineData("/vehiculos?estado=Vencido", true)]
    [InlineData("/vehiculos?agrupar=no", true)]
    [InlineData("/vehiculos?orden=matricula", true)]
    public void Restablecer_vista_solo_se_ofrece_cuando_la_vista_difiere_de_la_de_inicio(string ruta, bool seOfrece)
    {
        var cut = Montar(ruta);

        (Restablecer(cut) is not null).Should().Be(seOfrece);
    }

    [Fact]
    public async Task Restablecer_vista_aparece_y_desaparece_con_los_cambios_de_la_url()
    {
        var cut = Montar("/vehiculos");
        Restablecer(cut).Should().BeNull();

        Navegacion.NavigateTo("/vehiculos?orden=matricula");
        cut.WaitForAssertion(() => Restablecer(cut).Should().NotBeNull());
        Restablecer(cut)!.TextContent.Trim().Should().Be("Restablecer vista");

        Navegacion.NavigateTo("/vehiculos");
        cut.WaitForAssertion(() => Restablecer(cut).Should().BeNull());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Restablecer_vista_quita_todos_los_parametros_de_vista_en_una_navegacion_recarga_y_olvida()
    {
        var cut = Montar("/vehiculos?q=grua&estado=Vencido&agrupar=no&orden=matricula-desc&accion=crear");

        await Restablecer(cut)!.ClickAsync(new MouseEventArgs());

        var vista = _aplicadas.Should().ContainSingle("la página aplica la vista de inicio: es su recarga").Subject;
        vista.Values.Should().OnlyContain(v => v == null, "también el orden y la agrupación, que «Quitar filtros» no quita");
        vista.Keys.Should().BeEquivalentTo(ListaBlanca);
        _navegaciones.Should().Be(1);
        Consulta.Should().Be("?accion=crear", "lo que no es de vista no se toca");
        Escrituras.Should().ContainSingle("sin esperar al rebote").Which.Should().BeEquivalentTo(new OlvidarVistaRecordadaCommand("Vehiculos"));
        Restablecer(cut).Should().BeNull();
        Toasts.Mensajes.Should().ContainSingle().Which.Mensaje.Should().Be("Vista de fábrica restablecida.");

        await VencerElReboteAsync(cut);
        Escrituras.Should().ContainSingle("la navegación del restablecimiento no programa otra escritura");
    }

    [Fact]
    public async Task Restablecer_vista_con_un_cambio_pendiente_no_lo_escribe_despues()
    {
        var cut = Montar("/vehiculos");
        Navegacion.NavigateTo("/vehiculos?estado=Vencido");
        cut.WaitForAssertion(() => Restablecer(cut).Should().NotBeNull());

        await Restablecer(cut)!.ClickAsync(new MouseEventArgs());
        await VencerElReboteAsync(cut);

        Escrituras.Should().ContainSingle().Which.Should().BeOfType<OlvidarVistaRecordadaCommand>();
    }

    [Fact]
    public async Task Si_la_pagina_no_aplica_el_restablecimiento_lo_recordado_no_se_olvida()
    {
        // Proyectos pregunta antes si hay algo a medias; con «Seguir editando» la vista se queda como estaba.
        var cut = Montar("/vehiculos?estado=Vencido", alAplicar: _ => Task.CompletedTask);

        await Restablecer(cut)!.ClickAsync(new MouseEventArgs());
        await VencerElReboteAsync(cut);

        Escrituras.Should().BeEmpty();
        Toasts.Mensajes.Should().BeEmpty();
        Restablecer(cut).Should().NotBeNull();
    }

    // ------------------------------------------------------------ parámetros de contexto (Proyectos)

    [Fact]
    public async Task Un_parametro_de_contexto_se_recuerda_y_se_restaura_pero_no_es_desviacion_ni_lo_quita_Restablecer()
    {
        const string cliente = "11111111-1111-1111-1111-111111111111";
        _mediador.Recordada = $$"""{"cliente":"{{cliente}}"}""";

        var cut = Montar("/proyectos", contexto: ["cliente"]);

        Consulta.Should().Be($"?cliente={cliente}", "el Cliente empresarial recordado se restaura");
        Restablecer(cut).Should().BeNull("con solo el Cliente empresarial la vista es la de inicio");

        Navegacion.NavigateTo($"/proyectos?cliente={cliente}&estado=Cerrado");
        cut.WaitForAssertion(() => Restablecer(cut).Should().NotBeNull());
        await Restablecer(cut)!.ClickAsync(new MouseEventArgs());

        Consulta.Should().Be($"?cliente={cliente}", "«Restablecer vista» no quita el Cliente empresarial");
        Escrituras.Should().ContainSingle().Which.Should()
            .BeEquivalentTo(new GuardarVistaRecordadaCommand("Vehiculos", $$"""{"cliente":"{{cliente}}"}"""), "y lo sigue recordando");
    }

    [Fact]
    public async Task Un_parametro_de_contexto_solo_tambien_se_recuerda()
    {
        var cut = Montar("/proyectos", contexto: ["cliente"]);

        Navegacion.NavigateTo("/proyectos?cliente=22222222-2222-2222-2222-222222222222");
        await VencerElReboteAsync(cut);

        Escrituras.Should().ContainSingle().Which.Should().BeOfType<GuardarVistaRecordadaCommand>();
    }
}

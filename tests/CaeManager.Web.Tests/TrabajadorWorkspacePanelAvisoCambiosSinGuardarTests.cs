using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Trabajadores.Commands.EditarTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Trabajadores.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b (lote 1): la edición en línea de Información del panel de Trabajador pinta su
/// propio aviso de cambios sin guardar. Vive en el panel, no en la pestaña: cambiar de
/// pestaña no la pierde, así que el aviso va fuera de <c>Pestanas</c> y no pregunta.
/// </summary>
public class TrabajadorWorkspacePanelAvisoCambiosSinGuardarTests : BunitContext
{
    private readonly List<string> _pestanasPedidas = [];
    private MediatorFalso _mediador = default!;

    public TrabajadorWorkspacePanelAvisoCambiosSinGuardarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private sealed class MediatorFalso(TrabajadorDetalleDto detalle) : IMediator
    {
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            object? respuesta = request switch
            {
                ObtenerTrabajadorPorIdQuery => detalle,
                EditarTrabajadorCommand => Result.Exito(),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private async Task<IRenderedComponent<TrabajadorWorkspacePanel>> EditarInformacionAsync(bool sinFechaDeNacimiento = false)
    {
        var id = Guid.NewGuid();
        var detalle = new TrabajadorDetalleDto(
            id, Guid.NewGuid(), null, "Refrielectric S.A.", "Marco", "Vila", "12884021K",
            sinFechaDeNacimiento ? null : new DateOnly(1990, 1, 1), null, null, null, null, null, Guid.NewGuid());
        _mediador = new MediatorFalso(detalle);
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();

        var cut = Render<TrabajadorWorkspacePanel>(p => p
            .Add(x => x.EntidadId, id)
            .Add(x => x.PestanaActiva, "informacion")
            .Add(x => x.PestanaActivaChanged, (string v) => _pestanasPedidas.Add(v)));
        await cut.FindAll("button").Single(b => b.GetAttribute("aria-label") == "Editar información del trabajador")
            .ClickAsync(new MouseEventArgs());
        return cut;
    }

    private static IElement Control(IRenderedComponent<TrabajadorWorkspacePanel> cut, string etiqueta) =>
        cut.Find("#" + cut.FindAll("label").Single(l => l.TextContent.Trim() == etiqueta).GetAttribute("for"));

    [Fact]
    public async Task La_informacion_editada_pregunta_al_salir()
    {
        var cut = await EditarInformacionAsync();
        await Control(cut, "Apellidos").InputAsync(new ChangeEventArgs { Value = "Vila Soto" });

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    private static Task GuardarAsync(IRenderedComponent<TrabajadorWorkspacePanel> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

    private static Task EscribirFechaAsync(IRenderedComponent<TrabajadorWorkspacePanel> cut, string texto) =>
        cut.Find("input[data-campo-fecha]").InputAsync(new ChangeEventArgs { Value = texto });

    [Fact]
    public async Task Una_fecha_de_nacimiento_a_medias_no_edita_el_trabajador_sin_fecha()
    {
        var cut = await EditarInformacionAsync();

        await EscribirFechaAsync(cut, "15/03");
        await GuardarAsync(cut);

        _mediador.Enviadas.Should().NotContain(o => o.GetType().Name == "EditarTrabajadorCommand",
            "el texto sin fecha entera llega como «» y borraría la fecha de nacimiento sin que nadie lo decidiera");
        cut.Markup.Should().Contain("no es válida");

        await EscribirFechaAsync(cut, "15/03/2030");
        await GuardarAsync(cut);

        _mediador.Enviadas.OfType<EditarTrabajadorCommand>().Should().ContainSingle("control positivo: con la fecha entera sí se envía")
            .Which.FechaNacimiento.Should().Be(new DateOnly(2030, 3, 15));
    }

    // Ronda 4 de Codex: el campo vive en la pestaña Información; al volver de otra pestaña nacía sin marca, avisaba false y
    // Guardar enviaba EditarTrabajadorCommand sin la fecha de nacimiento (la borraba).
    [Fact]
    public async Task Una_fecha_de_nacimiento_a_medias_sigue_marcada_al_volver_de_otra_pestana_y_no_se_borra()
    {
        var cut = await EditarInformacionAsync();
        await EscribirFechaAsync(cut, "15/03");

        cut.Render(p => p.Add(x => x.PestanaActiva, "vehiculos"));
        cut.FindAll("input[data-campo-fecha]").Should().BeEmpty("control: en otra pestaña el campo no existe");
        cut.Render(p => p.Add(x => x.PestanaActiva, "informacion"));

        cut.WaitForAssertion(() => cut.Find("input[data-campo-fecha]").GetAttribute("aria-invalid").Should().Be("true",
            "el campo recreado nace con la marca y su aviso a la vista"));
        await GuardarAsync(cut);

        _mediador.Enviadas.Should().NotContain(o => o.GetType().Name == "EditarTrabajadorCommand",
            "volver de otra pestaña no convierte la fecha a medias en «sin fecha»");
        cut.Markup.Should().Contain("no es válida");

        await EscribirFechaAsync(cut, "15/03/2030");
        await GuardarAsync(cut);
        _mediador.Enviadas.OfType<EditarTrabajadorCommand>().Should().ContainSingle("control positivo: corregida, se envía")
            .Which.FechaNacimiento.Should().Be(new DateOnly(2030, 3, 15));
    }

    // El aviso de un «Guardar» rechazado deja de ser cierto cuando la fecha vuelve a ser entera o se vacía; con la fecha vacía al
    // empezar, borrar el texto no cambia el valor («»), así que solo el cambio de la bandera puede retirarlo.
    [Fact]
    public async Task El_aviso_de_fecha_no_valida_se_retira_al_corregir_o_vaciar_la_fecha()
    {
        var cut = await EditarInformacionAsync(sinFechaDeNacimiento: true);
        await EscribirFechaAsync(cut, "15/03");
        await GuardarAsync(cut);
        cut.Markup.Should().Contain("no es válida", "control: el intento rechazado avisa");

        await EscribirFechaAsync(cut, "15/03/2030");
        cut.Markup.Should().NotContain("no es válida", "la fecha ya es entera y el aviso del intento anterior no es cierto");

        await EscribirFechaAsync(cut, "15/03");
        await GuardarAsync(cut);
        cut.Markup.Should().Contain("no es válida", "control: vuelve a avisar con otro texto a medias");

        await EscribirFechaAsync(cut, "");
        cut.Markup.Should().NotContain("no es válida", "vaciar el campo también deja el texto a medias");
    }

    // La secuencia directa, sin pasar antes por una fecha entera: el valor es «» antes y después de borrar el texto, así que
    // solo el cambio de la bandera puede retirar el aviso.
    [Fact]
    public async Task El_aviso_de_fecha_no_valida_se_retira_al_vaciar_el_texto_sin_haber_pasado_por_una_fecha_entera()
    {
        var cut = await EditarInformacionAsync(sinFechaDeNacimiento: true);
        await EscribirFechaAsync(cut, "15/03");
        await GuardarAsync(cut);
        cut.Markup.Should().Contain("no es válida", "control: el intento rechazado avisa");

        await EscribirFechaAsync(cut, "");

        cut.Markup.Should().NotContain("no es válida");
    }

    // Con la fecha de nacimiento vacía al empezar, el campo entrega «» con el texto a medias: el valor no cambia y solo la
    // bandera sabe que hay algo sin guardar.
    [Fact]
    public async Task Una_fecha_a_medias_cuenta_como_cambio_sin_guardar_aunque_el_valor_no_cambie()
    {
        var cut = await EditarInformacionAsync(sinFechaDeNacimiento: true);
        await EscribirFechaAsync(cut, "15/03");

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Borrar_la_fecha_a_medias_vuelve_a_no_haber_cambios_sin_guardar()
    {
        var cut = await EditarInformacionAsync(sinFechaDeNacimiento: true);
        await EscribirFechaAsync(cut, "15/03");
        await EscribirFechaAsync(cut, "");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "borrado el texto a medias, no queda nada que descartar");
    }

    [Fact]
    public async Task Abrir_la_edicion_sin_tocar_nada_no_pregunta()
    {
        var cut = await EditarInformacionAsync();

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "los valores de partida no son un cambio de quien edita");
    }

    [Fact]
    public async Task Cancelar_tras_editar_no_pregunta()
    {
        var cut = await EditarInformacionAsync();
        await Control(cut, "Apellidos").InputAsync(new ChangeEventArgs { Value = "Vila Soto" });
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "«Cancelar» es una decisión explícita");
    }

    [Fact]
    public async Task Cambiar_de_pestana_con_la_edicion_a_medias_no_pregunta()
    {
        var cut = await EditarInformacionAsync();
        await Control(cut, "Apellidos").InputAsync(new ChangeEventArgs { Value = "Vila Soto" });

        // Se comprueba antes de esperar el clic: si preguntara, el clic esperaría la respuesta.
        var clic = cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Historial").ClickAsync(new MouseEventArgs());

        cut.FindAll(".modal-contenido").Should().BeEmpty("la edición vive en el panel y sobrevive al cambio de pestaña");
        _pestanasPedidas.Should().Equal(["historial"]);
        await clic;
    }
}

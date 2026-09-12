using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Vehiculos.Commands.EditarVehiculo;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculoPorId;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Vehiculos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Carreras A→B y guardas de reentrada de <see cref="VehiculoWorkspacePanel"/> y
/// <see cref="VehiculoPreviewDrawer"/> — mismo patrón que
/// <c>Empresa360Gen2Tests</c>. Las esperas retenidas se liberan desde
/// InvokeAsync para observar su continuación.
/// </summary>
public class Vehiculo360Gen2Tests : BunitContext
{
    public Vehiculo360Gen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediadorFalso : IMediator
    {
        public Dictionary<Guid, VehiculoDetalleDto?> Detalles { get; } = [];
        public Result Edicion { get; set; } = Result.Exito();
        public Func<object, Task?>? Retener { get; set; }
        public Func<object, Exception?>? Fallar { get; set; }
        public List<object> Enviadas { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public async Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request); Tokens.Add(cancellationToken);
            if (Retener?.Invoke(request) is { } espera) await espera;
            if (Fallar?.Invoke(request) is { } error) throw error;
            // El switch tiene ramas de tipos distintos, asi que se unifica en
            // object? y se convierte una sola vez: sin esto, CS0029 por rama.
            object? valor = request switch
            {
                ObtenerVehiculoPorIdQuery q => Detalles.GetValueOrDefault(q.Id),
                EditarVehiculoCommand => Edicion,
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return (T)valor!;
        }
        public Task Send<T>(T request, CancellationToken ct = default) where T : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken ct = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> r, CancellationToken ct = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task Publish(object n, CancellationToken ct = default) => Task.CompletedTask;
        public Task Publish<T>(T n, CancellationToken ct = default) where T : INotification => Task.CompletedTask;
    }

    private static VehiculoDetalleDto Detalle(Guid id, string nombre) =>
        new(id, Guid.NewGuid(), null, "Montajes Ebro S.L.", nombre, "Transit", "1234-ABC", Guid.NewGuid());

    private MediadorFalso Registrar(MediadorFalso m)
    {
        Services.AddScoped<IMediator>(_ => m);
        Services.AddScoped<ToastService>();
        return m;
    }

    private IRenderedComponent<VehiculoWorkspacePanel> RenderizarPanel(Guid id, string pestana = "informacion") =>
        Render<VehiculoWorkspacePanel>(p => p.Add(x => x.EntidadId, id).Add(x => x.PestanaActiva, pestana)
            .Add(x => x.PestanaActivaChanged, EventCallback.Factory.Create<string>(this, _ => { })));

    private static IElement Boton(IRenderedComponent<VehiculoWorkspacePanel> cut, string texto) =>
        cut.FindAll("button").Where(x => x.TextContent.Trim() == texto).Should().ContainSingle().Subject;

    // El botón de editar es solo icono (sin texto visible): el disparador
    // real es su aria-label, no su TextContent.
    private static IElement BotonEditar(IRenderedComponent<VehiculoWorkspacePanel> cut) =>
        cut.FindAll("button").Where(x => x.GetAttribute("aria-label") == "Editar información del vehículo").Should().ContainSingle().Subject;

    private static IElement Control(IRenderedComponent<VehiculoWorkspacePanel> cut, string etiqueta)
    {
        var id = cut.FindAll("label").Where(x => x.TextContent.Trim() == etiqueta).Should().ContainSingle().Subject.GetAttribute("for");
        return cut.Find($"#{id}");
    }

    private IReadOnlyList<ToastMensaje> Toasts => Services.GetRequiredService<ToastService>().Mensajes;

    // --- VehiculoWorkspacePanel -----------------------------------------------------------------

    [Fact]
    public async Task Retirar_el_panel_cancela_todas_sus_consultas()
    {
        var id = Guid.NewGuid();
        var m = Registrar(new MediadorFalso());
        m.Detalles[id] = Detalle(id, "Furgoneta de obra");
        var cut = RenderizarPanel(id);

        m.Tokens.Should().ContainSingle(x => x.CanBeCanceled && !x.IsCancellationRequested);
        await DisposeComponentsAsync();
        m.Tokens.Should().OnlyContain(x => x.IsCancellationRequested, "DisposeComponentsAsync retira el panel y cancela su ciclo");
    }

    [Fact]
    public async Task La_respuesta_tardia_de_la_cabecera_de_A_no_pinta_encima_de_B()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var espera = new TaskCompletionSource();
        var m = Registrar(new MediadorFalso { Retener = x => x is ObtenerVehiculoPorIdQuery q && q.Id == a ? espera.Task : null });
        m.Detalles[a] = Detalle(a, "Furgoneta de obra"); m.Detalles[b] = Detalle(b, "Camión grúa");
        var cut = RenderizarPanel(a);
        cut.FindAll(".workspace-titulo-entidad").Should().BeEmpty("la cabecera de A sigue en vuelo");

        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));
        cut.Find(".workspace-titulo-entidad").TextContent.Trim().Should().Be("Camión grúa");

        await cut.InvokeAsync(espera.SetResult);
        cut.Find(".workspace-titulo-entidad").TextContent.Trim().Should().Be("Camión grúa", "la generación de A ya no es vigente");
    }

    [Fact]
    public async Task Cambiar_de_vehiculo_cierra_el_formulario_de_edicion_que_pertenecia_al_anterior()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var m = Registrar(new MediadorFalso());
        m.Detalles[a] = Detalle(a, "Furgoneta de obra"); m.Detalles[b] = Detalle(b, "Camión grúa");
        var cut = RenderizarPanel(a);

        await BotonEditar(cut).ClickAsync(new MouseEventArgs());
        Control(cut, "Nombre");

        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));

        cut.Find(".workspace-titulo-entidad").TextContent.Trim().Should().Be("Camión grúa");
        cut.FindAll("input").Should().BeEmpty("el formulario de edición de A no puede seguir abierto sobre B");
    }

    [Fact]
    public async Task Un_guardado_de_A_en_vuelo_no_bloquea_el_guardado_de_B()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var espera = new TaskCompletionSource();
        var m = Registrar(new MediadorFalso { Retener = x => x is EditarVehiculoCommand q && q.Id == a ? espera.Task : null });
        m.Detalles[a] = Detalle(a, "Furgoneta de obra"); m.Detalles[b] = Detalle(b, "Camión grúa");
        var cut = RenderizarPanel(a);
        await BotonEditar(cut).ClickAsync(new MouseEventArgs());
        var guardarA = Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());
        m.Enviadas.OfType<EditarVehiculoCommand>().Should().ContainSingle("el guardado de A sigue retenido");

        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));
        await BotonEditar(cut).ClickAsync(new MouseEventArgs());
        var guardarB = Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());
        m.Enviadas.OfType<EditarVehiculoCommand>().Select(x => x.Id).Should().Equal([a, b], "B reinicia su guarda aunque A continúe en vuelo");

        await guardarB;
        await cut.InvokeAsync(espera.SetResult); await guardarA;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task El_fallo_tardio_al_guardar_se_anuncia_con_el_vehiculo_original(bool cambiarAB)
    {
        const string aNombre = "Furgoneta de obra"; const string bNombre = "Camión grúa"; const string motivo = "Ya existe otro vehículo con esta matrícula.";
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var espera = new TaskCompletionSource();
        var m = Registrar(new MediadorFalso
        {
            Retener = x => cambiarAB && x is EditarVehiculoCommand ? espera.Task : null,
            Edicion = Result.Fallo(Error.Crear("Vehiculo.MatriculaDuplicada", motivo))
        });
        m.Detalles[a] = Detalle(a, aNombre); m.Detalles[b] = Detalle(b, bNombre);
        var cut = RenderizarPanel(a);
        await BotonEditar(cut).ClickAsync(new MouseEventArgs());
        var guardar = Boton(cut, "Guardar").ClickAsync(new MouseEventArgs());

        if (cambiarAB)
        {
            cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));
            await cut.InvokeAsync(espera.SetResult);
        }
        await guardar;

        Toasts.Should().NotContain(x => x.Tono == TonoToast.Exito, "un Result fallido no es un guardado");
        if (!cambiarAB)
        {
            cut.FindAll(".alerta-formulario[role=alert]").Select(x => x.TextContent.Trim()).Should().Equal([motivo]);
            Toasts.Should().BeEmpty("el formulario de A ya identifica la ficha");
        }
        else
        {
            cut.Find(".workspace-titulo-entidad").TextContent.Trim().Should().Be(bNombre);
            cut.FindAll(".alerta-formulario[role=alert]").Should().BeEmpty();
            Toasts.Should().ContainSingle(x => x.Tono == TonoToast.Error && x.Mensaje.Contains(aNombre) && x.Mensaje.Contains(motivo));
        }
    }

    // --- VehiculoPreviewDrawer -------------------------------------------------------------------

    private IRenderedComponent<VehiculoPreviewDrawer> RenderizarDrawer() =>
        Render<VehiculoPreviewDrawer>(p => p.Add(x => x.Visible, false));

    [Fact]
    public async Task Reabrir_el_drawer_sobre_otro_vehiculo_no_deja_pintarse_la_respuesta_tardia_de_A()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var espera = new TaskCompletionSource();
        var m = Registrar(new MediadorFalso { Retener = x => x is ObtenerVehiculoPorIdQuery q && q.Id == a ? espera.Task : null });
        m.Detalles[a] = Detalle(a, "Furgoneta de obra"); m.Detalles[b] = Detalle(b, "Camión grúa");
        var cut = RenderizarDrawer();

        cut.Render(p => p.Add(x => x.VehiculoId, a).Add(x => x.Visible, true));
        cut.Markup.Should().NotContain("Furgoneta de obra", "la información de A sigue en vuelo");

        cut.Render(p => p.Add(x => x.VehiculoId, b).Add(x => x.Visible, true));
        cut.Markup.Should().Contain("Camión grúa");

        await cut.InvokeAsync(espera.SetResult);
        cut.Markup.Should().NotContain("Furgoneta de obra", "la generación de A ya no es vigente");
        cut.Markup.Should().Contain("Camión grúa", "B sigue en pantalla tras resolverse la respuesta tardía de A");
    }

    [Fact]
    public async Task Retirar_el_drawer_cancela_la_consulta_en_vuelo()
    {
        var id = Guid.NewGuid();
        var m = Registrar(new MediadorFalso());
        m.Detalles[id] = Detalle(id, "Furgoneta de obra");
        var cut = RenderizarDrawer();
        cut.Render(p => p.Add(x => x.VehiculoId, id).Add(x => x.Visible, true));

        m.Tokens.Should().ContainSingle(x => x.CanBeCanceled && !x.IsCancellationRequested);
        await DisposeComponentsAsync();
        m.Tokens.Should().OnlyContain(x => x.IsCancellationRequested, "DisposeComponentsAsync retira el drawer y cancela su ciclo");
    }
}

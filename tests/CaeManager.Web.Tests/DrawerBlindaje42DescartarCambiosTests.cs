using Bunit;
using CaeManager.Application.Blindaje42.Commands.SolicitarCertificacionTgss;
using CaeManager.Application.Blindaje42.Queries.ObtenerHistorialCertificacionesTgss;
using CaeManager.Domain.Blindaje42;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Blindaje42.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace CaeManager.Web.Tests;

/// <summary>
/// S3: el drawer del blindaje 42.1 tiene dos formularios (nueva solicitud y respuesta de la TGSS) y
/// no pasaba <c>HayCambios</c>: cerrarlo con la X, Escape o el fondo tiraba lo escrito sin preguntar.
/// Las fechas solo cuentan como cambio si se apartan de la que el drawer propuso (hoy); el formulario
/// de respuesta, solo mientras está abierto.
/// </summary>
public class DrawerBlindaje42DescartarCambiosTests : BunitContext
{
    private const string Pregunta = "¿Descartar cambios?";

    private readonly Guid _solicitudPendiente = Guid.NewGuid();
    private bool _visible = true;

    public DrawerBlindaje42DescartarCambiosTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<ToastService>();
        Services.AddSingleton(TimeProvider.System);
        this.ConRolDeEscritura(Roles.GestorCae);
        _mediador = new MediadorDeHistorial(
        [
            new SolicitudCertificacionTgssDto(
                _solicitudPendiente, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1), null, null,
                EstadoBlindaje42.PendienteRespuesta, null, false, null),
        ]);
        Services.AddScoped<IMediator>(_ => _mediador);
    }

    private readonly MediadorDeHistorial _mediador;

    private sealed class MediadorDeHistorial(List<SolicitudCertificacionTgssDto> historial) : IMediator
    {
        /// <summary>Si la solicitud que se registra queda la primera del historial (la más reciente) o la última (una retroactiva).</summary>
        public bool LaNuevaEsLaMasReciente { get; set; } = true;

        /// <summary>Si no es nulo, registrar una solicitud espera a que se complete: hay un guardado en curso.</summary>
        public TaskCompletionSource? SolicitudPendiente { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request switch
            {
                ObtenerHistorialCertificacionesTgssQuery => Task.FromResult((TResponse)(object)historial.ToList()),
                SolicitarCertificacionTgssCommand => RegistrarAsync<TResponse>(),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}."),
            };

        private async Task<TResponse> RegistrarAsync<TResponse>()
        {
            if (SolicitudPendiente is not null) await SolicitudPendiente.Task;
            var nueva = new SolicitudCertificacionTgssDto(
                Guid.NewGuid(), new DateOnly(2026, 9, 20), new DateOnly(2026, 10, 20), null, null,
                EstadoBlindaje42.PendienteRespuesta, null, false, null);
            if (LaNuevaEsLaMasReciente) historial.Insert(0, nueva);
            else historial.Add(nueva);
            return (TResponse)(object)Result.Exito(nueva.Id);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private IRenderedComponent<DrawerBlindaje42> Renderizar() => Render<DrawerBlindaje42>(p => p
        .Add(x => x.Visible, _visible)
        .Add(x => x.VisibleChanged, v => _visible = v)
        .Add(x => x.ClienteId, Guid.NewGuid())
        .Add(x => x.EmpresaId, Guid.NewGuid())
        .Add(x => x.EmpresaRazonSocial, "Pinturas Lauburu S.A."));

    private static bool Preguntando(IRenderedComponent<DrawerBlindaje42> cut) =>
        cut.FindAll("h2").Any(h => h.TextContent.Trim() == Pregunta);

    [Fact]
    public async Task Sin_cambios_la_X_cierra_directamente()
    {
        var cut = Renderizar();

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        _visible.Should().BeFalse();
        Preguntando(cut).Should().BeFalse();
    }

    [Fact]
    public async Task Con_observaciones_escritas_la_X_pregunta_y_seguir_editando_no_cierra()
    {
        var cut = Renderizar();
        var observaciones = cut.FindComponent<CampoTextarea>();
        await cut.InvokeAsync(() => observaciones.Instance.ValorChanged.InvokeAsync("Llamar a la TGSS el lunes"));

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        _visible.Should().BeTrue("con algo escrito se pregunta antes de cerrar");
        Preguntando(cut).Should().BeTrue();
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Seguir editando").ClickAsync(new MouseEventArgs());
        _visible.Should().BeTrue();
        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Descartar cambios").ClickAsync(new MouseEventArgs());
        _visible.Should().BeFalse("«Descartar cambios» cierra");
    }

    [Fact]
    public async Task Con_otra_fecha_de_solicitud_la_X_pregunta()
    {
        var cut = Renderizar();
        var fecha = cut.FindComponents<CampoTexto>().First(c => c.Instance.Etiqueta == "Fecha de la nueva solicitud");
        await cut.InvokeAsync(() => fecha.Instance.ValorChanged.InvokeAsync("2026-01-15"));

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        Preguntando(cut).Should().BeTrue("la fecha propuesta era hoy; otra fecha es algo que alguien escribió");
    }

    private async Task AbrirRespuestaAsync(IRenderedComponent<DrawerBlindaje42> cut) =>
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Registrar respuesta").ClickAsync(new MouseEventArgs());

    /// <summary>Abrir el formulario de respuesta sin tocar nada no es un cambio.</summary>
    [Fact]
    public async Task El_formulario_de_respuesta_recien_abierto_no_pregunta()
    {
        var cut = Renderizar();
        await AbrirRespuestaAsync(cut);

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        _visible.Should().BeFalse("abrir el formulario sin escribir nada no pregunta");
    }

    [Fact]
    public async Task Con_un_resultado_elegido_la_X_pregunta()
    {
        var cut = Renderizar();
        await AbrirRespuestaAsync(cut);
        var resultado = cut.FindComponents<CampoSelect>().Single(c => c.Instance.Etiqueta == "Resultado");
        await cut.InvokeAsync(() => resultado.Instance.ValorChanged.InvokeAsync(nameof(ResultadoCertificacionTgss.SinDescubiertos)));

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        Preguntando(cut).Should().BeTrue("con un resultado elegido hay algo que se perdería");
        _visible.Should().BeTrue();
    }

    [Fact]
    public async Task Con_otra_fecha_de_respuesta_la_X_pregunta()
    {
        var cut = Renderizar();
        await AbrirRespuestaAsync(cut);
        var fecha = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de respuesta");
        await cut.InvokeAsync(() => fecha.Instance.ValorChanged.InvokeAsync("2026-01-15"));

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        Preguntando(cut).Should().BeTrue();
    }

    [Fact]
    public async Task Con_un_justificante_cargado_la_X_pregunta()
    {
        var cut = Renderizar();
        await AbrirRespuestaAsync(cut);
        // El justificante llega por ZonaSoltarArchivo (InputFile), que bUnit no ejerce bien: se fija el estado que deja.
        typeof(DrawerBlindaje42).GetField("_evidenciaContenido", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(cut.Instance, new byte[] { 1, 2, 3 });

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        Preguntando(cut).Should().BeTrue("un archivo cargado y sin registrar se perdería");
    }

    /// <summary>
    /// Lo guardado ya no es un cambio pendiente: registrar una solicitud con una fecha retroactiva y cerrar
    /// después no pregunta (la fecha no vuelve a «hoy» al guardar).
    /// </summary>
    [Fact]
    public async Task Tras_registrar_una_solicitud_con_fecha_retroactiva_la_X_cierra_sin_preguntar()
    {
        var cut = Renderizar();
        var fecha = cut.FindComponents<CampoTexto>().First(c => c.Instance.Etiqueta == "Fecha de la nueva solicitud");
        await cut.InvokeAsync(() => fecha.Instance.ValorChanged.InvokeAsync("2026-09-20"));
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Registrar solicitud").ClickAsync(new MouseEventArgs());

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        Preguntando(cut).Should().BeFalse("la solicitud ya se guardó: no hay nada pendiente");
        _visible.Should().BeFalse();
    }

    // ------------------------------------------------ formulario de respuesta y solicitudes nuevas

    private async Task EscribirResultadoAsync(IRenderedComponent<DrawerBlindaje42> cut)
    {
        var resultado = cut.FindComponents<CampoSelect>().Single(c => c.Instance.Etiqueta == "Resultado");
        await cut.InvokeAsync(() => resultado.Instance.ValorChanged.InvokeAsync(nameof(ResultadoCertificacionTgss.SinDescubiertos)));
    }

    /// <summary>
    /// Si registrar una solicitud nueva y más reciente hace que el formulario de respuesta deje de pintarse,
    /// sus campos (aún en memoria) no son un cambio que preguntar: nadie los ve.
    /// </summary>
    [Fact]
    public async Task Un_formulario_de_respuesta_que_dejo_de_pintarse_no_cuenta_como_cambio()
    {
        _mediador.LaNuevaEsLaMasReciente = true;
        var cut = Renderizar();
        await AbrirRespuestaAsync(cut);
        await EscribirResultadoAsync(cut);
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Registrar solicitud").ClickAsync(new MouseEventArgs());
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Guardar respuesta", "control positivo: el formulario ya no se pinta");

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        Preguntando(cut).Should().BeFalse();
        _visible.Should().BeFalse();
    }

    /// <summary>
    /// Una solicitud retroactiva no desplaza a la pendiente (el historial va por fecha): su formulario de
    /// respuesta sigue a la vista, y lo escrito en él sí cuenta.
    /// </summary>
    [Fact]
    public async Task Registrar_una_solicitud_retroactiva_no_borra_la_respuesta_en_curso()
    {
        _mediador.LaNuevaEsLaMasReciente = false;
        var cut = Renderizar();
        await AbrirRespuestaAsync(cut);
        await EscribirResultadoAsync(cut);
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Registrar solicitud").ClickAsync(new MouseEventArgs());
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Guardar respuesta", "el formulario de respuesta sigue a la vista");

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        Preguntando(cut).Should().BeTrue("el resultado elegido sigue sin guardar");
    }

    /// <summary>Mientras un guardado está en curso el cierre se ignora; preguntar «¿Descartar cambios?» y luego no cerrar sería peor.</summary>
    [Fact]
    public async Task Con_un_guardado_en_curso_la_X_no_pregunta()
    {
        _mediador.SolicitudPendiente = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Renderizar();
        var observaciones = cut.FindComponent<CampoTextarea>();
        await cut.InvokeAsync(() => observaciones.Instance.ValorChanged.InvokeAsync("Observación"));
        var guardado = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Registrar solicitud").ClickAsync(new MouseEventArgs());

        try
        {
            await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

            Preguntando(cut).Should().BeFalse("con el guardado en curso no se pregunta");
            _visible.Should().BeTrue("el cierre se ignora mientras guarda");
        }
        finally
        {
            _mediador.SolicitudPendiente.TrySetResult();
            await guardado.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}

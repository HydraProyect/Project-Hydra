using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Retencion.Commands;
using CaeManager.Application.Retencion.Queries;
using CaeManager.Domain.Common;
using CaeManager.Domain.Retencion;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RetencionPage = CaeManager.Web.Features.Retencion.Pages.Retencion;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pantalla de Retención (/retencion) tras alinearla con el mockup Gen 2.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> qué peticiones llegan al mediador —y cuántas
/// veces— según la política de retención y lo que se pulse, y qué cuenta la
/// pantalla. Las propiedades destructivas («nada se destruye sin confirmar»,
/// «el diagnóstico no propone nada») se comprueban en el mediador, no en el
/// marcado.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> la autorización real de los comandos (handlers y
/// <c>AutorizacionEscrituraBehavior</c>, en Application), la política de
/// verdad (aquí es un <see cref="IOptions{TOptions}"/> fijado a mano), el
/// aspecto visual, ni el doble clic sobre la confirmación: esa guarda vive en
/// <see cref="DialogoConfirmacion"/> y la prueba <c>DialogoConfirmacionTests</c>.
/// </para>
/// </summary>
public class RetencionTests : BunitContext
{
    /// <summary><see cref="Modal"/> y <see cref="TextoFechaCopiable"/> importan módulos JS; quedan fuera de lo observado.</summary>
    public RetencionTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid SolicitudId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private sealed class MediatorRegistrador(Func<object, object> responder) : IMediator
    {
        public List<object> Enviados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return Task.FromResult((TResponse)responder(request));
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

    /// <summary>Captura nivel, mensaje y excepción: un registro sin la excepción no sirve para diagnosticar.</summary>
    private sealed class LoggerCapturador<T> : ILogger<T>
    {
        public List<(LogLevel Nivel, string Mensaje, Exception? Excepcion)> Eventos { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => AmbitoVacio.Instancia;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Eventos.Add((logLevel, formatter(state, exception), exception));

        private sealed class AmbitoVacio : IDisposable
        {
            public static readonly AmbitoVacio Instancia = new();
            public void Dispose() { }
        }
    }

    private readonly LoggerCapturador<RetencionPage> _logger = new();

    private static SolicitudPurgaDto PendienteDeRevision() =>
        new(SolicitudId, TipoDatoPurgable.Documentos, EstadoSolicitudPurga.PendienteDeRevision, 42, new DateOnly(2020, 3, 31),
            DateTime.UtcNow.AddDays(-2), null, null, null, null, null, null, null);

    private static SolicitudPurgaDto ListaParaEjecutar(TipoDatoPurgable tipo = TipoDatoPurgable.Documentos) =>
        new(SolicitudId, tipo, EstadoSolicitudPurga.Programada, 42, new DateOnly(2020, 3, 31),
            DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(-35),
            DateOnly.FromDateTime(DateTime.UtcNow), null, null, null, null, null);

    /// <summary>
    /// Sin ResultadoEjecucion a propósito: representa una solicitud ejecutada
    /// antes de que ese eje existiera (SolicitudPurga.RegistrarResultadoEjecucion).
    /// </summary>
    private static SolicitudPurgaDto Ejecutada() =>
        new(Guid.NewGuid(), TipoDatoPurgable.Documentos, EstadoSolicitudPurga.Ejecutada, 311, new DateOnly(2018, 12, 31),
            DateTime.UtcNow.AddDays(-90), DateTime.UtcNow.AddDays(-80),
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-70)), DateTime.UtcNow.AddDays(-70), null, null, null, null);

    private (IRenderedComponent<RetencionPage> Cut, MediatorRegistrador Mediator) Renderizar(
        bool politicaActiva,
        IReadOnlyList<SolicitudPurgaDto>? solicitudes = null,
        int? aniosTrabajadores = 5,
        ResultadoDiagnosticoPurgaDto? diagnostico = null,
        Func<EjecutarPurgaCommand, object>? alEjecutar = null,
        Func<ProgramarPurgaCommand, object>? alProgramar = null)
    {
        var mediator = new MediatorRegistrador(peticion => peticion switch
        {
            ObtenerSolicitudesPurgaQuery => (object)(solicitudes ?? Array.Empty<SolicitudPurgaDto>()),
            BuscarDatosPurgablesCommand => Result.Exito(0),
            DiagnosticarDatosPurgablesCommand => Result.Exito(diagnostico ?? new ResultadoDiagnosticoPurgaDto(128, 14, 142)),
            EjecutarPurgaCommand c => (alEjecutar ?? (_ => Result.Exito(42)))(c),
            ProgramarPurgaCommand c => (alProgramar ?? (_ => Result.Exito()))(c),
            CancelarPurgaCommand => Result.Exito(),
            _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
        });

        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddSingleton<ILogger<RetencionPage>>(_logger);
        Services.AddSingleton<IOptions<RetencionDatosOptions>>(Options.Create(
            new RetencionDatosOptions { Activa = politicaActiva, AniosRetencionTrabajadores = aniosTrabajadores }));

        return (Render<RetencionPage>(), mediator);
    }

    private static IElement BotonConTexto(IRenderedComponent<RetencionPage> cut, string texto) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == texto);

    private static IElement BotonDelDialogo(IRenderedComponent<RetencionPage> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == texto);

    private static Dictionary<string, string> CifrasDelDiagnostico(IRenderedComponent<RetencionPage> cut) =>
        cut.FindAll(".diagnostico-retencion-cifra").ToDictionary(
            c => c.QuerySelector(".diagnostico-retencion-rotulo")!.TextContent.Trim(),
            c => c.QuerySelector(".diagnostico-retencion-valor")!.TextContent.Trim());

    // ---------------------------------------------------------------- Política de retención

    /// <summary>
    /// Sin política, <see cref="BuscarDatosPurgablesCommand"/> falla siempre.
    /// Antes el botón se podía pulsar solo para recibir ese error.
    /// </summary>
    [Fact]
    public void Sin_politica_el_boton_de_buscar_esta_deshabilitado_y_la_pantalla_dice_por_que()
    {
        var (cut, _) = Renderizar(politicaActiva: false);

        BotonConTexto(cut, "Buscar datos que hayan cumplido plazo").HasAttribute("disabled").Should().BeTrue(
            "sin política activa el comando de buscar falla siempre: no se ofrece un botón que solo lleva a un error");
        cut.Find(".aviso-politica-retencion").TextContent.Should().Contain("Política desactivada")
            .And.Contain("No se pueden crear propuestas");
    }

    [Fact]
    public async Task Con_politica_no_hay_aviso_y_buscar_llega_al_comando()
    {
        var (cut, mediator) = Renderizar(politicaActiva: true);

        cut.FindAll(".aviso-politica-retencion").Should().BeEmpty();
        var buscar = BotonConTexto(cut, "Buscar datos que hayan cumplido plazo");
        buscar.HasAttribute("disabled").Should().BeFalse();

        await buscar.ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<BuscarDatosPurgablesCommand>().Should().ContainSingle();
    }

    /// <summary>
    /// DEC-35: sin política solo se puede diagnosticar. El diagnóstico cuenta
    /// por categoría y no crea, programa ni ejecuta nada.
    /// </summary>
    [Fact]
    public async Task El_diagnostico_cuenta_por_categoria_sin_proponer_ni_destruir()
    {
        var (cut, mediator) = Renderizar(politicaActiva: false);

        await BotonConTexto(cut, "Ver diagnóstico").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<DiagnosticarDatosPurgablesCommand>().Should().ContainSingle();
        mediator.Enviados.Should().NotContain(p => p is BuscarDatosPurgablesCommand || p is ProgramarPurgaCommand
                                                   || p is EjecutarPurgaCommand,
            "diagnosticar solo cuenta: no puede proponer, autorizar ni destruir nada");

        CifrasDelDiagnostico(cut).Should().Equal(new Dictionary<string, string>
        {
            ["Documentos"] = "128",
            ["Trabajadores dados de baja"] = "14",
            ["Total"] = "142",
        });
        BotonConTexto(cut, "Volver a contar").Should().NotBeNull();
    }

    /// <summary>
    /// Sin plazo para trabajadores, <c>DiagnosticarAsync</c> devuelve 0 sin
    /// haber contado. La pantalla no puede presentar ese 0 como un recuento.
    /// </summary>
    [Fact]
    public async Task Sin_plazo_para_trabajadores_el_diagnostico_no_presenta_un_cero_que_no_conto()
    {
        var (cut, _) = Renderizar(
            politicaActiva: false, aniosTrabajadores: null, diagnostico: new ResultadoDiagnosticoPurgaDto(128, 0, 128));

        await BotonConTexto(cut, "Ver diagnóstico").ClickAsync(new MouseEventArgs());

        CifrasDelDiagnostico(cut)["Trabajadores dados de baja"].Should().Be("No aplica");
        cut.Find(".diagnostico-retencion-nota").TextContent.Should().Contain("no fija plazo para los trabajadores");
    }

    // ---------------------------------------------------------------- Ejecutar (irreversible)

    [Fact]
    public async Task Pulsar_ejecutar_ahora_no_envia_nada_y_abre_la_confirmacion()
    {
        var (cut, mediator) = Renderizar(politicaActiva: true, solicitudes: [ListaParaEjecutar()]);

        await BotonConTexto(cut, "Ejecutar ahora").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<EjecutarPurgaCommand>().Should().BeEmpty(
            "ejecutar anonimiza y borra archivos sin vuelta atrás: no puede salir de un solo clic");
        cut.Find("[role=dialog] h2").TextContent.Should().Contain("Ejecutar la destrucción");
    }

    [Fact]
    public async Task Confirmar_ejecuta_esa_purga_exactamente_una_vez_y_cierra_el_dialogo()
    {
        var (cut, mediator) = Renderizar(politicaActiva: true, solicitudes: [ListaParaEjecutar()]);

        await BotonConTexto(cut, "Ejecutar ahora").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Destruir definitivamente").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<EjecutarPurgaCommand>().Should().ContainSingle()
            .Which.Should().Be(new EjecutarPurgaCommand(SolicitudId));
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelar_la_confirmacion_no_ejecuta()
    {
        var (cut, mediator) = Renderizar(politicaActiva: true, solicitudes: [ListaParaEjecutar()]);

        await BotonConTexto(cut, "Ejecutar ahora").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<EjecutarPurgaCommand>().Should().BeEmpty();
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    /// <summary>
    /// Una excepción no dice qué pasó: <c>EjecucionPurgaService</c> borra
    /// archivos antes de guardar, así que puede haber efectos parciales. El
    /// aviso no puede ser concluyente («no se ha dado por hecha» lo era), la
    /// excepción tiene que quedar en el log y la lista se recarga para enseñar
    /// el estado real. El diálogo se cierra: dejar «Destruir definitivamente»
    /// delante invitaría a repetir sin mirar.
    /// </summary>
    [Fact]
    public async Task Si_la_ejecucion_revienta_no_afirma_el_resultado_registra_y_recarga_el_estado_real()
    {
        var (cut, mediator) = Renderizar(
            politicaActiva: true, solicitudes: [ListaParaEjecutar()],
            alEjecutar: _ => throw new InvalidOperationException("Base de datos caída (simulada)."));

        await BotonConTexto(cut, "Ejecutar ahora").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Destruir definitivamente").ClickAsync(new MouseEventArgs());

        var aviso = Services.GetRequiredService<ToastService>().Mensajes
            .Should().ContainSingle(m => m.Tono == TonoToast.Error).Which.Mensaje;
        aviso.Should().Contain("No pudimos confirmar el resultado")
            .And.Contain("Revisa el estado de la propuesta antes de volver a intentarlo")
            .And.NotContain("No se ha dado por hecha", "tras una excepción no se sabe si hubo efectos parciales");

        _logger.Eventos.Should().ContainSingle(e => e.Nivel == LogLevel.Error)
            .Which.Excepcion.Should().BeOfType<InvalidOperationException>()
            .Which.Message.Should().Be("Base de datos caída (simulada).");

        mediator.Enviados.OfType<ObtenerSolicitudesPurgaQuery>().Should().HaveCount(2,
            "tras el fallo la lista se vuelve a pedir para que el estado mostrado sea el real");
        cut.FindAll("[role=dialog]").Should().BeEmpty(
            "con el resultado sin confirmar no se deja el botón de destruir delante");
    }

    /// <summary>
    /// <c>EjecucionPurgaService</c> solo borra archivos al purgar Documentos;
    /// anonimizar Trabajadores vacía sus datos identificativos y no toca
    /// ningún fichero. La confirmación no puede anunciar un borrado que la
    /// operación no hace, y para Trabajadores lo dice expresamente.
    /// </summary>
    [Theory]
    [InlineData(TipoDatoPurgable.Documentos, "y a borrar sus archivos asociados", "No se borra ningún archivo")]
    [InlineData(TipoDatoPurgable.TrabajadoresDadosDeBaja, "No se borra ningún archivo", "borrar sus archivos")]
    public async Task La_confirmacion_dice_que_pasa_con_los_archivos_segun_el_tipo(
        TipoDatoPurgable tipo, string dice, string noDice)
    {
        var (cut, _) = Renderizar(politicaActiva: true, solicitudes: [ListaParaEjecutar(tipo)]);

        await BotonConTexto(cut, "Ejecutar ahora").ClickAsync(new MouseEventArgs());

        var cuerpo = cut.Find("[role=dialog] .modal-cuerpo").TextContent;
        cuerpo.Should().Contain(dice).And.NotContain(noDice).And.Contain("no se puede deshacer");
    }

    // ---------------------------------------------------------------- Autorizar y descartar

    [Fact]
    public async Task Autorizar_envia_la_fecha_elegida_para_esa_propuesta()
    {
        var (cut, mediator) = Renderizar(politicaActiva: true, solicitudes: [PendienteDeRevision()]);

        await BotonConTexto(cut, "Autorizar").ClickAsync(new MouseEventArgs());
        await cut.Find("[role=dialog] input[type=date]").InputAsync(new ChangeEventArgs { Value = "2031-05-20" });
        await BotonDelDialogo(cut, "Autorizar").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<ProgramarPurgaCommand>().Should().ContainSingle()
            .Which.Should().Be(new ProgramarPurgaCommand(SolicitudId, new DateOnly(2031, 5, 20)));
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    /// <summary>La pantalla solo filtra lo que no es una fecha; no llega al comando.</summary>
    [Fact]
    public async Task Autorizar_sin_fecha_valida_no_envia_nada_y_lo_dice()
    {
        var (cut, mediator) = Renderizar(politicaActiva: true, solicitudes: [PendienteDeRevision()]);

        await BotonConTexto(cut, "Autorizar").ClickAsync(new MouseEventArgs());
        await cut.Find("[role=dialog] input[type=date]").InputAsync(new ChangeEventArgs { Value = "" });
        await BotonDelDialogo(cut, "Autorizar").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<ProgramarPurgaCommand>().Should().BeEmpty();
        cut.Find("[role=dialog] .alerta-formulario").TextContent.Should().Be("Indica una fecha válida.");
    }

    /// <summary>
    /// La fecha pasada NO la filtra la pantalla: la rechaza el dominio
    /// (<c>SolicitudPurga.Programar</c>) y el handler la devuelve como fallo.
    /// Lo que se observa aquí es que ese rechazo se enseña y el diálogo sigue
    /// abierto para corregirla.
    /// </summary>
    [Fact]
    public async Task Autorizar_con_fecha_pasada_ensena_el_rechazo_y_deja_el_dialogo_abierto()
    {
        const string rechazo = "La fecha de ejecución no puede ser anterior a hoy.";
        var (cut, mediator) = Renderizar(
            politicaActiva: true, solicitudes: [PendienteDeRevision()],
            alProgramar: _ => Result.Fallo(Error.Crear("SolicitudPurga.FechaNoValida", rechazo)));

        await BotonConTexto(cut, "Autorizar").ClickAsync(new MouseEventArgs());
        await cut.Find("[role=dialog] input[type=date]").InputAsync(new ChangeEventArgs { Value = "2001-01-01" });
        await BotonDelDialogo(cut, "Autorizar").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<ProgramarPurgaCommand>().Should().ContainSingle()
            .Which.FechaEjecucion.Should().Be(new DateOnly(2001, 1, 1));
        cut.Find("[role=dialog] .alerta-formulario").TextContent.Should().Be(rechazo);
    }

    /// <summary>
    /// Espejo de <c>CancelarPurgaCommandValidator</c> (NotEmpty rechaza vacío y
    /// solo espacios): sin motivo no sale ningún comando.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Descartar_sin_motivo_no_envia_nada_y_lo_pide(string motivo)
    {
        var (cut, mediator) = Renderizar(politicaActiva: true, solicitudes: [PendienteDeRevision()]);

        await BotonConTexto(cut, "Descartar").ClickAsync(new MouseEventArgs());
        await cut.Find("[role=dialog] input").InputAsync(new ChangeEventArgs { Value = motivo });
        await BotonDelDialogo(cut, "Descartar").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<CancelarPurgaCommand>().Should().BeEmpty(
            "descartar exige motivo: el histórico tiene que decir por qué se conservaron esos datos");
        cut.Find("[role=dialog] .alerta-formulario").TextContent.Should().Be("Indica por qué se descarta esta purga.");
    }

    [Fact]
    public async Task Descartar_con_motivo_envia_ese_motivo_para_esa_propuesta()
    {
        const string motivo = "La organización conserva estos documentos por política interna";
        var (cut, mediator) = Renderizar(politicaActiva: true, solicitudes: [PendienteDeRevision()]);

        await BotonConTexto(cut, "Descartar").ClickAsync(new MouseEventArgs());
        await cut.Find("[role=dialog] input").InputAsync(new ChangeEventArgs { Value = motivo });
        await BotonDelDialogo(cut, "Descartar").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<CancelarPurgaCommand>().Should().ContainSingle()
            .Which.Should().Be(new CancelarPurgaCommand(SolicitudId, motivo));
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    // ---------------------------------------------------------------- Histórico

    /// <summary>
    /// Comportamiento conservado: las resueltas se quedan como registro, sin
    /// ninguna acción, y la fila lo dice con un guion en vez de quedar vacía.
    /// </summary>
    [Fact]
    public void Una_propuesta_resuelta_se_conserva_sin_acciones()
    {
        var (cut, _) = Renderizar(politicaActiva: true, solicitudes: [Ejecutada()]);

        var fila = cut.Find("tr.fila-resuelta");
        fila.QuerySelectorAll(".acciones-purga button").Should().BeEmpty();
        fila.QuerySelector(".sin-acciones")!.TextContent.Should().Be("—");
        fila.TextContent.Should().Contain("Ejecutada el");
    }
}

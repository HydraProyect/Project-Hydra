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
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
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

    private static SolicitudPurgaDto ListaParaEjecutar(TipoDatoPurgable tipo = TipoDatoPurgable.Documentos) =>
        new(SolicitudId, tipo, EstadoSolicitudPurga.Programada, 42, new DateOnly(2020, 3, 31),
            DateTime.UtcNow.AddDays(-40), DateTime.UtcNow.AddDays(-35),
            DateOnly.FromDateTime(DateTime.UtcNow), null, null);

    private static SolicitudPurgaDto Ejecutada() =>
        new(Guid.NewGuid(), TipoDatoPurgable.Documentos, EstadoSolicitudPurga.Ejecutada, 311, new DateOnly(2018, 12, 31),
            DateTime.UtcNow.AddDays(-90), DateTime.UtcNow.AddDays(-80),
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-70)), DateTime.UtcNow.AddDays(-70), null);

    private (IRenderedComponent<RetencionPage> Cut, MediatorRegistrador Mediator) Renderizar(
        bool politicaActiva,
        IReadOnlyList<SolicitudPurgaDto>? solicitudes = null,
        int? aniosTrabajadores = 5,
        ResultadoDiagnosticoPurgaDto? diagnostico = null,
        Func<EjecutarPurgaCommand, object>? alEjecutar = null)
    {
        var mediator = new MediatorRegistrador(peticion => peticion switch
        {
            ObtenerSolicitudesPurgaQuery => (object)(solicitudes ?? Array.Empty<SolicitudPurgaDto>()),
            BuscarDatosPurgablesCommand => Result.Exito(0),
            DiagnosticarDatosPurgablesCommand => Result.Exito(diagnostico ?? new ResultadoDiagnosticoPurgaDto(128, 14, 142)),
            EjecutarPurgaCommand c => (alEjecutar ?? (_ => Result.Exito(42)))(c),
            _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
        });

        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
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
    /// Antes una excepción del mediador subía sin aviso. Ahora avisa y deja el
    /// diálogo abierto: cerrarlo diría que la destrucción ocurrió.
    /// </summary>
    [Fact]
    public async Task Si_la_ejecucion_revienta_avisa_y_deja_el_dialogo_abierto()
    {
        var (cut, _) = Renderizar(
            politicaActiva: true, solicitudes: [ListaParaEjecutar()],
            alEjecutar: _ => throw new InvalidOperationException("Base de datos caída (simulada)."));

        await BotonConTexto(cut, "Ejecutar ahora").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Destruir definitivamente").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Error);
        cut.FindAll("[role=dialog]").Should().ContainSingle("tras un fallo el diálogo sigue ahí para reintentar o cancelar");
    }

    /// <summary>
    /// <c>EjecucionPurgaService</c> solo borra archivos al purgar Documentos;
    /// anonimizar Trabajadores no toca ningún fichero. La confirmación no
    /// puede anunciar un borrado que la operación no hace.
    /// </summary>
    [Theory]
    [InlineData(TipoDatoPurgable.Documentos, true)]
    [InlineData(TipoDatoPurgable.TrabajadoresDadosDeBaja, false)]
    public async Task La_confirmacion_solo_anuncia_borrado_de_archivos_cuando_los_hay(TipoDatoPurgable tipo, bool anunciaArchivos)
    {
        var (cut, _) = Renderizar(politicaActiva: true, solicitudes: [ListaParaEjecutar(tipo)]);

        await BotonConTexto(cut, "Ejecutar ahora").ClickAsync(new MouseEventArgs());

        var cuerpo = cut.Find("[role=dialog] .modal-cuerpo").TextContent;
        cuerpo.Contains("archivos", StringComparison.Ordinal).Should().Be(anunciaArchivos, cuerpo);
        cuerpo.Should().Contain("no se puede deshacer");
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

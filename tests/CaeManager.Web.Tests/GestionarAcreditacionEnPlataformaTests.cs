using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionAceptada;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionPendienteDeSubir;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionRechazada;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Regla de Chris del 2026-10-19: el estado del documento en TALVEG (vigencia) y el de su acreditación en cada plataforma
/// son dos estados distintos. En Centro 360 cada fila los enseña en dos líneas; «Gestionar» abre el panel que cambia solo el
/// estado en la plataforma; si el documento falta o está vencido en TALVEG, la acción lleva a la ficha 360 del Trabajador; y
/// va en gris cuando los dos estados están al día.
/// </summary>
public class GestionarAcreditacionEnPlataformaTests : BunitContext
{
    public GestionarAcreditacionEnPlataformaTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public IReadOnlyList<TrabajadorAsignacionDocumentacionDto> Trabajadores { get; init; } = [];
        public List<object> Enviados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            object respuesta = request switch
            {
                ObtenerAsignacionesDocumentacionPorCentroQuery => Trabajadores,
                MarcarAcreditacionAceptadaCommand c => Result.Exito(new ResultadoAnotacionAcreditacionDto(
                    c.AcreditacionId, EstadoAcreditacion.Subida, VigenciaEnPlataforma.SinConfirmar, Guid.NewGuid())),
                _ when typeof(TResponse) == typeof(Result) => Result.Exito(),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta);
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

    private MediatorFalso Registrar(params TrabajadorAsignacionDocumentacionDto[] trabajadores)
    {
        var mediator = new MediatorFalso { Trabajadores = trabajadores };
        Services.AddLocalization();
        this.ConRolDeEscritura();
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddScoped<IFileStorageService, AlmacenQueNadieToca>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieToca>();
        return mediator;
    }

    private sealed class AlmacenQueNadieToca : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ConversorQueNadieToca : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static TrabajadorAsignacionDocumentacionDto Trabajador(EstadoDocumento estado, Guid? documentoId, params AcreditacionResumenDto[] acreditaciones) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Ruiz Peña, Ana", new DateOnly(2026, 1, 15), estado,
            [new DocumentoRequeridoDto(documentoId, Guid.NewGuid(), "Reconocimiento médico", estado, new DateOnly(2027, 3, 14),
                AcreditacionesEnElCentro: acreditaciones)],
            CumplimientoDocumental.Evaluar([estado]));

    private static AcreditacionResumenDto Acreditacion(EstadoAcreditacion estado) => new(Guid.NewGuid(), "Nalanda", estado);

    private async Task<IRenderedComponent<AcordeonAsignacionesCentro>> DesplegadoAsync()
    {
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(a => a.CentroId, Guid.NewGuid()).Add(a => a.CentroNombre, "Centro Norte"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));
        await cut.Find("button.fila-relacion-desplegar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("tabla-documentos-requeridos"));
        return cut;
    }

    [Fact]
    public async Task Cada_fila_ensena_En_TALVEG_y_En_la_plataforma_en_lineas_separadas()
    {
        Registrar(Trabajador(EstadoDocumento.Vigente, Guid.NewGuid(), Acreditacion(EstadoAcreditacion.PendienteDeSubir)));
        var cut = await DesplegadoAsync();

        var talveg = cut.Find(".tabla-documentos-requeridos [data-linea=talveg]");
        var plataforma = cut.Find(".tabla-documentos-requeridos [data-linea=plataforma]");
        talveg.TextContent.Should().Contain("En TALVEG").And.Contain("Vigente").And.NotContain("Nalanda");
        plataforma.TextContent.Should().Contain("En la plataforma").And.Contain("Nalanda: pendiente de envío").And.NotContain("Vigente");
        cut.Find(".celda-documento-nombre").TextContent.Should().NotContain("Nalanda", "el estado en la plataforma no se repite bajo el nombre");
    }

    [Theory]
    [InlineData(EstadoDocumento.Vencido)]
    [InlineData(EstadoDocumento.Faltante)]
    public async Task Si_falta_en_TALVEG_la_accion_lleva_a_la_ficha_del_Trabajador_y_no_abre_el_panel(EstadoDocumento estado)
    {
        var trabajador = Trabajador(estado, estado == EstadoDocumento.Faltante ? null : Guid.NewGuid(), Acreditacion(EstadoAcreditacion.Aceptada));
        Registrar(trabajador);
        var cut = await DesplegadoAsync();

        var accion = cut.Find("[data-accion=actualizar-en-ficha]");
        accion.TextContent.Trim().Should().Be("Actualizar en Trabajador 360");
        accion.GetAttribute("href").Should().Be($"/trabajadores/{trabajador.TrabajadorId}");
        cut.FindAll("[data-accion=gestionar-acreditacion]").Should().BeEmpty();
    }

    [Fact]
    public async Task Gestionar_va_en_gris_solo_cuando_los_dos_estados_estan_al_dia()
    {
        Registrar(Trabajador(EstadoDocumento.Vigente, Guid.NewGuid(), Acreditacion(EstadoAcreditacion.Aceptada)));
        var cut = await DesplegadoAsync();

        cut.Find("[data-accion=gestionar-acreditacion]").ClassList.Should().Contain("gestionar-discreto");
    }

    [Fact]
    public async Task Gestionar_no_va_en_gris_si_falta_subir_a_la_plataforma_aunque_este_vigente_en_TALVEG()
    {
        Registrar(Trabajador(EstadoDocumento.Vigente, Guid.NewGuid(), Acreditacion(EstadoAcreditacion.PendienteDeSubir)));
        var cut = await DesplegadoAsync();

        cut.Find("[data-accion=gestionar-acreditacion]").ClassList.Should().NotContain("gestionar-discreto");
    }

    [Fact]
    public async Task Gestionar_abre_el_panel_de_acreditacion_con_el_estado_en_TALVEG_de_solo_lectura()
    {
        Registrar(Trabajador(EstadoDocumento.Vigente, Guid.NewGuid(), Acreditacion(EstadoAcreditacion.PendienteDeSubir)));
        var cut = await DesplegadoAsync();

        await cut.Find("[data-accion=gestionar-acreditacion]").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Gestionar acreditación en plataforma"));
        cut.Find("[data-estado-talveg]").TextContent.Should().Contain("Vigente");
        cut.FindAll("input[name=estado-plataforma]").Should().HaveCount(4);
    }

    private (IRenderedComponent<PanelGestionarAcreditacion>, MediatorFalso, AcreditacionResumenDto) Panel(EstadoAcreditacion estado)
    {
        var mediator = Registrar();
        var acreditacion = Acreditacion(estado);
        var cut = Render<PanelGestionarAcreditacion>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.DocumentoNombre, "RNT")
            .Add(x => x.PropietarioNombre, "Montajes S.L.")
            .Add(x => x.EstadoTalveg, EstadoDocumento.Vigente)
            .Add(x => x.Acreditaciones, [acreditacion]));
        return (cut, mediator, acreditacion);
    }

    private static async Task ElegirYGuardarAsync(IRenderedComponent<PanelGestionarAcreditacion> cut, string destino)
    {
        cut.Find($"input[name=estado-plataforma][value={destino}]").Change(destino);
        await cut.FindAll("button").Single(b => b.TextContent.Contains("Guardar estado")).ClickAsync(new MouseEventArgs());
    }

    [Theory]
    [InlineData(PanelGestionarAcreditacion.DestinoPendienteSubir, typeof(MarcarAcreditacionPendienteDeSubirCommand))]
    [InlineData(PanelGestionarAcreditacion.DestinoPendienteValidacion, typeof(MarcarAcreditacionSubidaCommand))]
    public async Task Cada_estado_del_panel_envia_su_comando_de_acreditacion(string destino, Type comando)
    {
        var (cut, mediator, _) = Panel(EstadoAcreditacion.Aceptada);

        await ElegirYGuardarAsync(cut, destino);

        mediator.Enviados.Should().ContainSingle().Which.Should().BeOfType(comando);
    }

    [Fact]
    public async Task Subido_y_al_dia_envia_validada_con_la_vigencia_en_la_plataforma()
    {
        var (cut, mediator, acreditacion) = Panel(EstadoAcreditacion.Subida);
        cut.Find($"input[name=estado-plataforma][value={PanelGestionarAcreditacion.DestinoAlDia}]").Change(PanelGestionarAcreditacion.DestinoAlDia);
        cut.Find("input[type=date]").Input("2027-05-31");
        cut.Find("input[type=date]").Blur();

        await cut.FindAll("button").Single(b => b.TextContent.Contains("Guardar estado")).ClickAsync(new MouseEventArgs());

        var enviado = mediator.Enviados.Should().ContainSingle().Which.Should().BeOfType<MarcarAcreditacionAceptadaCommand>().Subject;
        enviado.AcreditacionId.Should().Be(acreditacion.Id);
        enviado.Vigencia.Should().Be(VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 5, 31)));
    }

    [Fact]
    public async Task Rechazado_sin_motivo_no_envia_nada_y_lo_pide()
    {
        var (cut, mediator, _) = Panel(EstadoAcreditacion.Subida);

        await ElegirYGuardarAsync(cut, PanelGestionarAcreditacion.DestinoRechazada);

        mediator.Enviados.Should().BeEmpty();
        cut.Markup.Should().Contain("Describe el motivo del rechazo.");
    }

    [Fact]
    public async Task Rechazado_con_motivo_envia_el_rechazo_con_su_motivo()
    {
        var (cut, mediator, _) = Panel(EstadoAcreditacion.Subida);
        cut.Find($"input[name=estado-plataforma][value={PanelGestionarAcreditacion.DestinoRechazada}]").Change(PanelGestionarAcreditacion.DestinoRechazada);
        cut.Find("textarea").Input("Firma ilegible");

        await cut.FindAll("button").Single(b => b.TextContent.Contains("Guardar estado")).ClickAsync(new MouseEventArgs());

        mediator.Enviados.Should().ContainSingle().Which.Should().BeOfType<MarcarAcreditacionRechazadaCommand>()
            .Which.MotivoLiteral.Should().Be("Firma ilegible");
    }
}

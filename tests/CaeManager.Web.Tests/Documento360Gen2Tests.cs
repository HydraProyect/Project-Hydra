using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Documentos.Commands.EliminarDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Documentos.Queries.ObtenerValidacionOficialDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class Documento360Gen2Tests : BunitContext
{
    private sealed class MediadorFalso : IMediator
    {
        public Dictionary<Guid, DocumentoDetalleDto> Detalles { get; } = [];
        public Result ResultadoRenovar { get; set; } = Result.Exito();
        public Result ResultadoEliminar { get; set; } = Result.Exito();
        public Func<object, Task?>? Retener { get; set; }
        public List<object> Enviadas { get; } = [];
        public List<(object Peticion, CancellationToken Token)> Tokens { get; } = [];

        public async Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            Tokens.Add((request, cancellationToken));
            if (Retener?.Invoke(request) is { } retenida) await retenida;
            object? respuesta = request switch
            {
                ObtenerDocumentoPorIdQuery q => Detalles.GetValueOrDefault(q.Id),
                ObtenerValidacionOficialDocumentoQuery => null,
                RenovarDocumentoCommand => ResultadoRenovar,
                EliminarDocumentoCommand => ResultadoEliminar,
                _ => throw new NotSupportedException($"Petición no prevista: {request.GetType().Name}.")
            };
            return (T)respuesta!;
        }

        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => Task.CompletedTask;
    }

    private MediadorFalso Registrar(MediadorFalso mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        return mediador;
    }

    private static DocumentoDetalleDto Detalle(Guid id, string tipo = "Certificado TGSS", Guid? version = null) => new(
        id, AmbitoAplicacion.Empresa, "Montajes Ebro S.L.", tipo, false,
        new DateOnly(2026, 1, 10), new DateOnly(2026, 8, 10), "documentos/certificado.pdf", "Original",
        null, null, null, null, version ?? Guid.NewGuid(), PerfilDocumentoOficial.Ninguno, Guid.NewGuid());

    private IRenderedComponent<DocumentoWorkspacePanel> Renderizar(Guid id, string pestana = "informacion") =>
        Render<DocumentoWorkspacePanel>(p => p.Add(x => x.EntidadId, id).Add(x => x.PestanaActiva, pestana));

    private static IElement Boton(IRenderedComponent<DocumentoWorkspacePanel> cut, string texto) =>
        cut.FindAll("button").Where(b => b.TextContent.Trim() == texto)
            .Should().ContainSingle($"debe existir exactamente un botón «{texto}»").Subject;

    [Fact]
    public void La_cabecera_y_la_isla_muestran_solo_el_documento_y_los_datos_que_el_DTO_entrega()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediadorFalso());
        mediador.Detalles[id] = Detalle(id);

        var cut = Renderizar(id);

        cut.Find(".kicker-documento-360").TextContent.Should().Be("Documento · Ámbito Empresa");
        cut.Find(".titulo-documento-360").TextContent.Should().Be("Certificado TGSS");
        var celdas = cut.FindAll(".celda-info-documento-360").ToList();
        celdas.Should().HaveCount(6, "el instrumento observa que la isla no está vacía");
        celdas.Select(c => c.TextContent.Trim()).Should().Contain("PropietarioMontajes Ebro S.L.").And.Contain("ArchivoVer PDF");
        cut.FindAll("[role=tab]").Should().HaveCount(4);
        cut.FindAll(".pestanas-contador").Should().BeEmpty("ninguna lista la carga este panel");
    }

    [Fact]
    public async Task Renovar_reenvia_la_version_y_no_inventa_un_archivo()
    {
        var id = Guid.NewGuid();
        var version = Guid.NewGuid();
        var mediador = Registrar(new MediadorFalso());
        mediador.Detalles[id] = Detalle(id, version: version);
        var cut = Renderizar(id);

        await Boton(cut, "Renovar").ClickAsync(new MouseEventArgs());
        var campos = cut.FindAll("input[type=date]");
        campos.Should().HaveCount(2, "el formulario de renovación tiene emisión y vencimiento manual");
        await campos[0].InputAsync(new ChangeEventArgs { Value = "2026-02-03" });
        await cut.Find("textarea").InputAsync(new ChangeEventArgs { Value = "Renovado" });
        await Boton(cut, "Guardar fechas y comentarios (sin sustituir el archivo)").ClickAsync(new MouseEventArgs());

        var comando = mediador.Enviadas.OfType<RenovarDocumentoCommand>().Should().ContainSingle().Subject;
        comando.Id.Should().Be(id);
        comando.FechaEmision.Should().Be(new DateOnly(2026, 2, 3));
        comando.Comentarios.Should().Be("Renovado");
        comando.ArchivoUrl.Should().BeNull("esta edición in situ no ofrece una subida de archivo");
        comando.Version.Should().Be(version, "la renovación debe detectar edición concurrente");
        var toast = Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(
            "una renovación correcta debe informar de su resultado").Subject;
        toast.Mensaje.Should().Be("Certificado TGSS: fechas y comentarios actualizados; el archivo no se ha sustituido.");
    }

    [Fact]
    public async Task Un_resultado_fallido_de_renovar_no_anuncia_exito_y_queda_en_el_formulario()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediadorFalso { ResultadoRenovar = Result.Fallo(Error.Crear("Documento.Conflicto", "Otra persona ya lo renovó.")) });
        mediador.Detalles[id] = Detalle(id);
        var cut = Renderizar(id);

        await Boton(cut, "Renovar").ClickAsync(new MouseEventArgs());
        await Boton(cut, "Guardar fechas y comentarios (sin sustituir el archivo)").ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=alert]").Should().ContainSingle("el formulario tiene que recibir el fallo");
        cut.Find("[role=alert]").TextContent.Should().Be("Otra persona ya lo renovó.");
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty("un Result fallido no es éxito");
    }

    [Fact]
    public async Task La_guarda_de_baja_del_panel_bloquea_la_segunda_entrada_del_callback_del_hijo()
    {
        var id = Guid.NewGuid();
        var espera = new TaskCompletionSource();
        var mediador = Registrar(new MediadorFalso { Retener = p => p is EliminarDocumentoCommand ? espera.Task : null });
        mediador.Detalles[id] = Detalle(id);
        var cut = Renderizar(id);
        await Boton(cut, "Dar de baja").ClickAsync(new MouseEventArgs());
        var dialogo = cut.FindComponent<DialogoConfirmacion>();

        var primera = cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
        var segunda = cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
        mediador.Enviadas.OfType<EliminarDocumentoCommand>().Should().ContainSingle("se entra por OnConfirmar del hijo, no por su botón protegido");

        await cut.InvokeAsync(() => espera.SetResult());
        await primera;
        await segunda;
    }

    [Fact]
    public async Task Las_consultas_llevan_el_token_del_ciclo_los_comandos_no_y_retirar_el_panel_lo_cancela()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediadorFalso());
        mediador.Detalles[id] = Detalle(id);
        var cut = Renderizar(id);

        var consultas = mediador.Tokens.Where(t => t.Peticion is not RenovarDocumentoCommand and not EliminarDocumentoCommand).Select(t => t.Token).ToList();
        consultas.Should().NotBeEmpty("hay que comprobar que el instrumento vio al menos una consulta");
        consultas.Should().OnlyContain(t => t.CanBeCanceled);
        consultas.Distinct().Should().ContainSingle();

        await Boton(cut, "Renovar").ClickAsync(new MouseEventArgs());
        await Boton(cut, "Guardar fechas y comentarios (sin sustituir el archivo)").ClickAsync(new MouseEventArgs());
        mediador.Tokens.Where(t => t.Peticion is RenovarDocumentoCommand).Should().ContainSingle()
            .Which.Token.CanBeCanceled.Should().BeFalse("cerrar la ficha no deshace una escritura ya solicitada");

        await Boton(cut, "Dar de baja").ClickAsync(new MouseEventArgs());
        var dialogo = cut.FindComponent<DialogoConfirmacion>();
        await cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());
        mediador.Enviadas.OfType<EliminarDocumentoCommand>().Should().ContainSingle(
            "el instrumento debe haber observado el comando antes de comprobar su token");
        mediador.Tokens.Where(t => t.Peticion is EliminarDocumentoCommand).Should().ContainSingle()
            .Which.Token.CanBeCanceled.Should().BeFalse("cerrar la ficha no deshace una escritura ya solicitada");

        var token = consultas[0];
        await DisposeComponentsAsync();
        token.IsCancellationRequested.Should().BeTrue("retirar el panel ejecuta su Dispose, no cut.Dispose()");
    }

    [Fact]
    public async Task La_cabecera_que_llega_tarde_de_otro_documento_no_pisa_la_ficha_actual()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var esperaA = new TaskCompletionSource();
        var mediador = Registrar(new MediadorFalso
        {
            Retener = p => p is ObtenerDocumentoPorIdQuery q && q.Id == a ? esperaA.Task : null
        });
        mediador.Detalles[a] = Detalle(a, "Documento A");
        mediador.Detalles[b] = Detalle(b, "Documento B");
        var cut = Renderizar(a);

        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));
        await cut.InvokeAsync(() => esperaA.SetResult());

        var titulo = cut.FindAll(".titulo-documento-360");
        titulo.Should().ContainSingle("el instrumento observa la cabecera que quedó en pantalla");
        titulo[0].TextContent.Should().Be("Documento B");
        mediador.Enviadas.OfType<ObtenerValidacionOficialDocumentoQuery>().Should().ContainSingle()
            .Which.DocumentoId.Should().Be(b, "la cadena de A cancelada no debe continuar hacia validación");
    }
}

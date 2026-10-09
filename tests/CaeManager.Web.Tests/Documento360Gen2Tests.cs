using AngleSharp.Dom;
using Bunit;
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

public partial class Documento360Gen2Tests : BunitContext
{
    private sealed class MediadorFalso : IMediator
    {
        public Dictionary<Guid, DocumentoDetalleDto> Detalles { get; } = [];
        public Result<Guid> ResultadoRenovar { get; set; } = Result.Exito(Guid.NewGuid());
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
                // Al abrir «Reclamar», el selector de la reclamación carga sus catálogos.
                CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento.ObtenerTiposDocumentoQuery
                    => (IReadOnlyList<CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento.TipoDocumentoListaDto>)[],
                CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector.ObtenerTrabajadoresParaSelectorQuery
                    => (IReadOnlyList<CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector.TrabajadorSelectorDto>)[],
                CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector.ObtenerEmpresasParaSelectorQuery
                    => (IReadOnlyList<CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector.EmpresaSelectorDto>)[],
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
        Services.AddLocalization();
        // «Renovar» va dentro de SoloConEscritura: sin rol, AuthorizeView no tendría con qué decidir.
        this.ConRolDeEscritura();
        return mediador;
    }

    private static DocumentoDetalleDto Detalle(Guid id, string tipo = "Certificado TGSS", Guid? version = null) => new(
        id, AmbitoAplicacion.Empresa, "Montajes Ebro S.L.", tipo, false,
        new DateOnly(2026, 1, 10), new DateOnly(2026, 8, 10), EstadoVigenciaDocumento.VenceEnFecha, "documentos/certificado.pdf", "Original",
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
        celdas.Select(c => c.TextContent.Trim()).Should().Contain("PropietarioMontajes Ebro S.L.").And.Contain("ArchivoDescargar PDF");
        cut.FindAll("[role=tab]").Should().HaveCount(4);
        cut.FindAll(".pestanas-contador").Should().BeEmpty("ninguna lista la carga este panel");
        // Sin FechaEmision: en este panel solo la vigencia es copiable (P9,
        // 2026-09-18) — la emisión ya tiene su propia celda, sin Alt+clic. El
        // listado sí lo ofrece desde la decisión del 2026-10-08.
        var fecha = cut.FindComponent<TextoFechaCopiable>().Instance;
        fecha.Fecha.Should().Be(mediador.Detalles[id].FechaVencimiento);
        fecha.FechaEmision.Should().BeNull();
    }

    [Fact]
    public void El_archivo_se_ofrece_como_descarga_como_en_el_mockup()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediadorFalso());
        mediador.Detalles[id] = Detalle(id);

        var enlace = Renderizar(id).FindAll("a").Where(a => a.TextContent.Trim() == "Descargar PDF")
            .Should().ContainSingle().Subject;

        enlace.GetAttribute("href").Should().Be($"/documentos/{id}/archivo");
        enlace.HasAttribute("download").Should().BeTrue();
    }

    /// <summary>
    /// «Reclamar» abre la reclamación existente (DrawerReclamacionLote de la Bandeja)
    /// con el titular del documento preseleccionado: la Empresa en un documento de
    /// Empresa, el Trabajador en uno de Trabajador. Nada se envía sin la revisión.
    /// </summary>
    [Theory]
    [InlineData(AmbitoAplicacion.Empresa)]
    [InlineData(AmbitoAplicacion.Trabajador)]
    public async Task Reclamar_abre_la_reclamacion_con_el_titular_del_documento(AmbitoAplicacion ambito)
    {
        var id = Guid.NewGuid();
        var titular = Guid.NewGuid();
        var mediador = Registrar(new MediadorFalso());
        mediador.Detalles[id] = Detalle(id) with
        {
            Ambito = ambito,
            EmpresaId = ambito == AmbitoAplicacion.Empresa ? titular : null,
            TrabajadorId = ambito == AmbitoAplicacion.Trabajador ? titular : null,
        };
        var cut = Renderizar(id);
        var drawer = cut.FindComponent<CaeManager.Web.Features.Bandeja.Components.DrawerReclamacionLote>().Instance;
        drawer.Visible.Should().BeFalse("control del instrumento: cerrado hasta pulsar");

        await Boton(cut, "Reclamar").ClickAsync(new MouseEventArgs());

        drawer = cut.FindComponent<CaeManager.Web.Features.Bandeja.Components.DrawerReclamacionLote>().Instance;
        drawer.Visible.Should().BeTrue();
        drawer.AmbitoInicial.Should().Be(ambito);
        drawer.EntidadIdInicial.Should().Be(titular);
        mediador.Enviadas.Should().NotContain(p => p.GetType().Name.StartsWith("EnviarReclamacion"),
            "abrir la reclamación no envía nada");
    }

    /// <summary>
    /// El Context Workspace reutiliza el panel al pasar a otro documento: una
    /// reclamación abierta para el anterior se cierra y la nueva lleva su titular
    /// (hallazgo de Codex en la revisión de esta PR).
    /// </summary>
    [Fact]
    public async Task Cambiar_de_documento_cierra_la_reclamacion_abierta_del_anterior()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var empresaB = Guid.NewGuid();
        var mediador = Registrar(new MediadorFalso());
        mediador.Detalles[a] = Detalle(a);
        mediador.Detalles[b] = Detalle(b) with { EmpresaId = empresaB };
        var cut = Renderizar(a);
        await Boton(cut, "Reclamar").ClickAsync(new MouseEventArgs());
        cut.FindComponent<CaeManager.Web.Features.Bandeja.Components.DrawerReclamacionLote>().Instance.Visible
            .Should().BeTrue("control del instrumento: la reclamación del primero estaba abierta");

        cut.Render(p => p.Add(x => x.EntidadId, b).Add(x => x.PestanaActiva, "informacion"));

        cut.WaitForAssertion(() => cut.Find(".titulo-documento-360"));
        var drawer = cut.FindComponent<CaeManager.Web.Features.Bandeja.Components.DrawerReclamacionLote>().Instance;
        drawer.Visible.Should().BeFalse();
        drawer.EntidadIdInicial.Should().Be(empresaB);
    }

    [Fact]
    public void Sin_camino_de_reclamacion_para_el_ambito_no_se_ofrece_Reclamar()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediadorFalso());
        mediador.Detalles[id] = Detalle(id) with { Ambito = AmbitoAplicacion.Vehiculo, EmpresaId = null };

        var cut = Renderizar(id);

        Boton(cut, "Renovar");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Reclamar");
        cut.FindComponents<CaeManager.Web.Features.Bandeja.Components.DrawerReclamacionLote>().Should().BeEmpty();
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
        var mediador = Registrar(new MediadorFalso { ResultadoRenovar = Result.Fallo<Guid>(Error.Crear("Documento.Conflicto", "Otra persona ya lo renovó.")) });
        mediador.Detalles[id] = Detalle(id);
        var cut = Renderizar(id);

        await Boton(cut, "Renovar").ClickAsync(new MouseEventArgs());
        await Boton(cut, "Guardar fechas y comentarios (sin sustituir el archivo)").ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=alert]").Should().ContainSingle("el formulario tiene que recibir el fallo");
        cut.Find("[role=alert]").TextContent.Should().Be("Otra persona ya lo renovó.");
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty("un Result fallido no es éxito");
    }

    /// <summary>
    /// P41b (decisión del propietario, 2026-09-19): «Dar de baja» del documento
    /// solo vive en la lista. La ficha no la ofrece —ni botón, ni diálogo— y el
    /// doble de mediador ya no responde a <c>EliminarDocumentoCommand</c>.
    /// </summary>
    [Fact]
    public void La_ficha_no_ofrece_dar_de_baja_al_documento()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediadorFalso());
        mediador.Detalles[id] = Detalle(id);

        var cut = Renderizar(id);

        Boton(cut, "Renovar");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Dar de baja", StringComparison.Ordinal));
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public async Task Las_consultas_llevan_el_token_del_ciclo_los_comandos_no_y_retirar_el_panel_lo_cancela()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediadorFalso());
        mediador.Detalles[id] = Detalle(id);
        var cut = Renderizar(id);

        var consultas = mediador.Tokens.Where(t => t.Peticion is not RenovarDocumentoCommand).Select(t => t.Token).ToList();
        consultas.Should().NotBeEmpty("hay que comprobar que el instrumento vio al menos una consulta");
        consultas.Should().OnlyContain(t => t.CanBeCanceled);
        consultas.Distinct().Should().ContainSingle();

        await Boton(cut, "Renovar").ClickAsync(new MouseEventArgs());
        await Boton(cut, "Guardar fechas y comentarios (sin sustituir el archivo)").ClickAsync(new MouseEventArgs());
        mediador.Tokens.Where(t => t.Peticion is RenovarDocumentoCommand).Should().ContainSingle()
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

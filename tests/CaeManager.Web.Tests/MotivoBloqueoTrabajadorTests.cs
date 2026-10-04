using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Ficha 12: junto al «Bloqueado» de un Trabajador en un Centro, el motivo y los documentos que faltan, con «Pedir». La pieza solo
/// dice lo que la regla única por Centro ya evaluó (los bloqueos entran hechos) y «Pedir» abre el flujo de reclamación existente:
/// nunca envía por sí solo. Se ofrece para lo vencido, para el documento que falta del todo y para el «Sin confirmar» sin fecha
/// (el flujo ya sabe pedirlos); no se ofrece donde no hay a quién pedírselo (documento de Empresa sin Empresa identificada).
/// </summary>
public class MotivoBloqueoTrabajadorTests : BunitContext
{
    private static readonly Guid Ana = Guid.NewGuid();
    private static readonly Guid Luis = Guid.NewGuid();
    private static readonly Guid EmpresaId = Guid.NewGuid();

    public MotivoBloqueoTrabajadorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private static DocumentacionBloqueantePendienteDto Bloqueo(
        Guid trabajador, string tipo, SituacionDeRequisitoBloqueante situacion, AmbitoAplicacion ambito = AmbitoAplicacion.Trabajador,
        Guid? empresaId = null, DateOnly? vencimiento = null, int tolerancia = 0) =>
        new(Guid.NewGuid(), "Centro Norte", trabajador, trabajador == Ana ? "Ruiz Peña, Ana" : "Gil Mora, Luis",
            Guid.NewGuid(), tipo, EmpresaId: empresaId, Ambito: ambito, Situacion: situacion,
            ToleranciaDias: tolerancia, VencimientoEfectivo: vencimiento);

    private IRenderedComponent<MotivoBloqueoTrabajador> Pintar(
        IReadOnlyList<DocumentacionBloqueantePendienteDto> bloqueos, IReadOnlyList<DocumentoRequeridoDto>? otros,
        List<MotivoBloqueoTrabajador.EntidadAPedir> pedidos) =>
        Render<MotivoBloqueoTrabajador>(p => p
            .Add(c => c.TrabajadorId, Ana)
            .Add(c => c.Bloqueos, bloqueos)
            .Add(c => c.Otros, otros ?? [])
            .Add(c => c.OnPedir, EventCallback.Factory.Create<MotivoBloqueoTrabajador.EntidadAPedir>(this, pedidos.Add)));

    [Fact]
    public async Task Un_documento_vencido_del_Trabajador_dice_el_motivo_y_Pedir_abre_la_reclamacion_de_ese_Trabajador()
    {
        var pedidos = new List<MotivoBloqueoTrabajador.EntidadAPedir>();
        var cut = Pintar([Bloqueo(Ana, "Aptitud médica", SituacionDeRequisitoBloqueante.Vencido, vencimiento: new DateOnly(2026, 9, 2))], null, pedidos);

        cut.Markup.Should().Contain("Aptitud médica: venció el 02/09/2026");
        await cut.Find("button.motivo-bloqueo-pedir").ClickAsync(new MouseEventArgs());

        pedidos.Should().ContainSingle().Which.Should().Be(new MotivoBloqueoTrabajador.EntidadAPedir(AmbitoAplicacion.Trabajador, Ana));
    }

    [Fact]
    public async Task Un_documento_que_falta_del_todo_dice_el_motivo_y_Pedir_abre_la_reclamacion_de_ese_Trabajador()
    {
        var pedidos = new List<MotivoBloqueoTrabajador.EntidadAPedir>();
        var cut = Pintar([Bloqueo(Ana, "Formación PRL", SituacionDeRequisitoBloqueante.Ausente)], null, pedidos);

        cut.Markup.Should().Contain("Formación PRL: no tiene el documento");
        cut.Markup.Should().NotContain("no se puede pedir desde aquí");
        await cut.Find("button.motivo-bloqueo-pedir").ClickAsync(new MouseEventArgs());

        pedidos.Should().ContainSingle().Which.Should().Be(new MotivoBloqueoTrabajador.EntidadAPedir(AmbitoAplicacion.Trabajador, Ana));
    }

    [Fact]
    public async Task Un_documento_de_Empresa_que_falta_se_pide_a_la_Empresa_y_no_al_Trabajador()
    {
        var pedidos = new List<MotivoBloqueoTrabajador.EntidadAPedir>();
        var cut = Pintar([Bloqueo(Ana, "Seguro de responsabilidad civil", SituacionDeRequisitoBloqueante.Ausente, AmbitoAplicacion.Empresa, EmpresaId)], null, pedidos);

        await cut.Find("button.motivo-bloqueo-pedir").ClickAsync(new MouseEventArgs());

        pedidos.Should().ContainSingle().Which.Should().Be(new MotivoBloqueoTrabajador.EntidadAPedir(AmbitoAplicacion.Empresa, EmpresaId));
    }

    [Fact]
    public void Un_documento_de_Empresa_que_falta_sin_Empresa_identificada_no_ofrece_Pedir_y_lo_dice()
    {
        var cut = Pintar([Bloqueo(Ana, "Seguro de responsabilidad civil", SituacionDeRequisitoBloqueante.Ausente, AmbitoAplicacion.Empresa, empresaId: null)], null, []);

        cut.FindAll("button.motivo-bloqueo-pedir").Should().BeEmpty();
        cut.Markup.Should().Contain("no se puede pedir desde aquí");
    }

    [Fact]
    public async Task Un_documento_de_Empresa_vencido_se_pide_a_la_Empresa_y_no_al_Trabajador()
    {
        var pedidos = new List<MotivoBloqueoTrabajador.EntidadAPedir>();
        var cut = Pintar([Bloqueo(Ana, "Certificado de la Seguridad Social", SituacionDeRequisitoBloqueante.Vencido, AmbitoAplicacion.Empresa,
            EmpresaId, new DateOnly(2026, 9, 20), tolerancia: 10)], null, pedidos);

        cut.Markup.Should().Contain("Certificado de la Seguridad Social (de la Empresa)");
        cut.Markup.Should().Contain("10", "con tolerancia, lo dice el texto compartido de fin de tolerancia");
        await cut.Find("button.motivo-bloqueo-pedir").ClickAsync(new MouseEventArgs());

        pedidos.Should().ContainSingle().Which.Should().Be(new MotivoBloqueoTrabajador.EntidadAPedir(AmbitoAplicacion.Empresa, EmpresaId));
    }

    [Fact]
    public void Un_vencido_de_Empresa_sin_Empresa_identificada_no_ofrece_Pedir()
    {
        var cut = Pintar([Bloqueo(Ana, "Certificado de la Seguridad Social", SituacionDeRequisitoBloqueante.Vencido, AmbitoAplicacion.Empresa,
            empresaId: null, vencimiento: new DateOnly(2026, 9, 20))], null, []);

        cut.FindAll("button.motivo-bloqueo-pedir").Should().BeEmpty();
    }

    [Fact]
    public void Los_pendientes_que_no_bloquean_salen_aparte_y_se_pide_el_que_vence_y_el_sin_confirmar_sin_fecha()
    {
        var hoy = DiaDeNegocio.Hoy();
        var otros = new[]
        {
            new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Formación PRL", EstadoDocumento.Proximo, hoy.AddDays(6)),
            new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Seguro RC", EstadoDocumento.SinConfirmar, null)
        };
        var cut = Pintar([Bloqueo(Ana, "Aptitud médica", SituacionDeRequisitoBloqueante.Ausente)], otros, []);

        cut.Markup.Should().Contain("Además, por atender en este Centro");
        cut.Markup.Should().Contain($"Formación PRL — caduca {hoy.AddDays(6):dd/MM/yyyy}");
        cut.Markup.Should().Contain("Seguro RC — sin confirmar");
        // Bloqueo ausente: uno. Próximo con fecha: uno. Sin confirmar sin fecha: uno (el flujo lo pide por su vigencia).
        cut.FindAll("button.motivo-bloqueo-pedir").Should().HaveCount(3);
    }

    [Fact]
    public void Un_pendiente_que_ni_cabe_en_la_ventana_ni_es_un_sin_confirmar_con_documento_no_ofrece_Pedir()
    {
        var hoy = DiaDeNegocio.Hoy();
        // Un próximo a vencer fuera de la ventana de 3 meses no es reclamable todavía; un requisito sin Documento (sin Id) tampoco
        // es un «Sin confirmar»: lo que falta del todo se pide desde su bloqueo, no desde «además».
        var otros = new[]
        {
            new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Seguro RC", EstadoDocumento.Proximo, hoy.AddYears(2)),
            new DocumentoRequeridoDto(null, Guid.NewGuid(), "Plan de emergencia", EstadoDocumento.SinConfirmar, null),
        };

        var cut = Pintar([Bloqueo(Ana, "Aptitud médica", SituacionDeRequisitoBloqueante.Vencido, AmbitoAplicacion.Empresa, empresaId: null, vencimiento: hoy.AddDays(-1))], otros, []);

        cut.Markup.Should().Contain("Seguro RC — caduca");
        cut.FindAll("button.motivo-bloqueo-pedir").Should().BeEmpty();
    }

    [Theory]
    [InlineData(EstadoDocumento.Vigente, false)]
    [InlineData(EstadoDocumento.Vencido, false)]
    [InlineData(EstadoDocumento.Faltante, false)]
    [InlineData(EstadoDocumento.Urgente, true)]
    [InlineData(EstadoDocumento.Proximo, true)]
    [InlineData(EstadoDocumento.EnTolerancia, true)]
    [InlineData(EstadoDocumento.SinConfirmar, true)]
    public void Solo_son_pendientes_que_no_bloquean_los_estados_previos_al_bloqueo(EstadoDocumento estado, bool esperado) =>
        MotivoBloqueoTrabajador.EsPendienteQueNoBloquea(estado).Should().Be(esperado);

    // ---- Integración con el acordeón del Centro 360 ----

    private sealed class MediatorFalso(IReadOnlyList<TrabajadorAsignacionDocumentacionDto> trabajadores) : IMediator
    {
        public List<object> Recibidas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Recibidas.Add(request);
            object respuesta = request switch
            {
                ObtenerAsignacionesDocumentacionPorCentroQuery => trabajadores,
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)[],
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[],
                ObtenerEmpresasParaSelectorQuery => (IReadOnlyList<EmpresaSelectorDto>)[],
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class SinArchivos : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SinConversor : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static TrabajadorAsignacionDocumentacionDto Fila(Guid trabajadorId, string nombre, params DocumentoRequeridoDto[] documentos) =>
        new(Guid.NewGuid(), trabajadorId, nombre, new DateOnly(2026, 1, 15), EstadoDocumento.Vencido, documentos,
            CumplimientoDocumental.Evaluar(documentos.Select(d => d.Estado)));

    private (IRenderedComponent<AcordeonAsignacionesCentro> Cut, MediatorFalso Mediator) PintarAcordeon(
        IReadOnlyList<DocumentacionBloqueantePendienteDto> bloqueos, params TrabajadorAsignacionDocumentacionDto[] trabajadores)
    {
        var mediator = new MediatorFalso(trabajadores);
        this.ConRolDeEscritura();
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IFileStorageService, SinArchivos>();
        Services.AddScoped<IConversorWordPdfService, SinConversor>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, Guid.NewGuid())
            .Add(a => a.CentroNombre, "Centro Norte")
            .Add(a => a.Bloqueos, bloqueos));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ruiz Peña, Ana"));
        return (cut, mediator);
    }

    [Fact]
    public void En_el_acordeon_solo_el_Trabajador_bloqueado_lleva_motivo_y_no_se_repite_el_Tipo_que_ya_bloquea()
    {
        var hoy = DiaDeNegocio.Hoy();
        var tipoBloqueante = Guid.NewGuid();
        var ana = Fila(Ana, "Ruiz Peña, Ana",
            new DocumentoRequeridoDto(Guid.NewGuid(), tipoBloqueante, "Aptitud médica", EstadoDocumento.Vencido, hoy.AddDays(-10)),
            new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Formación PRL", EstadoDocumento.Proximo, hoy.AddDays(6)));
        var luis = Fila(Luis, "Gil Mora, Luis",
            new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Formación PRL", EstadoDocumento.Proximo, hoy.AddDays(6)));
        var bloqueo = Bloqueo(Ana, "Aptitud médica", SituacionDeRequisitoBloqueante.Vencido, vencimiento: hoy.AddDays(-10)) with { TipoDocumentoId = tipoBloqueante };

        var (cut, _) = PintarAcordeon([bloqueo], ana, luis);

        cut.FindAll(".motivo-bloqueo").Should().ContainSingle("Luis no está bloqueado: su fila no lleva motivo, aunque tenga un próximo a vencer");
        var bloque = cut.Find(".motivo-bloqueo");
        bloque.TextContent.Should().Contain("Aptitud médica: venció el");
        bloque.TextContent.Should().Contain("Formación PRL — caduca", "lo que no bloquea sale como «además»");
        System.Text.RegularExpressions.Regex.Matches(bloque.TextContent, "Aptitud médica").Count.Should().Be(1,
            "el Tipo que ya sale como bloqueo no se repite entre los pendientes");
    }

    [Fact]
    public async Task En_el_acordeon_Pedir_abre_el_selector_de_reclamacion_apuntado_al_ambito_de_la_linea()
    {
        var hoy = DiaDeNegocio.Hoy();
        var ana = Fila(Ana, "Ruiz Peña, Ana", new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Aptitud médica", EstadoDocumento.Vencido, hoy.AddDays(-10)));
        var bloqueo = Bloqueo(Ana, "Aptitud médica", SituacionDeRequisitoBloqueante.Vencido, vencimiento: hoy.AddDays(-10));

        var (cut, mediator) = PintarAcordeon([bloqueo], ana);
        mediator.Recibidas.OfType<ObtenerTiposDocumentoQuery>().Should().BeEmpty("con el drawer cerrado no se carga el selector");

        await cut.Find("button.motivo-bloqueo-pedir").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => mediator.Recibidas.OfType<ObtenerTiposDocumentoQuery>().Should().ContainSingle()
            .Which.AmbitoAplicacion.Should().Be(AmbitoAplicacion.Trabajador));
        mediator.Recibidas.Should().NotContain(r => r.GetType().Name.Contains("EnviarReclamacion"), "«Pedir» no envía nada: lo envía el Gestor CAE desde el drawer");
    }
}

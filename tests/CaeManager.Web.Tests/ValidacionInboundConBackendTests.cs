using Bunit;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerColaValidacionInbound;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.EvidenciasInbound;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Inbound.Validacion;
using CaeManager.Web.Features.Inbound.Validacion.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// Las pantallas de validación del Tenant TCE sobre el backend de la demo mínima (DECISION_LOG 2026-11-17, opción b):
/// <see cref="ServicioValidacionInboundConBackend"/> con un mediador falso. Fija lo que cambia de comportamiento al
/// cablearlas: la cola pinta los pendientes reales, Validar y Rechazar mandan <see cref="RegistrarEvidenciaInboundCommand"/>
/// (Rechazar, con su causa y su motivo), el motivo sigue siendo obligatorio, un veredicto firme no ofrece Deshacer y el
/// documento resuelto deja la cola. Datos ficticios.
/// </summary>
public class ValidacionInboundConBackendTests : BunitContext
{
    private static readonly Guid Contrato = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid DocA = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid DocB = Guid.Parse("00000000-0000-0000-0000-0000000000d2");
    private static readonly Guid TipoTce = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    private readonly MediadorFalso _mediador = new();
    private readonly OpcionesValidacionInbound _opciones = new() { Habilitado = true };

    public ValidacionInboundConBackendTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<ToastService>();
        Services.AddSingleton<IMediator>(_mediador);
        Services.AddSingleton<ISender>(_mediador);
        Services.AddSingleton(Options.Create(_opciones));
        Services.AddScoped<IServicioValidacionInbound, ServicioValidacionInboundConBackend>();
        Services.AddScoped<ColaEnCursoValidacion>();
        _mediador.Cola.AddRange([Pendiente(DocA, "Ana Pérez"), Pendiente(DocB, "Luis Gil")]);
    }

    [Fact]
    public void La_opcion_de_Validar_viene_apagada_por_defecto() =>
        new OpcionesValidacionInbound().Habilitado.Should().BeFalse();

    [Theory]
    [InlineData("Development", "true", true)]
    [InlineData("Development", null, false)]
    [InlineData("Development", "false", false)]
    [InlineData("Production", "true", false)]
    [InlineData("Staging", "true", false)]
    public void La_opcion_solo_se_enciende_en_Development_con_su_clave(string entorno, string? valor, bool esperado)
    {
        var configuracion = new ConfigurationBuilder()
            .AddInMemoryCollection(valor is null ? [] : [new("Inbound:Validacion:Habilitado", valor)])
            .Build();
        var servicios = new ServiceCollection();
        servicios.AddSingleton<ISender>(_mediador);

        servicios.AddValidacionInbound(configuracion, new EntornoFalso(entorno));

        servicios.BuildServiceProvider().GetRequiredService<IOptions<OpcionesValidacionInbound>>().Value.Habilitado
            .Should().Be(esperado);
    }

    [Fact]
    public async Task Con_la_opcion_apagada_la_cola_se_ve_y_Validar_y_Rechazar_no_estan_disponibles()
    {
        _opciones.Habilitado = false;
        var servicio = Services.GetRequiredService<IServicioValidacionInbound>();

        var cola = await servicio.ObtenerColaAsync();
        var id = cola.Valor!.Documentos[0].Id;
        var documento = (await servicio.ObtenerDocumentoAsync(id)).Valor!;
        var validar = await servicio.ValidarAsync(new SolicitudValidar(id, documento.Emision, documento.Vencimiento, ConfirmadoConErrores: true, []));
        var rechazar = await servicio.RechazarAsync(new SolicitudRechazar(id, "Otro", "Falta la firma."));

        cola.Valor.Documentos.Should().HaveCount(2);
        validar.Should().Be(RespuestaValidacion<VeredictoRegistrado>.NoDisponible);
        rechazar.Should().Be(RespuestaValidacion<VeredictoRegistrado>.NoDisponible);
        _mediador.Comandos.Should().BeEmpty();
    }

    private sealed class EntornoFalso(string nombre) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = nombre;
        public string ApplicationName { get; set; } = "CaeManager.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static DocumentoPendienteInboundDto Pendiente(Guid documento, string titular, Guid? tipo = null) =>
        new(Contrato, documento, "Formación PRL", tipo ?? TipoTce, Guid.NewGuid(), titular, "12345678Z", "Montajes Moncayo S.L.",
            "B12345674", "Planta Zaragoza Norte", new DateOnly(2026, 3, 1), new DateOnly(2027, 3, 1),
            EstadoVigenciaDocumento.VenceEnFecha, SoloConclusionAptitud: false);

    private static Guid Fila(Guid documento) => ServicioValidacionInboundConBackend.IdDeFila(Contrato, documento);

    private IRenderedComponent<ValidacionDocumento> Detalle(Guid documento)
    {
        var cut = Render<ValidacionDocumento>(p => p.Add(x => x.Id, Fila(documento)));
        // «Al terminar, pasar al siguiente» viene marcada: se desmarca para ver el estado del documento tras el veredicto.
        cut.FindAll("input[type=checkbox]").Last().Change(false);
        return cut;
    }

    private static void Pulsar(IRenderedComponent<ValidacionDocumento> cut, string texto) =>
        cut.FindAll("button").First(b => b.TextContent.Trim() == texto).Click();

    private void Confirmar(IRenderedComponent<ValidacionDocumento> cut) =>
        cut.FindAll("button").Last(b => b.TextContent.Contains("Validar")).Click();

    [Fact]
    public void La_cola_pinta_los_pendientes_del_backend_sin_inventar_una_lectura_con_IA()
    {
        var cut = Render<DocumentosPorValidar>();

        cut.Markup.Should().Contain("Ana Pérez").And.Contain("Luis Gil").And.Contain("Montajes Moncayo S.L.").And.Contain("Planta Zaragoza Norte");
        cut.Markup.Should().Contain("! Sin lectura con IA").And.NotContain("✓ Todo correcto").And.NotContain("0 sin detectar");
        cut.FindAll("tbody input[type=checkbox]").Should().OnlyContain(c => c.HasAttribute("disabled"), "sin lectura con IA nada entra en lote");
    }

    [Fact]
    public void El_documento_se_abre_con_Validar_habilitado_y_sin_descarga_ni_Deshacer_que_no_existen()
    {
        var cut = Detalle(DocA);

        cut.Find("section.vd-datos").TextContent.Should().Contain("Ana Pérez").And.Contain("12345678Z").And.Contain("No detectado",
            "sin lectura con IA se ve de quién es el documento por la ficha, sin inventar lo que pone el documento");
        cut.Markup.Should().Contain("La lectura con IA no está disponible todavía");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Validar").HasAttribute("disabled").Should().BeFalse();
        cut.FindAll("a.vd-descargar").Should().BeEmpty("no hay descarga del original: el enlace no se pinta");
        cut.Markup.Should().Contain("El documento todavía no se puede mostrar ni descargar aquí.");
    }

    [Fact]
    public void Validar_manda_el_registro_de_la_evidencia_y_el_documento_queda_resuelto_sin_ofrecer_Deshacer()
    {
        var cut = Detalle(DocA);

        Pulsar(cut, "Validar");
        // Sin lectura con IA la pantalla pide confirmarlo antes de registrar.
        _mediador.Comandos.Should().BeEmpty();
        Confirmar(cut);

        var comando = _mediador.Comandos.Should().ContainSingle().Subject;
        (comando.ContratoAccesoId, comando.DocumentoOrigenId, comando.TipoDocumentoId, comando.Conclusion, comando.Rechazo)
            .Should().Be((Contrato, DocA, TipoTce, (ConclusionAptitud?)null, (RechazoInicialEvidenciaInbound?)null));
        cut.Markup.Should().NotContain("Deshacer (", "sin B-22 el veredicto es firme");
        cut.FindAll("button").Where(b => b.TextContent.Trim() == "Validar").Should().OnlyContain(b => b.HasAttribute("disabled"),
            "el documento ya tiene veredicto");
    }

    [Fact]
    public void Rechazar_exige_explicacion_y_manda_causa_y_motivo_literal()
    {
        var cut = Detalle(DocA);

        Pulsar(cut, "Rechazar");
        cut.Find("textarea").Input("   ");
        Pulsar(cut, "Rechazar");
        _mediador.Comandos.Should().BeEmpty("sin explicación no sale ningún rechazo");

        cut.Find("select").Change("Fuera de vigencia");
        cut.Find("textarea").Input("La fecha de emisión es de 2019.");
        Pulsar(cut, "Rechazar");

        var comando = _mediador.Comandos.Should().ContainSingle().Subject;
        comando.Rechazo.Should().Be(new RechazoInicialEvidenciaInbound(CausaRechazoAcreditacion.CaducadoAlPresentar, "La fecha de emisión es de 2019."));
        cut.Markup.Should().NotContain("Deshacer (");
    }

    [Fact]
    public void Un_rechazo_del_servidor_se_dice_y_el_documento_sigue_pendiente()
    {
        _mediador.Respuesta = Result.Fallo<Guid>(EvidenciaInboundErrores.NoEncontrada);
        var cut = Detalle(DocA);

        Pulsar(cut, "Validar");
        Confirmar(cut);

        _mediador.Comandos.Should().ContainSingle();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Validar").HasAttribute("disabled").Should().BeFalse(
            "el veredicto no se registró: el documento sigue pendiente");
    }

    [Fact]
    public void Una_validacion_del_Command_se_dice_en_la_pantalla_y_el_documento_sigue_pendiente()
    {
        _mediador.Excepcion = new FluentValidation.ValidationException(
            [new FluentValidation.Results.ValidationFailure("Rechazo.Motivo", "Indica el motivo del rechazo.")]);
        var cut = Detalle(DocA);

        Pulsar(cut, "Rechazar");
        cut.Find("#vd-explicacion-rechazo").GetAttribute("maxlength").Should().Be("1000", "el límite del motivo literal");
        cut.Find("textarea").Input("Falta la firma.");
        Pulsar(cut, "Rechazar");

        _mediador.Comandos.Should().ContainSingle();
        Services.GetRequiredService<ToastService>().Mensajes.Select(m => m.Mensaje)
            .Should().Contain("Indica el motivo del rechazo.", "no cae al límite de errores de la página");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Validar").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Un_tipo_que_el_catalogo_del_Tenant_TCE_no_tiene_no_llega_al_backend()
    {
        _mediador.Cola.Clear();
        _mediador.Cola.Add(Pendiente(DocA, "Ana Pérez", tipo: Guid.Empty) with { TipoDocumentoId = null });
        var cut = Detalle(DocA);

        Pulsar(cut, "Validar");
        Confirmar(cut);

        _mediador.Comandos.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_documento_resuelto_deja_la_cola()
    {
        var servicio = Services.GetRequiredService<IServicioValidacionInbound>();
        var cut = Detalle(DocA);
        Pulsar(cut, "Validar");
        Confirmar(cut);
        _mediador.Cola.RemoveAll(p => p.DocumentoOrigenId == DocA);

        var cola = await servicio.ObtenerColaAsync();

        cola.Valor!.Documentos.Select(d => d.Titular).Should().Equal("Luis Gil");
    }

    private sealed class MediadorFalso : IMediator
    {
        public List<DocumentoPendienteInboundDto> Cola { get; } = [];
        public List<RegistrarEvidenciaInboundCommand> Comandos { get; } = [];
        public Result<Guid> Respuesta { get; set; } = Result.Exito(Guid.NewGuid());

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta = request switch
            {
                ObtenerColaValidacionInboundQuery => Cola.ToList(),
                RegistrarEvidenciaInboundCommand comando => Registrar(comando),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return Task.FromResult((TResponse)respuesta);
        }

        public Exception? Excepcion { get; set; }

        private Result<Guid> Registrar(RegistrarEvidenciaInboundCommand comando)
        {
            Comandos.Add(comando);
            return Excepcion is { } excepcion ? throw excepcion : Respuesta;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
            where TRequest : IRequest => throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}

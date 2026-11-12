using System.Reflection;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using Microsoft.AspNetCore.Components;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Documentos;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Un vencimiento manual a medias o ilegible llega al formulario como «»; guardar así crearía el Documento «sin vencimiento»
/// sin que nadie lo decidiera. DrawerGestionDocumento consume la bandera del campo y bloquea Guardar mientras sea true
/// (la regla «todo consumidor que guarda consume la bandera»).
/// </summary>
public class DrawerGestionDocumentoVencimientoNoValidoTests : BunitContext
{
    private static readonly Guid TipoManual = Guid.NewGuid();
    private static readonly Guid TipoAutomatico = Guid.NewGuid();

    private sealed class MediatorQueAnotaComandos : IMediator
    {
        public List<CrearDocumentoCommand> Creados { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta = request switch
            {
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[],
                // Un tipo sin vencimiento automático: el vencimiento se escribe a mano (RequiereVencimientoManual).
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)
                [
                    new TipoDocumentoListaDto(TipoManual, "Formación Art. 19", null, false, 1, AmbitoAplicacion.Trabajador, default, default,
                        null, null, null, null, false, false, false, default, []),
                    new TipoDocumentoListaDto(TipoAutomatico, "Certificado TGSS", 12, true, 2, AmbitoAplicacion.Trabajador, default, default,
                        null, null, null, null, false, false, false, default, []),
                ],
                CrearDocumentoCommand crear => Anotar(crear),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta);
        }

        private Result<Guid> Anotar(CrearDocumentoCommand comando)
        {
            Creados.Add(comando);
            return Result.Exito(Guid.NewGuid());
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class AlmacenQueNadieDebeTocar : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private readonly MediatorQueAnotaComandos _mediator = new();

    public DrawerGestionDocumentoVencimientoNoValidoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<IFileStorageService, AlmacenQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    }

    private async Task<IRenderedComponent<DrawerGestionDocumento>> AbrirAltaConTipoManualAsync()
    {
        var cut = Render<DrawerGestionDocumento>();
        await cut.InvokeAsync(() => cut.Instance.AbrirCrearAsync());
        await cut.InvokeAsync(() =>
        {
            Escribir(cut.Instance, "_trabajadorId", Guid.NewGuid().ToString());
            Escribir(cut.Instance, "_tipoDocumentoId", TipoManual.ToString());
        });
        cut.Render();
        return cut;
    }

    private static void Escribir(DrawerGestionDocumento instancia, string nombre, object valor)
    {
        var campo = typeof(DrawerGestionDocumento).GetField(nombre, BindingFlags.Instance | BindingFlags.NonPublic);
        campo.Should().NotBeNull($"el test necesita escribir {nombre} directamente");
        campo!.SetValue(instancia, valor);
    }

    private static Task PulsarGuardarAsync(IRenderedComponent<DrawerGestionDocumento> cut) =>
        cut.Find(".drawer-pie .boton-primario").ClickAsync(new MouseEventArgs());

    private static async Task EscribirVencimientoAsync(IRenderedComponent<DrawerGestionDocumento> cut, string texto) =>
        await cut.FindAll("input[data-campo-fecha]")[1].InputAsync(new ChangeEventArgs { Value = texto });

    [Theory]
    [InlineData("15/03")]
    [InlineData("10/1/2030")]
    public async Task Un_vencimiento_manual_a_medias_no_crea_el_documento_sin_vencimiento(string tecleado)
    {
        var cut = await AbrirAltaConTipoManualAsync();
        cut.FindAll("input[data-campo-fecha]").Should().HaveCount(2, "el tipo pide el vencimiento a mano: hay dos campos de fecha");

        await EscribirVencimientoAsync(cut, tecleado);
        await PulsarGuardarAsync(cut);

        _mediator.Creados.Should().BeEmpty("el texto sin fecha entera llega como «» y se guardaría como «sin vencimiento»");
        cut.Markup.Should().Contain("no es válida");
    }

    // Rediseño fail-closed: se parte de un vencimiento VÁLIDO y no nulo y se edita a medias sin salir del campo. Guardar no puede
    // crear el Documento con el vencimiento anterior a la edición (ni sin vencimiento).
    [Fact]
    public async Task Un_vencimiento_manual_a_medias_sobre_uno_valido_no_guarda_el_anterior()
    {
        var cut = await AbrirAltaConTipoManualAsync();
        await EscribirVencimientoAsync(cut, "15/03/2030");
        // El atributo value no se actualiza al teclear (CampoFecha lo reserva al render); el control lee el estado del drawer.
        var campo = typeof(DrawerGestionDocumento).GetField("_fechaVencimientoManual", BindingFlags.Instance | BindingFlags.NonPublic);
        campo.Should().NotBeNull();
        campo!.GetValue(cut.Instance).Should().Be("2030-03-15", "control: partimos de un vencimiento válido");

        await EscribirVencimientoAsync(cut, "15/03/203");
        await PulsarGuardarAsync(cut);

        _mediator.Creados.Should().BeEmpty("ni con 15/03/2030 anterior a la edición ni sin vencimiento");
        cut.Markup.Should().Contain("no es válida");
    }

    // Con un tipo de vencimiento automático el campo desaparece: el aviso de un envío anterior no puede quedarse sin campo.
    [Fact]
    public async Task Cambiar_a_un_tipo_de_vencimiento_automatico_retira_el_aviso_del_vencimiento_manual()
    {
        var cut = await AbrirAltaConTipoManualAsync();
        await EscribirVencimientoAsync(cut, "15/03");
        await PulsarGuardarAsync(cut);
        cut.Markup.Should().Contain("no es válida", "control: el intento rechazado avisa");

        await cut.InvokeAsync(() => cut.FindComponent<SelectorTipoDocumento>().Instance.ValorChanged.InvokeAsync(TipoAutomatico.ToString()));

        cut.FindAll("input[data-campo-fecha]").Should().HaveCount(1, "el tipo automático calcula el vencimiento: solo queda la emisión");
        cut.Markup.Should().NotContain("no es válida", "sin el campo, el aviso de su fecha ya no tiene a qué referirse");
    }

    // Ronda 4 de Codex: cambiar a un tipo automático borraba la marca del vencimiento manual; al volver al tipo manual el campo
    // nacía vacío y sin marca y Guardar creaba el Documento sin el vencimiento que se estaba escribiendo. La marca se conserva,
    // pero la guarda de Guardar solo la aplica mientras el tipo use el campo.
    [Fact]
    public async Task Volver_al_tipo_manual_tras_uno_automatico_conserva_la_marca_del_vencimiento_a_medias()
    {
        var cut = await AbrirAltaConTipoManualAsync();
        await EscribirVencimientoAsync(cut, "15/03");

        await cut.InvokeAsync(() => cut.FindComponent<SelectorTipoDocumento>().Instance.ValorChanged.InvokeAsync(TipoAutomatico.ToString()));
        cut.FindAll("input[data-campo-fecha]").Should().HaveCount(1, "control: con el tipo automático el campo no existe");
        var guarda = typeof(DrawerGestionDocumento).GetMethod("BloquearGuardadoSiVencimientoNoValido", BindingFlags.Instance | BindingFlags.NonPublic);
        guarda.Should().NotBeNull();
        (await cut.InvokeAsync(() => (bool)guarda!.Invoke(cut.Instance, null)!)).Should().BeFalse(
            "con un tipo que no usa el campo, su marca no bloquea Guardar");

        await cut.InvokeAsync(() => cut.FindComponent<SelectorTipoDocumento>().Instance.ValorChanged.InvokeAsync(TipoManual.ToString()));
        var campos = cut.FindAll("input[data-campo-fecha]");
        campos.Should().HaveCount(2);
        campos[1].GetAttribute("aria-invalid").Should().Be("true", "el campo recreado nace con la marca y su aviso a la vista");
        await PulsarGuardarAsync(cut);

        _mediator.Creados.Should().BeEmpty("volver al tipo manual no convierte el vencimiento a medias en «sin vencimiento»");
        cut.Markup.Should().Contain("no es válida");
    }

    // La marca conservada del vencimiento no cuenta mientras el tipo no use el campo: con un tipo automático, corregir la emisión
    // retira el aviso aunque el vencimiento manual siga marcado (si no, el aviso se quedaría sin ninguna fecha a la vista que
    // corregir).
    [Fact]
    public async Task Con_un_tipo_automatico_corregir_la_emision_retira_el_aviso_aunque_el_vencimiento_manual_siga_marcado()
    {
        var cut = await AbrirAltaConTipoManualAsync();
        await EscribirVencimientoAsync(cut, "15/03");
        await cut.FindAll("input[data-campo-fecha]")[0].InputAsync(new ChangeEventArgs { Value = "01/02" });
        await PulsarGuardarAsync(cut);
        cut.Markup.Should().Contain("no es válida", "control: el intento rechazado avisa");

        await cut.InvokeAsync(() => cut.FindComponent<SelectorTipoDocumento>().Instance.ValorChanged.InvokeAsync(TipoAutomatico.ToString()));
        cut.Markup.Should().Contain("no es válida", "control: la emisión sigue a medias y a la vista");

        await cut.FindAll("input[data-campo-fecha]")[0].InputAsync(new ChangeEventArgs { Value = "01/02/2026" });

        cut.Markup.Should().NotContain("no es válida", "con el tipo automático solo queda la emisión, y ya es una fecha");
    }

    // Un vencimiento a medias no cambia el valor del formulario («»), pero es texto que se pierde al cerrar.
    [Fact]
    public async Task Un_vencimiento_manual_a_medias_cuenta_como_cambio_sin_guardar()
    {
        var cut = await AbrirAltaConTipoManualAsync();
        await cut.InvokeAsync(() => typeof(DrawerGestionDocumento)
            .GetMethod("FijarInstantaneaFormulario", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(cut.Instance, null));
        var drawer = () => cut.FindComponent<DrawerFormulario>().Instance;
        drawer().HayCambios().Should().BeFalse("control: sin tocar nada no hay nada que descartar");

        await EscribirVencimientoAsync(cut, "15/03");

        drawer().HayCambios().Should().BeTrue();
    }

    [Fact]
    public async Task Corregir_el_vencimiento_retira_el_aviso_y_deja_guardar()
    {
        var cut = await AbrirAltaConTipoManualAsync();
        await EscribirVencimientoAsync(cut, "15/03");
        await PulsarGuardarAsync(cut);
        _mediator.Creados.Should().BeEmpty("control positivo: a medias no se guarda");

        await EscribirVencimientoAsync(cut, "15/03/2030");
        cut.Markup.Should().NotContain("no es válida", "el aviso de un intento anterior ya no es cierto");
        await PulsarGuardarAsync(cut);

        _mediator.Creados.Should().ContainSingle().Which.FechaVencimientoManual.Should().Be(new DateOnly(2030, 3, 15));
    }
}

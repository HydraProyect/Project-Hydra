using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.TiposDocumento.Commands.EstablecerToleranciaClienteEmpresarial;
using CaeManager.Application.TiposDocumento.Queries.ObtenerToleranciasClienteEmpresarial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Clientes.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// La pantalla de la tolerancia por defecto del Cliente empresarial (sin mockup; se construye con el kit DrawerFormulario y el
/// patrón del selector del Centro). Lee con ObtenerToleranciasClienteEmpresarialQuery y escribe con
/// EstablecerToleranciaClienteEmpresarialCommand, solo lo que cambió.
/// </summary>
public class ToleranciasClienteEmpresarialDrawerTests : BunitContext
{
    private static readonly Guid ClienteEmpresarial = Guid.NewGuid();
    private static readonly Guid TipoPrl = Guid.NewGuid();
    private static readonly Guid TipoSeguro = Guid.NewGuid();

    public ToleranciasClienteEmpresarialDrawerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed class MediatorFalso : IMediator
    {
        public List<ToleranciaTipoDocumentoDto> Tolerancias { get; } =
        [
            new(TipoPrl, "Formación PRL", AmbitoAplicacion.Trabajador, 10),
            new(TipoSeguro, "Seguro RC", AmbitoAplicacion.Empresa, 0)
        ];

        public Result Resultado { get; set; } = Result.Exito();
        public Guid? FallaSoloElTipo { get; set; }
        public bool LaLecturaLanza { get; set; }
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            object? respuesta = request switch
            {
                ObtenerToleranciasClienteEmpresarialQuery => LaLecturaLanza
                    ? throw new InvalidOperationException("la base no responde")
                    : (IReadOnlyList<ToleranciaTipoDocumentoDto>)Tolerancias,
                EstablecerToleranciaClienteEmpresarialCommand c => FallaSoloElTipo is { } t && c.TipoDocumentoId != t
                    ? Result.Exito()
                    : Resultado,
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            };
            return Task.FromResult((TResponse)respuesta!);
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

    private MediatorFalso Registrar(MediatorFalso? mediador = null)
    {
        mediador ??= new MediatorFalso();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddLocalization();
        return mediador;
    }

    private IRenderedComponent<DrawerToleranciasClienteEmpresarial> Abrir(Action<bool>? visibleCambiado = null, Action? guardado = null) =>
        Render<DrawerToleranciasClienteEmpresarial>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.ClienteEmpresarialId, ClienteEmpresarial)
            .Add(x => x.VisibleChanged, (bool v) => visibleCambiado?.Invoke(v))
            .Add(x => x.OnGuardado, () => guardado?.Invoke()));

    private static IElement Guardar(IRenderedComponent<DrawerToleranciasClienteEmpresarial> cut) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar");

    [Fact]
    public void Al_abrir_lista_cada_tipo_con_su_tolerancia_actual_y_pide_las_de_ESTE_Cliente_empresarial()
    {
        var mediador = Registrar();

        var cut = Abrir();

        mediador.Enviadas.OfType<ObtenerToleranciasClienteEmpresarialQuery>().Should().ContainSingle()
            .Which.ClienteEmpresarialId.Should().Be(ClienteEmpresarial);
        var selects = cut.FindAll("select");
        selects.Should().HaveCount(2);
        selects[0].GetAttribute("value").Should().Be("10");
        selects[1].GetAttribute("value").Should().Be("0");
        cut.Markup.Should().Contain("Formación PRL").And.Contain("Seguro RC (de la Empresa)");
    }

    [Fact]
    public void Sin_cambios_Guardar_esta_deshabilitado_y_dice_por_que()
    {
        Registrar();

        var cut = Abrir();

        var guardar = Guardar(cut);
        guardar.HasAttribute("disabled").Should().BeTrue();
        guardar.GetAttribute("title").Should().Be("No has cambiado ninguna tolerancia.");
    }

    [Fact]
    public async Task Guardar_envia_solo_lo_que_cambio_y_cierra()
    {
        var mediador = Registrar();
        bool? visible = null;
        var guardado = false;
        var cut = Abrir(v => visible = v, () => guardado = true);

        cut.FindAll("select")[1].Change("20");
        await cut.InvokeAsync(() => Guardar(cut).Click());

        mediador.Enviadas.OfType<EstablecerToleranciaClienteEmpresarialCommand>().Should().ContainSingle()
            .Which.Should().Be(new EstablecerToleranciaClienteEmpresarialCommand(ClienteEmpresarial, TipoSeguro, 20));
        visible.Should().BeFalse("guardado con éxito, el panel se cierra");
        guardado.Should().BeTrue();
    }

    [Fact]
    public async Task Una_tolerancia_personalizada_fuera_de_la_cota_no_se_envia_y_marca_el_campo()
    {
        var mediador = Registrar();
        var cut = Abrir();

        cut.FindAll("select")[0].Change("personalizado");
        // El campo tiene rebote: sin perder el foco el valor no llega a la pantalla y el test pasaría por una razón falsa
        // (campo vacío = inválido). Blur lo entrega, como el clic real en «Guardar».
        var campo = cut.Find("input[type=number]");
        campo.Input((TipoDocumentoCentro.ToleranciaMaximaDias + 1).ToString());
        campo.Blur();
        await cut.InvokeAsync(() => Guardar(cut).Click());

        mediador.Enviadas.OfType<EstablecerToleranciaClienteEmpresarialCommand>().Should().BeEmpty(
            "la cota es la del comando; la pantalla no manda un valor que el servidor rechazaría");
        cut.Markup.Should().Contain($"entre 0 y {TipoDocumentoCentro.ToleranciaMaximaDias}");
    }

    [Fact]
    public async Task Si_el_comando_falla_el_formulario_lo_dice_y_no_se_cierra()
    {
        var mediador = Registrar(new MediatorFalso
        {
            Resultado = Result.Fallo(Error.Crear("ToleranciaCliente.SinAcceso", "No tienes acceso a este Cliente empresarial."))
        });
        bool? visible = null;
        var cut = Abrir(v => visible = v);

        cut.FindAll("select")[1].Change("5");
        await cut.InvokeAsync(() => Guardar(cut).Click());

        cut.Markup.Should().Contain("No tienes acceso a este Cliente empresarial.");
        visible.Should().BeNull("un guardado fallido no cierra el panel");
        mediador.Enviadas.OfType<EstablecerToleranciaClienteEmpresarialCommand>().Should().ContainSingle();
    }

    /// <summary>
    /// Si la segunda fila falla, la primera ya está guardada: el formulario NO se recarga (borraría lo que la persona estaba
    /// editando), conserva los valores y, al reintentar, solo envía lo que aún no entró.
    /// </summary>
    [Fact]
    public async Task Si_falla_la_segunda_fila_se_conservan_las_ediciones_y_el_reintento_no_reenvia_la_primera()
    {
        var mediador = Registrar(new MediatorFalso
        {
            Resultado = Result.Fallo(Error.Crear("ToleranciaCliente.Falla", "No se pudo guardar el Seguro.")),
            FallaSoloElTipo = TipoSeguro
        });
        var cut = Abrir();

        cut.FindAll("select")[0].Change("30");
        cut.FindAll("select")[1].Change("15");
        await cut.InvokeAsync(() => Guardar(cut).Click());

        cut.Markup.Should().Contain("No se pudo guardar el Seguro.");
        mediador.Enviadas.OfType<ObtenerToleranciasClienteEmpresarialQuery>().Should().ContainSingle("un fallo no recarga el formulario");
        cut.FindAll("select")[0].GetAttribute("value").Should().Be("30");
        cut.FindAll("select")[1].GetAttribute("value").Should().Be("15", "lo que se estaba editando se conserva");

        mediador.Resultado = Result.Exito();
        await cut.InvokeAsync(() => Guardar(cut).Click());

        var enviadas = mediador.Enviadas.OfType<EstablecerToleranciaClienteEmpresarialCommand>().ToList();
        enviadas.Should().HaveCount(3, "primer intento: PRL y Seguro; reintento: solo el Seguro");
        enviadas[2].Should().Be(new EstablecerToleranciaClienteEmpresarialCommand(ClienteEmpresarial, TipoSeguro, 15));
    }

    [Fact]
    public void Si_la_lectura_falla_el_drawer_lo_dice_y_no_deja_guardar()
    {
        Registrar(new MediatorFalso { LaLecturaLanza = true });

        var cut = Abrir();

        cut.Markup.Should().Contain("No pudimos cargar las tolerancias");
        cut.FindAll("select").Should().BeEmpty();
        Guardar(cut).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Un_valor_guardado_fuera_de_las_opciones_predefinidas_sale_como_personalizado_con_su_numero()
    {
        var mediador = Registrar();
        mediador.Tolerancias[0] = new(TipoPrl, "Formación PRL", AmbitoAplicacion.Trabajador, 45);

        var cut = Abrir();

        cut.FindAll("select")[0].GetAttribute("value").Should().Be("personalizado");
        cut.Find("input[type=number]").GetAttribute("value").Should().Be("45");
    }
}

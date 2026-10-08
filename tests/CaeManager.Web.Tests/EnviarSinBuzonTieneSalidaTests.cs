using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// FS-15: enviar sin buzón de Microsoft 365 conectado tiene salida. El compositor ya no se cierra con un toast: dice por
/// qué no se puede enviar, enlaza a Conexiones de integración solo a quien puede abrirla (rol Administrador del Tenant
/// propietario de los datos) y deja siempre a mano copiar el texto y bajar el adjunto para enviarlo desde el correo propio.
/// </summary>
public class EnviarSinBuzonTieneSalidaTests : BunitContext
{
    public EnviarSinBuzonTieneSalidaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediator);
        Services.AddScoped<ToastService>();
    }

    private readonly MediatorDeBuzones _mediator = new();

    private sealed class MediatorDeBuzones : IMediator
    {
        public bool HayBuzon { get; set; }
        public List<EnviarMensajeNuevoCommand> Envios { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerConexionesIntegracionQuery:
                    IReadOnlyList<ConexionIntegracionListaDto> conexiones = HayBuzon
                        ? [new(Guid.NewGuid(), "cae@example.invalid", "CAE Norte", null, null, EstadoConexionIntegracion.Habilitada, DateTime.UtcNow, null, null)]
                        : [];
                    return Task.FromResult((TResponse)(object)conexiones);
                case EnviarMensajeNuevoCommand envio:
                    Envios.Add(envio);
                    return Task.FromResult((TResponse)(object)Result.Exito(Guid.NewGuid()));
                default:
                    throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.");
            }
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

    private const string RutaZip = "/visitas/11111111-1111-1111-1111-111111111111/paquete-documental.zip";

    private (IRenderedComponent<RedactarMensajeDrawer> Cut, List<bool> Cierres) AbrirCompositor(string rol, bool hayBuzon, bool conAdjunto = true)
    {
        this.ConRolDeEscritura(rol);
        _mediator.HayBuzon = hayBuzon;
        var cierres = new List<bool>();
        var cut = Render<RedactarMensajeDrawer>(p =>
        {
            p.Add(x => x.Visible, true)
                .Add(x => x.VisibleChanged, v => cierres.Add(v))
                .Add(x => x.DestinatariosIniciales, "acceso@centronorte.es")
                .Add(x => x.AsuntoInicial, "Solicitud de acceso")
                .Add(x => x.CuerpoInicial, "Adjuntamos la documentación vigente.");
            if (conAdjunto)
                p.Add(x => x.Adjunto, new AdjuntoParaEnviarDto("paquete.zip", "application/zip", [1, 2, 3]))
                    .Add(x => x.RutaDescargaAdjunto, RutaZip);
        });
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        return (cut, cierres);
    }

    private static AngleSharp.Dom.IElement BotonEnviar(IRenderedComponent<RedactarMensajeDrawer> cut) =>
        cut.FindAll(".drawer-panel button").Single(b => b.TextContent.Trim() == "Enviar");

    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.GestorCae)]
    public void Sin_buzon_el_compositor_sigue_abierto_dice_el_motivo_y_no_lanza_un_toast_que_se_va(string rol)
    {
        var (cut, cierres) = AbrirCompositor(rol, hayBuzon: false);

        cut.WaitForAssertion(() => cut.Find(".aviso-sin-buzon").TextContent.Should().Contain("no hay ningún buzón de Microsoft 365 conectado"));
        cierres.Should().BeEmpty("cerrarse solo dejaba al Gestor CAE sin saber qué hacer: era el flujo sin salida");
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty("el motivo se queda a la vista, no en un toast");
    }

    [Fact]
    public void Sin_buzon_el_rol_que_puede_conectar_tiene_el_enlace_a_Conexiones()
    {
        var (cut, _) = AbrirCompositor(Roles.Administrador, hayBuzon: false);

        cut.WaitForAssertion(() => cut.Find(".aviso-sin-buzon a.aviso-sin-buzon-conectar").GetAttribute("href").Should().Be("/integraciones"));
        cut.FindAll(".aviso-sin-buzon-pedir").Should().BeEmpty();
    }

    [Fact]
    public void Sin_buzon_el_rol_que_no_puede_conectar_no_recibe_un_enlace_que_no_abre_sino_a_quien_pedirselo()
    {
        var (cut, _) = AbrirCompositor(Roles.GestorCae, hayBuzon: false);

        cut.WaitForAssertion(() => cut.Find(".aviso-sin-buzon-pedir").TextContent.Should().Contain("rol Administrador de esta organización"));
        cut.FindAll(".aviso-sin-buzon a.aviso-sin-buzon-conectar").Should().BeEmpty("/integraciones exige el rol Administrador: enlazarlo sería otra puerta cerrada");
    }

    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.GestorCae)]
    public void Sin_buzon_siempre_quedan_copiar_el_texto_y_descargar_el_adjunto(string rol)
    {
        var (cut, _) = AbrirCompositor(rol, hayBuzon: false);

        cut.WaitForAssertion(() => cut.FindAll(".aviso-sin-buzon").Should().ContainSingle());
        var copiar = cut.FindComponent<BotonCopiar>().Instance;
        copiar.Valor.Should().Be("Solicitud de acceso\n\nAdjuntamos la documentación vigente.");
        cut.Find(".aviso-sin-buzon-para").TextContent.Should().Contain("acceso@centronorte.es");
        var descarga = cut.Find(".aviso-sin-buzon a.aviso-sin-buzon-descargar");
        descarga.GetAttribute("href").Should().Be(RutaZip);
        descarga.TextContent.Should().Contain("paquete.zip");
    }

    [Fact]
    public void Sin_buzon_y_sin_ruta_de_descarga_no_se_ofrece_un_enlace_vacio_pero_copiar_sigue()
    {
        var (cut, _) = AbrirCompositor(Roles.GestorCae, hayBuzon: false, conAdjunto: false);

        cut.WaitForAssertion(() => cut.FindAll(".aviso-sin-buzon").Should().ContainSingle());
        cut.FindAll(".aviso-sin-buzon-descargar").Should().BeEmpty();
        cut.FindComponents<BotonCopiar>().Should().ContainSingle();
    }

    [Fact]
    public async Task Sin_buzon_Enviar_esta_deshabilitado_con_su_motivo_y_no_envia_nada()
    {
        var (cut, _) = AbrirCompositor(Roles.GestorCae, hayBuzon: false);

        cut.WaitForAssertion(() => BotonEnviar(cut).HasAttribute("disabled").Should().BeTrue());
        BotonEnviar(cut).GetAttribute("title").Should().Be("No hay ningún buzón conectado desde el que enviar.");
        cut.FindAll(".drawer-panel input.campo-input").Should().BeEmpty("sin buzón no hay formulario que rellenar");
        await cut.InvokeAsync(() => { });
        _mediator.Envios.Should().BeEmpty();
    }

    [Fact]
    public void Con_buzon_el_compositor_es_el_de_siempre_sin_aviso_y_con_Enviar_habilitado()
    {
        var (cut, cierres) = AbrirCompositor(Roles.GestorCae, hayBuzon: true);

        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel input.campo-input").Should().NotBeEmpty());
        cut.FindAll(".aviso-sin-buzon").Should().BeEmpty();
        BotonEnviar(cut).HasAttribute("disabled").Should().BeFalse();
        cierres.Should().BeEmpty();
    }

    [Fact]
    public void El_aviso_sin_texto_que_copiar_no_pinta_un_boton_de_copiar_vacio()
    {
        this.ConRolDeEscritura(Roles.GestorCae);

        var cut = Render<AvisoSinBuzonCorreo>();

        cut.FindAll(".aviso-sin-buzon").Should().ContainSingle();
        cut.FindComponents<BotonCopiar>().Should().BeEmpty();
        cut.Find(".aviso-sin-buzon").TextContent.Should().Contain("desde tu propio correo");
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("Asunto", null, "Asunto\n\n")]
    [InlineData(null, "Cuerpo", "Cuerpo")]
    [InlineData("Asunto", "Cuerpo", "Asunto\n\nCuerpo")]
    public void El_texto_que_se_copia_es_el_asunto_y_debajo_el_cuerpo(string? asunto, string? cuerpo, string? esperado) =>
        AvisoSinBuzonCorreo.TextoDeCorreo(asunto, cuerpo).Should().Be(esperado);
}

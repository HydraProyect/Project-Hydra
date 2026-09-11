using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Facturacion.Commands.EliminarTarifaCliente;
using CaeManager.Application.Facturacion.Queries.ObtenerTarifasCliente;
using CaeManager.Application.Trabajadores.Commands.ResolverDeteccionAusente;
using CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPorEmpresa;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Empresas.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Dos acciones destructivas iban directas del botón al comando, con un solo
/// clic y sin confirmación, mientras el resto de pantallas usaban
/// <see cref="DialogoConfirmacion"/>:
/// <list type="bullet">
/// <item>«Dar de baja» en Detección de personal, que envía
/// <c>ResolverDeteccionAusenteCommand</c> con desactivar a true: elimina al
/// trabajador (soft delete) y le cierra las asignaciones.</item>
/// <item>«Eliminar» una tarifa en Facturación.</item>
/// </list>
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> qué comandos llegan al mediador y cuántas
/// veces, y qué avisa la pantalla. La propiedad — «nada destructivo sale hasta
/// confirmar» — se comprueba en el mediador, no en el marcado: un diálogo que
/// se pinta pero no impide el envío seguiría pasando una prueba que solo
/// mirase el DOM.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> el aspecto del diálogo, la autorización real de
/// los comandos (la ponen sus handlers y <c>AutorizacionEscrituraBehavior</c>,
/// probados en Application), la trampa de foco del modal, que es JavaScript, ni
/// el doble clic: esa guarda vive en <see cref="DialogoConfirmacion"/> y la
/// prueba <c>DialogoConfirmacionTests</c>.
/// </para>
/// </summary>
public class ConfirmacionAccionesDestructivasTests : BunitContext
{
    /// <summary><see cref="Modal"/> importa dialogo-foco.js al abrirse; queda fuera de lo que se observa aquí.</summary>
    public ConfirmacionAccionesDestructivasTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid EmpresaId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid DeteccionId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid ClienteId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid TarifaId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    /// <summary>
    /// Mediador que responde por tipo y, sobre todo, <b>registra todo lo que se
    /// le envía</b>: las aserciones de este fichero son sobre esa lista.
    /// </summary>
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

    private static IElement BotonDelDialogo<T>(IRenderedComponent<T> cut, string texto) where T : IComponent =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == texto);

    // ---------------------------------------------------------------- Detección de personal

    /// <summary>
    /// Por defecto el comando responde lo que haría el handler real con un
    /// trabajador activo: baja si se pidió, mantenido si no.
    /// </summary>
    /// <param name="alResolver">Sustituye esa respuesta, para simular un fallo o un trabajador que ya no estaba activo.</param>
    private (IRenderedComponent<DeteccionTrabajadores> Cut, MediatorRegistrador Mediator) RenderizarDeteccion(
        Func<ResolverDeteccionAusenteCommand, object>? alResolver = null)
    {
        var ausente = new DeteccionTrabajadorDto(
            DeteccionId, TipoDeteccion.Ausente, "Javier", "Salas Moreno", "12345678Z", Guid.NewGuid(), DateTime.UtcNow, AsignacionesActivas: 2);

        alResolver ??= c => Result.Exito(c.Desactivar
            ? ResultadoResolucionAusente.DadoDeBaja
            : ResultadoResolucionAusente.Mantenido);

        var mediator = new MediatorRegistrador(peticion => peticion switch
        {
            ObtenerEmpresaPorIdQuery => (object)new EmpresaDetalleDto(
                EmpresaId, "Montajes Ebro S.L.", null, DateTime.UtcNow, [], Guid.NewGuid()),
            ObtenerDeteccionesPorEmpresaQuery => Result.Exito<IReadOnlyList<DeteccionTrabajadorDto>>(new[] { ausente }),
            ResolverDeteccionAusenteCommand c => alResolver(c),
            _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
        });

        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();

        return (Render<DeteccionTrabajadores>(p => p.Add(c => c.EmpresaId, EmpresaId)), mediator);
    }

    private static IElement BotonDeFila(IRenderedComponent<DeteccionTrabajadores> cut, string texto) =>
        cut.FindAll("td button").Single(b => b.TextContent.Trim() == texto);

    private async Task PedirYConfirmarLaBaja(IRenderedComponent<DeteccionTrabajadores> cut)
    {
        await BotonDeFila(cut, "Dar de baja").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Dar de baja").ClickAsync(new MouseEventArgs());
    }

    [Fact]
    public async Task Pulsar_dar_de_baja_no_envia_nada_y_abre_la_confirmacion()
    {
        var (cut, mediator) = RenderizarDeteccion();

        await BotonDeFila(cut, "Dar de baja").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<ResolverDeteccionAusenteCommand>().Should().BeEmpty(
            "dar de baja elimina al trabajador y le cierra las asignaciones: no puede salir de un solo clic");
        cut.Find("[role=dialog] h2").TextContent.Should().Contain("Javier Salas Moreno",
            "quien confirma tiene que ver a quién está dando de baja");
        cut.Find(".modal-cuerpo p").TextContent.Should().Contain("se cierran sus 2 asignaciones vigentes",
            "quien confirma tiene que ver cuánto va a cerrar, no solo que algo se cierra");
    }

    [Fact]
    public async Task Cancelar_la_confirmacion_no_da_de_baja()
    {
        var (cut, mediator) = RenderizarDeteccion();

        await BotonDeFila(cut, "Dar de baja").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<ResolverDeteccionAusenteCommand>().Should().BeEmpty();
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public async Task Confirmar_da_de_baja_exactamente_una_vez_y_con_desactivar()
    {
        var (cut, mediator) = RenderizarDeteccion();

        await PedirYConfirmarLaBaja(cut);

        mediator.Enviados.OfType<ResolverDeteccionAusenteCommand>().Should().ContainSingle()
            .Which.Should().Be(new ResolverDeteccionAusenteCommand(DeteccionId, Desactivar: true));
        cut.FindAll("[role=dialog]").Should().BeEmpty("tras una baja aplicada el diálogo se cierra");
    }

    /// <summary>
    /// La otra mitad del contrato: la confirmación es para lo que destruye, no
    /// para todo. «Mantener activo» solo da la detección por resuelta, y pedir
    /// confirmación ahí sería fricción sin motivo.
    /// </summary>
    [Fact]
    public async Task Mantener_activo_sigue_siendo_un_clic_y_no_pregunta()
    {
        var (cut, mediator) = RenderizarDeteccion();

        await BotonDeFila(cut, "Mantener activo").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<ResolverDeteccionAusenteCommand>().Should().ContainSingle()
            .Which.Desactivar.Should().BeFalse();
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    /// <summary>
    /// Antes la baja solo tenía <c>finally</c>: una excepción del mediador subía
    /// sin avisar y el diálogo quedaba abierto sin explicación. Ahora avisa, y el
    /// diálogo sigue abierto a propósito para reintentar o cancelar.
    /// </summary>
    [Fact]
    public async Task Si_la_baja_revienta_avisa_y_deja_el_dialogo_abierto_para_reintentar()
    {
        var (cut, mediator) = RenderizarDeteccion(
            alResolver: _ => throw new InvalidOperationException("Base de datos caída (simulada)."));

        await PedirYConfirmarLaBaja(cut);

        mediator.Enviados.OfType<ResolverDeteccionAusenteCommand>().Should().ContainSingle(
            "se intentó una vez; lo que se observa es qué pasa cuando ese intento revienta");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Error);
        cut.FindAll("[role=dialog]").Should().ContainSingle("tras un fallo el diálogo sigue ahí para reintentar o cancelar");
    }

    /// <summary>
    /// El caso que el handler no distinguía: se pide la baja pero el trabajador
    /// ya no estaba activo. La pantalla no puede decir «dado de baja» por una
    /// baja que esta operación no hizo.
    /// </summary>
    [Fact]
    public async Task Si_el_trabajador_ya_no_estaba_activo_no_se_anuncia_una_baja()
    {
        var (cut, _) = RenderizarDeteccion(
            alResolver: _ => Result.Exito(ResultadoResolucionAusente.YaNoEstabaActivo));

        await PedirYConfirmarLaBaja(cut);

        var mensajes = Services.GetRequiredService<ToastService>().Mensajes;
        mensajes.Should().NotContain(m => m.Mensaje.Contains("dado de baja"),
            "esta operación no dio de baja a nadie");
        mensajes.Should().ContainSingle(m => m.Mensaje.Contains("ya no estaba activo"));
        cut.FindAll("[role=dialog]").Should().BeEmpty("la operación terminó: la detección queda cerrada");
    }

    // ---------------------------------------------------------------- Facturación

    private async Task<(IRenderedComponent<Features.Facturacion.Pages.Facturacion> Cut, MediatorRegistrador Mediator)> RenderizarFacturacionConTarifa()
    {
        var tarifa = new TarifaClienteDto(
            TarifaId, ClienteId, default, "Gestión documental mensual", 120m, "EUR", Guid.NewGuid());

        var mediator = new MediatorRegistrador(peticion => peticion switch
        {
            ObtenerClientesParaSelectorQuery => (object)new[] { new ClienteSelectorDto(ClienteId, "Refrielectric S.L.") },
            ObtenerTarifasClienteQuery => new List<TarifaClienteDto> { tarifa },
            EliminarTarifaClienteCommand => Result.Exito(),
            _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
        });

        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();

        var cut = Render<Features.Facturacion.Pages.Facturacion>();
        await cut.Find("#sel-cliente").ChangeAsync(new ChangeEventArgs { Value = ClienteId.ToString() });

        return (cut, mediator);
    }

    [Fact]
    public async Task Eliminar_una_tarifa_no_envia_nada_hasta_confirmar()
    {
        var (cut, mediator) = await RenderizarFacturacionConTarifa();

        await cut.Find("td button.enlace-peligro").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<EliminarTarifaClienteCommand>().Should().BeEmpty();
        cut.Find("[role=dialog] h2").TextContent.Should().Contain("Gestión documental mensual");
    }

    [Fact]
    public async Task Confirmar_elimina_esa_tarifa_una_sola_vez()
    {
        var (cut, mediator) = await RenderizarFacturacionConTarifa();

        await cut.Find("td button.enlace-peligro").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Eliminar").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<EliminarTarifaClienteCommand>().Should().ContainSingle()
            .Which.Should().Be(new EliminarTarifaClienteCommand(TarifaId));
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelar_no_elimina_la_tarifa()
    {
        var (cut, mediator) = await RenderizarFacturacionConTarifa();

        await cut.Find("td button.enlace-peligro").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        mediator.Enviados.OfType<EliminarTarifaClienteCommand>().Should().BeEmpty();
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }
}

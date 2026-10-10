using Bunit;
using CaeManager.Application.Documentos.Commands;
using CaeManager.Application.Documentos.Commands.ConfirmarVigenciaAcreditacion;
using CaeManager.Application.Documentos.Commands.RestaurarAnotacionAcreditacion;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// D-23: «Anotar vigencia» en bloque en la pestaña Plataformas CAE. La pantalla
/// no tiene comando propio: despacha ConfirmarVigenciaAcreditacionCommand por
/// cada fila marcada, así que cada una pasa por la misma autorización y el mismo
/// alcance que anotarla de una en una. Observa los comandos despachados y el
/// resumen de la cabecera; no observa la autorización real del handler.
/// </summary>
public sealed class PlataformaTabVigenciaEnBloqueTests : BunitContext
{
    private readonly Mediador _mediador = new();
    private readonly ToastService _toasts = new();

    public PlataformaTabVigenciaEnBloqueTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
        Services.AddSingleton<IMediator>(_mediador);
        Services.AddSingleton(_toasts);
        Services.AddLocalization();
    }

    [Fact]
    public void La_cabecera_cuenta_las_aceptadas_sin_vigencia()
    {
        var cut = Render<PlataformaTab>();

        cut.Find(".plataforma-proveedor-resumen").TextContent.Should().Contain("2 validada(s) sin vigencia");
    }

    [Fact]
    public async Task Anotar_vigencia_en_bloque_manda_el_comando_unitario_por_cada_fila_marcada()
    {
        var cut = Render<PlataformaTab>();

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Seleccionar sin vigencia").ClickAsync(new MouseEventArgs());
        await cut.FindAll("button").Single(b => b.TextContent.Contains("Anotar vigencia (2)")).ClickAsync(new MouseEventArgs());

        var fecha = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de vencimiento");
        await cut.InvokeAsync(() => fecha.Instance.ValorChanged.InvokeAsync("2027-03-01"));
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Guardar vigencia").ClickAsync(new MouseEventArgs());

        var comandos = _mediador.Recibidas.OfType<ConfirmarVigenciaAcreditacionCommand>().ToList();
        comandos.Should().HaveCount(2);
        comandos.Select(c => c.AcreditacionId).Should().BeEquivalentTo(_mediador.SinVigenciaIds);
        comandos.Should().OnlyContain(c => c.Vigencia.FechaVencimiento == new DateOnly(2027, 3, 1));
    }

    [Fact]
    public async Task Tras_anotar_en_bloque_el_aviso_ofrece_Deshacer_y_devuelve_cada_recibo_a_su_comando()
    {
        var cut = await AbrirVigenciaEnBloqueConFechaAsync();
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Guardar vigencia").ClickAsync(new MouseEventArgs());

        var aviso = _toasts.Mensajes.Single(t => t.TextoAccion == "Deshacer");
        await cut.InvokeAsync(aviso.OnAccion!);

        var restauraciones = _mediador.Recibidas.OfType<RestaurarAnotacionAcreditacionCommand>().ToList();
        restauraciones.Should().HaveCount(2);
        restauraciones.Select(r => r.AcreditacionId).Should().BeEquivalentTo(_mediador.SinVigenciaIds);
        restauraciones.Should().OnlyContain(r => r.EstadoPrevio == EstadoAcreditacion.Subida
            && r.VigenciaPrevia == VigenciaEnPlataforma.SinConfirmar && r.VersionEsperada == _mediador.VersionDe(r.AcreditacionId),
            "se devuelve el recibo que dio el servidor, no lo que la pantalla tenía cargado");
    }

    [Fact]
    public async Task Si_el_servidor_rechaza_deshacer_el_aviso_dice_por_que_y_no_recarga_en_falso()
    {
        var cut = await AbrirVigenciaEnBloqueConFechaAsync();
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Guardar vigencia").ClickAsync(new MouseEventArgs());
        _mediador.FallaRestaurarCon = "No se puede deshacer: esta acreditación cambió.";

        await cut.InvokeAsync(_toasts.Mensajes.Single(t => t.TextoAccion == "Deshacer").OnAccion!);

        _toasts.Mensajes.Should().Contain(t => t.Mensaje.Contains("0 de 2 deshechas") && t.Mensaje.Contains("cambió") && t.Tono == TonoToast.Advertencia);
    }

    private async Task<IRenderedComponent<PlataformaTab>> AbrirVigenciaEnBloqueConFechaAsync()
    {
        var cut = Render<PlataformaTab>();
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Seleccionar sin vigencia").ClickAsync(new MouseEventArgs());
        await cut.FindAll("button").Single(b => b.TextContent.Contains("Anotar vigencia (2)")).ClickAsync(new MouseEventArgs());
        var fecha = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de vencimiento");
        await cut.InvokeAsync(() => fecha.Instance.ValorChanged.InvokeAsync("2027-03-01"));
        return cut;
    }

    [Fact]
    public async Task Si_el_servidor_rechaza_todas_el_motivo_sale_en_el_aviso_fijo_y_el_modal_sigue_abierto_con_la_fecha()
    {
        _mediador.FallaConfirmarVigenciaCon = "La acreditación no admite vigencia.";
        var cut = await AbrirVigenciaEnBloqueConFechaAsync();

        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Guardar vigencia").ClickAsync(new MouseEventArgs());

        _mediador.Recibidas.OfType<ConfirmarVigenciaAcreditacionCommand>().Should().HaveCount(2, "control positivo: se intentó");
        cut.Find("[role=dialog] [role=alert]").TextContent.Trim().Should().Be("La acreditación no admite vigencia.");
        cut.FindAll("[role=dialog] .modal-cuerpo [role=alert]").Should().BeEmpty("el aviso va fuera del cuerpo desplazable (D-20)");
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de vencimiento").Instance.Valor
            .Should().Be("2027-03-01", "se puede reintentar sin reescribir");
    }

    [Fact]
    public async Task El_error_de_la_fecha_vacia_se_va_al_cambiar_la_fecha_o_el_estado_y_no_deja_el_modal_sin_Escape()
    {
        var cut = Render<PlataformaTab>();
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Seleccionar sin vigencia").ClickAsync(new MouseEventArgs());
        await cut.FindAll("button").Single(b => b.TextContent.Contains("Anotar vigencia (2)")).ClickAsync(new MouseEventArgs());

        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Guardar vigencia").ClickAsync(new MouseEventArgs());
        var campo = () => cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de vencimiento");
        campo().Instance.MensajeError.Should().NotBeNullOrWhiteSpace("control positivo: la fecha vacía se rechaza en el campo");
        _mediador.Recibidas.OfType<ConfirmarVigenciaAcreditacionCommand>().Should().BeEmpty();

        await cut.InvokeAsync(() => campo().Instance.ValorChanged.InvokeAsync("2027-03-01"));
        campo().Instance.MensajeError.Should().BeNull("el error era del intento anterior; si no, el kit sigue bloqueando Escape y el clic fuera");
    }

    [Fact]
    public async Task Cancelar_con_la_fecha_escrita_pregunta_y_sin_ella_cierra()
    {
        var cut = Render<PlataformaTab>();
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Seleccionar sin vigencia").ClickAsync(new MouseEventArgs());
        await cut.FindAll("button").Single(b => b.TextContent.Contains("Anotar vigencia (2)")).ClickAsync(new MouseEventArgs());
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().BeEmpty("sin nada escrito «Cancelar» cierra sin preguntar");

        cut = await AbrirVigenciaEnBloqueConFechaAsync();
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Descartar cambios", "con la fecha escrita «Cancelar» pregunta como la X");
    }

    [Fact]
    public void Sin_nada_marcado_el_boton_en_bloque_esta_deshabilitado_y_dice_por_que()
    {
        var cut = Render<PlataformaTab>();

        var boton = cut.FindAll("button").Single(b => b.TextContent.Contains("Anotar vigencia (0)"));
        boton.HasAttribute("disabled").Should().BeTrue();
        boton.GetAttribute("title").Should().NotBeNullOrWhiteSpace();
    }

    private sealed class Mediador : IMediator
    {
        public List<object> Recibidas { get; } = [];
        public List<Guid> SinVigenciaIds { get; } = [];
        public string? FallaConfirmarVigenciaCon { get; set; }
        public string? FallaRestaurarCon { get; set; }
        private readonly Dictionary<Guid, Guid> _versiones = [];
        public Guid VersionDe(Guid acreditacionId) => _versiones[acreditacionId];
        private readonly IReadOnlyList<ProveedorAcreditacionesDto> _datos;

        public Mediador()
        {
            var a = Fila("Documento A", EstadoAcreditacion.Aceptada);
            var b = Fila("Documento B", EstadoAcreditacion.Aceptada);
            SinVigenciaIds.AddRange([a.AcreditacionId, b.AcreditacionId]);
            _datos =
            [
                new ProveedorAcreditacionesDto(Guid.NewGuid(), "Dokify", "dokify",
                [
                    new ClienteAcreditacionesDto(Guid.NewGuid(), "Cliente Norte S.A.",
                        [a, b, Fila("Documento C", EstadoAcreditacion.PendienteDeSubir)])
                ])
            ];
        }

        private static AcreditacionDrillDownDto Fila(string tipo, EstadoAcreditacion estado) =>
            new(Guid.NewGuid(), Guid.NewGuid(), "Iker Etxeberria", tipo, estado, null);

        // El recibo lo fabrica el «servidor»: el valor previo es Subida/SinConfirmar aunque la fila de la
        // pantalla diga otra cosa, que es justo lo que el test de Deshacer necesita distinguir.
        private ResultadoAnotacionAcreditacionDto Recibo(Guid id)
        {
            var version = Guid.NewGuid();
            _versiones[id] = version;
            return new ResultadoAnotacionAcreditacionDto(id, EstadoAcreditacion.Subida, VigenciaEnPlataforma.SinConfirmar, version);
        }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Recibidas.Add(request);
            object valor = request switch
            {
                ObtenerAcreditacionesPorProveedorQuery => _datos,
                ConfirmarVigenciaAcreditacionCommand c => FallaConfirmarVigenciaCon is { } motivo
                    ? Result.Fallo<ResultadoAnotacionAcreditacionDto>(Error.Crear("vigencia.rechazada", motivo))
                    : Result.Exito(Recibo(c.AcreditacionId)),
                RestaurarAnotacionAcreditacionCommand => FallaRestaurarCon is { } fallo ? Result.Fallo(Error.Crear("Concurrencia.Conflicto", fallo)) : Result.Exito(),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult((TResponse)valor);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }
}

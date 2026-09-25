using System.Globalization;
using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P0-9b (FS-02 de la auditoría de flujos sin salida, 2026-09-24): «Corregir en
/// {plataforma}» aterrizaba en la pestaña Plataforma con todas las plataformas, la
/// fila Rechazada solo ofrecía «Marcar subido» (sin versión nueva) y la corrección
/// real vivía en otra pantalla sin enlace. La pestaña tiene que llevar a la
/// acreditación concreta y ofrecer sobre la Rechazada subir la versión corregida y
/// abrir el portal del canal.
/// </summary>
public sealed class PlataformaTabBucleCorreccionTests : BunitContext
{
    private static readonly Guid RechazadaId = Guid.NewGuid();
    private static readonly Guid DocumentoRechazadoId = Guid.NewGuid();
    private static readonly Guid PendienteOtraPlataformaId = Guid.NewGuid();

    private readonly Mediador _mediador = new();

    public PlataformaTabBucleCorreccionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton<IMediator>(_mediador);
        Services.AddSingleton(new ToastService());
    }

    [Fact]
    public void La_rechazada_ofrece_subir_la_version_corregida_y_abrir_el_portal_y_no_marcar_subido()
    {
        var cut = Render<PlataformaTab>(p => p.Add(x => x.OnSubirVersionCorregida, (Guid _) => { }));

        var fila = Fila(cut, RechazadaId);
        Botones(fila).Should().Equal("Subir versión corregida", "Marcar aceptado", "Marcar rechazado…");
        Botones(fila).Should().NotContain("Marcar subido", "sin versión nueva, MarcarAcreditacionSubidaCommand la rechaza");

        var portal = fila.QuerySelector("a")!;
        portal.TextContent.Trim().Should().Be("Abrir portal ↗");
        portal.GetAttribute("href").Should().Be("https://portal.nalanda.example/login");
        portal.GetAttribute("target").Should().Be("_blank");
        portal.GetAttribute("rel").Should().Contain("noopener");

        // Control positivo: la Pendiente de la otra plataforma sigue marcándose subida.
        Botones(Fila(cut, PendienteOtraPlataformaId)).Should().Contain("Marcar subido");
    }

    [Fact]
    public async Task Subir_version_corregida_entrega_el_documento_de_la_fila()
    {
        Guid? recibido = null;
        var cut = Render<PlataformaTab>(p => p.Add(x => x.OnSubirVersionCorregida, (Guid id) => recibido = id));

        await Fila(cut, RechazadaId).QuerySelectorAll("button")
            .Single(b => b.TextContent.Trim() == "Subir versión corregida")
            .ClickAsync(new MouseEventArgs());

        recibido.Should().Be(DocumentoRechazadoId);
    }

    [Fact]
    public void Sin_quien_abra_el_drawer_no_pinta_un_boton_que_no_haria_nada()
    {
        var cut = Render<PlataformaTab>();

        Botones(Fila(cut, RechazadaId)).Should().NotContain("Subir versión corregida");
    }

    [Fact]
    public async Task Recargar_desde_fuera_repinta_la_fila_ya_pendiente_de_subir()
    {
        var cut = Render<PlataformaTab>(p => p.Add(x => x.OnSubirVersionCorregida, (Guid _) => { }));
        _mediador.EstadoRechazada = EstadoAcreditacion.PendienteDeSubir;

        // Lo que hace Documentos tras guardar la versión corregida en su drawer.
        await cut.InvokeAsync(() => cut.Instance.RecargarAsync());

        Botones(Fila(cut, RechazadaId)).Should().Contain("Marcar subido")
            .And.NotContain("Subir versión corregida");
    }

    [Fact]
    public void Un_portal_no_navegable_no_se_convierte_en_enlace()
    {
        _mediador.UrlPortal = "javascript:alert(1)";

        var cut = Render<PlataformaTab>();

        Fila(cut, RechazadaId).QuerySelector("a").Should().BeNull("solo http(s), como en Centro 360");
    }

    [Fact]
    public void El_deep_link_muestra_solo_su_plataforma_y_resalta_su_fila()
    {
        var cut = Render<PlataformaTab>(p => p.Add(x => x.AcreditacionId, RechazadaId));

        cut.FindAll(".plataforma-proveedor").Should().ContainSingle()
            .Which.TextContent.Should().Contain("Nalanda");
        var fila = Fila(cut, RechazadaId);
        fila.ClassList.Should().Contain("plataforma-fila-documento--destacada");
        fila.GetAttribute("aria-current").Should().Be("true");
        fila.GetAttribute("tabindex").Should().Be("-1");
        cut.FindAll(".plataforma-fila-documento--destacada").Should().ContainSingle();
        cut.Find(".plataforma-aviso-destino").TextContent.Should().Contain("Nalanda");
    }

    [Fact]
    public async Task Ver_todas_las_plataformas_quita_el_filtro_del_deep_link()
    {
        var cut = Render<PlataformaTab>(p => p.Add(x => x.AcreditacionId, RechazadaId));

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Ver todas las plataformas")
            .ClickAsync(new MouseEventArgs());

        cut.FindAll(".plataforma-proveedor").Should().HaveCount(2);
    }

    [Fact]
    public void Un_deep_link_a_una_acreditacion_que_ya_no_esta_lo_dice_y_ensena_todas()
    {
        var cut = Render<PlataformaTab>(p => p.Add(x => x.AcreditacionId, Guid.NewGuid()));

        cut.Find(".plataforma-aviso-destino").TextContent.Should().Contain("ya no está pendiente");
        cut.FindAll(".plataforma-proveedor").Should().HaveCount(2);
        cut.FindAll(".plataforma-fila-documento--destacada").Should().BeEmpty();
    }

    [Fact]
    public void Sin_deep_link_no_hay_aviso_ni_fila_resaltada()
    {
        var cut = Render<PlataformaTab>();

        cut.FindAll(".plataforma-aviso-destino").Should().BeEmpty();
        cut.FindAll(".plataforma-fila-documento--destacada").Should().BeEmpty();
        cut.FindAll(".plataforma-proveedor").Should().HaveCount(2);
    }

    [Fact]
    public void Los_rotulos_nuevos_salen_en_catalan()
    {
        var anterior = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("ca-ES");
        try
        {
            var cut = Render<PlataformaTab>(p => p
                .Add(x => x.AcreditacionId, RechazadaId)
                .Add(x => x.OnSubirVersionCorregida, (Guid _) => { }));

            var fila = Fila(cut, RechazadaId);
            Botones(fila).Should().Contain("Puja la versió corregida");
            fila.QuerySelector("a")!.TextContent.Trim().Should().Be("Obre el portal ↗");
            cut.Find(".plataforma-aviso-destino").TextContent.Should().Contain("Mostra totes les plataformes");
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    private static AngleSharp.Dom.IElement Fila(IRenderedComponent<PlataformaTab> cut, Guid acreditacionId) =>
        cut.Find($".plataforma-fila-documento[data-acreditacion-id='{acreditacionId}']");

    private static List<string> Botones(AngleSharp.Dom.IElement fila) =>
        fila.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ToList();

    private sealed class Mediador : IMediator
    {
        public string UrlPortal { get; set; } = "portal.nalanda.example/login";
        public EstadoAcreditacion EstadoRechazada { get; set; } = EstadoAcreditacion.Rechazada;

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object valor = request switch
            {
                ObtenerAcreditacionesPorProveedorQuery => (IReadOnlyList<ProveedorAcreditacionesDto>)
                [
                    new ProveedorAcreditacionesDto(Guid.NewGuid(), "Nalanda", "nalanda",
                    [
                        new ClienteAcreditacionesDto(Guid.NewGuid(), "Cliente Norte S.A.",
                        [
                            new AcreditacionDrillDownDto(RechazadaId, DocumentoRechazadoId, "Iker Etxeberria", "Formación PRL",
                                EstadoRechazada, "Firma ilegible", UrlAccesoCanal: UrlPortal)
                        ])
                    ]),
                    new ProveedorAcreditacionesDto(Guid.NewGuid(), "Dokify", "dokify",
                    [
                        new ClienteAcreditacionesDto(Guid.NewGuid(), "Cliente Sur S.L.",
                        [
                            new AcreditacionDrillDownDto(PendienteOtraPlataformaId, Guid.NewGuid(), "Ane Garmendia", "Reconocimiento médico",
                                EstadoAcreditacion.PendienteDeSubir, null)
                        ])
                    ])
                ],
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

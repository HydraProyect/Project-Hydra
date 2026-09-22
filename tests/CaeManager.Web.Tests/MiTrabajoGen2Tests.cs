using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Web.Features.Bandeja;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MiTrabajoPagina = CaeManager.Web.Features.Bandeja.Pages.MiTrabajo;

namespace CaeManager.Web.Tests;

/// <summary>
/// Mi trabajo Gen2 (Nivel 1 multi-Tenant). Lo crítico es la navegación: cada
/// acción tiene que entrar en el Tenant propietario de SU fila, por el POST con
/// antiforgery de /cuenta/cliente-activo, y aterrizar en la pantalla exacta
/// (contrato Gen2 § 8). El resto fija la gramática del mockup: pliegue de
/// calendario, chips, carril de cartera y Tenant de origen aparte (§ 10).
/// </summary>
public class MiTrabajoGen2Tests : BunitContext
{
    public MiTrabajoGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid TenantOrigen = Guid.Parse("0a0a0a0a-0000-0000-0000-000000000001");
    private static readonly Guid TenantRefri = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid TenantDexter = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");
    private static readonly Guid DocumentoVencido = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid DocumentoDexter = Guid.Parse("d0000000-0000-0000-0000-000000000002");

    private static ItemBandejaDto Item(string id, TipoItemBandeja tipo, string titulo, string? cliente = null,
        Guid? documentoId = null, Guid? trabajadorId = null, Guid? tipoDocumentoId = null, string? proveedor = null) => new(
        Id: id, Tipo: tipo, Titulo: titulo, Subtitulo: $"Trabajador {id}", Fecha: new DateOnly(2026, 10, 1),
        TrabajadorId: trabajadorId ?? Guid.NewGuid(), CentroId: Guid.NewGuid(), DocumentoId: documentoId,
        TipoDocumentoId: tipoDocumentoId ?? Guid.NewGuid(), RequisitoId: null,
        ClienteId: cliente is null ? null : Guid.NewGuid(), ClienteNombre: cliente, ProveedorNombre: proveedor);

    private static MiTrabajoTenantDto Tenant(Guid id, string nombre, bool esOrigen,
        IReadOnlyList<ItemBandejaDto> bloqueoActuacion, IReadOnlyList<ItemBandejaDto>? proximos = null, IReadOnlyList<ItemBandejaDto>? seguimiento = null)
    {
        proximos ??= [];
        seguimiento ??= [];
        var bloqueos = bloqueoActuacion.Count(ObtenerMiTrabajoAgregadoQueryHandler.EsBloqueo);
        return new MiTrabajoTenantDto(id, nombre, esOrigen, ObtenerBandejaAgrupadaQueryHandler.Agrupar(bloqueoActuacion), proximos, seguimiento,
            new ResumenMiTrabajoTenantDto(id, nombre, esOrigen, bloqueoActuacion.Count + proximos.Count + seguimiento.Count,
                bloqueos, bloqueoActuacion.Count - bloqueos, proximos.Count, seguimiento.Count));
    }

    private static MiTrabajoAgregadoDto Cartera() => new(
    [
        Tenant(TenantOrigen, "ArcoSPA", esOrigen: true,
            [Item("o1", TipoItemBandeja.Vencido, "Formación del origen", "Cliente del origen")]),
        Tenant(TenantRefri, "Refrielectric", esOrigen: false,
            [
                Item("r1", TipoItemBandeja.Vencido, "Reconocimiento médico", "Transportes Planet Express", documentoId: DocumentoVencido),
                Item("r2", TipoItemBandeja.RequisitoPendiente, "Requisito del centro", "Hostelería Krusty Krab"),
                Item("r3", TipoItemBandeja.RevisionIa, "Lectura IA", "Hostelería Krusty Krab"),
            ],
            proximos: [Item("r4", TipoItemBandeja.VencimientoProximo, "EPI por vencer", "Transportes Planet Express")],
            seguimiento: [Item("r5", TipoItemBandeja.EnPlataformaSeguimiento, "Enviado a plataforma", "Hostelería Krusty Krab", proveedor: "CTAIMA")]),
        Tenant(TenantDexter, "Laboratorios Dexter", esOrigen: false,
            [Item("d1", TipoItemBandeja.PlataformaRechazada, "Rechazado por la plataforma", "Cervezas Duff Ibérica", documentoId: DocumentoDexter, proveedor: "Nalanda")]),
    ]);

    private sealed class MediadorFijo(Func<MiTrabajoAgregadoDto> respuesta) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => request switch
        {
            ObtenerMiTrabajoAgregadoQuery => Task.FromResult((TResponse)(object)respuesta()),
            _ => throw new NotSupportedException($"Petición no prevista: {request.GetType().Name}.")
        };

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-de-prueba", "__RequestVerificationToken");
    }

    private IRenderedComponent<MiTrabajoPagina> Renderizar(Func<MiTrabajoAgregadoDto>? respuesta = null)
    {
        Services.AddScoped<IMediator>(_ => new MediadorFijo(respuesta ?? Cartera));
        Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();
        return Render<MiTrabajoPagina>();
    }

    private static IElement FilaDe(IRenderedComponent<MiTrabajoPagina> cut, string titulo) =>
        cut.FindAll(".mi-trabajo-fila").Single(f => f.QuerySelector(".mi-trabajo-fila-titulo")!.TextContent == titulo);

    private static (string Accion, string Metodo, string Tenant, string ReturnUrl, string Token) Formulario(IElement contenedor)
    {
        var form = contenedor.QuerySelector("form[data-accion-cross-tenant]")!;
        string Campo(string nombre) => form.QuerySelector($"input[name='{nombre}']")!.GetAttribute("value")!;
        return (form.GetAttribute("action")!, form.GetAttribute("method")!, Campo("tenantId"), Campo("returnUrl"), Campo("__RequestVerificationToken"));
    }

    private static IReadOnlyList<string> TitulosVisibles(IRenderedComponent<MiTrabajoPagina> cut) =>
        cut.FindAll(".mi-trabajo-fila-titulo").Select(e => e.TextContent).ToList();

    [Fact]
    public void Cada_accion_entra_por_POST_con_antiforgery_en_el_Tenant_de_su_fila_y_en_la_pantalla_exacta()
    {
        var cut = Renderizar();

        var refri = Formulario(FilaDe(cut, "Reconocimiento médico"));
        refri.Should().Be(("/cuenta/cliente-activo", "post", TenantRefri.ToString(), $"/documentos?documentoId={DocumentoVencido}", "token-de-prueba"));

        var dexter = Formulario(FilaDe(cut, "Rechazado por la plataforma"));
        dexter.Tenant.Should().Be(TenantDexter.ToString(), "la fila de otro Tenant no puede heredar el Tenant de la primera tarjeta");
        dexter.ReturnUrl.Should().Be("/documentos?pestana=plataforma");
    }

    [Fact]
    public void RequisitoPendiente_sin_url_propia_aterriza_en_la_bandeja_del_Tenant()
    {
        var cut = Renderizar();

        var requisito = Formulario(FilaDe(cut, "Requisito del centro"));

        requisito.Tenant.Should().Be(TenantRefri.ToString());
        requisito.ReturnUrl.Should().Be("/bandeja");
    }

    [Fact]
    public void El_Tenant_de_origen_del_Operador_CAE_no_aparece_en_ninguna_parte()
    {
        var cut = Renderizar();

        TitulosVisibles(cut).Should().NotContain("Formación del origen");
        cut.FindAll(".mi-trabajo-grupo-nombre").Select(e => e.TextContent).Should().Equal("Refrielectric", "Laboratorios Dexter");

        var todo = cut.FindAll(".mi-trabajo-cartera-fila")[0];
        todo.QuerySelector(".mi-trabajo-cartera-total")!.TextContent.Should().Be("6", "5 de Refrielectric + 1 de Dexter; el origen no suma");

        cut.Markup.Should().NotContain("ArcoSPA", "contrato § 10: en la demo el Operador CAE no gestiona su propio Tenant desde aquí");
        cut.FindAll("input[name='tenantId']").Select(i => i.GetAttribute("value")).Should().NotContain(TenantOrigen.ToString());
    }

    [Fact]
    public void Ninguna_accion_manda_a_la_bandeja_de_una_organizacion_para_ver_su_cola_completa()
    {
        var cut = Renderizar();

        cut.Markup.Should().NotContain("cola completa", "contrato § 5: el resto de la cola se despliega aquí con «Ver»");
    }

    [Fact]
    public void Una_busqueda_encuentra_lo_plegado_en_calendario_y_el_total_del_grupo_la_refleja()
    {
        var cut = Renderizar();

        cut.Find(".mi-trabajo-filtro").Input("EPI");

        TitulosVisibles(cut).Should().Equal("EPI por vencer");
        cut.FindAll(".mi-trabajo-pliegue").Should().BeEmpty();
        cut.FindAll(".mi-trabajo-grupo-nombre").Select(e => e.TextContent).Should().Equal("Refrielectric");
        cut.Find(".mi-trabajo-grupo-total").TextContent.Should().Be("1");
    }

    [Fact]
    public void Una_busqueda_encuentra_lo_que_esta_en_una_organizacion_plegada()
    {
        var cut = Renderizar();
        cut.FindAll(".mi-trabajo-grupo-cabecera-tenant").First(c => c.TextContent.Contains("Refrielectric")).Click();

        cut.Find(".mi-trabajo-filtro").Input("Lectura");

        TitulosVisibles(cut).Should().Equal("Lectura IA");
    }

    [Fact]
    public void Proximo_y_seguimiento_se_pliegan_tras_el_resumen_de_calendario_hasta_abrirlo()
    {
        var cut = Renderizar();

        TitulosVisibles(cut).Should().NotContain(["EPI por vencer", "Enviado a plataforma"]);
        var pliegue = cut.Find(".mi-trabajo-pliegue");
        pliegue.TextContent.Should().Contain("2 vencimientos y envíos en calendario");

        pliegue.Click();

        TitulosVisibles(cut).Should().Contain(["EPI por vencer", "Enviado a plataforma"]);
        cut.FindAll(".mi-trabajo-pliegue").Should().BeEmpty();
    }

    [Fact]
    public void El_chip_de_bloqueos_deja_solo_los_bloqueos_y_los_recuentos_no_dependen_del_chip()
    {
        var cut = Renderizar();

        string Cuenta(string etiqueta) => cut.FindAll(".mi-trabajo-chip")
            .Single(c => c.TextContent.Trim().StartsWith(etiqueta)).QuerySelector(".mi-trabajo-chip-cuenta")!.TextContent;

        cut.FindAll(".mi-trabajo-chip").Single(c => c.TextContent.Trim().StartsWith("Bloqueos")).Click();

        TitulosVisibles(cut).Should().BeEquivalentTo("Reconocimiento médico", "Requisito del centro", "Rechazado por la plataforma");
        (Cuenta("Todas"), Cuenta("Bloqueos"), Cuenta("Actuación"), Cuenta("Próximo"), Cuenta("Seguimiento"))
            .Should().Be(("6", "3", "1", "1", "1"));
        cut.Find(".mi-trabajo-titular").TextContent.Should().Be("3 bloqueos que resolver hoy");
    }

    [Fact]
    public void Elegir_una_organizacion_del_carril_filtra_la_cola_a_ella()
    {
        var cut = Renderizar();

        cut.FindAll(".mi-trabajo-cartera-fila").Single(f => f.TextContent.Contains("Laboratorios Dexter")).Click();

        TitulosVisibles(cut).Should().Equal("Rechazado por la plataforma");
        cut.FindAll(".mi-trabajo-grupo-nombre").Select(e => e.TextContent).Should().Equal("Laboratorios Dexter");
    }

    [Fact]
    public void Plegar_la_cabecera_de_una_organizacion_esconde_sus_filas_y_resume_lo_que_tiene()
    {
        var cut = Renderizar();

        cut.FindAll(".mi-trabajo-grupo-cabecera-tenant").First(c => c.TextContent.Contains("Refrielectric")).Click();

        TitulosVisibles(cut).Should().Equal("Rechazado por la plataforma");
        cut.Find(".mi-trabajo-grupo-resumen").TextContent.Should().Be("2 bloqueos · 1 por actuar · 2 en calendario");
    }

    [Fact]
    public void Abrir_una_fila_pinta_el_detalle_con_organizacion_y_cliente_empresarial_separados()
    {
        var cut = Renderizar();

        FilaDe(cut, "Rechazado por la plataforma").Click();

        var detalle = cut.Find(".mi-trabajo-detalle");
        var datos = detalle.QuerySelectorAll("dd").Select(d => d.TextContent).ToList();
        datos.Should().ContainInOrder("Laboratorios Dexter", "Cervezas Duff Ibérica", "Nalanda");
        Formulario(detalle).Tenant.Should().Be(TenantDexter.ToString());
    }

    [Fact]
    public void Agrupar_por_severidad_pone_la_organizacion_en_el_contexto_de_cada_fila()
    {
        var cut = Renderizar();

        cut.FindAll(".mi-trabajo-pestana").Single(b => b.TextContent == "Severidad").Click();

        cut.FindAll(".mi-trabajo-grupo-nombre").Select(e => e.TextContent).Should().Equal("Bloqueo", "Requiere actuación", "Próximo", "Seguimiento");
        FilaDe(cut, "Rechazado por la plataforma").QuerySelector(".mi-trabajo-fila-contexto")!.TextContent
            .Should().Contain("Laboratorios Dexter");
    }

    [Fact]
    public void Si_la_consulta_falla_ofrece_reintentar()
    {
        var cut = Renderizar(() => throw new InvalidOperationException("caída"));

        cut.Markup.Should().Contain("No pudimos consultar tu cartera");
    }

    [Fact]
    public void Sin_trabajo_en_la_cartera_dice_que_esta_al_dia()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto([Tenant(TenantRefri, "Refrielectric", false, [])]));

        cut.Markup.Should().Contain("Cartera al día");
    }

    [Fact]
    public void La_pantalla_nunca_dice_tenant_al_usuario()
    {
        var cut = Renderizar();
        FilaDe(cut, "Reconocimiento médico").Click();

        cut.Find(".contenedor-pagina").TextContent.Should().NotContainEquivalentOf("tenant");
    }
}

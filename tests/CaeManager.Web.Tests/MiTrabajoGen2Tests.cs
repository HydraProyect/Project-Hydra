using System.Globalization;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
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
/// calendario, chips, carril de cartera, Tenant de origen fuera (§ 10) y
/// rótulos de pantalla (§ 14).
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
        IReadOnlyList<ItemBandejaDto> bloqueoActuacion, IReadOnlyList<ItemBandejaDto>? proximos = null, IReadOnlyList<ItemBandejaDto>? seguimiento = null,
        bool alcanceCero = false)
    {
        proximos ??= [];
        seguimiento ??= [];
        var bloqueos = bloqueoActuacion.Count(ObtenerMiTrabajoAgregadoQueryHandler.EsBloqueo);
        return new MiTrabajoTenantDto(id, nombre, esOrigen, ObtenerBandejaAgrupadaQueryHandler.Agrupar(bloqueoActuacion), proximos, seguimiento,
            new ResumenMiTrabajoTenantDto(id, nombre, esOrigen, bloqueoActuacion.Count + proximos.Count + seguimiento.Count,
                bloqueos, bloqueoActuacion.Count - bloqueos, proximos.Count, seguimiento.Count),
            alcanceCero);
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
            // Rechazada que el cálculo de su Centro de Trabajo cuenta como
            // bloqueante (D-7): la Query la entrega ya marcada, y por eso es Bloqueo.
            [Item("d1", TipoItemBandeja.PlataformaRechazada, "Rechazado por la plataforma", "Cervezas Duff Ibérica", documentoId: DocumentoDexter, proveedor: "Nalanda")
                with { RechazoBloqueaCentro = true }]),
    ]);

    /// <summary>Por defecto el usuario no es Gestor CAE: la Query de candidatos responde <c>SinPermiso</c>.</summary>
    private static Result<IReadOnlyList<CandidatoIncorporacionCarteraDto>> SinPermiso() =>
        Result.Fallo<IReadOnlyList<CandidatoIncorporacionCarteraDto>>(ErroresSolicitudCartera.SinPermiso);

    private static Result<IReadOnlyList<CandidatoIncorporacionCarteraDto>> Candidatos(params CandidatoIncorporacionCarteraDto[] candidatos) =>
        Result.Exito<IReadOnlyList<CandidatoIncorporacionCarteraDto>>(candidatos);

    private sealed class MediadorFijo(
        Func<MiTrabajoAgregadoDto> respuesta,
        Func<Result<IReadOnlyList<CandidatoIncorporacionCarteraDto>>> candidatos) : IMediator
    {
        public int ConsultasCandidatos { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) => request switch
        {
            ObtenerMiTrabajoAgregadoQuery => Task.FromResult((TResponse)(object)respuesta()),
            ObtenerCandidatosIncorporacionCarteraQuery => Task.FromResult((TResponse)(object)ContarCandidatos()),
            _ => throw new NotSupportedException($"Petición no prevista: {request.GetType().Name}.")
        };

        private Result<IReadOnlyList<CandidatoIncorporacionCarteraDto>> ContarCandidatos()
        {
            ConsultasCandidatos++;
            return candidatos();
        }

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

    private MediadorFijo? _mediador;

    private IRenderedComponent<MiTrabajoPagina> Renderizar(
        Func<MiTrabajoAgregadoDto>? respuesta = null,
        Func<Result<IReadOnlyList<CandidatoIncorporacionCarteraDto>>>? candidatos = null)
    {
        _mediador = new MediadorFijo(respuesta ?? Cartera, candidatos ?? SinPermiso);
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();
        Services.AddSingleton<ToastService>();
        Services.AddLocalization();
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

    [Theory]
    [InlineData("es-ES", "Mi trabajo", "Todas", "1 de octubre de 2026")]
    [InlineData("ca-ES", "La meva feina", "Totes", "octubre de 2026")]
    public void Los_textos_y_la_fecha_siguen_la_cultura_de_la_interfaz(string cultura, string titulo, string chipTodas, string fecha)
    {
        var (anterior, anteriorUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
        try
        {
            var cut = Renderizar();
            FilaDe(cut, "Reconocimiento médico").Click();

            cut.Find("h1").TextContent.Should().Contain(titulo);
            cut.Find(".mi-trabajo-chip").TextContent.Should().Contain(chipTodas);
            var plazo = cut.Find(".mi-trabajo-detalle-plazo").TextContent;
            plazo.Should().Contain(fecha);
            plazo.Should().NotContain("de de").And.NotContain("de d’", "el nombre de mes catalán ya lleva su preposición");
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (anterior, anteriorUi);
        }
    }

    private static ItemBandejaDto DeEmpresa(string subtitulo, bool? esPropia, string? empresa = "Refrielectric") =>
        Item("e1", TipoItemBandeja.PlataformaRechazada, "Seguro RC", "Transportes Planet Express") with
        {
            TrabajadorId = null,
            EmpresaId = Guid.NewGuid(),
            EmpresaNombre = empresa,
            Subtitulo = subtitulo,
            EmpresaEsPropia = esPropia
        };

    [Theory]
    [InlineData("Refrielectric", true, "Documentación de empresa")]
    [InlineData("Refrielectric — Firma caducada", true, "Documentación de empresa — Firma caducada")]
    [InlineData("Refrielectric — Firma caducada", false, "Subcontrata · Refrielectric — Firma caducada")]
    [InlineData("Refrielectric — Firma caducada", null, "Refrielectric — Firma caducada")]
    [InlineData("Otra razón social", true, "Otra razón social")]
    public void El_sujeto_de_una_tarea_de_Empresa_distingue_la_propia_de_la_Subcontrata(string subtitulo, bool? esPropia, string esperado)
    {
        var (anterior, anteriorUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");
        try
        {
            MiTrabajoVista.Sujeto(DeEmpresa(subtitulo, esPropia)).Should().Be(esperado);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (anterior, anteriorUi);
        }
    }

    [Fact]
    public void La_fila_y_el_detalle_de_una_tarea_de_la_Empresa_propia_la_rotulan_Documentacion_de_empresa()
    {
        var cartera = new MiTrabajoAgregadoDto(
        [
            Tenant(TenantRefri, "Refrielectric", esOrigen: false, [DeEmpresa("Refrielectric — Firma caducada", esPropia: true)]),
        ]);
        var cut = Renderizar(() => cartera);

        var fila = FilaDe(cut, "Seguro RC");
        fila.QuerySelector(".mi-trabajo-fila-sujeto")!.TextContent.Should().Be("· Documentación de empresa — Firma caducada");
        fila.Click();
        cut.Find(".mi-trabajo-detalle-sujeto").TextContent.Should().Be("Documentación de empresa — Firma caducada");
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
    public void Ninguna_accion_manda_a_la_bandeja_de_un_Tenant_para_ver_su_cola_completa()
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
    public void Una_busqueda_encuentra_lo_que_esta_en_un_Tenant_plegado()
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
    public void Elegir_un_Tenant_del_carril_filtra_la_cola_a_el()
    {
        var cut = Renderizar();

        cut.FindAll(".mi-trabajo-cartera-fila").Single(f => f.TextContent.Contains("Laboratorios Dexter")).Click();

        TitulosVisibles(cut).Should().Equal("Rechazado por la plataforma");
        cut.FindAll(".mi-trabajo-grupo-nombre").Select(e => e.TextContent).Should().Equal("Laboratorios Dexter");
    }

    [Fact]
    public void Plegar_la_cabecera_de_un_Tenant_esconde_sus_filas_y_resume_lo_que_tiene()
    {
        var cut = Renderizar();

        cut.FindAll(".mi-trabajo-grupo-cabecera-tenant").First(c => c.TextContent.Contains("Refrielectric")).Click();

        TitulosVisibles(cut).Should().Equal("Rechazado por la plataforma");
        cut.Find(".mi-trabajo-grupo-resumen").TextContent.Should().Be("2 bloqueos · 1 por actuar · 2 en calendario");
    }

    [Fact]
    public void Abrir_una_fila_pinta_el_detalle_con_Tenant_y_Cliente_empresarial_separados_y_rotulados_segun_el_contrato()
    {
        var cut = Renderizar();

        FilaDe(cut, "Rechazado por la plataforma").Click();

        // Contrato § 14: en pantalla el Tenant propietario es «Empresa» y el
        // Cliente empresarial es «Cliente»; nunca comparten rótulo.
        var detalle = cut.Find(".mi-trabajo-detalle");
        detalle.QuerySelectorAll("dt").Select(d => d.TextContent).Should().Equal("Empresa", "Cliente", "Plataforma CAE");
        detalle.QuerySelectorAll("dd").Select(d => d.TextContent).Should().Equal("Laboratorios Dexter", "Cervezas Duff Ibérica", "Nalanda");
        Formulario(detalle).Tenant.Should().Be(TenantDexter.ToString());
    }

    [Fact]
    public void Agrupar_por_severidad_pone_el_Tenant_en_el_contexto_de_cada_fila()
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
        cut.Markup.Should().NotContain("Sin Asignación de Cartera");
    }

    // P2.3 de la demo a Dirección: «Cartera al día» solo con alguna Empresa en
    // cartera con alcance y ningún pendiente. Con alcance cero —un Gestor CAE
    // sin Asignación de Cartera vigente, o el Administrador que ve los Tenants
    // delegados sin cartera en ellos— no hay nada que vigilar, y decir «al día»
    // afirmaría un cumplimiento que nadie ha mirado.

    [Fact]
    public void Con_alcance_cero_en_toda_la_cartera_no_dice_al_dia_sino_que_falta_la_Asignacion_de_Cartera()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto(
        [
            Tenant(TenantOrigen, "ArcoSPA", esOrigen: true, []),
            Tenant(TenantRefri, "Refrielectric", false, [], alcanceCero: true),
            Tenant(TenantDexter, "Laboratorios Dexter", false, [], alcanceCero: true),
        ]));

        var vacio = cut.Find(".mi-trabajo-cola");
        vacio.TextContent.Should().Contain("Sin Asignación de Cartera").And.Contain("Coordinador CAE");
        cut.Markup.Should().NotContain("Cartera al día").And.NotContain("Ningún bloqueo hoy");
        cut.Find(".mi-trabajo-titular").TextContent.Should().Be("Nada que vigilar todavía");
        cut.FindAll(".mi-trabajo-cartera-sub").Select(e => e.TextContent)
            .Should().Contain("Sin Asignación de Cartera").And.NotContain("Sin trabajo pendiente");
    }

    [Fact]
    public void Sin_ninguna_Empresa_en_cartera_tampoco_dice_al_dia()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto([Tenant(TenantOrigen, "ArcoSPA", esOrigen: true, [])]));

        cut.Find(".mi-trabajo-cola").TextContent.Should().Contain("Sin Asignación de Cartera");
        cut.Markup.Should().NotContain("Cartera al día");
    }

    [Fact]
    public void Con_alguna_Empresa_con_alcance_y_sin_pendientes_la_cartera_si_esta_al_dia()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto(
        [
            Tenant(TenantRefri, "Refrielectric", false, []),
            Tenant(TenantDexter, "Laboratorios Dexter", false, [], alcanceCero: true),
        ]));

        cut.Find(".mi-trabajo-cola").TextContent.Should().Contain("Cartera al día").And.NotContain("Sin Asignación de Cartera");
        cut.Find(".mi-trabajo-titular").TextContent.Should().Be("Ningún bloqueo hoy");
    }

    [Fact]
    public void Con_pendientes_en_cartera_muestra_la_cola_y_ningun_estado_vacio()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto(
        [
            Tenant(TenantRefri, "Refrielectric", false, [Item("r1", TipoItemBandeja.Vencido, "Reconocimiento médico", "Transportes Planet Express")]),
            Tenant(TenantDexter, "Laboratorios Dexter", false, [], alcanceCero: true),
        ]));

        TitulosVisibles(cut).Should().Equal("Reconocimiento médico");
        cut.Find(".mi-trabajo-cola").TextContent.Should().NotContain("Cartera al día").And.NotContain("Sin Asignación de Cartera");
    }

    [Fact]
    public void Al_elegir_una_Empresa_sin_alcance_dice_que_falta_su_Asignacion_de_Cartera()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto(
        [
            Tenant(TenantRefri, "Refrielectric", false, []),
            Tenant(TenantDexter, "Laboratorios Dexter", false, [], alcanceCero: true),
        ]));

        cut.FindAll(".mi-trabajo-cartera-fila").Single(f => f.TextContent.Contains("Laboratorios Dexter")).Click();

        cut.Find(".mi-trabajo-cola").TextContent.Should().Contain("Laboratorios Dexter, sin Asignación de Cartera");
        cut.Markup.Should().NotContain("Laboratorios Dexter, al día");
    }

    [Fact]
    public void Con_alcance_cero_un_Gestor_CAE_puede_pedir_la_incorporacion_desde_el_estado_vacio()
    {
        var cut = Renderizar(
            () => new MiTrabajoAgregadoDto([Tenant(TenantRefri, "Refrielectric", false, [], alcanceCero: true)]),
            () => Candidatos(CandidatoCatering));

        cut.Find(".mi-trabajo-cola").QuerySelectorAll("button").Select(b => b.TextContent.Trim())
            .Should().Contain("Añadir a mi cartera");
    }

    [Fact]
    public void Un_chip_de_severidad_sin_coincidencias_no_se_confunde_con_una_cartera_al_dia()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto(
            [Tenant(TenantRefri, "Refrielectric", false, [Item("r1", TipoItemBandeja.Vencido, "Reconocimiento médico", "Transportes Planet Express")])]));

        cut.FindAll(".mi-trabajo-chip").Single(c => c.TextContent.Trim().StartsWith("Seguimiento")).Click();

        cut.Markup.Should().Contain("Sin resultados para este filtro").And.NotContain("Cartera al día");
    }

    [Fact]
    public void La_pantalla_nunca_dice_tenant_al_usuario()
    {
        var cut = Renderizar();
        FilaDe(cut, "Reconocimiento médico").Click();

        cut.Find(".contenedor-pagina").TextContent.Should().NotContainEquivalentOf("tenant");
    }

    // «Añadir a mi cartera» (contrato Gen2 § 13): el botón existe solo si hay
    // alguna Empresa que pedir; para quien no es Gestor CAE, ni botón ni error.

    private static readonly CandidatoIncorporacionCarteraDto CandidatoCatering =
        new(Guid.Parse("c3c3c3c3-0000-0000-0000-000000000003"), "Catering Los Pollos", SolicitudPendienteId: null);

    private static IReadOnlyList<string> AccionesCabecera(IRenderedComponent<MiTrabajoPagina> cut) =>
        cut.FindAll(".cabecera-pagina button").Select(b => b.TextContent.Trim()).ToList();

    [Fact]
    public void Con_candidatos_la_cabecera_ofrece_anadir_a_mi_cartera_y_abre_el_dialogo_de_solicitud()
    {
        var cut = Renderizar(candidatos: () => Candidatos(CandidatoCatering));

        AccionesCabecera(cut).Should().Contain("Añadir a mi cartera");
        cut.Markup.Should().NotContain("Solicitar incorporación a cartera");

        cut.FindAll(".cabecera-pagina button").Single(b => b.TextContent.Trim() == "Añadir a mi cartera").Click();

        // El diálogo recarga los candidatos al abrirse: una consulta la hizo la página, otra el diálogo.
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Solicitar incorporación a cartera").And.Contain("Catering Los Pollos"));
        _mediador!.ConsultasCandidatos.Should().Be(2);
    }

    [Fact]
    public void Sin_candidatos_no_hay_boton_de_anadir_a_mi_cartera()
    {
        var cut = Renderizar(candidatos: () => Candidatos());

        AccionesCabecera(cut).Should().NotContain("Añadir a mi cartera");
        _mediador!.ConsultasCandidatos.Should().Be(1);
    }

    [Fact]
    public void Quien_no_es_Gestor_CAE_no_ve_el_boton_ni_un_mensaje_de_error()
    {
        var cut = Renderizar(candidatos: SinPermiso);

        AccionesCabecera(cut).Should().NotContain("Añadir a mi cartera");
        cut.Markup.Should().NotContain("Tu rol no permite esta acción.");
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty();
        _mediador!.ConsultasCandidatos.Should().Be(1);
    }

    [Theory]
    [InlineData("es-ES", "Añadir a mi cartera")]
    [InlineData("ca-ES", "Afegir a la meva cartera")]
    public void El_boton_de_anadir_a_mi_cartera_sigue_la_cultura_de_la_interfaz(string cultura, string rotulo)
    {
        var (anterior, anteriorUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
        try
        {
            var cut = Renderizar(candidatos: () => Candidatos(CandidatoCatering));

            AccionesCabecera(cut).Should().Contain(rotulo);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (anterior, anteriorUi);
        }
    }
}

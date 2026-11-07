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
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Bandeja;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
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
public partial class MiTrabajoGen2Tests : BunitContext
{
    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    public void PropagacionesReutilizaColaSinModificarTotalesNiAccionesExistentes(string rol)
    {
        var documento = Guid.NewGuid();
        var pendientes = new[]
        {
            Item("p1", TipoItemBandeja.PlataformaPendiente, "RNT", "Cliente Norte", documentoId: documento) with { CentroNombre = "Centro Norte", EsPropagacionPendiente = true },
            Item("p2", TipoItemBandeja.PlataformaPendiente, "RNT", "Cliente Sur", documentoId: documento) with { CentroNombre = "Centro Sur", EsPropagacionPendiente = true }
        };
        var datos = new MiTrabajoAgregadoDto([Tenant(TenantRefri, "Refrielectric", false, pendientes)]);
        var vista = new MiTrabajoVista(datos);
        vista.Propagaciones(new()).Should().HaveCount(2);
        vista.TotalCartera.Should().Be(2);
        vista.Propagaciones(new(Busqueda: "Centro Sur")).Should().ContainSingle();
        vista.Propagaciones(new(TenantId: TenantDexter)).Should().BeEmpty();
        vista.Propagaciones(new(Severidad: SeveridadMiTrabajo.Bloqueo)).Should().BeEmpty();

        var cut = Renderizar(() => datos, rol: rol);
        // Una sola acción por fila: «Gestionar»; el título es un enlace al documento, no una acción más.
        cut.FindAll(".propagaciones-fila button:not(.enlace-documento)").Should().HaveCount(2)
            .And.OnlyContain(b => b.TextContent.Trim() == "Gestionar");
        cut.FindAll(".propagaciones-fila button.enlace-documento").Should().HaveCount(2);
        cut.FindAll(".mi-trabajo-fila").Should().HaveCount(2);
        cut.Markup.Should().Contain("Empresa").And.Contain("Impacto").And.Contain("Cliente");
    }

    [Fact]
    public void ConsultaSigueFueraDeLosRolesAutorizadosDeMiTrabajo()
    {
        var autorizacion = typeof(MiTrabajoPagina).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();
        autorizacion.Roles!.Split(',').Should().NotContain(CaeManager.Infrastructure.Identity.Roles.Consulta);
    }

    /// <summary>
    /// ACC-1 / M-40: la fila se abre con teclado porque su control es un &lt;button&gt; real (Enter y
    /// Espacio los activa el navegador; bUnit no emula teclado, eso lo fija el E2E del Gestor CAE con
    /// cartera). Lo que sí fija este test: el control existe, es enfocable por defecto, lleva el nombre
    /// accesible «título · Tenant» y abre el detalle.
    /// </summary>
    [Fact]
    public void La_fila_se_abre_con_un_boton_real_con_nombre_accesible()
    {
        var cut = Renderizar(CarteraConFaltante);
        var fila = FilaDe(cut, "Falta Formación PRL");

        fila.GetAttribute("role").Should().BeNull("la fila ya no es un role=button que contiene controles");
        fila.GetAttribute("tabindex").Should().BeNull("el foco lo recibe el botón de la fila, no el contenedor");
        var abrir = fila.QuerySelectorAll("button[data-fila-abrir]").Should().ContainSingle().Subject;
        abrir.GetAttribute("type").Should().Be("button");
        abrir.GetAttribute("aria-label").Should().Contain("Falta Formación PRL").And.Contain(" · ");

        cut.FindAll(".mi-trabajo-detalle").Should().BeEmpty();
        abrir.Click();
        cut.Find(".mi-trabajo-detalle").TextContent.Should().Contain("Falta Formación PRL");
    }

    /// <summary>Ningún control interactivo (botón, enlace, formulario, campo) cuelga dentro de otro: ni de un role=button ni de un botón.</summary>
    [Fact]
    public void Ninguna_fila_anida_controles_interactivos_dentro_de_otro_control()
    {
        var cut = Renderizar(CarteraConFaltante);
        const string interactivos = "button, a[href], input, select, textarea, form, [role=button], [tabindex]";

        var filas = cut.FindAll(".mi-trabajo-fila");
        filas.Should().NotBeEmpty();
        foreach (var fila in filas)
        {
            foreach (var contenedor in fila.QuerySelectorAll("button, a[href], [role=button]").Concat(fila.Matches("[role=button]") ? [fila] : []))
                contenedor.QuerySelectorAll(interactivos).Should().BeEmpty($"«{contenedor.TextContent.Trim()}» no puede contener otros controles");

            // El control de abrir y las acciones de la fila son hermanos: el botón de abrir no contiene ningún formulario.
            fila.QuerySelectorAll("button[data-fila-abrir] form").Should().BeEmpty();
            fila.QuerySelectorAll("form[data-accion-cross-tenant] button[type=submit]").Should().NotBeEmpty();
        }
    }

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
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => request switch
        {
            ObtenerMiTrabajoPorPartesQuery => (IAsyncEnumerable<TResponse>)PartesDeMiTrabajo.De(respuesta(), null, cancellationToken),
            _ => throw new NotSupportedException()
        };
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-de-prueba", "__RequestVerificationToken");
    }

    private MediadorFijo? _mediador;

    /// <summary>
    /// La pantalla carga con el primer grupo desplegado y los demás plegados (maqueta, 11-06). La mayoría de los tests miran
    /// filas y «Vista y filtros», así que abren el panel y parten de «Expandir todas», como haría el Gestor CAE; los que fijan
    /// la carga inicial usan <see cref="RenderizarPlegado"/>.
    /// </summary>
    private IRenderedComponent<MiTrabajoPagina> Renderizar(
        Func<MiTrabajoAgregadoDto>? respuesta = null,
        Func<Result<IReadOnlyList<CandidatoIncorporacionCarteraDto>>>? candidatos = null,
        string? rol = null,
        string? url = null,
        bool interactivo = false)
    {
        var cut = RenderizarPlegado(respuesta, candidatos, rol, url, interactivo);
        ExpandirTodas(cut);
        return cut;
    }

    private IRenderedComponent<MiTrabajoPagina> RenderizarPlegado(
        Func<MiTrabajoAgregadoDto>? respuesta = null,
        Func<Result<IReadOnlyList<CandidatoIncorporacionCarteraDto>>>? candidatos = null,
        string? rol = null,
        string? url = null,
        bool interactivo = false)
    {
        _mediador = new MediadorFijo(respuesta ?? Cartera, candidatos ?? SinPermiso);
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();
        Services.AddSingleton<ToastService>();
        Services.AddLocalization();
        if (url is not null) Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        // El aviso de «sin acceso» necesita el renderizado interactivo que pinta la pila de toasts.
        if (interactivo) SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        if (rol is null) return Render<MiTrabajoPagina>();
        var estado = Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(
            new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, rol)], "prueba"))));
        return Render<MiTrabajoPagina>(p => p.AddCascadingValue(estado));
    }

    private static IElement FilaDe(IRenderedComponent<MiTrabajoPagina> cut, string titulo) =>
        cut.FindAll(".mi-trabajo-fila").Single(f => f.QuerySelector(".mi-trabajo-fila-titulo")!.TextContent == titulo);

    private static (string Accion, string Metodo, string Tenant, string ReturnUrl, string Token) Formulario(IElement contenedor)
    {
        var form = contenedor.QuerySelector("form[data-accion-cross-tenant]")!;
        string Campo(string nombre) => form.QuerySelector($"input[name='{nombre}']")!.GetAttribute("value")!;
        return (form.GetAttribute("action")!, form.GetAttribute("method")!, Campo("tenantId"), Campo("returnUrl"), Campo("__RequestVerificationToken"));
    }

    /// <summary>La vuelta a Mi trabajo sin filtros que lleva todo destino (RetornoMiTrabajo).</summary>
    private static readonly string Vuelta = $"volver={Uri.EscapeDataString("/mi-trabajo")}";

    /// <summary>«Vista y filtros» carga plegada salvo que la URL traiga un filtro: se abre para llegar a Empresa, Agrupar por, el filtro y Expandir todas.</summary>
    private static void AbrirVista(IRenderedComponent<MiTrabajoPagina> cut)
    {
        var alternar = cut.FindAll(".mi-trabajo-vista-alternar").FirstOrDefault();
        if (alternar is not null && alternar.GetAttribute("aria-expanded") != "true") alternar.Click();
    }

    /// <summary>Los tests que miran filas parten de todo desplegado, como haría el Gestor CAE con «Expandir todas».</summary>
    private static void ExpandirTodas(IRenderedComponent<MiTrabajoPagina> cut)
    {
        AbrirVista(cut);
        var boton = cut.FindAll(".mi-trabajo-plegar-todas").FirstOrDefault();
        var cabeceras = cut.FindAll(".mi-trabajo-grupo-cabecera");
        if (boton is null || cabeceras.Count == 0 || cabeceras.All(c => c.GetAttribute("aria-expanded") == "true")) return;
        // Con algún grupo desplegado el botón pliega todo (en cualquier cultura): primero pliega y después despliega.
        if (cabeceras.Any(c => c.GetAttribute("aria-expanded") == "true")) cut.Find(".mi-trabajo-plegar-todas").Click();
        cut.Find(".mi-trabajo-plegar-todas").Click();
    }

    private static IReadOnlyList<string> TitulosVisibles(IRenderedComponent<MiTrabajoPagina> cut) =>
        cut.FindAll(".mi-trabajo-fila-titulo").Select(e => e.TextContent).ToList();

    [Fact]
    public void Llegar_con_sinAcceso_avisa_una_vez_de_que_ya_no_hay_acceso_al_Tenant()
    {
        Services.AddSingleton<CaeManager.Application.Common.IAlertaOperativa>(new AlertaMudaMiTrabajo());
        var cut = Renderizar(url: "mi-trabajo?sinAcceso=true", interactivo: true);

        var toasts = Services.GetRequiredService<ToastService>().Mensajes;
        toasts.Should().ContainSingle("llegar desde Desasignarme avisa de que el Tenant ya no es accesible")
            .Which.Should().Match<ToastMensaje>(m => m.Tono == TonoToast.Advertencia && m.Mensaje == "Ya no tienes acceso a esa organización.");

        cut.Render();
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle("re-render no repite el aviso");
    }

    [Fact]
    public void Sin_sinAcceso_no_hay_aviso_de_acceso()
    {
        Services.AddSingleton<CaeManager.Application.Common.IAlertaOperativa>(new AlertaMudaMiTrabajo());
        Renderizar(interactivo: true);

        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty("control: la página sin el parámetro no avisa");
    }

    private sealed class AlertaMudaMiTrabajo : CaeManager.Application.Common.IAlertaOperativa
    {
        public void Emitir(string mensaje, CaeManager.Application.Common.NivelAlertaOperativa nivel) { }
        public void CapturarExcepcion(Exception excepcion) { }
        public void DejarMigaDePan(string mensaje) { }
        public IDisposable IniciarAmbitoDeCaptura() => new Nada();
        private sealed class Nada : IDisposable { public void Dispose() { } }
    }

    [Fact]
    public void Cada_accion_entra_por_POST_con_antiforgery_en_el_Tenant_de_su_fila_y_en_la_pantalla_exacta()
    {
        var cut = Renderizar();

        var refri = Formulario(FilaDe(cut, "Reconocimiento médico"));
        refri.Should().Be(("/cuenta/cliente-activo", "post", TenantRefri.ToString(), $"/documentos?documentoId={DocumentoVencido}&{Vuelta}", "token-de-prueba"));

        var dexter = Formulario(FilaDe(cut, "Rechazado por la plataforma"));
        dexter.Tenant.Should().Be(TenantDexter.ToString(), "la fila de otro Tenant no puede heredar el Tenant de la primera tarjeta");
        dexter.ReturnUrl.Should().Be($"/documentos?pestana=plataforma&{Vuelta}");
    }

    // ---- «Pedir» lo que falta (decisión 2026-10-04) ----

    private static readonly Guid TrabajadorQueFalta = Guid.Parse("c0000000-0000-0000-0000-0000000000f1");

    private static MiTrabajoAgregadoDto CarteraConFaltante() => new(
    [
        Tenant(TenantRefri, "Refrielectric", esOrigen: false,
            [
                Item("f1", TipoItemBandeja.Faltante, "Falta Formación PRL", "Transportes Planet Express", trabajadorId: TrabajadorQueFalta),
                Item("f2", TipoItemBandeja.Vencido, "Reconocimiento médico", "Transportes Planet Express", documentoId: DocumentoVencido),
                // Un «Faltante» sin Trabajador (no hay a quién pedírselo desde la cola) no ofrece Pedir.
                Item("f3", TipoItemBandeja.Faltante, "Falta Seguro RC", "Transportes Planet Express") with { TrabajadorId = null },
            ]),
    ]);

    [Fact]
    public void Una_fila_que_falta_ofrece_Pedir_que_entra_en_el_Tenant_de_su_fila_y_aterriza_en_la_cola_con_el_cajon_preseleccionado()
    {
        var cut = Renderizar(CarteraConFaltante);

        var fila = FilaDe(cut, "Falta Formación PRL");
        var pedir = fila.QuerySelector("form[data-accion-pedir]")!;
        pedir.GetAttribute("action").Should().Be("/cuenta/cliente-activo");
        pedir.GetAttribute("method").Should().Be("post");
        pedir.QuerySelector("input[name='tenantId']")!.GetAttribute("value").Should().Be(TenantRefri.ToString());
        pedir.QuerySelector("input[name='returnUrl']")!.GetAttribute("value").Should().Be($"/bandeja?pedir={TrabajadorQueFalta}&{Vuelta}");
        pedir.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value").Should().Be("token-de-prueba");
        pedir.QuerySelector("button")!.TextContent.Trim().Should().Be("Reclamar");

        // La acción primaria sigue siendo la suya (un solo marcador data-accion-cross-tenant por fila) y no se pisa.
        fila.QuerySelectorAll("form[data-accion-cross-tenant]").Should().ContainSingle();
        Formulario(fila).ReturnUrl.Should().StartWith($"/documentos?trabajadorId={TrabajadorQueFalta}&tipoDocumentoId=");
    }

    [Fact]
    public void Solo_ofrece_Pedir_la_fila_de_lo_que_falta_con_Trabajador()
    {
        var cut = Renderizar(CarteraConFaltante);

        FilaDe(cut, "Reconocimiento médico").QuerySelectorAll("form[data-accion-pedir]").Should().BeEmpty("un vencido ya tiene su camino de renovación");
        FilaDe(cut, "Falta Seguro RC").QuerySelectorAll("form[data-accion-pedir]").Should().BeEmpty("sin Trabajador no hay a quién pedírselo desde la cola");
        cut.FindAll("form[data-accion-pedir]").Should().ContainSingle();
    }

    [Fact]
    public void El_detalle_de_la_fila_que_falta_tambien_ofrece_Pedir()
    {
        var cut = Renderizar(CarteraConFaltante);
        FilaDe(cut, "Falta Formación PRL").Click();

        var pedir = cut.Find(".mi-trabajo-detalle form[data-accion-pedir]");
        pedir.QuerySelector("input[name='returnUrl']")!.GetAttribute("value").Should().StartWith($"/bandeja?pedir={TrabajadorQueFalta}");
        cut.FindAll(".mi-trabajo-detalle form[data-accion-cross-tenant]").Should().ContainSingle("la acción primaria del detalle sigue ahí");
    }

    [Theory]
    [InlineData("es-ES", "Reclamar")]
    [InlineData("ca-ES", "Reclamar")]
    public void Pedir_sigue_la_cultura_de_la_interfaz(string cultura, string texto)
    {
        var (anterior, anteriorUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
        try
        {
            var cut = Renderizar(CarteraConFaltante);

            FilaDe(cut, "Falta Formación PRL").QuerySelector("form[data-accion-pedir] button")!.TextContent.Trim().Should().Be(texto);
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (anterior, anteriorUi);
        }
    }

    [Theory]
    [InlineData("es-ES", "Reclama lo que falta de")]
    [InlineData("ca-ES", "Reclama el que falta de")]
    public void El_titulo_de_Pedir_dice_a_donde_lleva_en_la_cultura_de_la_interfaz(string cultura, string inicio)
    {
        var (anterior, anteriorUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
        try
        {
            var cut = Renderizar(CarteraConFaltante);

            FilaDe(cut, "Falta Formación PRL").QuerySelector("form[data-accion-pedir] button")!.GetAttribute("title").Should().StartWith(inicio).And.Contain("Refrielectric");
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (anterior, anteriorUi);
        }
    }

    [Theory]
    [InlineData("es-ES", "Mi trabajo", "Todos", "1 de octubre de 2026")]
    [InlineData("ca-ES", "La meva feina", "Tots", "octubre de 2026")]
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
    public void RequisitoPendiente_de_otro_Tenant_aterriza_en_los_requisitos_de_su_Centro()
    {
        var cut = Renderizar();

        var requisito = Formulario(FilaDe(cut, "Requisito del centro"));

        requisito.Tenant.Should().Be(TenantRefri.ToString());
        requisito.ReturnUrl.Should().StartWith("/centros/").And.Contain("?pestana=requisitos&").And.EndWith(Vuelta);
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
    public void El_chip_Bloquea_centro_deja_solo_los_bloqueos_sin_chip_de_tipo_propio_y_los_recuentos_no_dependen_del_chip()
    {
        var cut = Renderizar();

        string Cuenta(string etiqueta) => cut.FindAll(".mi-trabajo-chip")
            .Single(c => c.TextContent.Trim().StartsWith(etiqueta)).QuerySelector(".mi-trabajo-chip-cuenta")!.TextContent;

        cut.FindAll(".mi-trabajo-chip").Single(c => c.TextContent.Trim().StartsWith("Bloquea centro")).Click();

        TitulosVisibles(cut).Should().BeEquivalentTo("Requisito del centro", "Rechazado por la plataforma");
        (Cuenta("Todos"), Cuenta("Bloquea centro"), Cuenta("Gestiones"))
            .Should().Be(("6", "2", "0"), "el Vencido que bloquea cuenta en «Vencidos», no en «Bloquea centro»");
        // El titular cuenta la severidad Bloqueo (3, con el Vencido); el chip, solo los bloqueos sin chip de tipo propio (2).
        // Consecuencia declarada de la partición por tipo, a ratificar por Chris; no se toca el titular aquí.
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

    /// <summary>Maqueta (decisión del 2026-11-06 para la demo del 10-11): al cargar se despliega el primer grupo; los demás quedan plegados.</summary>
    [Fact]
    public void La_pantalla_carga_con_el_primer_grupo_desplegado_y_los_demas_plegados()
    {
        var cut = RenderizarPlegado();

        var cabeceras = cut.FindAll(".mi-trabajo-grupo-cabecera-tenant");
        cabeceras.Should().HaveCountGreaterThan(1);
        cabeceras[0].TextContent.Should().Contain("Refrielectric");
        cabeceras[0].GetAttribute("aria-expanded").Should().Be("true");
        cabeceras.Skip(1).Should().OnlyContain(c => c.GetAttribute("aria-expanded") == "false");
        TitulosVisibles(cut).Should().NotBeEmpty("el primer grupo enseña su trabajo urgente")
            .And.NotContain("Rechazado por la plataforma", "las filas de una Empresa plegada no se pintan");
        AbrirVista(cut);
        cut.Find(".mi-trabajo-plegar-todas").TextContent.Should().Be("Plegar todas", "hay un grupo desplegado");
    }

    /// <summary>El modo Severidad también carga con la primera cabecera desplegada, con cabeceras plegables y «Expandir/Plegar todas».</summary>
    [Fact]
    public void El_modo_Severidad_carga_con_la_primera_cabecera_desplegada_y_se_despliegan_una_a_una_o_todas()
    {
        var cut = RenderizarPlegado(url: "mi-trabajo?agrupar=severidad");

        var cabeceras = cut.FindAll(".mi-trabajo-grupo-cabecera-severidad");
        cabeceras.Should().HaveCountGreaterThan(2);
        cabeceras[0].GetAttribute("aria-expanded").Should().Be("true");
        cabeceras.Skip(1).Should().OnlyContain(c => c.GetAttribute("aria-expanded") == "false");
        TitulosVisibles(cut).Should().NotBeEmpty();
        cut.Find(".mi-trabajo-plegar-todas").TextContent.Should().Be("Plegar todas");

        cabeceras[1].Click();

        var tras = cut.FindAll(".mi-trabajo-grupo-cabecera-severidad");
        tras[0].GetAttribute("aria-expanded").Should().Be("true");
        tras[1].GetAttribute("aria-expanded").Should().Be("true");
        tras.Skip(2).Should().OnlyContain(c => c.GetAttribute("aria-expanded") == "false", "abrir una no abre las demás");

        cut.Find(".mi-trabajo-plegar-todas").Click();
        cut.Find(".mi-trabajo-plegar-todas").TextContent.Should().Be("Expandir todas", "con unas abiertas y otras plegadas el botón pliega todo");
        cut.FindAll(".mi-trabajo-grupo-cabecera-severidad").Should().OnlyContain(c => c.GetAttribute("aria-expanded") == "false");

        cut.Find(".mi-trabajo-plegar-todas").Click();
        cut.FindAll(".mi-trabajo-grupo-cabecera-severidad").Should().OnlyContain(c => c.GetAttribute("aria-expanded") == "true");

        cut.Find(".mi-trabajo-volver-plegar").Click();
        cut.FindAll(".mi-trabajo-grupo-cabecera-severidad").Should().OnlyContain(c => c.GetAttribute("aria-expanded") == "false");
        cut.FindAll(".mi-trabajo-volver-plegar").Should().BeEmpty();
    }

    [Fact]
    public void Expandir_todas_despliega_todo_y_cambia_su_rotulo_y_Volver_a_plegar_deja_todo_plegado()
    {
        var cut = RenderizarPlegado();
        AbrirVista(cut);
        cut.FindAll(".mi-trabajo-volver-plegar").Should().ContainSingle("el primer grupo está desplegado y se puede volver a plegar");
        cut.Find(".mi-trabajo-plegar-todas").TextContent.Should().Be("Plegar todas");

        cut.Find(".mi-trabajo-plegar-todas").Click();

        cut.FindAll(".mi-trabajo-grupo-cabecera-tenant").Should().OnlyContain(c => c.GetAttribute("aria-expanded") == "false");
        cut.Find(".mi-trabajo-plegar-todas").TextContent.Should().Be("Expandir todas");
        TitulosVisibles(cut).Should().BeEmpty();

        cut.Find(".mi-trabajo-plegar-todas").Click();

        cut.FindAll(".mi-trabajo-grupo-cabecera-tenant").Should().OnlyContain(c => c.GetAttribute("aria-expanded") == "true");
        cut.Find(".mi-trabajo-plegar-todas").TextContent.Should().Be("Plegar todas");
        TitulosVisibles(cut).Should().NotBeEmpty();

        cut.Find(".mi-trabajo-volver-plegar").Click();

        cut.FindAll(".mi-trabajo-grupo-cabecera-tenant").Should().OnlyContain(c => c.GetAttribute("aria-expanded") == "false");
        cut.Find(".mi-trabajo-plegar-todas").TextContent.Should().Be("Expandir todas");
        TitulosVisibles(cut).Should().BeEmpty();
    }

    [Fact]
    public void El_primer_grupo_desplegado_de_partida_se_puede_plegar_y_no_se_vuelve_a_abrir_al_repintar()
    {
        var cut = RenderizarPlegado();

        cut.FindAll(".mi-trabajo-grupo-cabecera-tenant")[0].Click();
        cut.Render();

        cut.FindAll(".mi-trabajo-grupo-cabecera-tenant").Should().OnlyContain(c => c.GetAttribute("aria-expanded") == "false");
        TitulosVisibles(cut).Should().BeEmpty();
    }

    /// <summary>
    /// «Reintentar la consulta» vuelve a cargar la cola, pero el primer grupo se abre una sola vez por visita: si el
    /// Gestor CAE lo plegó, la recarga no se lo vuelve a abrir.
    /// </summary>
    [Fact]
    public void Reintentar_la_consulta_no_vuelve_a_abrir_el_primer_grupo_que_el_Gestor_CAE_plego()
    {
        var cartera = Cartera();
        var cut = RenderizarPlegado(() => new MiTrabajoAgregadoDto(cartera.Tenants, [new TenantNoConsultadoDto(Guid.NewGuid(), "Otra Empresa", false)]));
        cut.FindAll(".mi-trabajo-grupo-cabecera-tenant")[0].GetAttribute("aria-expanded").Should().Be("true");

        cut.FindAll(".mi-trabajo-grupo-cabecera-tenant")[0].Click();
        cut.FindAll(".mi-trabajo-grupo-cabecera-tenant").Should().OnlyContain(c => c.GetAttribute("aria-expanded") == "false");

        cut.Find(".mi-trabajo-cartera-incompleta button").Click();

        cut.WaitForAssertion(() => cut.FindAll(".mi-trabajo-grupo-cabecera-tenant").Should().NotBeEmpty());
        cut.FindAll(".mi-trabajo-grupo-cabecera-tenant").Should().OnlyContain(c => c.GetAttribute("aria-expanded") == "false");
    }

    /// <summary>
    /// Lo que el Gestor CAE despliega vale para su visita a la pantalla y nada más: no se escribe ni se lee ningún
    /// almacenamiento del navegador, y una instancia nueva de la pantalla (otra carga) vuelve a partir con solo el
    /// primer grupo desplegado aunque la anterior terminara con otro Tenant desplegado.
    /// </summary>
    [Fact]
    public void Lo_desplegado_en_una_visita_no_se_restaura_en_la_siguiente()
    {
        var cut = RenderizarPlegado();
        cut.FindAll(".mi-trabajo-grupo-cabecera-tenant").First(c => c.TextContent.Contains("Laboratorios Dexter")).Click();
        cut.FindAll(".mi-trabajo-grupo-cabecera-tenant").First(c => c.TextContent.Contains("Laboratorios Dexter"))
            .GetAttribute("aria-expanded").Should().Be("true", "durante la visita se puede desplegar");
        TitulosVisibles(cut).Should().Contain("Rechazado por la plataforma");

        cut.Dispose();
        var siguiente = Render<MiTrabajoPagina>();

        var cabeceras = siguiente.FindAll(".mi-trabajo-grupo-cabecera-tenant");
        cabeceras[0].GetAttribute("aria-expanded").Should().Be("true");
        cabeceras.Skip(1).Should().OnlyContain(c => c.GetAttribute("aria-expanded") == "false");
        TitulosVisibles(siguiente).Should().NotContain("Rechazado por la plataforma");
        JSInterop.Invocations.Where(i => i.Identifier.Contains("Storage", StringComparison.OrdinalIgnoreCase)).Should()
            .BeEmpty("la expansión no se guarda en localStorage ni en ninguna preferencia");
    }

    [Fact]
    public void Plegar_la_cabecera_de_un_Tenant_esconde_sus_filas_y_resume_lo_que_tiene()
    {
        var cut = Renderizar();

        cut.FindAll(".mi-trabajo-grupo-cabecera-tenant").First(c => c.TextContent.Contains("Refrielectric")).Click();

        TitulosVisibles(cut).Should().Equal("Rechazado por la plataforma");
        cut.Find(".mi-trabajo-grupo-resumen").TextContent.Should().Be("2 bloqueos · 1 por actuar · 2 en calendario");
    }

    /// <summary>Maqueta: «Ordenar por [Impacto]» arriba a la derecha, en la cabecera; Cliente se conserva como segunda opción.</summary>
    [Fact]
    public void Ordenar_por_esta_en_la_cabecera_con_Impacto_y_Cliente_y_ya_no_en_la_barra_de_la_cola()
    {
        var cut = Renderizar();

        var selector = cut.Find("select[aria-label='Ordenar por']");
        selector.Closest(".cabecera-pagina").Should().NotBeNull("la maqueta lo pone arriba a la derecha, con el título");
        var opciones = selector.QuerySelectorAll("option").Select(o => o.TextContent.Trim()).ToList();
        opciones.Should().Equal("Impacto", "Cliente");
        opciones.Should().NotContain("Cliente empresarial", "CONTRATO_TERMINOLOGIA § 3.2, decisión de rótulo del 2026-10-09");
        selector.QuerySelector("option[selected]")!.TextContent.Trim().Should().Be("Impacto");
        cut.FindAll("[role=group][aria-label=Ordenar]").Should().BeEmpty("el orden ya no es una pestaña de la barra de la cola");
    }

    [Fact]
    public void Ordenar_por_Cliente_agrupa_las_filas_de_cada_Empresa_por_Cliente_y_Impacto_lo_deshace()
    {
        var cut = Renderizar();
        cut.FindAll(".mi-trabajo-subcabecera").Should().BeEmpty("por Impacto no hay subcabecera de Cliente");

        cut.Find("select[aria-label='Ordenar por']").Change("Cliente");

        cut.FindAll(".mi-trabajo-subcabecera").Should().NotBeEmpty("por Cliente cada Empresa enseña la subcabecera de su Cliente");
        cut.Find("select[aria-label='Ordenar por']").QuerySelector("option[selected]")!.TextContent.Trim().Should().Be("Cliente");

        cut.Find("select[aria-label='Ordenar por']").Change("Prioridad");

        cut.FindAll(".mi-trabajo-subcabecera").Should().BeEmpty();
    }

    /// <summary>«Vista y filtros» pliega lo que la maqueta no dibuja (Empresa, Agrupar por, filtro, Expandir todas): carga plegado.</summary>
    [Fact]
    public void Vista_y_filtros_carga_plegada_y_la_pantalla_no_dibuja_lo_que_la_maqueta_no_tiene()
    {
        var cut = RenderizarPlegado();

        var alternar = cut.Find(".mi-trabajo-vista-alternar");
        alternar.GetAttribute("aria-expanded").Should().Be("false");
        alternar.TextContent.Should().Contain("Vista y filtros");
        cut.FindAll(".mi-trabajo-barra, .mi-trabajo-cartera, .mi-trabajo-filtro, .mi-trabajo-plegar-todas, [aria-label='Agrupar por']").Should().BeEmpty();
        cut.FindAll(".mi-trabajo-vista-activos").Should().BeEmpty("sin ninguna opción activa no hay contador");
    }

    /// <summary>Retro: lo que la pantalla tenía en main y ahora vive tras «Vista y filtros» sigue ahí y funciona.</summary>
    [Fact]
    public void Vista_y_filtros_conserva_Empresa_Agrupar_por_el_filtro_de_la_cola_y_Expandir_todas()
    {
        var cut = RenderizarPlegado();

        cut.Find(".mi-trabajo-vista-alternar").Click();

        cut.Find(".mi-trabajo-vista-alternar").GetAttribute("aria-expanded").Should().Be("true");
        var panel = cut.Find("#mi-trabajo-vista");
        panel.QuerySelector(".mi-trabajo-cartera-etiqueta")!.TextContent.Should().Be("Empresa");
        panel.QuerySelector(".mi-trabajo-cartera-actual")!.TextContent.Should().Be("Toda mi cartera");
        panel.QuerySelectorAll("[role=group][aria-label='Agrupar por'] button").Select(b => b.TextContent.Trim()).Should().Equal("Empresa", "Severidad");
        panel.QuerySelector("input[type=search]")!.GetAttribute("placeholder").Should().NotBeNullOrWhiteSpace();
        panel.QuerySelector(".mi-trabajo-plegar-todas").Should().NotBeNull();
        cut.FindAll(".mi-trabajo-cartera-fila").Should().HaveCountGreaterThan(1, "Toda mi cartera y cada Empresa");

        panel.QuerySelectorAll("[role=group][aria-label='Agrupar por'] button")[1].Click();

        cut.FindAll(".mi-trabajo-grupo-cabecera-severidad").Should().NotBeEmpty("Agrupar por Severidad sigue funcionando desde el panel");
        cut.FindAll(".mi-trabajo-grupo-cabecera-tenant").Should().BeEmpty();
    }

    [Theory]
    [InlineData("mi-trabajo?agrupar=severidad")]
    [InlineData("mi-trabajo?q=Rechazado")]
    [InlineData("mi-trabajo?empresa=b2b2b2b2-0000-0000-0000-000000000002")]
    public void Un_filtro_de_la_URL_deja_Vista_y_filtros_abierta_de_partida(string url)
    {
        var cut = RenderizarPlegado(url: url);

        cut.Find(".mi-trabajo-vista-alternar").GetAttribute("aria-expanded").Should().Be("true", "un filtro activo no queda escondido");
    }

    [Fact]
    public void Con_Vista_y_filtros_plegada_el_boton_cuenta_las_opciones_activas()
    {
        var cut = RenderizarPlegado();
        cut.Find(".mi-trabajo-vista-alternar").Click();
        cut.FindAll("[role=group][aria-label='Agrupar por'] button")[1].Click();
        cut.Find("input.mi-trabajo-filtro").Input("Rechazado");

        cut.Find(".mi-trabajo-vista-alternar").Click();

        cut.Find(".mi-trabajo-vista-alternar").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find(".mi-trabajo-vista-activos").TextContent.Should().Be("2", "agrupación y búsqueda activas");
    }

    [Fact]
    public void Un_cambio_de_tipo_u_orden_no_reabre_Vista_y_filtros_que_el_Gestor_CAE_plego()
    {
        var cut = RenderizarPlegado();
        cut.Find(".mi-trabajo-vista-alternar").Click();
        cut.Find("input.mi-trabajo-filtro").Input("Rechazado");
        cut.Find(".mi-trabajo-vista-alternar").Click();

        cut.FindAll(".mi-trabajo-chip").Single(c => c.TextContent.Trim().StartsWith("Bloquea centro")).Click();
        cut.Find("select.mi-trabajo-ordenar-select").Change(nameof(OrdenMiTrabajo.Cliente));

        cut.Find(".mi-trabajo-vista-alternar").GetAttribute("aria-expanded").Should().Be("false");
        cut.Find(".mi-trabajo-vista-activos").TextContent.Should().Be("1");
    }
    [Fact]
    public void Abrir_una_fila_pinta_el_detalle_con_Tenant_y_Cliente_empresarial_separados_y_rotulados_segun_el_contrato()
    {
        var cut = Renderizar();

        FilaDe(cut, "Rechazado por la plataforma").Click();

        // Contrato § 14: en pantalla el Tenant propietario es «Empresa» y el
        // Cliente empresarial es «Cliente empresarial»; nunca comparten rótulo.
        var detalle = cut.Find(".mi-trabajo-detalle");
        detalle.QuerySelectorAll("dt").Select(d => d.TextContent).Should().Equal("Empresa", "Cliente", "Plataforma CAE");
        detalle.QuerySelectorAll("dd").Select(d => d.TextContent).Should().Equal("Laboratorios Dexter", "Cervezas Duff Ibérica", "Nalanda");
        Formulario(detalle).Tenant.Should().Be(TenantDexter.ToString());
    }

    [Fact]
    public void El_detalle_muestra_el_Centro_de_la_tarea_tras_la_Empresa_solo_si_el_item_lo_trae()
    {
        // Maqueta Flujos 11, «Detalle de la tarea»: Empresa, Centro y después Cliente.
        var datos = new MiTrabajoAgregadoDto([Tenant(TenantRefri, "Refrielectric", false,
        [
            Item("c1", TipoItemBandeja.Vencido, "Aptitud con Centro", "Cervezas Duff Ibérica", documentoId: DocumentoVencido) with { CentroNombre = "Sede Sevilla" },
            Item("c2", TipoItemBandeja.Vencido, "Aptitud sin Centro", "Cervezas Duff Ibérica", documentoId: DocumentoVencido),
        ])]);
        var cut = Renderizar(() => datos);

        FilaDe(cut, "Aptitud con Centro").Click();
        var detalle = cut.Find(".mi-trabajo-detalle");
        detalle.QuerySelectorAll("dt").Select(d => d.TextContent).Should().Equal("Empresa", "Centro", "Cliente");
        detalle.QuerySelectorAll("dd").Select(d => d.TextContent).Should().Equal("Refrielectric", "Sede Sevilla", "Cervezas Duff Ibérica");

        FilaDe(cut, "Aptitud sin Centro").Click();
        cut.Find(".mi-trabajo-detalle").QuerySelectorAll("dt").Select(d => d.TextContent).Should().Equal("Empresa", "Cliente");
    }

    [Fact]
    public void Sin_tarea_abierta_el_detalle_no_reserva_columna_y_con_tarea_abierta_si()
    {
        var cut = Renderizar();

        // Sin tarea abierta: ni panel ni tarjeta «Ninguna tarea abierta»; la
        // rejilla lleva el modificador de dos columnas y la cola gana el ancho.
        cut.FindAll(".mi-trabajo-detalle").Should().BeEmpty("sin tarea abierta el detalle no se pinta");
        cut.Markup.Should().NotContain("Ninguna tarea abierta");
        cut.Find(".mi-trabajo-paneles").ClassList.Should().Contain("mi-trabajo-paneles-sin-detalle");

        // Con tarea abierta: el panel existe, con nombre accesible, y la rejilla vuelve a tres columnas.
        FilaDe(cut, "Rechazado por la plataforma").Click();

        cut.Find(".mi-trabajo-detalle").GetAttribute("aria-label").Should().Be("Detalle de la tarea");
        cut.Find(".mi-trabajo-paneles").ClassList.Should().NotContain("mi-trabajo-paneles-sin-detalle");

        // Cerrarla lo contrae de nuevo.
        cut.FindAll(".mi-trabajo-detalle button").Single(b => b.TextContent.Trim() == "Cerrar detalle").Click();

        cut.FindAll(".mi-trabajo-detalle").Should().BeEmpty();
        cut.Find(".mi-trabajo-paneles").ClassList.Should().Contain("mi-trabajo-paneles-sin-detalle");
    }

    [Fact]
    public void Agrupar_por_severidad_pone_el_Tenant_en_el_contexto_de_cada_fila()
    {
        var cut = Renderizar();

        cut.FindAll(".mi-trabajo-pestana").Single(b => b.TextContent == "Severidad").Click();
        ExpandirTodas(cut);

        cut.FindAll(".mi-trabajo-grupo-nombre").Select(e => e.TextContent).Should().Equal("Bloqueo", "Requiere actuación", "Próximo", "Seguimiento");
        FilaDe(cut, "Rechazado por la plataforma").QuerySelector(".mi-trabajo-fila-contexto")!.TextContent
            .Should().Contain("Laboratorios Dexter");
    }

    [Fact]
    public void Si_la_consulta_falla_ofrece_reintentar()
    {
        var cut = Renderizar(() => throw new InvalidOperationException("caída"));

        cut.Markup.Should().Contain("No pudimos cargar tu trabajo");
    }

    [Fact]
    public void Sin_trabajo_en_la_cartera_dice_que_esta_al_dia()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto([Tenant(TenantRefri, "Refrielectric", false, [])]));

        cut.Markup.Should().Contain("La cola está vacía");
        cut.Markup.Should().NotContain("Sin Asignación de Cartera");
    }

    // P2.3 de la demo a Dirección: «La cola está vacía» solo con alguna Empresa en
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
        cut.Markup.Should().NotContain("La cola está vacía").And.NotContain("Ningún bloqueo hoy");
        cut.Find(".mi-trabajo-titular").TextContent.Should().Be("Nada que vigilar todavía");
        cut.FindAll(".mi-trabajo-cartera-sub").Select(e => e.TextContent)
            .Should().Contain("Sin Asignación de Cartera").And.NotContain("Sin trabajo pendiente");
    }

    // D-15 (residuo): al propio Coordinador CAE no se le dice «pídesela a tu Coordinador CAE»,
    // ni en la cartera entera ni con una Empresa filtrada.
    [Fact]
    public void Al_Coordinador_CAE_sin_alcance_no_se_le_remite_a_su_propio_rol()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto([Tenant(TenantOrigen, "ArcoSPA", esOrigen: true, [])]),
            rol: CaeManager.Infrastructure.Identity.Roles.CoordinadorCae);

        var vacio = cut.Find(".mi-trabajo-cola").TextContent;
        vacio.Should().Contain("Sin Asignación de Cartera").And.Contain("Gestores CAE de tu equipo").And.NotContain("Pídesela").And.NotContain("Pide a tu");
        cut.Find(".mi-trabajo-cola a[data-enlace=usuarios]").GetAttribute("href").Should().Be("/usuarios");
    }

    [Fact]
    public void Al_Coordinador_CAE_con_una_Empresa_filtrada_sin_alcance_tampoco_se_le_remite_a_su_propio_rol()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto(
        [
            Tenant(TenantOrigen, "ArcoSPA", esOrigen: true, []),
            Tenant(TenantRefri, "Refrielectric", false, [], alcanceCero: true),
        ]), rol: CaeManager.Infrastructure.Identity.Roles.CoordinadorCae);

        cut.FindAll(".mi-trabajo-cartera-fila").Single(f => f.TextContent.Contains("Refrielectric")).Click();

        var vacio = cut.Find(".mi-trabajo-cola").TextContent;
        vacio.Should().Contain("Refrielectric").And.Contain("Gestores CAE de tu equipo").And.NotContain("Pídesela").And.NotContain("Pide a tu");
    }

    [Fact]
    public void Al_Gestor_CAE_sin_alcance_se_le_sigue_remitiendo_a_su_Coordinador_CAE()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto([Tenant(TenantOrigen, "ArcoSPA", esOrigen: true, [])]),
            rol: CaeManager.Infrastructure.Identity.Roles.GestorCae);

        cut.Find(".mi-trabajo-cola").TextContent.Should().Contain("Pídesela a tu Coordinador CAE");
        cut.FindAll(".mi-trabajo-cola a[data-enlace=usuarios]").Should().BeEmpty("el Gestor CAE no asigna cartera");
    }

    [Fact]
    public void Sin_ninguna_Empresa_en_cartera_tampoco_dice_al_dia()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto([Tenant(TenantOrigen, "ArcoSPA", esOrigen: true, [])]));

        cut.Find(".mi-trabajo-cola").TextContent.Should().Contain("Sin Asignación de Cartera");
        cut.Markup.Should().NotContain("La cola está vacía");
    }

    [Fact]
    public void Con_alguna_Empresa_con_alcance_y_sin_pendientes_la_cartera_si_esta_al_dia()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto(
        [
            Tenant(TenantRefri, "Refrielectric", false, []),
            Tenant(TenantDexter, "Laboratorios Dexter", false, [], alcanceCero: true),
        ]));

        cut.Find(".mi-trabajo-cola").TextContent.Should().Contain("La cola está vacía").And.NotContain("Sin Asignación de Cartera");
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
        cut.Find(".mi-trabajo-cola").TextContent.Should().NotContain("La cola está vacía").And.NotContain("Sin Asignación de Cartera");
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
    public void Un_chip_de_tipo_sin_coincidencias_no_se_confunde_con_una_cartera_al_dia()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto(
            [Tenant(TenantRefri, "Refrielectric", false, [Item("r1", TipoItemBandeja.Vencido, "Reconocimiento médico", "Transportes Planet Express")])]));

        cut.FindAll(".mi-trabajo-chip").Single(c => c.TextContent.Trim().StartsWith("Detecciones")).Click();

        cut.Markup.Should().Contain("Nada con este filtro").And.NotContain("La cola está vacía");
    }

    /// <summary>
    /// FS-07: una Empresa de la cartera cuya cola no se pudo consultar no
    /// deja sin cola a las demás. Lo que sí cargó se pinta, y el aviso nombra
    /// la que falló; nunca se la da por «sin Asignación de Cartera» ni al día.
    /// </summary>
    [Fact]
    public void Si_una_Empresa_de_la_cartera_no_se_pudo_consultar_se_pinta_el_resto_y_se_nombra_la_que_fallo()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto(
            [Tenant(TenantRefri, "Refrielectric", false, [Item("r1", TipoItemBandeja.Vencido, "Reconocimiento médico", "Transportes Planet Express")])],
            [new TenantNoConsultadoDto(Guid.NewGuid(), "Laboratorios Dexter", EsOrigen: false)]));

        cut.Find(".mi-trabajo-cartera-incompleta").TextContent.Should().Contain("Laboratorios Dexter");
        TitulosVisibles(cut).Should().Contain("Reconocimiento médico");
        cut.Find(".mi-trabajo-cartera").TextContent.Should().NotContain("Laboratorios Dexter",
            "sin su cola no se sabe qué tiene pendiente: pintarla a cero la daría por al día");
    }

    [Fact]
    public void Si_no_se_pudo_consultar_ninguna_Empresa_de_la_cartera_no_se_dice_que_no_hay_nada_que_vigilar()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto(
            [], [new TenantNoConsultadoDto(Guid.NewGuid(), "Laboratorios Dexter", EsOrigen: false)]));

        cut.Find(".mi-trabajo-cartera-incompleta").TextContent.Should().Contain("Laboratorios Dexter");
        cut.Markup.Should().NotContain("sin Asignación de Cartera").And.NotContain("La cola está vacía")
            .And.NotContain("Ningún bloqueo hoy");
        cut.Markup.Should().Contain("Lo consultado está al día");
    }

    [Fact]
    public void Si_el_que_falla_es_el_Tenant_de_origen_la_cartera_no_esta_incompleta()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto(
            [Tenant(TenantRefri, "Refrielectric", false, [])],
            [new TenantNoConsultadoDto(TenantOrigen, "ArcoSPA", EsOrigen: true)]));

        cut.FindAll(".mi-trabajo-cartera-incompleta").Should().BeEmpty("el Tenant de origen no se gestiona desde Mi trabajo (§ 10)");
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

    private static MiTrabajoAgregadoDto CarteraConDiezPendientesPorTrabajador(Guid trabajador, int cuantos = 10) => new(
    [
        Tenant(TenantRefri, "Refrielectric", esOrigen: false,
            Enumerable.Range(1, cuantos)
                .Select(n => Item($"p{n}", TipoItemBandeja.PlataformaPendiente, $"Documento {n}", "Transportes Planet Express",
                    trabajadorId: trabajador, proveedor: "CTAIMA") with { Subtitulo = "Zoidberg Juan" })
                .Append(Item("o9", TipoItemBandeja.Vencido, "Reconocimiento médico", "Transportes Planet Express"))
                .ToList()),
    ]);

    [Fact]
    public void Las_gestiones_de_un_mismo_trabajador_se_agrupan_en_una_fila_y_el_total_no_cambia()
    {
        var trabajador = Guid.NewGuid();
        var cut = Renderizar(() => CarteraConDiezPendientesPorTrabajador(trabajador));

        cut.FindAll(".mi-trabajo-lote").Should().ContainSingle().Which.TextContent.Should().Contain("10 gestiones").And.Contain("Zoidberg Juan");
        TitulosVisibles(cut).Should().Equal("Reconocimiento médico");
        cut.Find(".mi-trabajo-grupo-total").TextContent.Should().Be("11");
    }

    [Fact]
    public void Abrir_el_lote_devuelve_cada_fila_con_su_accion_y_plegarlo_la_esconde()
    {
        var cut = Renderizar(() => CarteraConDiezPendientesPorTrabajador(Guid.NewGuid()));

        cut.Find(".mi-trabajo-lote").Click();

        TitulosVisibles(cut).Should().HaveCount(11);
        cut.FindAll(".mi-trabajo-fila-en-lote form[data-accion-cross-tenant]").Should().HaveCount(10);
        cut.Find(".mi-trabajo-lote").GetAttribute("aria-expanded").Should().Be("true");

        cut.Find(".mi-trabajo-lote").Click();

        TitulosVisibles(cut).Should().Equal("Reconocimiento médico");
    }

    [Fact]
    public void Una_busqueda_abre_los_lotes_para_alcanzar_cada_gestion()
    {
        var cut = Renderizar(() => CarteraConDiezPendientesPorTrabajador(Guid.NewGuid()));

        cut.Find("input.mi-trabajo-filtro").Input("Zoidberg");

        TitulosVisibles(cut).Should().HaveCount(10);
    }

    [Fact]
    public void Menos_de_tres_gestiones_del_trabajador_no_se_agrupan()
    {
        var cut = Renderizar(() => CarteraConDiezPendientesPorTrabajador(Guid.NewGuid(), cuantos: 2));

        cut.FindAll(".mi-trabajo-lote").Should().BeEmpty();
        TitulosVisibles(cut).Should().HaveCount(3);
    }

    [Fact]
    public void Un_bloqueo_nunca_se_pliega_en_un_lote()
    {
        var trabajador = Guid.NewGuid();
        var cut = Renderizar(() => new MiTrabajoAgregadoDto(
        [
            Tenant(TenantRefri, "Refrielectric", esOrigen: false,
                Enumerable.Range(1, 4).Select(n => Item($"b{n}", TipoItemBandeja.Vencido, $"Vencido {n}", "Cliente X", trabajadorId: trabajador)).ToList()),
        ]));

        cut.FindAll(".mi-trabajo-lote").Should().BeEmpty();
        TitulosVisibles(cut).Should().HaveCount(4);
    }

    [Fact]
    public void Dos_tramos_con_la_misma_clave_base_no_comparten_clave_ni_estado_de_apertura()
    {
        var trabajador = Guid.NewGuid();
        var ajeno = Guid.NewGuid();
        ItemBandejaDto P(string id, Guid quien) => Item(id, TipoItemBandeja.PlataformaPendiente, $"Doc {id}", "Cliente X", trabajadorId: quien, proveedor: "CTAIMA") with { Subtitulo = "Mismo centro" };
        var cut = Renderizar(() => new MiTrabajoAgregadoDto(
        [
            Tenant(TenantRefri, "Refrielectric", esOrigen: false,
                [P("a1", trabajador), P("a2", trabajador), P("a3", trabajador), P("b1", ajeno), P("a4", trabajador), P("a5", trabajador), P("a6", trabajador)]),
        ]));

        var lotes = cut.FindAll(".mi-trabajo-lote");
        lotes.Should().HaveCount(2);

        lotes[0].Click();

        cut.FindAll(".mi-trabajo-lote")[0].GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll(".mi-trabajo-lote")[1].GetAttribute("aria-expanded").Should().Be("false", "abrir uno no abre el otro");
    }

    [Fact]
    public async Task J_sobre_un_lote_plegado_abre_el_lote_y_enfoca_su_primera_fila()
    {
        var cut = Renderizar(() => CarteraConDiezPendientesPorTrabajador(Guid.NewGuid()));
        var manejar = typeof(MiTrabajoPagina).GetMethod("ManejarAtajoAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        // La primera fila en pantalla es el Vencido (Bloqueo); la segunda ya cae dentro del lote.
        await cut.InvokeAsync(() => (Task)manejar.Invoke(cut.Instance, ["j"])!);
        cut.Find(".mi-trabajo-lote").GetAttribute("aria-expanded").Should().Be("false");
        await cut.InvokeAsync(() => (Task)manejar.Invoke(cut.Instance, ["j"])!);

        cut.Find(".mi-trabajo-lote").GetAttribute("aria-expanded").Should().Be("true");
        cut.FindAll(".mi-trabajo-fila-enfocada").Should().ContainSingle();
    }

    // ── Filtros en la URL y vuelta desde la acción de una fila ────────────────

    private string UrlActual => Services.GetRequiredService<NavigationManager>().Uri;

    [Fact]
    public void Los_filtros_de_la_URL_se_leen_al_montar()
    {
        var cut = Renderizar(url: $"/mi-trabajo?tipo=bloqueacentro&empresa={TenantRefri}&q=Requisito&agrupar=severidad&orden=cliente");

        TitulosVisibles(cut).Should().BeEquivalentTo("Requisito del centro");
        cut.Find("input.mi-trabajo-filtro").GetAttribute("value").Should().Be("Requisito");
        cut.FindAll(".mi-trabajo-chip-activo").Single().TextContent.Should().Contain("Bloquea centro");
        cut.FindAll(".mi-trabajo-cartera-fila-activa").Single().TextContent.Should().Contain("Refrielectric");
        cut.FindAll(".mi-trabajo-pestana-activa").Select(e => e.TextContent.Trim()).Should().BeEquivalentTo("Severidad");
        cut.Find("select.mi-trabajo-ordenar-select").GetAttribute("aria-label").Should().Be("Ordenar por");
        cut.FindAll("select.mi-trabajo-ordenar-select option").Single(o => o.HasAttribute("selected")).TextContent.Should().Be("Cliente");
    }

    [Fact]
    public void La_busqueda_de_la_URL_filtra_por_si_sola()
    {
        var cut = Renderizar(url: $"/mi-trabajo?empresa={TenantRefri}&q=Requisito");

        TitulosVisibles(cut).Should().BeEquivalentTo("Requisito del centro");
    }

    [Fact]
    public void Un_valor_de_la_URL_que_no_se_reconoce_se_ignora()
    {
        var cut = Renderizar(url: "/mi-trabajo?severidad=no-existe&empresa=no-es-un-guid&agrupar=99&orden=xx");

        TitulosVisibles(cut).Should().HaveCount(4, "sin filtros reconocidos se ve toda la cola");
        cut.FindAll(".mi-trabajo-chip-activo").Single().TextContent.Should().Contain("Todos");
    }

    [Fact]
    public void Cambiar_los_filtros_se_escribe_en_la_URL_y_el_Tenant_viaja_como_empresa()
    {
        var cut = Renderizar();

        cut.FindAll(".mi-trabajo-chip").Single(c => c.TextContent.Trim().StartsWith("Bloquea centro")).Click();
        UrlActual.Should().EndWith("?tipo=bloqueacentro");

        cut.FindAll(".mi-trabajo-cartera-fila").Single(f => f.TextContent.Contains("Laboratorios Dexter")).Click();
        cut.Find("input.mi-trabajo-filtro").Input("Rechazado");
        cut.FindAll(".mi-trabajo-pestana").Single(b => b.TextContent.Trim() == "Severidad").Click();
        cut.Find("select.mi-trabajo-ordenar-select").Change(nameof(OrdenMiTrabajo.Cliente));

        var consulta = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(UrlActual).Query);
        consulta["tipo"].ToString().Should().Be("bloqueacentro");
        consulta["empresa"].ToString().Should().Be(TenantDexter.ToString());
        consulta["q"].ToString().Should().Be("Rechazado");
        consulta["agrupar"].ToString().Should().Be("severidad");
        consulta["orden"].ToString().Should().Be("cliente");
    }

    [Fact]
    public void Quitar_filtros_limpia_tambien_la_URL()
    {
        var cut = Renderizar();
        cut.FindAll(".mi-trabajo-chip").Single(c => c.TextContent.Trim().StartsWith("Detecciones")).Click();
        cut.Find("input.mi-trabajo-filtro").Input("zzz-sin-resultados");
        UrlActual.Should().Contain("tipo=detecciones").And.Contain("q=zzz-sin-resultados");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Quitar filtros").Click();

        new Uri(UrlActual).Query.Should().BeEmpty("OnParametersSet repondría los filtros desde una URL que los conserve");
        TitulosVisibles(cut).Should().HaveCount(4);
    }

    // ── Chips por tipo de tarea (maqueta) ─────────────────────────────────────

    [Theory]
    [InlineData(TipoItemBandeja.Vencido, SeveridadMiTrabajo.Actuacion, TipoTareaMiTrabajo.Vencidos)]
    [InlineData(TipoItemBandeja.PlataformaVencida, SeveridadMiTrabajo.Actuacion, TipoTareaMiTrabajo.Vencidos)]
    [InlineData(TipoItemBandeja.Faltante, SeveridadMiTrabajo.Actuacion, TipoTareaMiTrabajo.Faltantes)]
    [InlineData(TipoItemBandeja.SugerenciaVisitaUrgente, SeveridadMiTrabajo.Actuacion, TipoTareaMiTrabajo.Visitas)]
    [InlineData(TipoItemBandeja.VisitaUrgente, SeveridadMiTrabajo.Actuacion, TipoTareaMiTrabajo.Visitas)]
    [InlineData(TipoItemBandeja.RevisionIa, SeveridadMiTrabajo.Actuacion, TipoTareaMiTrabajo.RevisionIa)]
    [InlineData(TipoItemBandeja.DeteccionPendiente, SeveridadMiTrabajo.Actuacion, TipoTareaMiTrabajo.Detecciones)]
    [InlineData(TipoItemBandeja.Vencido, SeveridadMiTrabajo.Bloqueo, TipoTareaMiTrabajo.Vencidos)]
    [InlineData(TipoItemBandeja.PlataformaVencida, SeveridadMiTrabajo.Bloqueo, TipoTareaMiTrabajo.Vencidos)]
    [InlineData(TipoItemBandeja.Faltante, SeveridadMiTrabajo.Bloqueo, TipoTareaMiTrabajo.Faltantes)]
    [InlineData(TipoItemBandeja.SugerenciaVisitaUrgente, SeveridadMiTrabajo.Bloqueo, TipoTareaMiTrabajo.Visitas)]
    [InlineData(TipoItemBandeja.PlataformaRechazada, SeveridadMiTrabajo.Bloqueo, null)]
    [InlineData(TipoItemBandeja.Urgente, SeveridadMiTrabajo.Actuacion, null)]
    [InlineData(TipoItemBandeja.VencimientoProximo, SeveridadMiTrabajo.Proximo, null)]
    [InlineData(TipoItemBandeja.EnPlataformaSeguimiento, SeveridadMiTrabajo.Seguimiento, null)]
    public void Cada_tipo_de_item_cae_en_su_chip_de_tipo_sea_o_no_un_bloqueo(
        TipoItemBandeja tipo, SeveridadMiTrabajo severidad, TipoTareaMiTrabajo? esperado) =>
        MiTrabajoVista.TipoDeTarea(new FilaMiTrabajo(TenantRefri, "Refrielectric", 0, severidad, Item("x", tipo, "t")))
            .Should().Be(esperado);

    private static FilaMiTrabajo FilaDeTipo(TipoItemBandeja tipo, SeveridadMiTrabajo severidad) =>
        new(TenantRefri, "Refrielectric", 0, severidad, Item("x", tipo, "t"));

    private static IReadOnlyList<TipoTareaMiTrabajo> ChipsDe(FilaMiTrabajo fila) =>
        Enum.GetValues<TipoTareaMiTrabajo>().Where(t => MiTrabajoVista.EnChip(fila, t)).ToList();

    // Partición por tipo (decisión de Chris, 2026-11-06): cada tarea en exactamente un chip distinto de «Todos».
    [Theory]
    [InlineData(TipoItemBandeja.Vencido, TipoTareaMiTrabajo.Vencidos)]
    [InlineData(TipoItemBandeja.PlataformaVencida, TipoTareaMiTrabajo.Vencidos)]
    [InlineData(TipoItemBandeja.Faltante, TipoTareaMiTrabajo.Faltantes)]
    [InlineData(TipoItemBandeja.SugerenciaVisitaUrgente, TipoTareaMiTrabajo.Visitas)]
    public void Un_bloqueo_con_chip_de_tipo_propio_esta_solo_en_el_chip_de_su_tipo_y_no_en_Bloquea_centro(TipoItemBandeja tipo, TipoTareaMiTrabajo chipDelTipo)
    {
        var fila = FilaDeTipo(tipo, SeveridadMiTrabajo.Bloqueo);

        MiTrabajoVista.EnChip(fila, chipDelTipo).Should().BeTrue();
        MiTrabajoVista.EnChip(fila, TipoTareaMiTrabajo.BloqueaCentro).Should().BeFalse("el chip de su tipo gana a «Bloquea centro»");
        ChipsDe(fila).Should().Equal([chipDelTipo], "la pertenencia es única");
    }

    [Theory]
    [InlineData(TipoItemBandeja.RequisitoPendiente)]
    [InlineData(TipoItemBandeja.PlataformaRechazada)]
    public void Un_bloqueo_sin_chip_de_tipo_propio_solo_esta_en_Bloquea_centro(TipoItemBandeja tipo) =>
        ChipsDe(FilaDeTipo(tipo, SeveridadMiTrabajo.Bloqueo)).Should().Equal([TipoTareaMiTrabajo.BloqueaCentro]);

    [Theory]
    [InlineData(TipoItemBandeja.PlataformaRechazada, SeveridadMiTrabajo.Actuacion)]
    [InlineData(TipoItemBandeja.PlataformaPendiente, SeveridadMiTrabajo.Actuacion)]
    [InlineData(TipoItemBandeja.Urgente, SeveridadMiTrabajo.Actuacion)]
    [InlineData(TipoItemBandeja.VencimientoProximo, SeveridadMiTrabajo.Proximo)]
    [InlineData(TipoItemBandeja.EnPlataformaSeguimiento, SeveridadMiTrabajo.Seguimiento)]
    public void Lo_que_ni_bloquea_ni_tiene_chip_de_tipo_propio_solo_cuenta_en_Todos(TipoItemBandeja tipo, SeveridadMiTrabajo severidad) =>
        ChipsDe(FilaDeTipo(tipo, severidad)).Should().BeEmpty("hueco conocido de la partición: la maqueta no le asigna chip");

    [Theory]
    [InlineData(SeveridadMiTrabajo.Actuacion)]
    [InlineData(SeveridadMiTrabajo.Proximo)]
    public void Lo_que_no_bloquea_no_entra_en_Bloquea_centro_aunque_su_tipo_sea_el_de_un_bloqueo_habitual(SeveridadMiTrabajo severidad) =>
        MiTrabajoVista.EnChip(FilaDeTipo(TipoItemBandeja.Vencido, severidad), TipoTareaMiTrabajo.BloqueaCentro).Should().BeFalse();

    private static MiTrabajoAgregadoDto CarteraMixtaSinHuecos() => new([Tenant(TenantRefri, "Refrielectric", esOrigen: false,
    [
        Item("v", TipoItemBandeja.Vencido, "Vencido que bloquea"),
        Item("pv", TipoItemBandeja.PlataformaVencida, "Plataforma vencida que bloquea"),
        Item("f", TipoItemBandeja.Faltante, "Faltante que bloquea"),
        Item("s", TipoItemBandeja.SugerenciaVisitaUrgente, "Sugerencia de visita que bloquea"),
        Item("vu", TipoItemBandeja.VisitaUrgente, "Visita urgente"),
        Item("rq", TipoItemBandeja.RequisitoPendiente, "Requisito que bloquea"),
        Item("pr", TipoItemBandeja.PlataformaRechazada, "Rechazada que bloquea") with { RechazoBloqueaCentro = true },
        Item("ia", TipoItemBandeja.RevisionIa, "Lectura IA"),
        Item("de", TipoItemBandeja.DeteccionPendiente, "Detección"),
    ])]);

    [Fact]
    public void Los_chips_de_tipo_son_una_particion_de_Todos_y_los_bloqueos_van_a_su_tipo()
    {
        var vista = new MiTrabajoVista(CarteraMixtaSinHuecos());

        vista.ContarTipo(new(), TipoTareaMiTrabajo.Vencidos).Should().Be(2);
        vista.ContarTipo(new(), TipoTareaMiTrabajo.Faltantes).Should().Be(1);
        vista.ContarTipo(new(), TipoTareaMiTrabajo.Visitas).Should().Be(2);
        vista.ContarTipo(new(), TipoTareaMiTrabajo.BloqueaCentro).Should().Be(2, "solo el Requisito y la Rechazada que bloquea");
        vista.ContarTipo(new(), TipoTareaMiTrabajo.RevisionIa).Should().Be(1);
        vista.ContarTipo(new(), TipoTareaMiTrabajo.Detecciones).Should().Be(1);
        vista.ContarTipo(new(), TipoTareaMiTrabajo.Gestiones).Should().Be(0);
        vista.ContarTipo(new(), null).Should().Be(9);

        Enum.GetValues<TipoTareaMiTrabajo>().Sum(t => vista.ContarTipo(new(), t))
            .Should().Be(vista.ContarTipo(new(), null), "la suma de los chips es «Todos»: ningún chip repite una tarea");

        TitulosAlcance(vista, TipoTareaMiTrabajo.Vencidos).Should().BeEquivalentTo("Vencido que bloquea", "Plataforma vencida que bloquea");
        TitulosAlcance(vista, TipoTareaMiTrabajo.Faltantes).Should().BeEquivalentTo("Faltante que bloquea");
        TitulosAlcance(vista, TipoTareaMiTrabajo.Visitas).Should().BeEquivalentTo("Sugerencia de visita que bloquea", "Visita urgente");
        TitulosAlcance(vista, TipoTareaMiTrabajo.BloqueaCentro).Should().BeEquivalentTo("Requisito que bloquea", "Rechazada que bloquea");
    }

    [Fact]
    public void El_recuento_de_cada_chip_es_lo_que_el_filtrado_enseña_con_y_sin_busqueda()
    {
        var vista = new MiTrabajoVista(CarteraMixtaSinHuecos());

        foreach (var busqueda in new[] { "", "bloquea" })
            foreach (var tipo in Enum.GetValues<TipoTareaMiTrabajo>())
                vista.ContarTipo(new(Busqueda: busqueda), tipo).Should().Be(vista.Alcance(new(Busqueda: busqueda, Tipo: tipo)).Count, $"{tipo} con «{busqueda}»");
    }

    [Fact]
    public void Las_tareas_sin_chip_propio_que_no_bloquean_solo_suman_en_Todos()
    {
        var datos = new MiTrabajoAgregadoDto([Tenant(TenantRefri, "Refrielectric", esOrigen: false,
            [Item("v", TipoItemBandeja.Vencido, "Vencido"), Item("u", TipoItemBandeja.Urgente, "Urgente"), Item("pp", TipoItemBandeja.PlataformaPendiente, "Pendiente de subir")],
            proximos: [Item("p", TipoItemBandeja.VencimientoProximo, "Por vencer")],
            seguimiento: [Item("s", TipoItemBandeja.EnPlataformaSeguimiento, "Enviado")])]);
        var vista = new MiTrabajoVista(datos);

        vista.ContarTipo(new(), null).Should().Be(5);
        Enum.GetValues<TipoTareaMiTrabajo>().Sum(t => vista.ContarTipo(new(), t)).Should().Be(1, "las otras cuatro no tienen chip: nunca se cuentan dos veces");
    }

    private static IReadOnlyList<string> TitulosAlcance(MiTrabajoVista vista, TipoTareaMiTrabajo tipo) =>
        vista.Alcance(new(Tipo: tipo)).Select(f => f.Item.Titulo).ToList();

    [Fact]
    public void En_pantalla_los_contadores_de_los_chips_suman_Todos_y_cada_chip_enseña_sus_filas()
    {
        var cut = Renderizar(() => CarteraMixtaSinHuecos());
        string Cuenta(string etiqueta) => cut.FindAll(".mi-trabajo-chip")
            .Single(c => c.TextContent.Trim().StartsWith(etiqueta)).QuerySelector(".mi-trabajo-chip-cuenta")!.TextContent;
        void Elegir(string etiqueta) => cut.FindAll(".mi-trabajo-chip").Single(c => c.TextContent.Trim().StartsWith(etiqueta)).Click();

        (Cuenta("Todos"), Cuenta("Vencidos"), Cuenta("Faltantes"), Cuenta("Visitas"), Cuenta("Bloquea centro"), Cuenta("Revisión IA"), Cuenta("Detecciones"), Cuenta("Gestiones"))
            .Should().Be(("9", "2", "1", "2", "2", "1", "1", "0"));
        new[] { "Vencidos", "Faltantes", "Visitas", "Bloquea centro", "Revisión IA", "Detecciones", "Gestiones" }.Sum(c => int.Parse(Cuenta(c)))
            .Should().Be(int.Parse(Cuenta("Todos")), "la suma de los chips es «Todos»");

        Elegir("Vencidos");
        TitulosVisibles(cut).Should().BeEquivalentTo("Vencido que bloquea", "Plataforma vencida que bloquea");
        Elegir("Bloquea centro");
        TitulosVisibles(cut).Should().BeEquivalentTo("Requisito que bloquea", "Rechazada que bloquea");
        Elegir("Visitas");
        TitulosVisibles(cut).Should().BeEquivalentTo("Sugerencia de visita que bloquea", "Visita urgente");
    }

    [Fact]
    public void El_chip_Gestiones_sale_inerte_y_a_cero_porque_no_hay_fuente_de_datos()
    {
        var cut = Renderizar();

        var gestiones = cut.FindAll(".mi-trabajo-chip").Single(c => c.TextContent.Trim().StartsWith("Gestiones"));
        gestiones.HasAttribute("disabled").Should().BeTrue();
        gestiones.ClassList.Should().Contain("mi-trabajo-chip-inerte");
        gestiones.QuerySelector(".mi-trabajo-chip-cuenta")!.TextContent.Should().Be("0");
        cut.FindAll(".mi-trabajo-chip").Should().HaveCount(8, "Todos, Vencidos, Faltantes, Visitas, Bloquea centro, Revisión IA, Detecciones y Gestiones");
    }

    [Fact]
    public void Un_filtro_de_severidad_de_la_URL_se_ignora_porque_la_maqueta_no_tiene_control_de_severidad()
    {
        var cut = Renderizar(url: "/mi-trabajo?severidad=bloqueo");

        TitulosVisibles(cut).Should().HaveCount(4, "un filtro de severidad sin control visible escondería trabajo sin que nadie pudiera quitarlo");
        cut.FindAll(".mi-trabajo-chip-activo").Single().TextContent.Should().Contain("Todos");

        // 6 es el total de la Cartera del fixture; las 4 filas son las no plegadas en el Calendario. Sin filtro oculto, los dos coinciden con el enlace sin ?severidad=.
        cut.FindAll(".mi-trabajo-chip").Single(c => c.TextContent.Trim().StartsWith("Todos"))
            .QuerySelector(".mi-trabajo-chip-cuenta")!.TextContent.Should().Be("6");
    }

    [Fact]
    public void El_primer_cambio_de_filtro_retira_de_la_URL_el_parametro_de_severidad_heredado()
    {
        var cut = Renderizar(url: "/mi-trabajo?severidad=bloqueo");

        cut.Find("input.mi-trabajo-filtro").Input("Requisito");

        var consulta = new Uri(UrlActual).Query;
        consulta.Should().Be("?q=Requisito");
        consulta.Should().NotContain("severidad");
    }

    [Fact]
    public void La_fila_de_resumen_cuenta_grupos_y_tareas_visibles_y_enseña_los_atajos_que_existen()
    {
        var cut = Renderizar();

        var grupos = cut.FindAll(".mi-trabajo-grupo").Count;
        var tareas = cut.FindAll(".mi-trabajo-chip").Single(c => c.TextContent.Trim().StartsWith("Todos")).QuerySelector(".mi-trabajo-chip-cuenta")!.TextContent;
        cut.Find(".mi-trabajo-resumen").TextContent.Trim().Should().Be($"{grupos} grupos · {tareas} tareas visibles");
        cut.FindAll(".mi-trabajo-atajos kbd").Select(k => k.TextContent).Should().Equal("j", "k", "↵");

        cut.Find("input.mi-trabajo-filtro").Input("Rechazado");
        cut.Find(".mi-trabajo-resumen").TextContent.Trim().Should().Be("1 grupo · 1 tarea visible");
    }

    [Fact]
    public void El_grupo_que_bloquea_lleva_la_banda_roja_y_su_Gestionar_abre_la_primera_tarea_aunque_este_plegado()
    {
        var cut = RenderizarPlegado();

        var bloquea = cut.FindAll(".mi-trabajo-grupo").Where(g => g.ClassList.Contains("mi-trabajo-grupo-bloqueo")).ToList();
        bloquea.Should().HaveCountGreaterThan(1, "hay Empresas con un Bloqueo");
        bloquea[0].QuerySelectorAll(".mi-trabajo-fila").Should().NotBeEmpty("el primer grupo carga desplegado");
        bloquea[1].QuerySelectorAll(".mi-trabajo-fila").Should().BeEmpty("el segundo carga plegado");
        cut.FindAll(".mi-trabajo-grupo-gestionar").Select(b => b.TextContent.Trim()).Should().OnlyContain(t => t == "Gestionar");

        bloquea[1].QuerySelector(".mi-trabajo-grupo-gestionar")!.Click();

        cut.FindAll(".mi-trabajo-grupo-bloqueo")[1].QuerySelector(".mi-trabajo-grupo-gestionar")!.GetAttribute("aria-label").Should().StartWith("Gestionar las tareas de ");
        cut.FindAll(".mi-trabajo-fila-abierta").Should().ContainSingle("Gestionar abre una tarea del grupo en el detalle");
        cut.FindAll(".mi-trabajo-grupo-bloqueo")[1].QuerySelector(".mi-trabajo-fila-abierta").Should().NotBeNull("la tarea abierta es del grupo pulsado");
        cut.FindAll(".mi-trabajo-grupo-bloqueo")[1].QuerySelector(".mi-trabajo-fila-abierta")!.ClassList.Should().Contain("mi-trabajo-fila-bloqueo", "abre la de mayor severidad, la que motiva la banda");
        cut.Find(".mi-trabajo-detalle").Should().NotBeNull();
    }

    [Fact]
    public void Gestionar_en_un_grupo_que_solo_tiene_calendario_abre_el_pliegue_y_la_primera_tarea()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto(
            [Tenant(TenantRefri, "Refrielectric", false, [], [Item("r1", TipoItemBandeja.VencimientoProximo, "EPI por vencer", "Transportes Planet Express")])]));

        var gestionar = cut.Find(".mi-trabajo-grupo-gestionar");
        gestionar.Click();

        cut.FindAll(".mi-trabajo-fila-abierta").Should().ContainSingle();
        TitulosVisibles(cut).Should().Contain("EPI por vencer");
        cut.FindAll(".mi-trabajo-pliegue").Should().BeEmpty();
    }

    [Fact]
    public void Gestionar_en_el_modo_Severidad_abre_una_tarea_de_esa_severidad()
    {
        var cut = Renderizar(url: "/mi-trabajo?agrupar=severidad");

        var primera = cut.FindAll(".mi-trabajo-grupo").First();
        primera.QuerySelector(".mi-trabajo-grupo-nombre")!.TextContent.Should().Be("Bloqueo");
        primera.QuerySelector(".mi-trabajo-grupo-gestionar")!.Click();

        cut.FindAll(".mi-trabajo-fila-abierta").Should().ContainSingle();
        cut.Find(".mi-trabajo-fila-abierta").ClassList.Should().Contain("mi-trabajo-fila-bloqueo");
    }

    [Fact]
    public void Gestionar_en_un_grupo_de_calendario_del_modo_Severidad_lo_despliega_y_abre_su_tarea()
    {
        var cut = RenderizarPlegado(() => new MiTrabajoAgregadoDto(
            [Tenant(TenantRefri, "Refrielectric", false, [], [Item("r1", TipoItemBandeja.VencimientoProximo, "EPI por vencer", "Transportes Planet Express")])]),
            url: "/mi-trabajo?agrupar=severidad");
        cut.Find(".mi-trabajo-grupo-cabecera-severidad").GetAttribute("aria-expanded").Should().Be("true", "el único grupo carga desplegado");
        cut.Find(".mi-trabajo-grupo-cabecera-severidad").Click();
        cut.FindAll(".mi-trabajo-fila").Should().BeEmpty("el calendario, plegado");

        cut.Find(".mi-trabajo-grupo-gestionar").Click();

        cut.FindAll(".mi-trabajo-fila-abierta").Should().ContainSingle("Gestionar despliega el grupo y abre su tarea en un solo clic");
        TitulosVisibles(cut).Should().Contain("EPI por vencer");
    }

    [Fact]
    public void Gestionar_dos_veces_sobre_un_grupo_que_estaba_plegado_no_lo_vuelve_a_plegar()
    {
        var cut = RenderizarPlegado();
        cut.FindAll(".mi-trabajo-grupo-bloqueo")[1].QuerySelectorAll(".mi-trabajo-fila").Should().BeEmpty("el segundo grupo carga plegado");

        cut.FindAll(".mi-trabajo-grupo-bloqueo")[1].QuerySelector(".mi-trabajo-grupo-gestionar")!.Click();
        cut.FindAll(".mi-trabajo-grupo-bloqueo")[1].QuerySelector(".mi-trabajo-grupo-gestionar")!.Click();

        cut.FindAll(".mi-trabajo-fila-abierta").Should().ContainSingle("el segundo clic no repliega ni abre otra tarea");
    }

    [Theory]
    [InlineData(TipoItemBandeja.VisitaUrgente, "visitas")]
    [InlineData(TipoItemBandeja.SugerenciaVisitaUrgente, "visitas")]
    [InlineData(TipoItemBandeja.RevisionIa, "ia")]
    [InlineData(TipoItemBandeja.DeteccionPendiente, "ia")]
    [InlineData(TipoItemBandeja.PlataformaRechazada, "plataforma")]
    [InlineData(TipoItemBandeja.EnPlataformaSeguimiento, "plataforma")]
    [InlineData(TipoItemBandeja.Vencido, "reloj")]
    [InlineData(TipoItemBandeja.VencimientoProximo, "reloj")]
    [InlineData(TipoItemBandeja.Urgente, "advertencia")]
    [InlineData(TipoItemBandeja.Faltante, "documentos")]
    [InlineData(TipoItemBandeja.PlataformaPendiente, "plataforma")]
    [InlineData(TipoItemBandeja.PlataformaVencida, "plataforma")]
    [InlineData(TipoItemBandeja.RequisitoPendiente, "documentos")]
    public void Cada_familia_de_tarea_tiene_su_icono_de_fila(TipoItemBandeja tipo, string icono) =>
        TipoItemBandejaUi.Icono(Item("x", tipo, "t")).Should().Be(icono);

    [Fact]
    public void Un_grupo_sin_Bloqueo_no_lleva_la_banda_roja()
    {
        var cut = Renderizar(() => new MiTrabajoAgregadoDto(
            [Tenant(TenantRefri, "Refrielectric", false, [], [Item("r1", TipoItemBandeja.VencimientoProximo, "EPI por vencer", "Transportes Planet Express")])]));

        cut.FindAll(".mi-trabajo-grupo").Should().NotBeEmpty();
        cut.FindAll(".mi-trabajo-grupo-bloqueo").Should().BeEmpty();
    }

    [Fact]
    public void Cada_fila_lleva_su_icono_y_la_severidad_a_la_derecha_en_vez_del_tipo_a_la_izquierda()
    {
        var cut = Renderizar();
        ExpandirTodas(cut);

        var fila = FilaDe(cut, "Reconocimiento médico");
        fila.QuerySelector(".mi-trabajo-fila-icono")!.QuerySelector("svg").Should().NotBeNull();
        fila.QuerySelector(".mi-trabajo-fila-derecha")!.TextContent.Should().Contain("Bloqueo");
        fila.QuerySelector(".mi-trabajo-fila-linea")!.TextContent.Should().NotContain("Bloqueo");
        fila.QuerySelector(".mi-trabajo-fila-contexto")!.TextContent.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Un_tipo_Gestiones_en_la_URL_se_ignora_porque_el_chip_es_inerte()
    {
        var cut = Renderizar(url: "/mi-trabajo?tipo=gestiones");

        TitulosVisibles(cut).Should().HaveCount(4, "Gestiones no tiene fuente de datos: filtrar por él dejaría la cola vacía");
        cut.FindAll(".mi-trabajo-chip-activo").Single().TextContent.Should().Contain("Todos");
    }

    [Fact]
    public void El_control_de_cartera_se_abre_con_el_resumen_y_se_cierra_con_Escape_o_fuera_o_al_elegir_Empresa()
    {
        var cut = Renderizar();
        bool Abierto() => cut.Find(".mi-trabajo-cartera-resumen").GetAttribute("aria-expanded") == "true";

        Abierto().Should().BeFalse();
        cut.Find(".mi-trabajo-cartera-lista").HasAttribute("hidden").Should().BeTrue();

        cut.Find(".mi-trabajo-cartera-resumen").Click();
        Abierto().Should().BeTrue();
        cut.Find(".mi-trabajo-cartera-lista").HasAttribute("hidden").Should().BeFalse();

        // Escape con el foco en una fila de la lista: el evento burbujea hasta el contenedor.
        cut.Find(".mi-trabajo-cartera-fila").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        Abierto().Should().BeFalse();

        cut.Find(".mi-trabajo-cartera-resumen").Click();
        cut.Find(".mi-trabajo-cartera-fondo").Click();
        Abierto().Should().BeFalse();

        cut.Find(".mi-trabajo-cartera-resumen").Click();
        cut.FindAll(".mi-trabajo-cartera-fila").Single(f => f.TextContent.Contains("Laboratorios Dexter")).Click();
        Abierto().Should().BeFalse();
        cut.Find(".mi-trabajo-cartera-actual").TextContent.Should().Contain("Laboratorios Dexter");
    }

    [Fact]
    public void La_accion_de_una_fila_ofrece_la_vuelta_a_Mi_trabajo_con_los_filtros_vigentes()
    {
        var cut = Renderizar(url: "/mi-trabajo");
        cut.FindAll(".mi-trabajo-chip").Single(c => c.TextContent.Trim().StartsWith("Vencidos")).Click();

        var destino = Formulario(FilaDe(cut, "Reconocimiento médico")).ReturnUrl;

        destino.Should().Be($"/documentos?documentoId={DocumentoVencido}&volver={Uri.EscapeDataString("/mi-trabajo?tipo=vencidos")}");
    }

    [Theory]
    [InlineData("/mi-trabajo", "/mi-trabajo")]
    [InlineData("/mi-trabajo?severidad=bloqueo&q=Vega", "/mi-trabajo?severidad=bloqueo&q=Vega")]
    [InlineData("https://atacante.example/mi-trabajo", null)]
    [InlineData("//atacante.example", null)]
    [InlineData("/\atacante.example", null)]
    [InlineData("/mi-trabajo	/x", null)]
    [InlineData("/mi-trabajoX", null)]
    [InlineData("/otra?x=/mi-trabajo", null)]
    [InlineData("/documentos", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void El_retorno_solo_admite_una_ruta_local_de_Mi_trabajo(string? valor, string? esperado) =>
        RetornoMiTrabajo.Validar(valor).Should().Be(esperado);

    [Fact]
    public void El_enlace_de_vuelta_solo_se_pinta_con_un_retorno_valido()
    {
        Services.AddLocalization();
        var nav = Services.GetRequiredService<NavigationManager>();

        nav.NavigateTo("/documentos?volver=" + Uri.EscapeDataString("/mi-trabajo?severidad=bloqueo"));
        var valido = Render<EnlaceVolverAMiTrabajo>();
        valido.Find("a[data-enlace='volver-mi-trabajo']").GetAttribute("href").Should().Be("/mi-trabajo?severidad=bloqueo");

        nav.NavigateTo("/documentos?volver=" + Uri.EscapeDataString("https://atacante.example"));
        Render<EnlaceVolverAMiTrabajo>().FindAll("a").Should().BeEmpty("un retorno externo no se enlaza");

        nav.NavigateTo("/documentos");
        Render<EnlaceVolverAMiTrabajo>().FindAll("a").Should().BeEmpty();
    }
}

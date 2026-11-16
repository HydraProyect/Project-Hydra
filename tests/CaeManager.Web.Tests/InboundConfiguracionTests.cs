using System.Security.Claims;
using Bunit;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Inbound.Configuracion;
using CaeManager.Web.Features.Inbound.Configuracion.Components;
using CaeManager.Web.Features.Inbound.Configuracion.Gobierno;
using CaeManager.Web.Features.Inbound.Configuracion.ReglasAviso;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Las tres pantallas de configuración del Tenant TCE de Inbound (pantallas 19, 18 y 20 de la maqueta): rótulos, acciones, estados y
/// estados vacíos. Los datos ficticios viven solo aquí: la pantalla real, sin backend fusionado, no pinta ninguno.
/// </summary>
public class InboundConfiguracionTests : BunitContext
{
    private static readonly Guid ContratoId = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid TrabajadorPilar = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid TenantConservas = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid TenantPlasticos = Guid.Parse("00000000-0000-0000-0000-0000000000b2");

    private readonly NoAplicaFalso _noAplica = new();
    private readonly ReglasFalso _reglas = new();
    private readonly GobiernoFalso _gobierno = new();

    public InboundConfiguracionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<ToastService>();
        Services.AddScoped<IServicioNoAplica>(_ => _noAplica);
        Services.AddScoped<IServicioReglasAviso>(_ => _reglas);
        Services.AddScoped<IServicioGobiernoInbound>(_ => _gobierno);
        this.ConRolDeEscritura(Roles.Administrador);
    }

    // ---------------------------------------------------------------- «No aplica» (pantalla 19)

    private static RequerimientoDeContrato Req(string nombre, OrigenRequerimiento origen, string entidad = "Empresa", bool n1 = false,
        ExcepcionNoAplica? excepcion = null) =>
        new(Guid.NewGuid(), nombre, "Sin vencimiento", entidad, origen, origen.ToString(), n1, EstadoRequerimientoContrato.PendienteDeSubir, string.Empty, excepcion);

    private static RequerimientosDeContrato Contrato(params RequerimientoDeContrato[] filas) =>
        new("Montajes Moncayo S.L.", "Planta Zaragoza Norte", "Logística Delta Sur S.A.", filas,
            [new TrabajadorDelContrato(TrabajadorPilar, "Pilar Sanjuán Aznar", false), new TrabajadorDelContrato(Guid.NewGuid(), "Marta Gimeno Royo", true)]);

    private IRenderedComponent<PanelRequerimientosContrato> RenderPanel() =>
        Render<PanelRequerimientosContrato>(p => p
            .Add(c => c.ContratoId, ContratoId).Add(c => c.TituloVacio, "Aún no se muestran los Requerimientos").Add(c => c.DescripcionVacio, "Llegarán con B-11."));

    [Fact]
    public void Los_requerimientos_sin_backend_enseñan_el_estado_vacio_y_ninguna_fila()
    {
        var cut = RenderPanel();

        cut.Markup.Should().Contain("Aún no se muestran los Requerimientos");
        cut.FindAll("table").Should().BeEmpty("sin backend no se inventa ninguna fila");
    }

    [Fact]
    public void Los_requerimientos_pintan_rotulos_y_bloquean_No_aplica_en_legales_preventivos_y_N1()
    {
        var excepcion = new ExcepcionNoAplica(null, null, new DateOnly(2026, 12, 31), "Cubierta por la póliza del grupo", "Ricardo Ainsa Bel, director de operaciones");
        _noAplica.Datos = Contrato(
            Req("Certificado de estar al corriente con la TGSS", OrigenRequerimiento.Legal),
            Req("Evaluación de riesgos", OrigenRequerimiento.Preventivo),
            Req("Seguro propio de nivel uno", OrigenRequerimiento.ExigenciaDelCliente, n1: true),
            Req("Permiso de trabajos en caliente", OrigenRequerimiento.Centro),
            Req("Póliza de responsabilidad civil", OrigenRequerimiento.ExigenciaDelCliente, excepcion: excepcion));

        var cut = RenderPanel();

        foreach (var rotulo in new[] { "Requisito", "Entidad", "Origen", "Estado" })
            cut.Markup.Should().Contain(rotulo);
        var botones = cut.FindAll("tbody tr").Select(f => f.QuerySelector("button")!).ToList();
        botones.Select(b => b.HasAttribute("disabled")).Should().Equal(true, true, true, false, false);
        botones[3].TextContent.Trim().Should().Be("No aplica");
        botones[4].TextContent.Trim().Should().Be("Editar");
        cut.Markup.Should().Contain("No exigido en este contrato").And.Contain("hasta el 31/12/2026");
        cut.Find("[data-pieza=nota-no-aplica]").TextContent.Should().Contain("Logística Delta Sur S.A.");
    }

    [Fact]
    public async Task El_dialogo_No_aplica_exige_motivo_y_quien_autoriza_y_guarda_la_excepcion()
    {
        _noAplica.Datos = Contrato(Req("Aptitud médica", OrigenRequerimiento.Actividad, "Trabajadores"));
        var cut = RenderPanel();

        cut.Find("tbody button").Click();

        cut.Markup.Should().Contain("Aptitud médica · Montajes Moncayo S.L. · Planta Zaragoza Norte");
        foreach (var rotulo in new[] { "A quién se aplica", "Todos los Trabajadores del contrato", "Un Trabajador", "Duración", "Permanente", "Con vigencia", "Motivo", "Autorizado por, en Logística Delta Sur S.A.", "Así se verá" })
            cut.Markup.Should().Contain(rotulo);
        cut.Markup.Should().Contain("No exigido en este contrato");
        Guardar(cut).HasAttribute("disabled").Should().BeTrue("falta el motivo");

        cut.Find("textarea").Input("No maneja plataformas elevadoras");
        Guardar(cut).HasAttribute("disabled").Should().BeTrue("falta quién lo autoriza");
        Escribir(cut, "input[placeholder='Nombre y cargo']", "Ricardo Ainsa Bel, director de operaciones");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Un Trabajador").Click();
        cut.Markup.Should().Contain("Elige un Trabajador");
        Guardar(cut).HasAttribute("disabled").Should().BeTrue("falta elegir el Trabajador: " + Guardar(cut).OuterHtml);
        cut.Find("select").Change(TrabajadorPilar.ToString());
        cut.Markup.Should().Contain("este requisito no bloquea a Pilar Sanjuán Aznar");
        Guardar(cut).HasAttribute("disabled").Should().BeFalse(Guardar(cut).OuterHtml);

        Guardar(cut).Click();
        await cut.WaitForAssertionAsync(() => _noAplica.Guardadas.Should().ContainSingle());

        var solicitud = _noAplica.Guardadas.Single();
        solicitud.ContratoId.Should().Be(ContratoId);
        solicitud.Excepcion.TrabajadorId.Should().Be(TrabajadorPilar);
        solicitud.Excepcion.Hasta.Should().BeNull("es permanente");
        solicitud.Excepcion.Motivo.Should().Be("No maneja plataformas elevadoras");
    }

    [Fact]
    public void El_dialogo_No_aplica_no_ofrece_nada_sobre_un_requisito_que_no_lo_admite()
    {
        var legal = Req("Certificado TGSS", OrigenRequerimiento.Legal);
        _noAplica.Datos = Contrato(legal);

        var cut = Render<DialogoNoAplica>(p => p.Add(c => c.Visible, true).Add(c => c.Requerimiento, legal).Add(c => c.TenantTce, "Logística Delta Sur S.A."));

        cut.Markup.Should().Contain("no admite «No aplica»");
        cut.FindAll("textarea").Should().BeEmpty();
        Guardar(cut).HasAttribute("disabled").Should().BeTrue();
    }

    // CampoTexto retrasa ValorChanged mientras se escribe y lo entrega al perder el foco.
    private static void Escribir<T>(IRenderedComponent<T> cut, string selector, string texto) where T : Microsoft.AspNetCore.Components.IComponent
    {
        cut.Find(selector).Input(texto);
        cut.Find(selector).Blur();
    }

    private static AngleSharp.Dom.IElement Guardar<T>(IRenderedComponent<T> cut) where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.FindAll("button").Last(b => b.TextContent.Trim() == "Guardar");

    // ---------------------------------------------------------------- Reglas de aviso (pantalla 18)

    private static ReglaAviso Regla(string nombre, bool serie = false, DisparadorAviso disparador = DisparadorAviso.Vencimiento, int dias = 30,
        bool activa = true, bool gestor = false, params string[] centros) =>
        new(Guid.NewGuid(), nombre, serie, disparador, dias, 0, disparador == DisparadorAviso.EstadoDelRequerimiento ? ["Rechazado"] : [], "lunes",
            !gestor, gestor, true, true, centros, activa, LogicaReglasAviso.Plantilla(disparador).Asunto, LogicaReglasAviso.Plantilla(disparador).Cuerpo);

    private static ReglasDeAviso ReglasEjemplo() => new("Logística Delta Sur S.A.",
        [Regla("Vencimiento a 30 días", serie: true), Regla("Rechazado o pendiente", serie: true, disparador: DisparadorAviso.EstadoDelRequerimiento, dias: 0),
         Regla("Almacén Huesca: aviso a 15 días", dias: 15, centros: "Almacén Huesca"), Regla("Incompleto: recordatorio", activa: false)],
        ["Planta Zaragoza Norte", "Almacén Plaza", "Almacén Huesca"]);

    [Fact]
    public void Las_reglas_de_aviso_sin_backend_dicen_que_no_estan_disponibles_y_no_pintan_reglas()
    {
        var cut = Render<ReglasAvisoInbound>();

        cut.Markup.Should().Contain("Reglas de aviso").And.Contain("Las reglas de aviso no están disponibles");
        cut.FindAll("table").Should().BeEmpty("sin backend no se inventa ninguna regla");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Añadir").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Las_reglas_de_aviso_pintan_rotulos_notas_filas_y_no_dejan_eliminar_las_de_serie()
    {
        _reglas.Datos = ReglasEjemplo();

        var cut = Render<ReglasAvisoInbound>();

        foreach (var rotulo in new[] { "Regla", "Se envía a", "Cuándo", "Canal", "Ámbito", "Estado", "Añadir", "Todas", "Activas", "Pausadas", "De serie" })
            cut.Markup.Should().Contain(rotulo);
        cut.Find("[data-pieza=nota-firma]").TextContent.Should().Contain("Logística Delta Sur S.A. vía TALVEG").And.Contain("Nunca ve el nombre del Gestor CAE");
        cut.Find("[data-pieza=nota-tolerancia]").TextContent.Should().Contain("Vencido · en tolerancia hasta 05/12");
        cut.Markup.Should().Contain("30 días antes del vencimiento").And.Contain("Todos los Centros").And.Contain("Almacén Huesca");
        var filas = cut.FindAll("tbody tr");
        filas.Should().HaveCount(4);
        filas.Select(f => f.QuerySelectorAll("button").Any(b => b.TextContent.Trim() == "Eliminar")).Should().Equal(false, false, true, true);
        cut.FindAll("[role=switch]").Select(s => s.GetAttribute("aria-checked")).Should().Equal("true", "true", "true", "false");
    }

    [Fact]
    public void Las_reglas_de_aviso_se_filtran_por_texto_y_por_estado_y_enseñan_el_vacio()
    {
        _reglas.Datos = ReglasEjemplo();
        var cut = Render<ReglasAvisoInbound>();

        cut.FindAll("button").First(b => b.TextContent.Contains("Pausadas")).Click();
        cut.FindAll("tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("Incompleto: recordatorio");

        cut.FindAll("button").First(b => b.TextContent.Contains("Pausadas")).Click();
        Escribir(cut, "input[placeholder='Filtrar: regla, Centro o destinatario']", "zzz");
        cut.Find("[data-pieza=sin-coincidencias]").TextContent.Should().Contain("Ninguna regla coincide con el filtro");
    }

    [Fact]
    public async Task Pausar_una_regla_la_cambia_en_el_servicio()
    {
        _reglas.Datos = ReglasEjemplo();
        var cut = Render<ReglasAvisoInbound>();

        cut.Find("[role=switch][aria-label='Pausar la regla Vencimiento a 30 días']").Click();

        await cut.WaitForAssertionAsync(() => _reglas.Cambios.Should().ContainSingle().Which.Activa.Should().BeFalse());
    }

    [Fact]
    public async Task Añadir_una_regla_pide_lo_que_falta_enseña_la_vista_previa_y_guarda()
    {
        _reglas.Datos = ReglasEjemplo();
        var cut = Render<ReglasAvisoInbound>();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Añadir").Click();

        cut.Markup.Should().Contain("Añadir regla de aviso").And.Contain("Regla propia de Logística Delta Sur S.A.");
        foreach (var rotulo in new[] { "Disparador", "Umbral y repetición", "Destinatarios", "Canal", "Ámbito", "Asunto", "Cuerpo", "Variables:", "Vista previa con datos ficticios" })
            cut.Markup.Should().Contain(rotulo);
        cut.Find("[data-pieza=vista-previa]").TextContent.Should().Contain("De: Logística Delta Sur S.A. vía TALVEG").And.Contain("Montajes Moncayo S.L.")
            .And.Contain("Formación en trabajos en altura").And.NotContain("{requerimiento}");
        cut.Find("[data-pieza=vista-previa]").TextContent.Should().NotContain("Elena", "los avisos nunca llevan el nombre del Gestor CAE");
        Guardar(cut).GetAttribute("title").Should().Contain("falta: el nombre");

        Escribir(cut, "[role=dialog] input[type=text]", "Aviso a 20 días");
        Guardar(cut).HasAttribute("disabled").Should().BeFalse();
        Guardar(cut).Click();

        await cut.WaitForAssertionAsync(() => _reglas.Guardadas.Should().ContainSingle());
        _reglas.Guardadas.Single().Nombre.Should().Be("Aviso a 20 días");
        _reglas.Guardadas.Single().DeSerie.Should().BeFalse();
    }

    [Fact]
    public void La_logica_de_las_reglas_dice_cuando_y_a_quien_y_pide_lo_que_falta()
    {
        LogicaReglasAviso.Cuando(Regla("a", dias: 7) with { RepetirCadaDias = 2 }).Should().Be("7 días antes del vencimiento y cada 2 días hasta que vence");
        LogicaReglasAviso.Cuando(Regla("b", dias: 0)).Should().Be("El día que vence");
        LogicaReglasAviso.Destino(Regla("c", gestor: true)).Should().Be("Gestor CAE");
        LogicaReglasAviso.Ambito(Regla("d", centros: ["A", "B"])).Should().Be("2 Centros");

        var vacio = BorradorRegla.Nuevo();
        vacio.ParaEmpresa = false;
        vacio.PorCorreo = false;
        vacio.AlIniciarSesion = false;
        LogicaReglasAviso.Faltas(vacio).Should().Contain(["el nombre", "algún destinatario", "algún canal"]);
        LogicaReglasAviso.Sustituir("{Centro} {desconocida}", "E", "Planta").Should().Be("Planta {desconocida}");
    }

    // ---------------------------------------------------------------- Gobierno de Inbound (pantalla 20)

    private static GobiernoInbound GobiernoEjemplo(bool activa = true) => new(
        "Logística Delta Sur S.A.", "Gestiones CAE Ebro S.L.", "Vigente", new DateOnly(2026, 3, 1), new DateOnly(2027, 2, 28),
        ["Conservas Moncayo Norte S.A.", "Plásticos Jalón S.A."],
        new ConvalidacionConfigurada(activa, [TenantConservas], ["Aptitud médica"]),
        [new TenantTceConvalidable(TenantConservas, "Conservas Moncayo Norte S.A.", "Gestionado por Gestiones CAE Ebro S.L."),
         new TenantTceConvalidable(TenantPlasticos, "Plásticos Jalón S.A.", "Gestionado por Gestiones CAE Ebro S.L.")],
        [new TipoRequisitoConvalidable("Aptitud médica", "Trabajador"), new TipoRequisitoConvalidable("Entrega de EPI", "Trabajador"),
         new TipoRequisitoConvalidable("Póliza de responsabilidad civil", "Empresa")],
        [new CambioGobierno(new DateTime(2026, 11, 2, 10, 5, 0), "Elena Gracia Sanz", "Activó la convalidación entre Tenants TCE con Conservas Moncayo Norte S.A. y 1 tipo de requisito.")]);

    [Fact]
    public void El_gobierno_sin_backend_dice_que_no_esta_disponible_y_no_pinta_ajustes()
    {
        var cut = Render<GobiernoDeInbound>();

        cut.Markup.Should().Contain("Gobierno de Inbound").And.Contain("El gobierno de Inbound no está disponible");
        cut.FindAll("[role=switch]").Should().BeEmpty("sin backend no se enseña ningún ajuste");
    }

    [Fact]
    public async Task El_gobierno_pinta_las_secciones_y_guarda_solo_si_hay_cambios()
    {
        _gobierno.Datos = GobiernoEjemplo();
        var cut = Render<GobiernoDeInbound>();

        foreach (var rotulo in new[] { "Convalidación entre organizaciones", "Con qué organizaciones", "Qué tipos de requisito", "Condición fija", "Validado por convalidación",
                     "Validación automática por tipo de documento", "Próximamente", "Quién gestiona", "Gestiones CAE Ebro S.L.", "Asignación de Operación", "Registro de cambios",
                     "Sin cambios por guardar." })
            cut.Markup.Should().Contain(rotulo);
        var auto = cut.Find("[data-pieza=validacion-automatica] [role=switch]");
        auto.HasAttribute("disabled").Should().BeTrue("la validación automática llega en la ola siguiente");
        cut.Find("[data-pieza=registro]").TextContent.Should().Contain("02/11/2026 11:05").And.Contain("Elena Gracia Sanz");
        cut.FindAll("button").Last(b => b.TextContent.Trim() == "Guardar").HasAttribute("disabled").Should().BeTrue();

        cut.FindAll("[data-pieza=convalidacion] input[type=checkbox]").First(c => c.ParentElement!.TextContent.Contains("Plásticos Jalón")).Change(true);

        cut.Markup.Should().Contain("Hay cambios sin guardar.");
        cut.FindAll("button").Last(b => b.TextContent.Trim() == "Guardar").Click();
        await cut.WaitForAssertionAsync(() => _gobierno.Guardadas.Should().ContainSingle());
        _gobierno.Guardadas.Single().Tenants.Should().BeEquivalentTo([TenantConservas, TenantPlasticos]);
        _gobierno.Guardadas.Single().Tipos.Should().BeEquivalentTo(["Aptitud médica"]);
    }

    [Fact]
    public void El_gobierno_se_niega_a_si_mismo_con_un_encargo_no_pleno()
    {
        _gobierno.Datos = GobiernoEjemplo();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, Roles.Administrador), new Claim(CaeManager.Web.Services.RolEfectivoDelWorkspaceMiddleware.TipoClaimEncargoAdministracion, "1")], "prueba"));
        Services.AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>(_ => new EstadoDePrueba(principal));

        var cut = Render<GobiernoDeInbound>();

        cut.Markup.Should().Contain("no está disponible con el encargo de administración");
        cut.FindAll("[role=switch]").Should().BeEmpty();
    }

    [Fact]
    public void Con_la_convalidacion_apagada_sus_opciones_quedan_deshabilitadas()
    {
        _gobierno.Datos = GobiernoEjemplo(activa: false);

        var cut = Render<GobiernoDeInbound>();

        cut.Find("[data-pieza=convalidacion] [role=switch]").GetAttribute("aria-checked").Should().Be("false");
        cut.FindAll("[data-pieza=convalidacion] input[type=checkbox]").Should().OnlyContain(c => c.HasAttribute("disabled"));
    }

    // ---------------------------------------------------------------- Menú y autorización

    private static ContextoMenuLateral ContextoMenu(bool lineaInbound, string rol) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "prueba")),
        null, true, false, PerfilVocabularioTenant.Consultora, false, LineaInboundVigente: lineaInbound);

    [Fact]
    public void La_configuracion_de_Inbound_solo_aparece_con_la_linea_vigente_y_Gobierno_solo_al_Administrador()
    {
        CatalogoMenuLateral.Visibles(ContextoMenu(false, Roles.Administrador)).Select(g => g.Grupo.Id).Should().NotContain("inbound-config");

        var admin = CatalogoMenuLateral.Visibles(ContextoMenu(true, Roles.Administrador)).Single(g => g.Grupo.Id == "inbound-config");
        admin.Grupo.Titulo.Should().Be("Configuración");
        admin.Enlaces.Select(e => e.Ruta).Should().Contain(["inbound/reglas-aviso", "inbound/gobierno"]);

        var gestor = CatalogoMenuLateral.Visibles(ContextoMenu(true, Roles.GestorCae)).Single(g => g.Grupo.Id == "inbound-config");
        gestor.Enlaces.Select(e => e.Ruta).Should().Contain("inbound/reglas-aviso").And.NotContain("inbound/gobierno");

        CatalogoMenuLateral.Visibles(ContextoMenu(true, Roles.Consulta)).Select(g => g.Grupo.Id).Should().NotContain("inbound-config");
    }

    [Fact]
    public void Gobierno_exige_el_rol_Administrador_y_Reglas_de_aviso_no_admite_Consulta()
    {
        var gobierno = typeof(GobiernoDeInbound).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();
        gobierno.Roles.Should().Be(Roles.Administrador);

        var reglas = typeof(ReglasAvisoInbound).GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single();
        reglas.Roles!.Split(',').Should().NotContain(Roles.Consulta);
    }

    // ---------------------------------------------------------------- Falsos

    private sealed class EstadoDePrueba(ClaimsPrincipal principal) : Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider
    {
        public override Task<Microsoft.AspNetCore.Components.Authorization.AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(principal));
    }

    private sealed class NoAplicaFalso : IServicioNoAplica
    {
        public RequerimientosDeContrato? Datos { get; set; }

        public List<SolicitudNoAplica> Guardadas { get; } = [];

        public Task<RespuestaConfiguracion<RequerimientosDeContrato>> ObtenerAsync(Guid contratoId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Datos is null ? RespuestaConfiguracion<RequerimientosDeContrato>.NoDisponible : RespuestaConfiguracion<RequerimientosDeContrato>.Correcta(Datos));

        public Task<RespuestaConfiguracion<ExcepcionNoAplica>> GuardarAsync(SolicitudNoAplica solicitud, CancellationToken cancellationToken = default)
        {
            Guardadas.Add(solicitud);
            return Task.FromResult(RespuestaConfiguracion<ExcepcionNoAplica>.Correcta(solicitud.Excepcion));
        }
    }

    private sealed class ReglasFalso : IServicioReglasAviso
    {
        public ReglasDeAviso? Datos { get; set; }

        public List<ReglaAviso> Guardadas { get; } = [];

        public List<(Guid Id, bool Activa)> Cambios { get; } = [];

        public Task<RespuestaConfiguracion<ReglasDeAviso>> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Datos is null ? RespuestaConfiguracion<ReglasDeAviso>.NoDisponible : RespuestaConfiguracion<ReglasDeAviso>.Correcta(Datos));

        public Task<RespuestaConfiguracion<ReglaAviso>> GuardarAsync(ReglaAviso regla, CancellationToken cancellationToken = default)
        {
            Guardadas.Add(regla);
            return Task.FromResult(RespuestaConfiguracion<ReglaAviso>.Correcta(regla));
        }

        public Task<RespuestaConfiguracion<ReglaAviso>> CambiarEstadoAsync(Guid id, bool activa, CancellationToken cancellationToken = default)
        {
            Cambios.Add((id, activa));
            return Task.FromResult(RespuestaConfiguracion<ReglaAviso>.Correcta(Datos!.Reglas.First(r => r.Id == id) with { Activa = activa }));
        }

        public Task<RespuestaConfiguracion<Guid>> EliminarAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(RespuestaConfiguracion<Guid>.Correcta(id));
    }

    private sealed class GobiernoFalso : IServicioGobiernoInbound
    {
        public GobiernoInbound? Datos { get; set; }

        public List<ConvalidacionConfigurada> Guardadas { get; } = [];

        public Task<RespuestaConfiguracion<GobiernoInbound>> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Datos is null ? RespuestaConfiguracion<GobiernoInbound>.NoDisponible : RespuestaConfiguracion<GobiernoInbound>.Correcta(Datos));

        public Task<RespuestaConfiguracion<GobiernoInbound>> GuardarConvalidacionAsync(ConvalidacionConfigurada configuracion, CancellationToken cancellationToken = default)
        {
            Guardadas.Add(configuracion);
            Datos = Datos! with { Convalidacion = configuracion };
            return Task.FromResult(RespuestaConfiguracion<GobiernoInbound>.Correcta(Datos));
        }
    }
}

using System.Globalization;
using System.Reflection;
using System.Resources;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones.ApoyoCartera;
using CaeManager.Application.Operaciones.ApoyoCartera.Commands;
using CaeManager.Application.Operaciones.ApoyoCartera.Queries;
using CaeManager.Application.Usuarios.Queries.ObtenerPersonasConCartera;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.IncorporacionCartera.Components;
using CaeManager.Web.Features.IncorporacionCartera.Recursos;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Panel «Dar acceso» (propuesta de apoyo entre Gestores CAE de un mismo Operador CAE) y fin de
/// un apoyo: «Desasignarme», «Retirar acceso» y «Revocar».
///
/// <para>
/// <b>Lo que SÍ observa:</b> qué enseña el panel a partir de lo que devuelven las Queries, y qué
/// Command envía con qué datos. <b>No observa</b> la autorización: quién puede proponer, a quién
/// y sobre qué lo deciden los handlers (Application.Tests) y la RLS (IntegrationTests). Aquí el
/// mediador es falso.
/// </para>
/// </summary>
public class PanelDarAccesoTests : BunitContext
{
    private static readonly Guid Yo = Guid.NewGuid();

    private readonly MediatorFalso _mediador = new();
    private readonly Action<string> _fijarRol;

    public PanelDarAccesoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        // Las acciones de fin de apoyo van dentro de SoloConEscritura: hace falta un rol que escriba.
        var sesion = AddAuthorization();
        sesion.SetAuthorized("yo");
        sesion.SetRoles("GestorCae");
        _fijarRol = rol => sesion.SetRoles(rol);
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ICurrentUserService>(_ => new UsuarioActualFalso(Yo));
    }

    private static PersonaConCartera Persona(Guid id, string nombre, DateTime? vigenciaHasta = null) =>
        new(id, nombre, "GestorCae", vigenciaHasta);

    /// <summary>Un apoyo vivo sobre <paramref name="operacion"/>, tal y como lo da la Query.</summary>
    private static ApoyoDeCarteraDto Apoyo(
        CarterasDeOperacion operacion, Guid apoyoId, string nombreApoyo, Guid proponenteId, string nombreProponente,
        DateOnly? ultimoDia = null) =>
        new(Guid.NewGuid(), operacion.TenantId, operacion.NombreTenant, operacion.AsignacionOperacionId,
            apoyoId, nombreApoyo, proponenteId, nombreProponente, ultimoDia);

    private static CarterasDeOperacion Operacion(string tenant, Guid? principal, params string[] apoyos) =>
        new(Guid.NewGuid(), Guid.NewGuid(), tenant,
            principal is { } id ? Persona(id, id == Yo ? "Yo" : "Otra persona") : null,
            apoyos.Select(a => Persona(Guid.NewGuid(), a)).ToList());

    private static PropuestaApoyoDto Enviada(CarterasDeOperacion operacion, string destinatario) =>
        new(Guid.NewGuid(), operacion.TenantId, operacion.NombreTenant, operacion.AsignacionOperacionId,
            Yo, "Yo", Guid.NewGuid(), destinatario, DateTime.UtcNow);

    private static AngleSharp.Dom.IElement Boton(IRenderedComponent<PanelDarAcceso> cut, string texto) =>
        cut.FindAll("button").Single(b => b.TextContent.Trim() == texto);

    [Fact]
    public void Quien_no_es_principal_de_ningun_Tenant_no_ve_el_panel()
    {
        _mediador.Operaciones = [Operacion("Empresa Ajena", Guid.NewGuid(), "Lucía"), Operacion("Empresa Sin Principal", null)];

        var cut = Render<PanelDarAcceso>();

        cut.Markup.Trim().Should().BeEmpty();
        _mediador.Enviadas.OfType<ObtenerPropuestasApoyoPendientesQuery>().Should().BeEmpty("sin nada que enseñar no hace falta preguntar");
    }

    [Fact]
    public void Solo_lista_los_Tenants_de_los_que_soy_principal_con_sus_apoyos_y_mis_propuestas_sin_responder()
    {
        var mia = Operacion("Empresa Mía", Yo, "Lucía", "Pau");
        var ajena = Operacion("Empresa Ajena", Guid.NewGuid());
        var propuesta = Enviada(mia, "Nuria");
        _mediador.Operaciones = [mia, ajena];
        _mediador.MisPropuestas = [propuesta];

        var cut = Render<PanelDarAcceso>();

        cut.FindAll("[data-dar-acceso-operacion]").Should().ContainSingle()
            .Which.GetAttribute("data-dar-acceso-operacion").Should().Be(mia.AsignacionOperacionId.ToString());
        var fila = cut.Find("[data-dar-acceso-operacion]");
        fila.TextContent.Should().Contain("Empresa Mía").And.Contain("Con acceso de apoyo:");
        fila.QuerySelectorAll("[data-apoyo-de]").Select(a => a.TextContent.Trim())
            .Should().Equal("Lucía · Apoyo", "Pau · Apoyo");
        fila.QuerySelector($"[data-propuesta-enviada='{propuesta.Id}']")!.TextContent.Should().Contain("Nuria, sin responder todavía");
        cut.Markup.Should().NotContain("Empresa Ajena");
    }

    [Fact]
    public void Con_TenantId_pregunta_solo_por_ese_Tenant()
    {
        var tenantId = Guid.NewGuid();

        Render<PanelDarAcceso>(p => p.Add(c => c.TenantId, tenantId));

        _mediador.Enviadas.OfType<ObtenerPersonasConCarteraQuery>().Should().ContainSingle().Which.TenantId.Should().Be(tenantId);
    }

    [Fact]
    public async Task Proponer_envia_la_operacion_y_el_destinatario_elegido_y_nada_mas()
    {
        var mia = Operacion("Empresa Mía", Yo);
        var lucia = new DestinatarioDeApoyoDto(Guid.NewGuid(), "Lucía");
        _mediador.Operaciones = [mia];
        _mediador.Destinatarios = [lucia, new DestinatarioDeApoyoDto(Guid.NewGuid(), "Pau")];
        var cut = Render<PanelDarAcceso>();

        await cut.InvokeAsync(() => cut.Find("[data-dar-acceso-operacion] > button").Click());

        _mediador.Enviadas.OfType<ObtenerDestinatariosDeApoyoQuery>().Should().ContainSingle()
            .Which.AsignacionOperacionId.Should().Be(mia.AsignacionOperacionId);
        cut.FindAll("select option").Select(o => o.TextContent).Should().Equal("Elige un Gestor CAE", "Lucía", "Pau");
        cut.FindAll("input[type=date]").Should().ContainSingle("la fecha de fin es opcional (D-5)")
            .Which.GetAttribute("value").Should().BeNullOrEmpty("sin fecha, el apoyo no caduca");
        cut.FindAll("select").Should().ContainSingle("solo se elige a quién: el rol no es parámetro de quien propone");

        cut.Find("select").Change(lucia.UsuarioId.ToString());
        await cut.InvokeAsync(() => Boton(cut, "Proponer").Click());

        _mediador.Enviadas.OfType<ProponerApoyoCarteraCommand>().Should().ContainSingle()
            .Which.Should().Be(new ProponerApoyoCarteraCommand(mia.AsignacionOperacionId, lucia.UsuarioId));
        cut.FindComponent<DrawerFormulario>().Instance.Visible.Should().BeFalse("propuesta enviada, el formulario se cierra");
    }

    [Fact]
    public async Task Proponer_sin_elegir_a_nadie_no_envia_el_comando_y_lo_dice()
    {
        _mediador.Operaciones = [Operacion("Empresa Mía", Yo)];
        _mediador.Destinatarios = [new DestinatarioDeApoyoDto(Guid.NewGuid(), "Lucía")];
        var cut = Render<PanelDarAcceso>();
        await cut.InvokeAsync(() => cut.Find("[data-dar-acceso-operacion] > button").Click());

        await cut.InvokeAsync(() => Boton(cut, "Proponer").Click());

        _mediador.Enviadas.OfType<ProponerApoyoCarteraCommand>().Should().BeEmpty();
        cut.FindComponent<DrawerFormulario>().Instance.MensajeError.Should().Be("Elige a quién se lo propones.");
    }

    [Fact]
    public async Task Si_el_handler_rechaza_la_propuesta_el_formulario_sigue_abierto_con_el_motivo()
    {
        var lucia = new DestinatarioDeApoyoDto(Guid.NewGuid(), "Lucía");
        _mediador.Operaciones = [Operacion("Empresa Mía", Yo)];
        _mediador.Destinatarios = [lucia];
        _mediador.AlProponer = Result.Fallo<Guid>(ErroresPropuestaApoyo.NoEresElPrincipal);
        var cut = Render<PanelDarAcceso>();
        await cut.InvokeAsync(() => cut.Find("[data-dar-acceso-operacion] > button").Click());
        cut.Find("select").Change(lucia.UsuarioId.ToString());

        await cut.InvokeAsync(() => Boton(cut, "Proponer").Click());

        var drawer = cut.FindComponent<DrawerFormulario>().Instance;
        drawer.Visible.Should().BeTrue();
        drawer.MensajeError.Should().Contain("Solo quien es principal");
    }

    [Fact]
    public async Task Sin_nadie_a_quien_proponerselo_el_formulario_lo_dice_y_no_deja_proponer()
    {
        _mediador.Operaciones = [Operacion("Empresa Mía", Yo)];
        _mediador.Destinatarios = [];
        var cut = Render<PanelDarAcceso>();

        await cut.InvokeAsync(() => cut.Find("[data-dar-acceso-operacion] > button").Click());

        cut.FindAll("select").Should().BeEmpty();
        cut.Markup.Should().Contain("No hay ningún otro Gestor CAE de tu organización");
        var proponer = Boton(cut, "Proponer");
        proponer.HasAttribute("disabled").Should().BeTrue();
        proponer.GetAttribute("title").Should().Be("No hay a quién proponérselo");
    }

    [Fact]
    public async Task Retirar_una_propuesta_pregunta_antes_y_solo_al_confirmar_envia_el_comando()
    {
        var mia = Operacion("Empresa Mía", Yo);
        var propuesta = Enviada(mia, "Nuria");
        _mediador.Operaciones = [mia];
        _mediador.MisPropuestas = [propuesta];
        var cut = Render<PanelDarAcceso>();

        await cut.InvokeAsync(() => Boton(cut, "Retirar propuesta").Click());

        _mediador.Enviadas.OfType<RetirarPropuestaApoyoCarteraCommand>().Should().BeEmpty();
        var dialogo = cut.FindComponent<DialogoConfirmacion>();
        dialogo.Instance.Visible.Should().BeTrue();
        dialogo.Instance.Mensaje.Should().Contain("Nuria").And.Contain("Empresa Mía");

        await cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());

        _mediador.Enviadas.OfType<RetirarPropuestaApoyoCarteraCommand>().Should().ContainSingle()
            .Which.Should().Be(new RetirarPropuestaApoyoCarteraCommand(propuesta.Id));
        cut.FindAll("[data-propuesta-enviada]").Should().BeEmpty("retirada, la recarga ya no la trae");
    }

    // ---- D-5: fecha de fin opcional y rótulo ----

    [Fact]
    public async Task Proponer_con_fecha_envia_el_ultimo_dia_elegido()
    {
        var mia = Operacion("Empresa Mía", Yo);
        var lucia = new DestinatarioDeApoyoDto(Guid.NewGuid(), "Lucía");
        var ultimoDia = DiaDeNegocio.Hoy().AddDays(10);
        _mediador.Operaciones = [mia];
        _mediador.Destinatarios = [lucia];
        var cut = Render<PanelDarAcceso>();
        await cut.InvokeAsync(() => cut.Find("[data-dar-acceso-operacion] > button").Click());

        cut.Find("select").Change(lucia.UsuarioId.ToString());
        await cut.Find("input[type=date]").InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = ultimoDia.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        await cut.InvokeAsync(() => Boton(cut, "Proponer").Click());

        _mediador.Enviadas.OfType<ProponerApoyoCarteraCommand>().Should().ContainSingle()
            .Which.Should().Be(new ProponerApoyoCarteraCommand(mia.AsignacionOperacionId, lucia.UsuarioId, ultimoDia));
    }

    [Fact]
    public async Task Proponer_con_una_fecha_ya_pasada_no_envia_nada_y_lo_dice_en_el_formulario()
    {
        var lucia = new DestinatarioDeApoyoDto(Guid.NewGuid(), "Lucía");
        _mediador.Operaciones = [Operacion("Empresa Mía", Yo)];
        _mediador.Destinatarios = [lucia];
        var cut = Render<PanelDarAcceso>();
        await cut.InvokeAsync(() => cut.Find("[data-dar-acceso-operacion] > button").Click());

        cut.Find("select").Change(lucia.UsuarioId.ToString());
        await cut.Find("input[type=date]").InputAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = DiaDeNegocio.Hoy().AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) });
        await cut.InvokeAsync(() => Boton(cut, "Proponer").Click());

        _mediador.Enviadas.OfType<ProponerApoyoCarteraCommand>().Should().BeEmpty();
        cut.FindComponent<DrawerFormulario>().Instance.MensajeError.Should().Be("La fecha de fin del apoyo no puede ser anterior a hoy.");
    }

    [Fact]
    public void El_rotulo_es_Apoyo_hasta_la_fecha_solo_si_la_cartera_lleva_fecha_de_fin()
    {
        var lucia = Guid.NewGuid();
        var pau = Guid.NewGuid();
        var ultimoDia = new DateOnly(2031, 3, 14);
        var mia = new CarterasDeOperacion(Guid.NewGuid(), Guid.NewGuid(), "Empresa Mía", Persona(Yo, "Yo"),
            [Persona(lucia, "Lucía", VigenciaDeApoyo.HastaElFinalDe(ultimoDia)), Persona(pau, "Pau")]);
        var ajena = Operacion("Empresa Ajena", Guid.NewGuid());
        _mediador.Operaciones = [mia];
        _mediador.Apoyos = new ApoyosDeCarteraDto(
            [Apoyo(ajena, Yo, "Yo", Guid.NewGuid(), "Marta", ultimoDia), Apoyo(Operacion("Empresa Sin Fecha", Guid.NewGuid()), Yo, "Yo", Guid.NewGuid(), "Marta")],
            [], []);

        var cut = Render<PanelDarAcceso>();

        cut.Find($"[data-apoyo-de='{lucia}']").TextContent.Trim().Should().Be("Lucía · Apoyo hasta 14/03/2031",
            "«hasta el día D» incluye el día D: se pinta el último día con acceso, no el instante de cierre");
        cut.Find($"[data-apoyo-de='{pau}']").TextContent.Trim().Should().Be("Pau · Apoyo");
        cut.FindAll("[data-mi-apoyo] .dar-acceso-nota").Select(n => n.TextContent.Trim())
            .Should().Equal("Apoyo hasta 14/03/2031, a propuesta de Marta", "Apoyo, a propuesta de Marta");
    }

    // ---- Fin de un apoyo: Desasignarme, Retirar acceso (D-6) y Revocar (D-4) ----

    [Fact]
    public async Task Quien_solo_es_apoyo_ve_sus_accesos_y_Desasignarme_pregunta_antes_de_enviar_el_comando()
    {
        var ajena = Operacion("Empresa Ajena", Guid.NewGuid());
        var mio = Apoyo(ajena, Yo, "Yo", Guid.NewGuid(), "Marta");
        _mediador.Operaciones = [ajena];
        _mediador.Apoyos = new ApoyosDeCarteraDto([mio], [], []);

        var cut = Render<PanelDarAcceso>();

        cut.FindAll("[data-dar-acceso-operacion]").Should().BeEmpty("no es principal de nada: no puede dar acceso");
        cut.Find($"[data-mi-apoyo='{mio.PropuestaId}']").TextContent.Should().Contain("Empresa Ajena");

        await cut.InvokeAsync(() => Boton(cut, "Desasignarme").Click());

        _mediador.Enviadas.OfType<DesasignarmeDeApoyoCommand>().Should().BeEmpty("quitarse un Tenant no se deshace: pregunta antes");
        var dialogo = cut.FindComponents<DialogoConfirmacion>().Single(d => d.Instance.Visible);
        dialogo.Instance.Mensaje.Should().Contain("Empresa Ajena");

        await cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());

        _mediador.Enviadas.OfType<DesasignarmeDeApoyoCommand>().Should().ContainSingle()
            .Which.Should().Be(new DesasignarmeDeApoyoCommand(mio.PropuestaId));
        _mediador.Enviadas.Should().NotContain(e => e is RetirarApoyoConcedidoCommand || e is RevocarApoyoCarteraCommand);
        cut.FindAll("[data-mi-apoyo]").Should().BeEmpty("terminado, la recarga ya no lo trae");
    }

    [Fact]
    public async Task Retirar_acceso_solo_se_ofrece_junto_al_apoyo_que_concedi_yo_y_envia_su_comando()
    {
        var lucia = Guid.NewGuid();
        var pau = Guid.NewGuid();
        var mia = new CarterasDeOperacion(Guid.NewGuid(), Guid.NewGuid(), "Empresa Mía", Persona(Yo, "Yo"),
            [Persona(lucia, "Lucía"), Persona(pau, "Pau")]);
        var concedido = Apoyo(mia, lucia, "Lucía", Yo, "Yo");
        _mediador.Operaciones = [mia];
        _mediador.Apoyos = new ApoyosDeCarteraDto([], [concedido], []);

        var cut = Render<PanelDarAcceso>();

        cut.FindAll("[data-retirar-apoyo]").Should().ContainSingle("a Pau no le di yo el acceso: no lo retiro desde aquí (D-6)")
            .Which.GetAttribute("data-retirar-apoyo").Should().Be(concedido.PropuestaId.ToString());
        cut.Find($"[data-apoyo-de='{pau}']").QuerySelectorAll("button").Should().BeEmpty();

        await cut.InvokeAsync(() => cut.Find("[data-retirar-apoyo]").Click());
        var dialogo = cut.FindComponents<DialogoConfirmacion>().Single(d => d.Instance.Visible);
        dialogo.Instance.Mensaje.Should().Contain("Lucía").And.Contain("Empresa Mía");
        _mediador.Enviadas.OfType<RetirarApoyoConcedidoCommand>().Should().BeEmpty();

        await cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());

        _mediador.Enviadas.OfType<RetirarApoyoConcedidoCommand>().Should().ContainSingle()
            .Which.Should().Be(new RetirarApoyoConcedidoCommand(concedido.PropuestaId));
        _mediador.Enviadas.Should().NotContain(e => e is DesasignarmeDeApoyoCommand || e is RevocarApoyoCarteraCommand);
    }

    [Fact]
    public async Task Revocar_se_ofrece_sobre_los_apoyos_revocables_y_envia_su_comando()
    {
        var ajena = Operacion("Empresa Ajena", Guid.NewGuid());
        var revocable = Apoyo(ajena, Guid.NewGuid(), "Lucía", Guid.NewGuid(), "Marta");
        _fijarRol("CoordinadorCae");
        _mediador.Apoyos = new ApoyosDeCarteraDto([], [], [revocable]);

        var cut = Render<PanelDarAcceso>();

        cut.Find($"[data-apoyo-revocable='{revocable.PropuestaId}']").TextContent
            .Should().Contain("Empresa Ajena").And.Contain("Lucía · Apoyo, a propuesta de Marta");

        await cut.InvokeAsync(() => Boton(cut, "Revocar").Click());
        var dialogo = cut.FindComponents<DialogoConfirmacion>().Single(d => d.Instance.Visible);
        await cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());

        _mediador.Enviadas.OfType<RevocarApoyoCarteraCommand>().Should().ContainSingle()
            .Which.Should().Be(new RevocarApoyoCarteraCommand(revocable.PropuestaId));
        _mediador.Enviadas.Should().NotContain(e => e is DesasignarmeDeApoyoCommand || e is RetirarApoyoConcedidoCommand);
    }

    [Fact]
    public async Task Si_el_handler_rechaza_el_fin_del_apoyo_no_se_da_por_hecho_y_la_lista_se_recarga()
    {
        var ajena = Operacion("Empresa Ajena", Guid.NewGuid());
        var mio = Apoyo(ajena, Yo, "Yo", Guid.NewGuid(), "Marta");
        _mediador.Apoyos = new ApoyosDeCarteraDto([mio], [], []);
        _mediador.AlTerminar = Result.Fallo(ErroresPropuestaApoyo.EresElPrincipal);
        var cut = Render<PanelDarAcceso>();
        var lecturasAntes = _mediador.Enviadas.OfType<ObtenerApoyosDeCarteraQuery>().Count();

        await cut.InvokeAsync(() => Boton(cut, "Desasignarme").Click());
        await cut.InvokeAsync(() => cut.FindComponents<DialogoConfirmacion>().Single(d => d.Instance.Visible).Instance.OnConfirmar.InvokeAsync());

        cut.FindAll("[data-mi-apoyo]").Should().ContainSingle("el handler lo rechazó: el acceso sigue ahí");
        _mediador.Enviadas.OfType<ObtenerApoyosDeCarteraQuery>().Should().HaveCount(lecturasAntes + 1);
        cut.FindComponents<DialogoConfirmacion>().Should().OnlyContain(d => !d.Instance.Visible);
    }

    /// <summary>
    /// Con un rol que no escribe (una delegación de solo consulta), <c>SoloConEscritura</c> oculta
    /// las tres acciones: el Command las denegaría igual (AutorizacionEscrituraBehavior).
    /// </summary>
    [Fact]
    public void Con_un_rol_sin_escritura_no_se_ofrece_ninguna_accion_de_fin_de_apoyo()
    {
        var lucia = Guid.NewGuid();
        var mia = new CarterasDeOperacion(Guid.NewGuid(), Guid.NewGuid(), "Empresa Mía", Persona(Yo, "Yo"), [Persona(lucia, "Lucía")]);
        var ajena = Operacion("Empresa Ajena", Guid.NewGuid());
        _fijarRol("Consulta");
        _mediador.Operaciones = [mia];
        _mediador.Apoyos = new ApoyosDeCarteraDto(
            [Apoyo(ajena, Yo, "Yo", Guid.NewGuid(), "Marta")],
            [Apoyo(mia, lucia, "Lucía", Yo, "Yo")],
            [Apoyo(ajena, Guid.NewGuid(), "Pau", Guid.NewGuid(), "Marta")]);

        var cut = Render<PanelDarAcceso>();

        cut.FindAll("[data-mi-apoyo]").Should().ContainSingle("se sigue viendo qué accesos hay");
        cut.FindAll("button").Select(b => b.TextContent.Trim())
            .Should().NotContain(["Desasignarme", "Retirar acceso", "Revocar"]);
    }

    // ---- Recursos: cada código PropuestaApoyo.X tiene su ErrorX en es y en ca-ES ----

    public static TheoryData<string> Culturas => new() { "", "ca-ES" };

    /// <summary>
    /// El cruce literal de claves no ve una clave compuesta: sin esto, un código nuevo sin texto
    /// caería al genérico sin que nada se pusiera en rojo. Sin tryParents: una clave que solo
    /// esté en el neutral no cuenta para ca-ES.
    /// </summary>
    [Theory]
    [MemberData(nameof(Culturas))]
    public void Cada_codigo_de_error_de_la_propuesta_de_apoyo_tiene_su_texto(string cultura)
    {
        var textos = new ResourceManager(typeof(TextosApoyoCartera))
            .GetResourceSet(CultureInfo.GetCultureInfo(cultura), createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No hay recurso para la cultura «{cultura}».");
        var errores = typeof(ErroresPropuestaApoyo)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(Error))
            .Select(f => (Error)f.GetValue(null)!)
            .ToList();

        errores.Should().HaveCountGreaterThan(10, "si la reflexión no encuentra los códigos, el test no comprueba nada");
        errores.Should().OnlyContain(e => e.Codigo.StartsWith("PropuestaApoyo.", StringComparison.Ordinal));
        errores.Select(TextosApoyoCartera.ClaveDeError)
            .Where(clave => string.IsNullOrWhiteSpace(textos.GetString(clave)))
            .Should().BeEmpty();
    }

    private sealed class UsuarioActualFalso(Guid id) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(id);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("GestorCae");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class MediatorFalso : IMediator
    {
        public List<object> Enviadas { get; } = [];
        public IReadOnlyList<CarterasDeOperacion> Operaciones { get; set; } = [];

        /// <summary>Las propuestas que quien mira envió y siguen sin responder.</summary>
        public List<PropuestaApoyoDto> MisPropuestas { get; set; } = [];

        public IReadOnlyList<DestinatarioDeApoyoDto> Destinatarios { get; set; } = [];
        public Result<Guid> AlProponer { get; set; } = Result.Exito(Guid.NewGuid());

        /// <summary>Los apoyos vivos sobre los que quien mira puede hacer algo.</summary>
        public ApoyosDeCarteraDto Apoyos { get; set; } = ApoyosDeCarteraDto.Vacio;

        /// <summary>Lo que responden los tres Commands de fin de apoyo.</summary>
        public Result AlTerminar { get; set; } = Result.Exito();

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            object respuesta = request switch
            {
                ObtenerPersonasConCarteraQuery => Operaciones,
                ObtenerPropuestasApoyoPendientesQuery => new PropuestasApoyoPendientesDto([], MisPropuestas.ToList()),
                ObtenerDestinatariosDeApoyoQuery => Destinatarios,
                ProponerApoyoCarteraCommand => AlProponer,
                RetirarPropuestaApoyoCarteraCommand retirar => Retirar(retirar.PropuestaId),
                ObtenerApoyosDeCarteraQuery => Apoyos,
                DesasignarmeDeApoyoCommand fin => Terminar(fin.PropuestaId),
                RetirarApoyoConcedidoCommand fin => Terminar(fin.PropuestaId),
                RevocarApoyoCarteraCommand fin => Terminar(fin.PropuestaId),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return Task.FromResult((TResponse)respuesta);
        }

        private Result Terminar(Guid propuestaId)
        {
            if (AlTerminar.EsExitoso)
                Apoyos = new ApoyosDeCarteraDto(
                    Apoyos.Mios.Where(a => a.PropuestaId != propuestaId).ToList(),
                    Apoyos.Concedidos.Where(a => a.PropuestaId != propuestaId).ToList(),
                    Apoyos.Revocables.Where(a => a.PropuestaId != propuestaId).ToList());
            return AlTerminar;
        }

        private Result Retirar(Guid id)
        {
            MisPropuestas.RemoveAll(p => p.Id == id);
            return Result.Exito();
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
}

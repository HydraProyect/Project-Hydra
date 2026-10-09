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
/// Panel «Dar acceso» (propuesta de apoyo entre Gestores CAE de un mismo Operador CAE).
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

    public PanelDarAccesoTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ICurrentUserService>(_ => new UsuarioActualFalso(Yo));
    }

    private static PersonaConCartera Persona(Guid id, string nombre) => new(id, nombre, "GestorCae", null);

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
        fila.TextContent.Should().Contain("Empresa Mía").And.Contain("Con acceso de apoyo: Lucía, Pau");
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
        cut.FindAll("input[type=date]").Should().BeEmpty("el apoyo no ofrece fecha de fin");
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
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return Task.FromResult((TResponse)respuesta);
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

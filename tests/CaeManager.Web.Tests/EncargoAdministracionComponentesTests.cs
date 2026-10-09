using System.Security.Claims;
using Bunit;
using CaeManager.Application.Configuracion.Commands.ActualizarPresupuestoIa;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Tenants.Commands.RegistrarEncargoAdministracion;
using CaeManager.Application.Tenants.Commands.RetirarEncargoAdministracion;
using CaeManager.Application.Tenants.Queries.ObtenerEncargosAdministracion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Configuracion.Components;
using CaeManager.Web.Features.EncargoDeAdministracion;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pantalla del Encargo de administración (decisión D-8, 2026-10-08): el panel que lo muestra, lo
/// retira y lo registra, el aviso de quien administra por encargo y la tarjeta del presupuesto de IA.
///
/// <para>
/// <b>Lo que SÍ observa:</b> qué pinta cada componente con lo que devuelven las consultas, qué
/// comando envía cada botón y con qué datos, que retirar pregunta antes y que la cláusula vacía no
/// llega a enviarse.
/// </para>
/// <para>
/// <b>Lo que NO observa:</b> la autoridad. Quién puede leer, registrar o retirar el encargo lo
/// deciden las consultas y los comandos (Application.Tests y, bajo RLS, IntegrationTests); aquí las
/// consultas ya vienen respondidas. Tampoco que el claim del encargo esté bien puesto en el
/// principal: eso es del middleware del rol efectivo.
/// </para>
/// </summary>
public class EncargoAdministracionComponentesTests : BunitContext
{
    private const string Operador = "Operador CAE externo de prueba";
    private const string Clausula = "Cláusula 7.ª del contrato de servicio";

    private readonly MediatorFalso _mediator = new();

    public EncargoAdministracionComponentesTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddSingleton<IMediator>(_mediator);
        Services.AddSingleton<ToastService>();
    }

    private ToastService Toasts => Services.GetRequiredService<ToastService>();

    private static EncargoAdministracionDto Encargo(
        EstadoEncargoAdministracion estado = EstadoEncargoAdministracion.Vigente, string operador = Operador) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), operador, Clausula, "v1",
            OrigenEncargoAdministracion.AprovisionamientoDePlataforma, Guid.NewGuid(),
            new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
            null, null, estado == EstadoEncargoAdministracion.Retirado ? new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc) : null,
            estado);

    private void ComoUsuario(bool porEncargo)
    {
        Services.AddScoped<AuthenticationStateProvider>(_ => new Autenticacion(porEncargo));
        Services.AddAuthorizationCore();
        Services.AddCascadingAuthenticationState();
    }

    // ------------------------------------------------------------------ panel

    [Fact]
    public void El_panel_pinta_el_encargo_sin_retirar_con_su_Operador_CAE_y_su_clausula()
    {
        _mediator.Encargos = [Encargo(), Encargo(EstadoEncargoAdministracion.Retirado, "Operador CAE externo anterior")];

        var cut = Render<EncargoAdministracionPanel>();

        var fila = cut.FindAll("[data-testid=encargo-vigente]").Should().ContainSingle("el retirado ya no gobierna nada").Subject;
        fila.TextContent.Should().Contain(Operador).And.Contain(Clausula).And.Contain("Vigente").And.Contain("Sin fecha de fin");
        cut.Markup.Should().NotContain("Operador CAE externo anterior");
        cut.Markup.Should().Contain("administran por encargo").And.NotContain("es administrador de");
    }

    [Fact]
    public void Sin_nada_que_mostrar_el_panel_no_pinta_nada()
    {
        // Es lo que recibe quien no gobierna el encargo: las dos consultas le devuelven vacío.
        var cut = Render<EncargoAdministracionPanel>(p => p.Add(x => x.PermiteRegistrar, true));

        cut.FindAll("[data-testid=encargo-administracion]").Should().BeEmpty();
        cut.FindAll("button").Should().BeEmpty();
    }

    [Fact]
    public void Sin_PermiteRegistrar_no_se_piden_ni_se_ofrecen_las_operaciones_encargables()
    {
        _mediator.Encargos = [Encargo()];
        _mediator.Encargables = [new OperacionEncargableDto(Guid.NewGuid(), Guid.NewGuid(), "Otro Operador CAE externo")];

        var cut = Render<EncargoAdministracionPanel>();

        cut.FindAll("[data-testid=encargo-registrar]").Should().BeEmpty();
        _mediator.Consultas.Should().NotContain(c => c is ObtenerOperacionesEncargablesQuery);
    }

    [Fact]
    public async Task Retirar_pregunta_antes_y_solo_al_confirmar_envia_el_comando()
    {
        var encargo = Encargo();
        _mediator.Encargos = [encargo];
        var cut = Render<EncargoAdministracionPanel>();

        await cut.Find("[data-testid=encargo-retirar]").ClickAsync(new());

        var dialogo = cut.FindComponent<DialogoConfirmacion>();
        dialogo.Instance.Visible.Should().BeTrue();
        dialogo.Instance.Titulo.Should().Contain(Operador);
        _mediator.Comandos.Should().BeEmpty("retirar pregunta antes: el clic no envía nada");

        _mediator.Encargos = [];
        await cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());

        _mediator.Comandos.Should().ContainSingle().Which.Should().Be(new RetirarEncargoAdministracionCommand(encargo.Id));
        cut.FindAll("[data-testid=encargo-vigente]").Should().BeEmpty("tras retirar se vuelve a leer el estado");
        Toasts.Mensajes.Should().Contain(t => t.Tono == TonoToast.Exito);
    }

    [Fact]
    public async Task Descartar_la_pregunta_no_retira_nada()
    {
        _mediator.Encargos = [Encargo()];
        var cut = Render<EncargoAdministracionPanel>();

        await cut.Find("[data-testid=encargo-retirar]").ClickAsync(new());
        var dialogo = cut.FindComponent<DialogoConfirmacion>();
        await cut.InvokeAsync(() => dialogo.Instance.VisibleChanged.InvokeAsync(false));

        _mediator.Comandos.Should().BeEmpty();
        cut.FindComponent<DialogoConfirmacion>().Instance.Visible.Should().BeFalse();
        cut.FindAll("[data-testid=encargo-vigente]").Should().ContainSingle();
    }

    [Fact]
    public async Task El_rechazo_al_retirar_se_avisa_y_el_encargo_sigue_en_pantalla()
    {
        _mediator.Encargos = [Encargo()];
        _mediator.AlRetirar = _ => Result.Fallo(Error.Crear("EncargoAdministracion.SinPermiso", "No puedes retirar este encargo."));
        var cut = Render<EncargoAdministracionPanel>();

        await cut.Find("[data-testid=encargo-retirar]").ClickAsync(new());
        await cut.InvokeAsync(() => cut.FindComponent<DialogoConfirmacion>().Instance.OnConfirmar.InvokeAsync());

        Toasts.Mensajes.Should().Contain(t => t.Tono == TonoToast.Error && t.Mensaje == "No puedes retirar este encargo.");
        cut.FindAll("[data-testid=encargo-vigente]").Should().ContainSingle();
    }

    [Fact]
    public async Task Registrar_exige_la_clausula_y_la_vacia_no_llega_a_enviarse()
    {
        var operacion = new OperacionEncargableDto(Guid.NewGuid(), Guid.NewGuid(), Operador);
        _mediator.Encargables = [operacion];
        var cut = Render<EncargoAdministracionPanel>(p => p.Add(x => x.PermiteRegistrar, true));

        await cut.Find("[data-testid=encargo-registrar]").ClickAsync(new());
        var drawer = cut.FindComponent<DrawerFormulario>();
        drawer.Instance.Visible.Should().BeTrue();

        await cut.InvokeAsync(() => drawer.Instance.OnGuardar.InvokeAsync());

        _mediator.Comandos.Should().BeEmpty("sin cláusula no se envía nada");
        cut.Markup.Should().Contain("Indica la cláusula del contrato que ampara el encargo.");
        cut.FindComponent<DrawerFormulario>().Instance.Visible.Should().BeTrue();

        await cut.InvokeAsync(() => cut.FindComponent<CampoTextarea>().Instance.ValorChanged.InvokeAsync("   "));
        await cut.InvokeAsync(() => drawer.Instance.OnGuardar.InvokeAsync());

        _mediator.Comandos.Should().BeEmpty("unos espacios no son una cláusula");
    }

    [Fact]
    public async Task Con_clausula_registra_el_encargo_sobre_esa_operacion_y_sin_fecha_de_fin()
    {
        var operacion = new OperacionEncargableDto(Guid.NewGuid(), Guid.NewGuid(), Operador);
        _mediator.Encargables = [operacion];
        var cut = Render<EncargoAdministracionPanel>(p => p.Add(x => x.PermiteRegistrar, true));

        await cut.Find("[data-testid=encargo-registrar]").ClickAsync(new());
        await cut.InvokeAsync(() => cut.FindComponent<CampoTextarea>().Instance.ValorChanged.InvokeAsync(Clausula));
        _mediator.Encargos = [Encargo()];
        _mediator.Encargables = [];
        await cut.InvokeAsync(() => cut.FindComponent<DrawerFormulario>().Instance.OnGuardar.InvokeAsync());

        _mediator.Comandos.Should().ContainSingle().Which.Should()
            .Be(new RegistrarEncargoAdministracionCommand(operacion.AsignacionOperacionId, Clausula, null));
        cut.FindComponent<DrawerFormulario>().Instance.Visible.Should().BeFalse();
        cut.FindAll("[data-testid=encargo-vigente]").Should().ContainSingle("tras registrar se vuelve a leer el estado");
        cut.FindAll("[data-testid=encargo-registrar]").Should().BeEmpty();
    }

    [Fact]
    public async Task El_rechazo_al_registrar_se_muestra_en_el_formulario_y_no_lo_cierra()
    {
        _mediator.Encargables = [new OperacionEncargableDto(Guid.NewGuid(), Guid.NewGuid(), Operador)];
        _mediator.AlRegistrar = _ => Result.Fallo<Guid>(Error.Crear("EncargoAdministracion.YaExiste", "Esa operación ya tiene un encargo."));
        var cut = Render<EncargoAdministracionPanel>(p => p.Add(x => x.PermiteRegistrar, true));

        await cut.Find("[data-testid=encargo-registrar]").ClickAsync(new());
        await cut.InvokeAsync(() => cut.FindComponent<CampoTextarea>().Instance.ValorChanged.InvokeAsync(Clausula));
        await cut.InvokeAsync(() => cut.FindComponent<DrawerFormulario>().Instance.OnGuardar.InvokeAsync());

        var drawer = cut.FindComponent<DrawerFormulario>().Instance;
        drawer.Visible.Should().BeTrue();
        drawer.MensajeError.Should().Be("Esa operación ya tiene un encargo.");
    }

    // ------------------------------------------------------------------ aviso

    [Fact]
    public void Quien_administra_por_encargo_lo_ve_escrito()
    {
        ComoUsuario(porEncargo: true);

        var cut = Render<AvisoAdministraPorEncargo>();

        var aviso = cut.Find("[data-testid=aviso-por-encargo]");
        aviso.TextContent.Should().Contain("Por encargo").And.Contain("Administras esta organización por encargo");
        aviso.TextContent.Should().NotContain("es administrador de");
    }

    [Fact]
    public void Quien_no_lleva_el_claim_del_encargo_no_ve_el_aviso()
    {
        ComoUsuario(porEncargo: false);

        Render<AvisoAdministraPorEncargo>().FindAll("[data-testid=aviso-por-encargo]").Should().BeEmpty();
    }

    // ------------------------------------------------ presupuesto de IA reservado

    [Fact]
    public void A_quien_administra_por_encargo_no_se_le_ofrece_fijar_el_presupuesto_de_IA()
    {
        ComoUsuario(porEncargo: true);

        var cut = Render<ParametrosSistemaPanel>();

        cut.WaitForAssertion(() => cut.Find("[data-testid=presupuesto-ia-reservado]").TextContent.Should()
            .Contain("no forma parte del Encargo de administración"));
        cut.FindAll("input[type=number]").Should().HaveCount(4, "umbrales, horas y segundos siguen; el presupuesto no");
        cut.FindAll("button").Should().HaveCount(2, "quedan los dos «Guardar» de lo que el encargo sí abre");
    }

    [Fact]
    public async Task Un_Administrador_propio_si_ve_y_guarda_el_presupuesto_de_IA()
    {
        ComoUsuario(porEncargo: false);

        var cut = Render<ParametrosSistemaPanel>();

        cut.WaitForAssertion(() => cut.FindAll("input[type=number]").Should().HaveCount(5, "control positivo: con el presupuesto"));
        cut.FindAll("[data-testid=presupuesto-ia-reservado]").Should().BeEmpty();
        await cut.FindAll("button").Last().ClickAsync(new());
        _mediator.Comandos.Should().ContainSingle().Which.Should().BeOfType<ActualizarPresupuestoIaCommand>();
    }

    // ------------------------------------------------------------------ dobles

    private sealed class Autenticacion(bool porEncargo) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.Role, CaeManager.Infrastructure.Identity.Roles.Administrador) };
            if (porEncargo)
                claims.Add(new Claim(RolEfectivoDelWorkspaceMiddleware.TipoClaimEncargoAdministracion, Guid.NewGuid().ToString()));
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))));
        }
    }

    private sealed class MediatorFalso : IMediator
    {
        public IReadOnlyList<EncargoAdministracionDto> Encargos { get; set; } = [];
        public IReadOnlyList<OperacionEncargableDto> Encargables { get; set; } = [];
        public Func<RetirarEncargoAdministracionCommand, Result> AlRetirar { get; set; } = _ => Result.Exito();
        public Func<RegistrarEncargoAdministracionCommand, Result<Guid>> AlRegistrar { get; set; } = _ => Result.Exito(Guid.NewGuid());
        public List<object> Consultas { get; } = [];
        public List<object> Comandos { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta;
            switch (request)
            {
                case ObtenerEncargosAdministracionQuery:
                    Consultas.Add(request);
                    respuesta = Encargos;
                    break;
                case ObtenerOperacionesEncargablesQuery:
                    Consultas.Add(request);
                    respuesta = Encargables;
                    break;
                case RetirarEncargoAdministracionCommand retirar:
                    Comandos.Add(request);
                    respuesta = AlRetirar(retirar);
                    break;
                case RegistrarEncargoAdministracionCommand registrar:
                    Comandos.Add(request);
                    respuesta = AlRegistrar(registrar);
                    break;
                case ObtenerParametroSistemaQuery:
                    respuesta = new ParametroSistemaDto(30, 7, new TimeOnly(8, 0), new TimeOnly(17, 0), 160, false, 300, false, 50m);
                    break;
                case ActualizarPresupuestoIaCommand:
                    Comandos.Add(request);
                    respuesta = Result.Exito();
                    break;
                default:
                    throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
            }

            return Task.FromResult((TResponse)respuesta);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }
}

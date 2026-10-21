using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionAEmpresaEmpleadora;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Empresas.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Reclamar para acceso» (<c>/empresas/{EmpresaId}/reclamar-acceso</c>) contra su maqueta «Reclamacion por acceso TALVEG.dc.html».
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> qué consulta llega al mediador (Empresa, Centro y Trabajadores elegidos) y cuántas veces; que lo
/// que vuelve se pinta separado en documentos de la Empresa y de cada Trabajador, con a quién iría cada tarjeta; los estados
/// cargando, vacío, error, sin Centros y Empresa ausente; que «Reclamar a la Empresa» abre el cajón de siempre en su revisión sin
/// mandar ningún comando; y que Consulta ve la lista pero no el botón.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> qué exige de verdad cada Centro, el alcance de cartera ni el envío (los prueban las consultas y
/// comandos de Application e integración), ni el aspecto (bUnit no evalúa CSS), ni la lectura de <c>?centroId=</c> y
/// <c>?trabajadorId=</c> de la URL.
/// </para>
/// </summary>
public class ReclamarParaAccesoTests : BunitContext
{
    public ReclamarParaAccesoTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid EmpresaId = Guid.Parse("e1e1e1e1-0000-0000-0000-000000000001");
    private static readonly Guid Martorell = Guid.Parse("c1c1c1c1-0000-0000-0000-000000000001");
    private static readonly Guid Getafe = Guid.Parse("c2c2c2c2-0000-0000-0000-000000000002");
    private static readonly Guid Jordi = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid Marc = Guid.Parse("a2a2a2a2-0000-0000-0000-000000000002");
    private static readonly Guid Alvaro = Guid.Parse("a3a3a3a3-0000-0000-0000-000000000003");

    private sealed class Mediador(Func<object, Task<object?>> responder) : IMediator
    {
        public List<object> Recibidas { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Recibidas.Add(request);
            return (TResponse)(await responder(request))!;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Recibidas.Add(request!);
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Recibidas.Add(request);
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

    private sealed class Escenario
    {
        public bool EmpresaExiste { get; set; } = true;

        public IReadOnlyList<CentroConTrabajadoresDto> Centros { get; set; } =
        [
            new(Martorell, "Planta de Martorell", [new(Jordi, "Jordi Ferrer Vidal"), new(Marc, "Marc Soler Pujol")]),
            new(Getafe, "Almacén de Getafe", [new(Alvaro, "Álvaro Núñez Prieto")]),
        ];

        /// <summary>Lo que devuelve la reclamación por acceso para una petición; por defecto, los lotes de <see cref="LotesDeMuestra"/>.</summary>
        public Func<ObtenerReclamacionPorAccesoQuery, Task<IReadOnlyList<LoteReclamacionAgrupadoDto>>> PorAcceso { get; set; } =
            _ => Task.FromResult(LotesDeMuestra());

        public Task<object?> Responder(object peticion) => peticion switch
        {
            ObtenerEmpresaPorIdQuery q => Task.FromResult<object?>(EmpresaExiste
                ? new EmpresaDetalleDto(q.Id, "Obras y Estructuras del Ebro S.L.", "B50331406", DateTime.UtcNow, [], Guid.NewGuid())
                : null),
            ObtenerReclamacionPreventivaEmpresaQuery => Task.FromResult<object?>(new ReclamacionPreventivaEmpresaDto([], [], [], Centros)),
            ObtenerReclamacionPorAccesoQuery q => ComoObjeto(PorAcceso(q)),
            _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
        };

        private static async Task<object?> ComoObjeto(Task<IReadOnlyList<LoteReclamacionAgrupadoDto>> tarea) => await tarea;
    }

    private static readonly DateOnly Vence = new(2026, 11, 18);

    /// <summary>
    /// Lo de la Empresa viaja en el lote de la Delegación de Cataluña, junto con lo de Jordi; lo de Marc, sin Delegación, va a la agenda
    /// de la Empresa y no tiene ningún contacto.
    /// </summary>
    private static IReadOnlyList<LoteReclamacionAgrupadoDto> LotesDeMuestra()
    {
        var contacto = new DestinatarioAgendaDto(Guid.NewGuid(), "Montse Puig Casals", "montse.puig@ebro.example", ["Formación PRL"]);
        var delegacion = new LoteReclamacionAgrupadoDto(
            EmpresaId, "Obras y Estructuras del Ebro S.L.", AmbitoAplicacion.Trabajador, null,
            [
                new DocumentoReclamableDto(Guid.NewGuid(), null, null, Guid.NewGuid(), "Certificado TGSS", Vence, EstadoDocumento.Vencido),
                new DocumentoReclamableDto(Guid.NewGuid(), Jordi, "Jordi Ferrer Vidal", Guid.NewGuid(), "Reconocimiento médico", Vence, EstadoDocumento.Proximo),
            ],
            null, [contacto], null, new DestinoEmpresaEmpleadoraDto(Guid.NewGuid(), "Cataluña"));
        var agenda = new LoteReclamacionAgrupadoDto(
            EmpresaId, "Obras y Estructuras del Ebro S.L.", AmbitoAplicacion.Trabajador, null, [], null, [],
            [new DocumentoPendienteDto(null, Marc, "Marc Soler Pujol", Guid.NewGuid(), "Formación en altura", MotivoPendienteDeReclamacion.Ausente)],
            new DestinoEmpresaEmpleadoraDto(null, null));
        return [delegacion, agenda];
    }

    private sealed class AutorizacionPorRoles : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        {
            var cumple = requirements.All(r => r switch
            {
                RolesAuthorizationRequirement roles => roles.AllowedRoles.Any(user.IsInRole),
                DenyAnonymousAuthorizationRequirement => user.Identity?.IsAuthenticated == true,
                _ => true
            });

            return Task.FromResult(cumple ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(AuthorizationResult.Success());
    }

    private sealed class TenantActualFalso : ITenantActual
    {
        public Guid? TenantId => Guid.Parse("7e7e7e7e-0000-0000-0000-000000000007");
    }

    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-de-prueba", "__RequestVerificationToken");
    }

    private sealed class AutenticacionFalsa(string rol) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "test"))));
    }

    private (IRenderedComponent<ReclamarParaAcceso> Cut, Mediador Mediador) Renderizar(Escenario escenario, string rol = Roles.GestorCae)
    {
        var mediador = new Mediador(escenario.Responder);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddLocalization();
        Services.AddScoped<ToastService>();
        Services.AddScoped<AuthenticationStateProvider>(_ => new AutenticacionFalsa(rol));
        Services.AddAuthorizationCore();
        Services.AddScoped<IAuthorizationService, AutorizacionPorRoles>();
        Services.AddCascadingAuthenticationState();
        Services.AddScoped<ITenantActual, TenantActualFalso>();
        Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();

        var cut = Render<ReclamarParaAcceso>(p => p.Add(c => c.EmpresaId, EmpresaId));
        return (cut, mediador);
    }

    private static IEnumerable<ObtenerReclamacionPorAccesoQuery> PorAcceso(Mediador mediador) =>
        mediador.Recibidas.OfType<ObtenerReclamacionPorAccesoQuery>();

    private static IReadOnlyList<IElement> Casillas(IRenderedComponent<ReclamarParaAcceso> cut) =>
        [.. cut.FindAll(".reclamar-acceso-trabajadores-lista input[type=checkbox]")];

    private static string Texto(IElement elemento) => elemento.TextContent.Trim();

    [Fact]
    public void Con_los_Trabajadores_del_primer_Centro_marcados_pide_lo_que_exige_ese_Centro()
    {
        var (cut, mediador) = Renderizar(new Escenario());

        cut.WaitForAssertion(() => PorAcceso(mediador).Should().HaveCount(1));
        var consulta = PorAcceso(mediador).Single();
        consulta.EmpresaId.Should().Be(EmpresaId);
        consulta.CentroId.Should().Be(Martorell);
        consulta.TrabajadorIds.Should().BeEquivalentTo([Jordi, Marc]);
        Casillas(cut).Should().HaveCount(2).And.OnlyContain(c => c.HasAttribute("checked"));
    }

    [Fact]
    public void Separa_los_documentos_de_la_Empresa_y_los_de_cada_Trabajador_y_dice_a_quien_ira_cada_tarjeta()
    {
        var (cut, _) = Renderizar(new Escenario());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Documentos de Empresa"));
        var markup = cut.Markup;
        markup.Should().Contain("Certificado TGSS").And.Contain("Documentos de los Trabajadores");
        cut.FindAll(".reclamar-acceso-grupo-nombre").Select(Texto).Should().Equal("Jordi Ferrer Vidal", "Marc Soler Pujol");
        markup.Should().Contain("Reconocimiento médico").And.Contain("Formación en altura");
        markup.Should().Contain("Falta", "lo que nunca se subió sale como «Falta»");
        cut.FindAll(".reclamar-acceso-destino-titulo").Select(Texto).Should().Equal("Delegación Cataluña", "Contactos de la Empresa");
        markup.Should().Contain("Montse Puig Casals").And.Contain("montse.puig@ebro.example");
        cut.FindAll(".reclamar-acceso-destino .alerta-formulario").Should().ContainSingle("la tarjeta sin contacto avisa de que no hay a quién reclamar");
    }

    [Fact]
    public void Quitar_un_Trabajador_vuelve_a_pedir_solo_con_el_que_queda_y_quitar_todos_no_pide_nada()
    {
        var (cut, mediador) = Renderizar(new Escenario());
        cut.WaitForAssertion(() => PorAcceso(mediador).Should().HaveCount(1));

        Casillas(cut)[1].Change(false);
        cut.WaitForAssertion(() => PorAcceso(mediador).Should().HaveCount(2));
        PorAcceso(mediador).Last().TrabajadorIds.Should().Equal(Jordi);

        Casillas(cut)[0].Change(false);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Elige al menos un Trabajador"));
        PorAcceso(mediador).Should().HaveCount(2, "sin Trabajadores no hay acceso que calcular");
    }

    [Fact]
    public void Cambiar_de_Centro_ofrece_sus_Trabajadores_y_los_marca_todos()
    {
        var (cut, mediador) = Renderizar(new Escenario());
        cut.WaitForAssertion(() => PorAcceso(mediador).Should().HaveCount(1));

        cut.Find("select").Change(Getafe.ToString());

        cut.WaitForAssertion(() => PorAcceso(mediador).Should().HaveCount(2));
        var consulta = PorAcceso(mediador).Last();
        consulta.CentroId.Should().Be(Getafe);
        consulta.TrabajadorIds.Should().Equal(Alvaro);
        Casillas(cut).Should().ContainSingle();
    }

    [Fact]
    public void Sin_nada_que_reclamar_lo_dice_y_no_ofrece_reclamar()
    {
        var escenario = new Escenario { PorAcceso = _ => Task.FromResult<IReadOnlyList<LoteReclamacionAgrupadoDto>>([]) };
        var (cut, _) = Renderizar(escenario);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Nada que reclamar para este acceso"));
        cut.Markup.Should().Contain("Planta de Martorell");
        cut.FindAll("button").Select(Texto).Should().NotContain("Reclamar a la Empresa");
    }

    [Fact]
    public void Si_la_consulta_falla_dice_que_no_pudo_calcularlo_y_Reintentar_vuelve_a_pedirlo()
    {
        var intentos = 0;
        var escenario = new Escenario
        {
            PorAcceso = _ => ++intentos == 1
                ? throw new InvalidOperationException("fallo de prueba")
                : Task.FromResult(LotesDeMuestra())
        };
        var (cut, mediador) = Renderizar(escenario);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No pudimos calcular lo que exige este Centro"));
        cut.Markup.Should().NotContain("fallo de prueba", "el aviso nunca enseña el mensaje de la excepción");

        cut.FindAll("button").Single(b => Texto(b) == "Reintentar").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));
        PorAcceso(mediador).Should().HaveCount(2);
    }

    [Fact]
    public void Una_Empresa_sin_Trabajadores_en_Centros_con_gestion_CAE_lo_dice_y_no_pide_nada()
    {
        var (cut, mediador) = Renderizar(new Escenario { Centros = [] });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Sin Centros con gestión CAE"));
        PorAcceso(mediador).Should().BeEmpty();
    }

    [Fact]
    public void Una_Empresa_ausente_o_de_otro_Tenant_pinta_la_ficha_no_encontrada_y_no_pide_nada_mas()
    {
        var (cut, mediador) = Renderizar(new Escenario { EmpresaExiste = false });

        cut.WaitForAssertion(() => cut.FindComponents<FichaNoEncontrada>().Should().ContainSingle());
        cut.Markup.Should().NotContain("Reclamar para acceso", "no hay cabecera de una Empresa que no se ve");
        mediador.Recibidas.OfType<ObtenerReclamacionPreventivaEmpresaQuery>().Should().BeEmpty();
        PorAcceso(mediador).Should().BeEmpty();
    }

    [Fact]
    public void Reclamar_abre_el_cajon_de_siempre_en_su_revision_con_lo_que_se_ve_y_no_envia_nada()
    {
        var (cut, mediador) = Renderizar(new Escenario());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));

        cut.FindAll("button").Single(b => Texto(b) == "Reclamar a la Empresa").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Reclamar en lote"));
        cut.Markup.Should().Contain("Enviar (", "el cajón abre ya en la revisión de los lotes, sin pedir otro filtro");
        cut.Markup.Should().NotContain("Cambiar filtro", "abierto con lotes concretos no hay otro filtro que elegir: ese botón llevaría a lotes distintos de los que se ven");
        cut.Markup.Should().Contain("Obras y Estructuras del Ebro S.L. (Cataluña)");
        mediador.Recibidas.Select(r => r.GetType().Name)
            .Should().NotContain(n => n.Contains("Enviar"), "abrir el cajón no manda ningún correo: lo manda el clic del Gestor CAE en él");
    }

    [Fact]
    public void Consulta_ve_lo_que_falta_y_a_quien_se_reclamaria_pero_no_el_boton_de_reclamar()
    {
        var (cut, _) = Renderizar(new Escenario(), Roles.Consulta);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));
        cut.Markup.Should().Contain("Montse Puig Casals");
        cut.FindAll("button").Select(Texto).Should().NotContain("Reclamar a la Empresa");
    }

    [Fact]
    public void Con_un_solo_documento_sin_destinatario_el_boton_queda_deshabilitado_y_dice_por_que()
    {
        var sinContacto = new LoteReclamacionAgrupadoDto(
            EmpresaId, "Obras y Estructuras del Ebro S.L.", AmbitoAplicacion.Trabajador, null,
            [new DocumentoReclamableDto(Guid.NewGuid(), Jordi, "Jordi Ferrer Vidal", Guid.NewGuid(), "Reconocimiento médico", Vence, EstadoDocumento.Vencido)],
            null, [], null, new DestinoEmpresaEmpleadoraDto(null, null));
        var (cut, _) = Renderizar(new Escenario { PorAcceso = _ => Task.FromResult<IReadOnlyList<LoteReclamacionAgrupadoDto>>([sinContacto]) });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Reconocimiento médico"));
        cut.FindAll("button").Single(b => Texto(b) == "Reclamar a la Empresa").HasAttribute("disabled").Should().BeTrue();
        cut.Markup.Should().Contain("Ningún documento tiene destinatario.");
    }

    [Fact]
    public async Task Mientras_se_recalcula_no_se_puede_reclamar_porque_lo_que_se_ve_ya_no_es_lo_pedido()
    {
        var puerta = new TaskCompletionSource<IReadOnlyList<LoteReclamacionAgrupadoDto>>();
        var llamadas = 0;
        var (cut, mediador) = Renderizar(new Escenario { PorAcceso = _ => ++llamadas == 1 ? Task.FromResult(LotesDeMuestra()) : puerta.Task });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));
        cut.FindAll("button").Single(b => Texto(b) == "Reclamar a la Empresa").HasAttribute("disabled").Should().BeFalse();

        Casillas(cut)[1].Change(false);

        cut.WaitForAssertion(() => PorAcceso(mediador).Should().HaveCount(2));
        var boton = cut.FindAll("button").Single(b => Texto(b) == "Reclamar a la Empresa");
        boton.HasAttribute("disabled").Should().BeTrue("con una consulta en vuelo el cajón se abriría con lotes de la selección anterior");
        await boton.ClickAsync(new MouseEventArgs());
        cut.Markup.Should().NotContain("Reclamar en lote");

        puerta.SetResult(LotesDeMuestra());
        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => Texto(b) == "Reclamar a la Empresa").HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public void Al_cambiar_de_Centro_lo_del_Centro_anterior_no_se_queda_bajo_el_nombre_del_nuevo()
    {
        var puerta = new TaskCompletionSource<IReadOnlyList<LoteReclamacionAgrupadoDto>>();
        var llamadas = 0;
        var (cut, _) = Renderizar(new Escenario { PorAcceso = _ => ++llamadas == 1 ? Task.FromResult(LotesDeMuestra()) : puerta.Task });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));

        cut.Find("select").Change(Getafe.ToString());

        cut.WaitForAssertion(() => cut.FindAll("[aria-busy=true]").Should().NotBeEmpty());
        cut.Markup.Should().NotContain("Certificado TGSS").And.NotContain("Montse Puig Casals");
        cut.FindAll("button").Select(Texto).Should().NotContain("Reclamar a la Empresa");
    }
}
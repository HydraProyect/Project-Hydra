using System.Security.Claims;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionAEmpresaEmpleadora;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Empresas.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pestaña «Reclamar» de Empresa 360 (<c>?pestana=reclamar</c>) contra su maqueta «Reclamacion preventiva TALVEG.dc.html».
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> qué consulta llega al mediador y con qué filtros de Delegación y Oficio; que lo devuelto se pinta
/// agrupado (por Delegación, por Trabajador y por Tipo de documento) con a quién iría cada tarjeta; el filtro de estado en cliente; los
/// estados cargando, al día, vacío con filtros y error; que lo pendiente de subir sale aparte y de solo lectura; que «Reclamar» abre el
/// cajón de siempre en su revisión sin mandar ningún comando; y que Consulta ve la lista pero no los botones de reclamar.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> qué es reclamable de verdad, el alcance de cartera ni el envío (los prueban las consultas y comandos de
/// Application e integración), ni el aspecto (bUnit no evalúa CSS), ni la lectura de <c>?pestana=reclamar</c> de la URL (la cubre
/// <c>Empresa360PaginaTests</c>).
/// </para>
/// </summary>
public class PestanaReclamarEmpresaTests : BunitContext
{
    public PestanaReclamarEmpresaTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid EmpresaId = Guid.Parse("e1e1e1e1-0000-0000-0000-000000000001");
    private static readonly Guid Cataluna = Guid.Parse("d1d1d1d1-0000-0000-0000-000000000001");
    private static readonly Guid Aragon = Guid.Parse("d2d2d2d2-0000-0000-0000-000000000002");
    private static readonly Guid Tecnico = Guid.Parse("f1f1f1f1-0000-0000-0000-000000000001");
    private static readonly Guid Jordi = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid Marc = Guid.Parse("a2a2a2a2-0000-0000-0000-000000000002");
    private static readonly DateOnly Vence = new(2026, 11, 18);

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
        public IReadOnlyList<OpcionFiltroDto> Delegaciones { get; set; } = [new(Cataluna, "Cataluña"), new(Aragon, "Aragón")];

        public IReadOnlyList<OpcionFiltroDto> Oficios { get; set; } = [new(Tecnico, "Técnico")];

        public Func<ObtenerReclamacionPreventivaEmpresaQuery, Task<ReclamacionPreventivaEmpresaDto>> Preventiva { get; set; }

        public Func<IReadOnlyList<DocumentacionBloqueantePendienteDto>> PendientesDeSubir { get; set; } = () => [];

        public Escenario() => Preventiva = _ => Task.FromResult(Dto(LotesDeMuestra()));

        public ReclamacionPreventivaEmpresaDto Dto(IReadOnlyList<LoteReclamacionAgrupadoDto> lotes) => new(lotes, Delegaciones, Oficios, []);

        public Task<object?> Responder(object peticion) => peticion switch
        {
            ObtenerReclamacionPreventivaEmpresaQuery q => Comoobjeto(Preventiva(q)),
            ObtenerPendientesDeSubirAPlataformaEmpresaQuery => Task.FromResult<object?>(PendientesDeSubir()),
            _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
        };

        private static async Task<object?> Comoobjeto(Task<ReclamacionPreventivaEmpresaDto> tarea) => await tarea;
    }

    /// <summary>
    /// La Empresa (un documento vencido) tiene su propio lote; la Delegación de Cataluña lleva un vencido de Jordi y un por vencer de Marc;
    /// lo pendiente de otro Trabajador va a la agenda de la Empresa, que no tiene ningún contacto.
    /// </summary>
    private static IReadOnlyList<LoteReclamacionAgrupadoDto> LotesDeMuestra()
    {
        var contacto = new DestinatarioAgendaDto(Guid.NewGuid(), "Montse Puig Casals", "montse.puig@ebro.example", ["Prevención"]);
        var empresa = new LoteReclamacionAgrupadoDto(
            EmpresaId, "Obras y Estructuras del Ebro S.L.", AmbitoAplicacion.Empresa, null,
            [new DocumentoReclamableDto(Guid.NewGuid(), null, null, Guid.NewGuid(), "Certificado TGSS", Vence, EstadoDocumento.Vencido)],
            null, [new DestinatarioAgendaDto(Guid.NewGuid(), "Jon Arregi Lasa", "jon.arregi@ebro.example", [])]);
        var delegacion = new LoteReclamacionAgrupadoDto(
            EmpresaId, "Obras y Estructuras del Ebro S.L.", AmbitoAplicacion.Trabajador, DateTime.UtcNow.AddDays(-3),
            [
                new DocumentoReclamableDto(Guid.NewGuid(), Jordi, "Jordi Ferrer Vidal", Guid.NewGuid(), "Reconocimiento médico", Vence, EstadoDocumento.Vencido),
                new DocumentoReclamableDto(Guid.NewGuid(), Marc, "Marc Soler Pujol", Guid.NewGuid(), "Formación PRL", Vence, EstadoDocumento.Proximo),
            ],
            null, [contacto], null, new DestinoEmpresaEmpleadoraDto(Cataluna, "Cataluña"));
        var agenda = new LoteReclamacionAgrupadoDto(
            EmpresaId, "Obras y Estructuras del Ebro S.L.", AmbitoAplicacion.Trabajador, null, [], null, [],
            [new DocumentoPendienteDto(null, Guid.NewGuid(), "Laia Roca Serra", Guid.NewGuid(), "Información de riesgos", MotivoPendienteDeReclamacion.Ausente)],
            new DestinoEmpresaEmpleadoraDto(null, null));
        return [empresa, delegacion, agenda];
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

    private (IRenderedComponent<PestanaReclamarEmpresa> Cut, Mediador Mediador) Renderizar(Escenario escenario, string rol = Roles.GestorCae)
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

        var cut = Render<PestanaReclamarEmpresa>(p => p.Add(c => c.EmpresaId, EmpresaId));
        return (cut, mediador);
    }

    private static IEnumerable<ObtenerReclamacionPreventivaEmpresaQuery> Preventivas(Mediador mediador) =>
        mediador.Recibidas.OfType<ObtenerReclamacionPreventivaEmpresaQuery>();

    private static string Texto(IElement elemento) => System.Text.RegularExpressions.Regex.Replace(elemento.TextContent, @"\s+", " ").Trim();

    private static IElement Boton(IRenderedComponent<PestanaReclamarEmpresa> cut, string texto) =>
        cut.FindAll("button").Single(b => Texto(b).StartsWith(texto, StringComparison.Ordinal));

    [Fact]
    public void Pide_la_reclamacion_preventiva_de_la_Empresa_sin_filtros_y_la_pinta_agrupada_por_Delegacion()
    {
        var (cut, mediador) = Renderizar(new Escenario());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));
        var consulta = Preventivas(mediador).Single();
        consulta.EmpresaId.Should().Be(EmpresaId);
        consulta.DelegacionEmpresaId.Should().BeNull();
        consulta.OficioEmpresaId.Should().BeNull();
        cut.FindAll(".reclamar-empresa-grupo-titulo h3").Select(Texto)
            .Should().Equal("Documentos de Empresa", "Delegación Cataluña", "Contactos de la Empresa");
        cut.Markup.Should().Contain("Reconocimiento médico").And.Contain("Formación PRL").And.Contain("Información de riesgos");
        cut.Markup.Should().Contain("Se reclama a Montse Puig Casals").And.Contain("Reclamado hace 3 días").And.Contain("Sin reclamar");
        cut.FindAll(".reclamar-empresa-sin-contacto").Should().ContainSingle("la agenda sin contacto avisa de que no hay a quién reclamar");
    }

    [Fact]
    public void Cuenta_lo_de_la_Empresa_y_los_Trabajadores_afectados_y_ofrece_los_recuentos_por_estado()
    {
        var (cut, _) = Renderizar(new Escenario());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));
        var fichas = cut.FindAll(".reclamar-empresa-ficha").Select(Texto).ToList();
        fichas[0].Should().Contain("1").And.Contain("Documentos de Empresa vencidos o pendientes");
        fichas[1].Should().Contain("3").And.Contain("Trabajadores afectados");
        cut.FindAll(".reclamar-empresa-ficha-filtro").Select(Texto)
            .Should().Equal("1 Vencidos · Trabajadores", "1 Por vencer · Trabajadores", "1 Pendientes · Trabajadores");
    }

    [Fact]
    public void Elegir_Delegacion_vuelve_a_pedir_con_ese_filtro_y_Oficio_tambien()
    {
        var (cut, mediador) = Renderizar(new Escenario());
        cut.WaitForAssertion(() => Preventivas(mediador).Should().HaveCount(1));

        cut.Find("select[aria-label='Delegación']").Change(Cataluna.ToString());
        cut.WaitForAssertion(() => Preventivas(mediador).Should().HaveCount(2));
        Preventivas(mediador).Last().DelegacionEmpresaId.Should().Be(Cataluna);
        cut.WaitForAssertion(() => cut.Find("select[aria-label='Oficio']").HasAttribute("disabled").Should().BeFalse());

        cut.Find("select[aria-label='Oficio']").Change(Tecnico.ToString());
        cut.WaitForAssertion(() => Preventivas(mediador).Should().HaveCount(3));
        Preventivas(mediador).Last().Should().Be(new ObtenerReclamacionPreventivaEmpresaQuery(EmpresaId, Cataluna, Tecnico));
        cut.WaitForAssertion(() => Boton(cut, "Quitar filtros").HasAttribute("disabled").Should().BeFalse());

        Boton(cut, "Quitar filtros").Click();
        cut.WaitForAssertion(() => Preventivas(mediador).Should().HaveCount(4));
        Preventivas(mediador).Last().Should().Be(new ObtenerReclamacionPreventivaEmpresaQuery(EmpresaId));
    }

    [Fact]
    public void El_filtro_de_estado_se_aplica_en_cliente_sin_volver_a_pedir()
    {
        var (cut, mediador) = Renderizar(new Escenario());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));

        cut.Find("select[aria-label='Estado']").Change("PorVencer");

        cut.Markup.Should().Contain("Formación PRL").And.NotContain("Reconocimiento médico").And.NotContain("Certificado TGSS");
        Preventivas(mediador).Should().HaveCount(1, "el estado no es un filtro de la consulta");
        cut.FindAll(".reclamar-empresa-ficha-filtro[aria-pressed=true]").Should().ContainSingle().Which.GetAttribute("data-categoria").Should().Be("PorVencer");
    }

    [Fact]
    public void Una_ficha_de_estado_alterna_el_filtro()
    {
        var (cut, _) = Renderizar(new Escenario());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));

        cut.Find(".reclamar-empresa-ficha-filtro[data-categoria=Vencido]").Click();
        cut.Markup.Should().Contain("Reconocimiento médico").And.NotContain("Formación PRL");

        cut.Find(".reclamar-empresa-ficha-filtro[data-categoria=Vencido]").Click();
        cut.Markup.Should().Contain("Formación PRL");
    }

    [Fact]
    public void Agrupar_por_Trabajador_pone_primero_lo_de_la_Empresa_y_despues_cada_Trabajador()
    {
        var (cut, _) = Renderizar(new Escenario());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));

        Boton(cut, "Trabajador").Click();

        cut.FindAll(".reclamar-empresa-grupo-titulo h3").Select(Texto)
            .Should().Equal("Documentos de Empresa", "Jordi Ferrer Vidal", "Laia Roca Serra", "Marc Soler Pujol");
        cut.FindAll(".reclamar-empresa-grupo-destino").Should().BeEmpty("el destino es de la tarjeta por Delegación, no del grupo por Trabajador");
    }

    [Fact]
    public void Agrupar_por_Tipo_de_documento_nombra_el_titular_en_cada_fila()
    {
        var (cut, _) = Renderizar(new Escenario());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));

        Boton(cut, "Tipo de documento").Click();

        cut.FindAll(".reclamar-empresa-grupo-titulo h3").Select(Texto)
            .Should().BeEquivalentTo("Certificado TGSS", "Reconocimiento médico", "Formación PRL", "Información de riesgos");
        cut.Markup.Should().Contain("Jordi Ferrer Vidal · Vencimiento");
    }

    [Fact]
    public void Una_Empresa_sin_Delegaciones_agrupa_por_Trabajador_lo_avisa_y_no_ofrece_el_filtro()
    {
        var (cut, _) = Renderizar(new Escenario { Delegaciones = [] });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));
        cut.Markup.Should().Contain("Esta Empresa no tiene delegaciones");
        cut.FindAll("select[aria-label='Delegación']").Should().BeEmpty();
        cut.FindAll(".reclamar-empresa-segmento button").Single(b => Texto(b) == "Delegación").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll(".reclamar-empresa-grupo-titulo h3").Select(Texto).Should().Contain("Jordi Ferrer Vidal");
    }

    [Fact]
    public void Sin_nada_que_reclamar_lo_dice_sin_afirmar_que_la_Empresa_este_al_dia_y_no_ofrece_reclamar()
    {
        var (cut, _) = Renderizar(new Escenario { Preventiva = _ => Task.FromResult(new ReclamacionPreventivaEmpresaDto([], [], [], [])) });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Nada que reclamar a esta Empresa"));
        cut.Markup.Should().NotContain("al día", "la consulta devuelve lo mismo para una Empresa fuera de la cartera: no se afirma que cumpla");
        cut.FindAll("button").Select(Texto).Should().NotContain(t => t.StartsWith("Reclamar", StringComparison.Ordinal));
    }

    [Fact]
    public void Mientras_carga_pinta_el_estado_de_carga_y_no_el_al_dia()
    {
        var puerta = new TaskCompletionSource<ReclamacionPreventivaEmpresaDto>();
        var (cut, _) = Renderizar(new Escenario { Preventiva = _ => puerta.Task });

        cut.FindComponents<EstadoCargando>().Should().ContainSingle();
        cut.Markup.Should().NotContain("Nada que reclamar");

        puerta.SetResult(new ReclamacionPreventivaEmpresaDto([], [], [], []));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Nada que reclamar a esta Empresa"));
    }

    [Fact]
    public void Si_la_consulta_falla_lo_dice_sin_enseñar_la_excepcion_y_Reintentar_vuelve_a_pedirlo()
    {
        var intentos = 0;
        var (cut, mediador) = Renderizar(new Escenario
        {
            Preventiva = _ => ++intentos == 1 ? throw new InvalidOperationException("fallo de prueba") : Task.FromResult(new Escenario().Dto(LotesDeMuestra()))
        });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No pudimos cargar lo pendiente de esta Empresa"));
        cut.Markup.Should().NotContain("fallo de prueba");

        Boton(cut, "Reintentar").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));
        Preventivas(mediador).Should().HaveCount(2);
    }

    [Fact]
    public void Con_filtros_que_no_dejan_nada_dice_nada_con_estos_filtros_y_Quitar_filtros_los_limpia()
    {
        var (cut, mediador) = Renderizar(new Escenario
        {
            Preventiva = q => Task.FromResult(new ReclamacionPreventivaEmpresaDto(
                q.DelegacionEmpresaId is null ? LotesDeMuestra() : [], [new(Cataluna, "Cataluña")], [], []))
        });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));

        cut.Find("select[aria-label='Delegación']").Change(Cataluna.ToString());

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Nada con estos filtros"));
        cut.Markup.Should().NotContain("Nada que reclamar a esta Empresa", "con un filtro puesto, el estado vacío es el de los filtros");
        cut.FindAll("button").First(b => Texto(b) == "Quitar filtros").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));
        Preventivas(mediador).Last().DelegacionEmpresaId.Should().BeNull();
    }

    [Fact]
    public void Lo_pendiente_de_subir_sale_aparte_de_solo_lectura_y_sin_boton_de_reclamar()
    {
        var pendiente = new DocumentacionBloqueantePendienteDto(
            Guid.NewGuid(), "Planta de Martorell", Jordi, "Jordi Ferrer Vidal", Guid.NewGuid(), "Entrega de EPI",
            Situacion: SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma);
        var (cut, mediador) = Renderizar(new Escenario { PendientesDeSubir = () => [pendiente] });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Pendiente de subir a plataformas (1)"));
        cut.Markup.Should().Contain("Entrega de EPI").And.Contain("Jordi Ferrer Vidal · Planta de Martorell")
            .And.Contain("los sube el Gestor CAE");
        cut.Find(".reclamar-empresa-enlace").GetAttribute("href").Should().Be($"/empresas/{EmpresaId}?pestana=plataformas");
        mediador.Recibidas.OfType<ObtenerPendientesDeSubirAPlataformaEmpresaQuery>().Single().EmpresaId.Should().Be(EmpresaId);
    }

    [Fact]
    public void Si_falla_lo_pendiente_de_subir_la_reclamacion_sigue_y_se_avisa()
    {
        var (cut, _) = Renderizar(new Escenario { PendientesDeSubir = () => throw new InvalidOperationException("fallo de prueba") });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));
        cut.Markup.Should().Contain("No se pudo cargar lo pendiente de subir a plataformas.").And.NotContain("fallo de prueba");
    }

    [Fact]
    public void Reclamar_lo_visible_abre_el_cajon_de_siempre_en_su_revision_y_no_envia_nada()
    {
        var (cut, mediador) = Renderizar(new Escenario());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));

        Boton(cut, "Reclamar lo visible (4)").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Reclamar en lote"));
        cut.Markup.Should().Contain("Enviar (", "el cajón abre ya en la revisión de los lotes, sin pedir otro filtro");
        cut.Markup.Should().NotContain("Cambiar filtro");
        mediador.Recibidas.Select(r => r.GetType().Name)
            .Should().NotContain(n => n.Contains("Enviar"), "abrir el cajón no manda ningún correo: lo manda el clic del Gestor CAE en él");
    }

    [Fact]
    public void Reclamar_un_grupo_abre_el_cajon_solo_con_ese_lote_y_con_el_filtro_de_estado_aplicado()
    {
        var (cut, _) = Renderizar(new Escenario());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));
        cut.Find("select[aria-label='Estado']").Change("Vencido");

        var cabecera = cut.FindAll(".reclamar-empresa-grupo").Single(g => Texto(g.QuerySelector("h3")!) == "Delegación Cataluña");
        cabecera.QuerySelectorAll("button").Single(b => Texto(b) == "Reclamar").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Reclamar en lote"));
        var cajon = cut.Markup[cut.Markup.IndexOf("Reclamar en lote", StringComparison.Ordinal)..];
        cajon.Should().Contain("Reconocimiento médico").And.NotContain("Formación PRL", "el estado Vencido deja fuera lo por vencer")
            .And.NotContain("Certificado TGSS", "solo viaja el lote de la Delegación elegida");
    }

    [Fact]
    public void Un_lote_sin_destinatario_deja_su_boton_deshabilitado()
    {
        var (cut, _) = Renderizar(new Escenario());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));

        var agenda = cut.FindAll(".reclamar-empresa-grupo").Single(g => Texto(g.QuerySelector("h3")!) == "Contactos de la Empresa");
        agenda.QuerySelectorAll("button").Single(b => Texto(b) == "Reclamar").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Mientras_se_vuelve_a_pedir_no_se_puede_reclamar_porque_lo_que_se_ve_ya_no_es_lo_pedido()
    {
        var puerta = new TaskCompletionSource<ReclamacionPreventivaEmpresaDto>();
        var llamadas = 0;
        var escenario = new Escenario();
        escenario.Preventiva = _ => ++llamadas == 1 ? Task.FromResult(escenario.Dto(LotesDeMuestra())) : puerta.Task;
        var (cut, mediador) = Renderizar(escenario);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));
        Boton(cut, "Reclamar lo visible").HasAttribute("disabled").Should().BeFalse();

        cut.Find("select[aria-label='Delegación']").Change(Aragon.ToString());

        cut.WaitForAssertion(() => Preventivas(mediador).Should().HaveCount(2));
        Boton(cut, "Reclamar lo visible").HasAttribute("disabled").Should().BeTrue("el cajón copia los lotes al abrirse");
        await Boton(cut, "Reclamar lo visible").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.Markup.Should().NotContain("Reclamar en lote");

        puerta.SetResult(escenario.Dto(LotesDeMuestra()));
        cut.WaitForAssertion(() => Boton(cut, "Reclamar lo visible").HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public void Dos_Trabajadores_homonimos_son_dos_personas_en_el_recuento_y_en_los_grupos()
    {
        var otroJordi = Guid.Parse("a9a9a9a9-0000-0000-0000-000000000009");
        var lote = new LoteReclamacionAgrupadoDto(
            EmpresaId, "Obras y Estructuras del Ebro S.L.", AmbitoAplicacion.Trabajador, null,
            [
                new DocumentoReclamableDto(Guid.NewGuid(), Jordi, "Jordi Ferrer Vidal", Guid.NewGuid(), "Reconocimiento médico", Vence, EstadoDocumento.Vencido),
                new DocumentoReclamableDto(Guid.NewGuid(), otroJordi, "Jordi Ferrer Vidal", Guid.NewGuid(), "Formación en altura", Vence, EstadoDocumento.Vencido),
            ],
            null, [new DestinatarioAgendaDto(Guid.NewGuid(), "Montse Puig Casals", "montse.puig@ebro.example", [])], null,
            new DestinoEmpresaEmpleadoraDto(Cataluna, "Cataluña"));
        var escenario = new Escenario();
        escenario.Preventiva = _ => Task.FromResult(escenario.Dto([lote]));
        var (cut, _) = Renderizar(escenario);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Reconocimiento médico"));

        cut.FindAll(".reclamar-empresa-ficha")[1].TextContent.Should().Contain("2");
        Boton(cut, "Trabajador").Click();

        cut.FindAll(".reclamar-empresa-grupo-titulo h3").Select(Texto).Should().Equal("Jordi Ferrer Vidal", "Jordi Ferrer Vidal");
        cut.Markup.Should().Contain("2 Trabajadores");
    }

    [Fact]
    public void Mientras_se_vuelve_a_pedir_los_filtros_que_piden_al_servidor_quedan_deshabilitados_y_despues_se_liberan()
    {
        var puerta = new TaskCompletionSource<ReclamacionPreventivaEmpresaDto>();
        var llamadas = 0;
        var escenario = new Escenario();
        escenario.Preventiva = _ => ++llamadas == 1 ? Task.FromResult(escenario.Dto(LotesDeMuestra())) : puerta.Task;
        var (cut, mediador) = Renderizar(escenario);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));

        cut.Find("select[aria-label='Delegación']").Change(Cataluna.ToString());

        cut.WaitForAssertion(() => Preventivas(mediador).Should().HaveCount(2));
        cut.Find("select[aria-label='Oficio']").HasAttribute("disabled").Should().BeTrue("mientras carga no se encadenan consultas sobre el mismo contexto de datos");
        cut.Find("select[aria-label='Delegación']").HasAttribute("disabled").Should().BeTrue();
        cut.Find("select[aria-label='Estado']").HasAttribute("disabled").Should().BeFalse("el estado se filtra en cliente");

        puerta.SetResult(escenario.Dto(LotesDeMuestra()));
        cut.WaitForAssertion(() => cut.Find("select[aria-label='Oficio']").HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public void Mientras_se_vuelve_a_pedir_no_dice_que_ningun_documento_tiene_destinatario()
    {
        var puerta = new TaskCompletionSource<ReclamacionPreventivaEmpresaDto>();
        var llamadas = 0;
        var escenario = new Escenario();
        escenario.Preventiva = _ => ++llamadas == 1 ? Task.FromResult(escenario.Dto(LotesDeMuestra())) : puerta.Task;
        var (cut, mediador) = Renderizar(escenario);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));

        cut.Find("select[aria-label='Delegación']").Change(Aragon.ToString());

        cut.WaitForAssertion(() => Preventivas(mediador).Should().HaveCount(2));
        cut.Markup.Should().NotContain("Ningún documento visible tiene destinatario");
    }

    [Fact]
    public void Consulta_ve_lo_que_falta_y_a_quien_se_reclamaria_pero_ningun_boton_de_reclamar()
    {
        var (cut, _) = Renderizar(new Escenario(), Roles.Consulta);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Certificado TGSS"));
        cut.Markup.Should().Contain("Montse Puig Casals");
        cut.FindAll("button").Select(Texto).Should().NotContain(t => t.StartsWith("Reclamar", StringComparison.Ordinal));
    }
}

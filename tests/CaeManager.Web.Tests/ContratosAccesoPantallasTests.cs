using System.Security.Claims;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Inbound.ContratosAcceso.Commands.CerrarContratoAcceso;
using CaeManager.Application.Inbound.ContratosAcceso.Commands.EliminarContratoAcceso;
using CaeManager.Application.Inbound.ContratosAcceso.Queries;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerContratoAccesoPorId;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerContratosAcceso;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Inbound;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Pages;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Servicios;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pantallas de contratos de acceso de Inbound (listado, detalle y alta): rótulos, acciones, estados vacíos y lo que queda
/// como «Próximamente» sin backend. Los datos de aquí son de prueba: en runtime la pantalla solo enseña lo que devuelven las
/// Queries (o «—» donde aún no hay backend), nunca cifras inventadas.
/// </summary>
public class ContratosAccesoPantallasTests : BunitContext
{
    private static readonly Guid ContratoId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid CentroId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid EmpresaId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid ClienteId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid VersionContrato = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");

    public ContratosAccesoPantallasTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private static ContratoAccesoDto Contrato(
        EstadoContratoAcceso estado = EstadoContratoAcceso.Activo, DateOnly? fin = null, Guid? id = null, DateOnly? inicio = null) =>
        new(id ?? ContratoId, ClienteId, CentroId, EmpresaId, inicio ?? new DateOnly(2020, 1, 1), fin, null, true, true, null,
            VersionContrato, estado, TipoContratoAcceso.Recurrente, PropiaActividadContrato.Si, 1, 10, null, "Cupo acordado");

    private sealed class MediadorDePrueba : IMediator
    {
        public IReadOnlyList<ContratoAccesoDto> Contratos { get; init; } = [];

        public bool FallaLaLista { get; init; }

        public List<object> Enviadas { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Enviadas.Add(request);
            return (TResponse)(request switch
            {
                ObtenerContratosAccesoQuery when FallaLaLista => throw new InvalidOperationException("sin lista"),
                ObtenerContratosAccesoQuery => (object)Contratos,
                ObtenerContratoAccesoPorIdQuery q => Contratos.FirstOrDefault(c => c.Id == q.Id)!,
                ObtenerCentrosParaSelectorQuery => new[] { new CentroSelectorDto(CentroId, "Planta Zaragoza Norte", "Logística Delta Sur S.A.", "Logística Delta Sur S.A.", ClienteId) },
                ObtenerEmpresaPorIdQuery q => new EmpresaDetalleDto(q.Id, "Montajes Moncayo S.L.", "B12345674", DateTime.UtcNow, [], Guid.NewGuid()),
                ObtenerPerfilVocabularioActualQuery => PerfilVocabularioTenant.Consultora,
                CerrarContratoAccesoCommand => Result.Exito(),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}."),
            });
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class SelectorDePrueba(Guid empresaId) : ISelectorEmpresasContratistas
    {
        public Task<IReadOnlyList<EmpresaContratistaOpcion>> ListarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EmpresaContratistaOpcion>>([new EmpresaContratistaOpcion(empresaId, "Montajes Moncayo S.L.")]);
    }

    private MediadorDePrueba Preparar(params ContratoAccesoDto[] contratos) => Preparar(falla: false, contratos);

    private MediadorDePrueba Preparar(bool falla, params ContratoAccesoDto[] contratos)
    {
        var mediador = new MediadorDePrueba { Contratos = contratos, FallaLaLista = falla };
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba());
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<ResolutorContratosAcceso>();
        Services.AddSingleton<AntiforgeryStateProvider>(new AntiforgeryFalso());
        // La implementación vacía que se registra en Program: sin backend de actividad, la pantalla enseña «—».
        Services.AddScoped<IActividadDeContratoAcceso, ActividadDeContratoAccesoNoDisponible>();
        // Hay un selector de Empresas contratistas con una Empresa, para recorrer el alta; el de Program es el vacío.
        Services.AddScoped<ISelectorEmpresasContratistas>(_ => new SelectorDePrueba(EmpresaId));
        return mediador;
    }

    // Las pantallas cargan en el ciclo de vida (sin prerender): se espera a que desaparezca el esqueleto.
    private IRenderedComponent<T> Cargada<T>(IRenderedComponent<T> cut) where T : IComponent
    {
        cut.WaitForState(() => !cut.Markup.Contains("esqueleto-lista"), TimeSpan.FromSeconds(5));
        return cut;
    }

    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => null;
    }

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    // ------------------------------------------------------------ Listado (tarjeta 13)

    [Fact]
    public void El_listado_enseña_la_Empresa_contratista_el_Centro_el_estado_y_las_acciones()
    {
        Preparar(Contrato(fin: new DateOnly(2099, 12, 31)));

        var cut = Cargada(Render<ContratosAccesoLista>());

        cut.Markup.Should().Contain("Contratos de acceso");
        cut.Markup.Should().Contain("Montajes Moncayo S.L.").And.Contain("B12345674");
        cut.Markup.Should().Contain("Planta Zaragoza Norte");
        cut.Markup.Should().Contain("Activo");
        cut.Markup.Should().Contain("Dar de alta").And.Contain("Renovar").And.Contain("Editar");
        cut.Markup.Should().Contain("Pendiente de confirmar", "la franja de estados ofrece los estados de la maqueta");
        cut.Find("a.enlace-nombre-fila").GetAttribute("href").Should().Be($"/inbound/contratos/{ContratoId}");
    }

    [Fact]
    public void Sin_backend_de_actividad_el_listado_enseña_un_guion_y_no_una_cifra_inventada()
    {
        Preparar(Contrato(fin: new DateOnly(2099, 12, 31)));

        var cut = Cargada(Render<ContratosAccesoLista>());

        cut.FindAll("[title*='Aún no disponible']").Should().NotBeEmpty("Trabajadores y Pendiente dependen de B-17 y B-11");
        cut.Markup.Should().NotContain("Al día", "sin dato no se afirma que no haya nada pendiente");
    }

    [Fact]
    public void Sin_contratos_el_listado_invita_a_dar_de_alta_el_primero()
    {
        Preparar();

        var cut = Cargada(Render<ContratosAccesoLista>());

        cut.Markup.Should().Contain("Aún no hay contratos de acceso");
        cut.Markup.Should().NotContain("coincide con los filtros");
    }

    [Fact]
    public void Con_un_filtro_sin_resultados_no_invita_a_crear_el_primero()
    {
        Preparar(Contrato(fin: new DateOnly(2099, 12, 31)));
        var cut = Cargada(Render<ContratosAccesoLista>());

        cut.FindAll(".franja-estado-boton").First(b => b.TextContent.Contains("Rechazado")).Click();

        cut.Markup.Should().Contain("Ningún contrato de acceso coincide con los filtros.");
        cut.Markup.Should().NotContain("Aún no hay contratos de acceso");
    }

    [Fact]
    public void Si_la_lectura_falla_el_listado_lo_dice_y_ofrece_reintentar()
    {
        Preparar(falla: true);

        var cut = Cargada(Render<ContratosAccesoLista>());

        cut.Markup.Should().Contain("No se pudieron cargar los contratos de acceso").And.Contain("Reintentar");
    }

    [Fact]
    public void Consulta_no_ve_las_acciones_de_escritura()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        Preparar(Contrato(fin: new DateOnly(2099, 12, 31)));

        var cut = Cargada(Render<ContratosAccesoLista>());

        cut.Markup.Should().Contain("Montajes Moncayo S.L.");
        cut.Markup.Should().NotContain("Dar de alta").And.NotContain("Renovar");
    }

    // ------------------------------------------------------------ Detalle (tarjeta 11)

    [Fact]
    public void El_detalle_enseña_la_cabecera_los_indicadores_y_las_pestañas_como_proximamente()
    {
        Preparar(Contrato(fin: new DateOnly(2099, 12, 31)));

        var cut = Cargada(Render<ContratoAccesoDetalle>(p => p.Add(c => c.Id, ContratoId)));

        cut.Find("h1").TextContent.Should().Contain("Montajes Moncayo S.L.");
        cut.Find("[data-pieza=datos-contrato]").TextContent.Should().Contain("B12345674").And.Contain("Planta Zaragoza Norte").And.Contain("Recurrente");
        cut.Find("[data-pieza=kpis-contrato]").TextContent.Should().Contain("Cupo de 10").And.Contain("Hasta el nivel 1");
        cut.FindAll("[role=tab]").Select(t => t.TextContent).Should().Contain(t => t.Contains("Solicitudes"))
            .And.Contain(t => t.Contains("Trabajadores")).And.Contain(t => t.Contains("Empresas")).And.Contain(t => t.Contains("Requerimientos"));
        cut.FindAll("[data-pieza=insignia-proximamente]").Should().HaveCountGreaterThanOrEqualTo(4);
        cut.Markup.Should().Contain("Aún no se muestran las solicitudes");
        cut.Markup.Should().Contain("Renovar").And.Contain("Finalizar").And.Contain("Editar");
    }

    [Fact]
    public void Un_contrato_que_no_existe_o_no_se_ve_enseña_la_ficha_no_encontrada()
    {
        Preparar();

        var cut = Cargada(Render<ContratoAccesoDetalle>(p => p.Add(c => c.Id, Guid.NewGuid())));

        cut.Markup.Should().NotContain("Montajes Moncayo S.L.");
        cut.Markup.Should().Contain("ficha-no-encontrada");
    }

    [Fact]
    public void Finalizar_pide_confirmacion_y_envia_el_cierre_con_la_version_leida()
    {
        var mediador = Preparar(Contrato(fin: new DateOnly(2099, 12, 31)));
        var cut = Cargada(Render<ContratoAccesoDetalle>(p => p.Add(c => c.Id, ContratoId)));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Finalizar").Click();
        cut.Markup.Should().Contain("Finalizar contrato");
        cut.FindAll("button").Last(b => b.TextContent.Trim() == "Finalizar").Click();

        cut.WaitForAssertion(() => mediador.Enviadas.OfType<CerrarContratoAccesoCommand>().Should().ContainSingle());
        var cierre = mediador.Enviadas.OfType<CerrarContratoAccesoCommand>().Single();
        cierre.Id.Should().Be(ContratoId);
        cierre.Version.Should().Be(VersionContrato);
    }

    [Fact]
    public void Consulta_no_ve_las_acciones_del_detalle()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        Preparar(Contrato(fin: new DateOnly(2099, 12, 31)));

        var cut = Cargada(Render<ContratoAccesoDetalle>(p => p.Add(c => c.Id, ContratoId)));

        cut.Find("h1").TextContent.Should().Contain("Montajes Moncayo S.L.");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Renovar").And.NotContain("Finalizar");
    }

    [Fact]
    public void Un_contrato_Suspendido_se_renueva_pero_no_se_finaliza_ni_se_cancela()
    {
        Preparar(Contrato(EstadoContratoAcceso.Suspendido, fin: new DateOnly(2099, 12, 31)));

        var cut = Cargada(Render<ContratoAccesoDetalle>(p => p.Add(c => c.Id, ContratoId)));

        var botones = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();
        botones.Should().Contain("Renovar").And.NotContain("Finalizar", "Finalizar solo vale en un contrato Activo");
        cut.Markup.Should().NotContain("Más acciones del contrato", "sin Cancelar ni Eliminar posibles no hay menú vacío");
    }

    [Fact]
    public void Un_contrato_Pendiente_que_aun_no_ha_empezado_se_puede_cancelar_y_eliminar_con_la_version_leida()
    {
        var mediador = Preparar(Contrato(EstadoContratoAcceso.PendienteDeConfirmar, fin: new DateOnly(2099, 12, 31), inicio: new DateOnly(2098, 1, 1)));
        var cut = Cargada(Render<ContratoAccesoDetalle>(p => p.Add(c => c.Id, ContratoId)));

        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Finalizar");
        cut.Markup.Should().Contain("Más acciones del contrato");
        cut.Find("button[aria-haspopup=menu]").Click();

        cut.FindAll("[role=menuitem]").Select(i => i.TextContent.Trim()).Should().Contain("Cancelar contrato").And.Contain("Eliminar");
    }

    [Fact]
    public void Eliminar_pide_confirmacion_y_envia_la_baja_con_la_version_leida()
    {
        var mediador = Preparar(Contrato(EstadoContratoAcceso.PendienteDeConfirmar, fin: new DateOnly(2099, 12, 31), inicio: new DateOnly(2098, 1, 1)));
        var cut = Cargada(Render<ContratoAccesoDetalle>(p => p.Add(c => c.Id, ContratoId)));

        cut.Find("button[aria-haspopup=menu]").Click();
        cut.FindAll("[role=menuitem]").First(i => i.TextContent.Trim() == "Eliminar").Click();
        cut.FindAll("button").Last(b => b.TextContent.Trim() == "Eliminar").Click();

        cut.WaitForAssertion(() => mediador.Enviadas.OfType<EliminarContratoAccesoCommand>().Should().ContainSingle());
        var baja = mediador.Enviadas.OfType<EliminarContratoAccesoCommand>().Single();
        baja.Id.Should().Be(ContratoId);
        baja.Version.Should().Be(VersionContrato);
    }

    [Fact]
    public void Con_el_inicio_ya_pasado_un_contrato_Activo_no_se_cancela_ni_se_elimina()
    {
        Preparar(Contrato(fin: new DateOnly(2099, 12, 31)));

        var cut = Cargada(Render<ContratoAccesoDetalle>(p => p.Add(c => c.Id, ContratoId)));

        cut.Markup.Should().NotContain("Más acciones del contrato");
    }

    [Fact]
    public void Consulta_no_abre_el_drawer_de_condiciones_con_editar_en_la_URL()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        Preparar(Contrato(fin: new DateOnly(2099, 12, 31)));
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/inbound/contratos/{ContratoId}?editar=true");

        var cut = Cargada(Render<ContratoAccesoDetalle>(p => p.Add(c => c.Id, ContratoId)));

        cut.Markup.Should().NotContain("Editar contrato de acceso");
    }

    // ------------------------------------------------------------ Alta (tarjetas 1 a 3)

    [Fact]
    public void El_alta_va_en_tres_pasos_y_declara_lo_que_llega_mas_adelante()
    {
        Preparar();
        var cut = Cargada(Render<ContratosAccesoLista>());

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Dar de alta").Click();
        Cargada(cut);

        cut.Markup.Should().Contain("Dar de alta un contrato de acceso");
        cut.Markup.Should().Contain("Empresa contratista").And.Contain("Requisitos y control");
        cut.Find("[data-pieza=aviso-cotejo]").TextContent.Should().Contain("Próximamente");

        var botonContinuar = cut.FindAll("button").First(b => b.TextContent.Trim() == "Continuar");
        botonContinuar.HasAttribute("disabled").Should().BeTrue("sin Empresa contratista no se continúa");

        cut.Find("select").Change(EmpresaId.ToString());
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Continuar").Click();

        cut.Markup.Should().Contain("Tipo de contrato").And.Contain("Cupo de Trabajadores");
        cut.Find("[data-pieza=aviso-alta-agrupada]").TextContent.Should().Contain("Próximamente");
        cut.Find("[data-pieza=aviso-que-entra]").TextContent.Should().Contain("Próximamente");
        cut.Find("[data-pieza=aviso-subcontratas]").TextContent.Should().Contain("Próximamente");
    }

    // ------------------------------------------------------------ Menú

    private static ContextoMenuLateral ContextoMenu(bool lineaInbound, string rol = Roles.Administrador) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "prueba")),
        null, true, false, PerfilVocabularioTenant.Consultora, false, LineaInboundVigente: lineaInbound);

    [Fact]
    public void La_seccion_Inbound_del_menu_solo_aparece_con_la_linea_Inbound_vigente()
    {
        CatalogoMenuLateral.Visibles(ContextoMenu(lineaInbound: false)).Select(g => g.Grupo.Id).Should().NotContain("inbound");

        var grupos = CatalogoMenuLateral.Visibles(ContextoMenu(lineaInbound: true));

        var inbound = grupos.Single(g => g.Grupo.Id == "inbound");
        inbound.Enlaces.Select(e => e.Ruta).Should().BeEquivalentTo("inbound/contratos", "inbound/validacion");
    }

    [Fact]
    public async Task Sin_lectura_de_la_linea_Inbound_el_gate_falla_cerrado()
    {
        (await new LineaInboundNoDisponible().EstaVigenteAsync()).Should().BeFalse(
            "no hay Query de lectura de LineaProductoContratado: hasta que exista, el menú de Inbound no se enseña a nadie");
    }
}

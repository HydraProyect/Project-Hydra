using System.Security.Claims;
using CaeManager.Application.Inbound.AutorizacionesLectura.Queries.TieneAutorizacionLecturaConcedida;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Inbound;
using CaeManager.Web.Features.Inbound.TenantTec.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// El grupo «Mis Clientes» del menú lateral (demo de Inbound, pieza P4; decisión D6 del 2026-11-17): lo ve el
/// Administrador de un Tenant que, como Tenant propietario, tiene al menos una autorización de lectura documental concedida
/// (<see cref="IEsTenantTecInbound"/>), con independencia de la línea Inbound. Qué cuenta como «concedida» (vigente y sin
/// revocar) lo prueban los tests de la Query (Application) y su lectura bajo <c>cae_app_runtime</c> (integración, en CI); aquí
/// se prueba el catálogo, el puerto y el contexto de la paleta y los atajos.
/// </summary>
public class MenuTenantTecInboundTests
{
    private const string GrupoTec = "inbound-tec";
    private static readonly string[] RutasTec = ["inbound/requerimientos", "inbound/quien-lee"];

    private static ContextoMenuLateral Contexto(bool tenantTec, string rol = Roles.Administrador, bool lineaInbound = false) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "prueba")),
        null, true, false, PerfilVocabularioTenant.Consultora, false,
        LineaInboundVigente: lineaInbound, EsTenantTecInbound: tenantTec);

    // ------------------------------------------------------------ catálogo

    [Fact]
    public void Con_una_autorizacion_concedida_el_Administrador_ve_el_grupo_con_sus_dos_enlaces()
    {
        var grupo = CatalogoMenuLateral.Visibles(Contexto(tenantTec: true)).Single(g => g.Grupo.Id == GrupoTec);

        grupo.Enlaces.Select(e => e.Ruta).Should().Equal(RutasTec, "en este orden: Requerimientos y Quién lee mis documentos");
        grupo.Enlaces.Select(e => e.Id).Should().Equal("inbound-requerimientos", "inbound-quien-lee");
        grupo.Grupo.ClaveTitulo.Should().Be("MenuGrupoInboundTec", "el título se localiza: no es un literal de varias palabras");
        grupo.Enlaces.Should().OnlyContain(e => e.ClaveRotulo != null && e.RotuloEn(Rotulos) != e.ClaveRotulo,
            "cada rótulo sale de TextosComunes y la clave existe");
    }

    [Fact]
    public void Sin_autorizacion_concedida_el_grupo_no_aparece()
    {
        // Sin ninguna autorización, o con todas revocadas, caducadas o aún sin empezar, la Query responde «no» y el contexto lo lleva.
        CatalogoMenuLateral.Visibles(Contexto(tenantTec: false)).Select(g => g.Grupo.Id).Should().NotContain(GrupoTec);
        CatalogoMenuLateral.Visibles(Contexto(tenantTec: false)).SelectMany(g => g.Enlaces).Select(e => e.Ruta)
            .Should().NotContain(RutasTec);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void El_grupo_no_depende_de_la_linea_Inbound(bool lineaInbound)
    {
        var grupos = CatalogoMenuLateral.Visibles(Contexto(tenantTec: true, lineaInbound: lineaInbound));

        grupos.Select(g => g.Grupo.Id).Should().Contain(GrupoTec, $"con la línea Inbound {(lineaInbound ? "vigente" : "no vigente")}");
        grupos.Select(g => g.Grupo.Id).Contains("inbound").Should().Be(lineaInbound,
            "la sección «Inbound» del Tenant TCE sigue exigiendo su línea: el grupo nuevo no la abre ni la cierra");
    }

    [Fact]
    public void La_linea_Inbound_sola_no_abre_el_grupo_del_Tenant_TEC()
    {
        CatalogoMenuLateral.Visibles(Contexto(tenantTec: false, lineaInbound: true)).Select(g => g.Grupo.Id)
            .Should().Contain("inbound").And.NotContain(GrupoTec);
    }

    [Theory]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.GestorCae)]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Cliente)]
    public void Quien_no_es_Administrador_no_ve_el_grupo_ni_el_enlace_a_Requerimientos(string rol)
    {
        var visibles = CatalogoMenuLateral.Visibles(Contexto(tenantTec: true, rol));

        visibles.Select(g => g.Grupo.Id).Should().NotContain(GrupoTec, $"el rol {rol} no entra en las páginas del Tenant TEC");
        visibles.SelectMany(g => g.Enlaces).Select(e => e.Ruta).Should().NotContain("inbound/requerimientos").And.NotContain("inbound/quien-lee");
    }

    [Fact]
    public void Una_sesion_sin_rol_no_ve_el_grupo()
    {
        var sinRol = Contexto(tenantTec: true) with { Usuario = new ClaimsPrincipal(new ClaimsIdentity([], "prueba")) };

        CatalogoMenuLateral.Visibles(sinRol).Select(g => g.Grupo.Id).Should().NotContain(GrupoTec);
    }

    [Fact]
    public void El_menu_y_el_Authorize_de_las_dos_paginas_coinciden_en_el_rol()
    {
        // Los roles del grupo son los de las dos páginas: si una cambiara, el menú ofrecería un destino que la página niega.
        foreach (var pagina in new[] { typeof(Requerimientos), typeof(QuienLeeMisDocumentos) })
        {
            var autorizados = pagina.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>().Single()
                .Roles!.Split(',', StringSplitOptions.TrimEntries);

            foreach (var rol in new[] { Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae, Roles.Consulta })
            {
                var visible = CatalogoMenuLateral.Visibles(Contexto(tenantTec: true, rol)).SelectMany(g => g.Enlaces)
                    .Any(e => RutasTec.Contains(e.Ruta));
                visible.Should().Be(autorizados.Contains(rol), $"el menú y el [Authorize] de {pagina.Name} deben coincidir para {rol}");
            }
        }
    }

    [Fact]
    public void Las_rutas_del_grupo_son_las_de_las_paginas()
    {
        var rutas = new[] { typeof(Requerimientos), typeof(QuienLeeMisDocumentos) }
            .SelectMany(p => p.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), true)
                .Cast<Microsoft.AspNetCore.Components.RouteAttribute>().Select(r => r.Template))
            .ToList();

        rutas.Should().Contain(RutasTec.Select(r => "/" + r), "cada enlace del menú lleva a una página que existe");
    }

    private static readonly Microsoft.Extensions.Localization.IStringLocalizer Rotulos = new RotulosDeTextosComunes();

    /// <summary>Resuelve la clave contra el .resx neutro de TextosComunes; una clave que no existe devuelve la clave.</summary>
    private sealed class RotulosDeTextosComunes : Microsoft.Extensions.Localization.IStringLocalizer
    {
        private static readonly System.Resources.ResourceManager Gestor = new(typeof(CaeManager.Web.Recursos.TextosComunes));

        public Microsoft.Extensions.Localization.LocalizedString this[string name] =>
            new(name, Gestor.GetString(name, System.Globalization.CultureInfo.InvariantCulture) ?? name,
                Gestor.GetString(name, System.Globalization.CultureInfo.InvariantCulture) is null);

        public Microsoft.Extensions.Localization.LocalizedString this[string name, params object[] arguments] => this[name];

        public IEnumerable<Microsoft.Extensions.Localization.LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    // ------------------------------------------------------ implementación del puerto

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_puerto_responde_lo_que_dice_la_Query(bool concedida)
    {
        var mediador = new MediadorDelTenantTec(_ => concedida);

        (await new EsTenantTecPorAutorizacionConcedida(mediador).EsAsync()).Should().Be(concedida);
        mediador.Enviadas.Should().ContainSingle().Which.Should().BeOfType<TieneAutorizacionLecturaConcedidaQuery>();
    }

    [Fact]
    public async Task Si_la_Query_falla_o_la_deniega_un_behavior_el_puerto_falla_cerrado()
    {
        var mediador = new MediadorDelTenantTec(_ => throw new UnauthorizedAccessException("denegada por un behavior"));

        (await new EsTenantTecPorAutorizacionConcedida(mediador).EsAsync()).Should().BeFalse(
            "un error o una denegación (un Encargo no pleno, por ejemplo) no enseñan el grupo");
    }

    [Fact]
    public async Task Una_cancelacion_se_propaga_y_no_se_lee_como_un_no()
    {
        using var cancelacion = new CancellationTokenSource();
        await cancelacion.CancelAsync();
        var mediador = new MediadorDelTenantTec(ct => throw new OperationCanceledException(ct));

        var leer = () => new EsTenantTecPorAutorizacionConcedida(mediador).EsAsync(cancelacion.Token);

        await leer.Should().ThrowAsync<OperationCanceledException>("una cancelación no es una respuesta");
    }

    [Fact]
    public void El_puerto_esta_registrado_con_los_servicios_de_Inbound()
    {
        var servicios = InboundServicios.Registrar(new ServiceCollection());

        servicios.Should().Contain(s => s.ServiceType == typeof(IEsTenantTecInbound)
                                        && s.ImplementationType == typeof(EsTenantTecPorAutorizacionConcedida));
    }

    // --------------------------------------------------- AdmisionDeDestinos (paleta y atajos)

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_contexto_de_la_paleta_y_los_atajos_lleva_el_Tenant_TEC_del_puerto(bool tenantTec)
    {
        var contexto = await AdmisionDeDestinos.ContextoAsync(
            Sesion(Roles.Administrador), Servicios(new TenantTecFijo(tenantTec)), consultarPlataforma: false);

        contexto!.EsTenantTecInbound.Should().Be(tenantTec);
        contexto.LineaInboundVigente.Should().BeFalse("las dos señales son independientes");
        CatalogoMenuLateral.Visibles(contexto).Any(g => g.Grupo.Id == GrupoTec).Should().Be(tenantTec,
            "la paleta y los atajos deciden con el criterio del menú lateral");
    }

    [Fact]
    public async Task Si_el_puerto_falla_el_contexto_no_es_de_Tenant_TEC()
    {
        var contexto = await AdmisionDeDestinos.ContextoAsync(
            Sesion(Roles.Administrador), Servicios(new TenantTecQueFalla()), consultarPlataforma: true);

        contexto!.EsTenantTecInbound.Should().BeFalse("el gate falla cerrado y la paleta sigue funcionando");
    }

    [Fact]
    public async Task Sin_el_puerto_registrado_el_contexto_no_es_de_Tenant_TEC()
    {
        var contexto = await AdmisionDeDestinos.ContextoAsync(
            Sesion(Roles.Administrador), new ServiceCollection().BuildServiceProvider(), consultarPlataforma: false);

        contexto!.EsTenantTecInbound.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_sesion_no_se_consulta_el_Tenant_TEC()
    {
        var tenantTec = new TenantTecFijo(true);

        var contexto = await AdmisionDeDestinos.ContextoAsync(
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))), Servicios(tenantTec),
            consultarPlataforma: false);

        contexto!.EsTenantTecInbound.Should().BeFalse("una sesión anónima no ve ningún grupo del menú");
        tenantTec.Consultas.Should().Be(0);
    }

    private static Task<AuthenticationState> Sesion(string rol) =>
        Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "prueba"))));

    private static IServiceProvider Servicios(IEsTenantTecInbound tenantTec)
    {
        var servicios = new ServiceCollection();
        servicios.AddSingleton(tenantTec);
        return servicios.BuildServiceProvider();
    }

    private sealed class TenantTecFijo(bool esTenantTec) : IEsTenantTecInbound
    {
        public int Consultas { get; private set; }

        public Task<bool> EsAsync(CancellationToken cancellationToken = default)
        {
            Consultas++;
            return Task.FromResult(esTenantTec);
        }
    }

    private sealed class TenantTecQueFalla : IEsTenantTecInbound
    {
        public Task<bool> EsAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("sin base");
    }

    /// <summary>Solo responde a <see cref="TieneAutorizacionLecturaConcedidaQuery"/>; cualquier otra petición es un error del test.</summary>
    private sealed class MediadorDelTenantTec(Func<CancellationToken, bool> respuesta) : IMediator
    {
        public ListaSegura<object> Enviadas { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Enviadas.Add(request);
            return request is TieneAutorizacionLecturaConcedidaQuery
                ? (TResponse)(object)respuesta(cancellationToken)
                : throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => throw new NotSupportedException();
    }
}

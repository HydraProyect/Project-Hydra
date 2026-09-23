using System.Security.Claims;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.OrdenMenu;
using CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.Configuracion.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pantalla «Orden del menú» (mockup «Orden del Menu TALVEG.dc.html»): gate de interfaz del Actor
/// de Plataforma TALVEG, edición con teclado y botones, guardado con versión, conflicto,
/// restablecer y vista previa por rol. La autorización real (un Administrador de Tenant no puede
/// guardar) la prueban GuardarOrdenMenuLateralCommandTests y OrdenMenuLateralIntegrationTests.
/// </summary>
public class OrdenMenuLateralTests : BunitContext
{
    private static readonly Guid ActorPlataformaId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid OtroActorId = Guid.Parse("99999999-8888-7777-6666-555555555555");
    private static readonly DateTime GuardadoEnUtc = new(2026, 9, 23, 8, 42, 0, DateTimeKind.Utc);

    private readonly MediadorOrden _mediador = new();

    public OrdenMenuLateralTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            new AlmacenUsuarios(new()
            {
                [ActorPlataformaId.ToString()] = new ApplicationUser { Id = ActorPlataformaId, Email = "raiz@talveg.es" },
                [OtroActorId.ToString()] = new ApplicationUser { Id = OtroActorId, Email = "soporte@talveg.es" },
            }),
            null!, null!, null!, null!, null!, null!, null!, null!));
        Services.AddSingleton(Options.Create(new ComunicacionesOptions { Activo = true }));
        Services.AddSingleton<ILogger<OrdenMenuLateral>>(NullLogger<OrdenMenuLateral>.Instance);
    }

    // ---------------------------------------------------------------- dobles

    private sealed class MediadorOrden : IMediator
    {
        public bool EsAdministradorPlataforma { get; set; } = true;
        public OrdenMenuLateralDto? Orden { get; set; }
        public Func<GuardarOrdenMenuLateralCommand, Result>? AlGuardar { get; set; }
        public List<GuardarOrdenMenuLateralCommand> Guardados { get; } = [];
        public int LecturasDelOrden { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta = request switch
            {
                EsAdministradorPlataformaQuery => EsAdministradorPlataforma,
                ObtenerOrdenMenuLateralQuery => LeerOrden()!,
                GuardarOrdenMenuLateralCommand comando => Guardar(comando),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult((TResponse)respuesta);
        }

        private OrdenMenuLateralDto? LeerOrden()
        {
            LecturasDelOrden++;
            return Orden;
        }

        private Result Guardar(GuardarOrdenMenuLateralCommand comando)
        {
            Guardados.Add(comando);
            var resultado = AlGuardar?.Invoke(comando) ?? Result.Exito();
            if (resultado.EsExitoso)
                Orden = new OrdenMenuLateralDto(comando.Grupos, comando.Enlaces, Guid.NewGuid(), ActorPlataformaId, GuardadoEnUtc);
            return resultado;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    /// <summary>Solo responde a <c>FindByIdAsync</c>, que es lo único que la página pide.</summary>
    private sealed class AlmacenUsuarios(Dictionary<string, ApplicationUser> usuarios) : IUserStore<ApplicationUser>
    {
        private static Exception NoPrevisto() => new NotSupportedException("La página solo busca usuarios por Id.");

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
            Task.FromResult(usuarios.GetValueOrDefault(userId));

        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public void Dispose() { }
    }

    // ---------------------------------------------------------------- arnés

    private static readonly IReadOnlyList<string> GruposPorDefecto = CatalogoMenuLateral.Grupos.Select(g => g.Id).ToList();

    private IRenderedComponent<OrdenMenuLateral> Renderizar()
    {
        var cut = Render<OrdenMenuLateral>();
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("aria-busy=\"true\""));
        return cut;
    }

    private static List<string> GruposEnPantalla(IRenderedComponent<OrdenMenuLateral> cut) =>
        cut.FindAll("li.orden-menu-grupo").Select(li => li.GetAttribute("data-grupo")!).ToList();

    private static List<string> EnlacesEnPantalla(IRenderedComponent<OrdenMenuLateral> cut, string grupo) =>
        cut.FindAll($"li.orden-menu-grupo[data-grupo='{grupo}'] .orden-menu-enlaces [data-fila]")
            .Select(f => f.GetAttribute("data-fila")!.Split(':')[2]).ToList();

    private static string Fila(TipoDeFila tipo, string grupo, string id) =>
        tipo == TipoDeFila.Grupo ? $"[data-fila='Grupo::{id}']" : $"[data-fila='Enlace:{grupo}:{id}']";

    private enum TipoDeFila { Grupo, Enlace }

    // ---------------------------------------------------------------- gate

    [Fact]
    public void Sin_concesion_global_no_ofrece_la_edicion_ni_lee_el_orden()
    {
        _mediador.EsAdministradorPlataforma = false;

        var cut = Renderizar();

        cut.Markup.Should().Contain("Solo el Actor de Plataforma TALVEG ordena el menú");
        cut.FindAll("[data-fila]").Should().BeEmpty();
        cut.FindAll("[data-accion='guardar']").Should().BeEmpty();
        _mediador.LecturasDelOrden.Should().Be(0, "quien no puede ordenar no necesita el orden");
    }

    [Fact]
    public void Si_el_comando_responde_SinPermiso_la_pagina_retira_la_edicion()
    {
        _mediador.AlGuardar = _ => Result.Fallo(Error.Crear("OrdenMenu.SinPermiso", "sin permiso"));
        var cut = Renderizar();

        cut.Find(Fila(TipoDeFila.Grupo, "", GruposPorDefecto[0]) + " [data-flecha='abajo']").Click();
        cut.Find("[data-accion='guardar']").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Solo el Actor de Plataforma TALVEG ordena el menú"));
        cut.FindAll("[data-fila]").Should().BeEmpty();
    }

    // ---------------------------------------------------------------- carga y reconciliación

    [Fact]
    public void Sin_orden_guardado_enseña_el_orden_por_defecto_sin_distintivos_Nuevo_ni_historial()
    {
        var cut = Renderizar();

        GruposEnPantalla(cut).Should().Equal(GruposPorDefecto);
        cut.FindAll("[data-nuevo]").Should().BeEmpty();
        cut.Find("[data-sin-historial]").TextContent.Should().Contain("rige el orden por defecto");
        cut.Find("[data-estado-titulo]").TextContent.Should().Be("Sin cambios");
        cut.Find("[data-accion='guardar']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Con_orden_guardado_lo_reconcilia_y_marca_lo_que_el_orden_no_conocia()
    {
        var guardados = GruposPorDefecto.Where(g => g != "plataforma").Reverse().ToList();
        _mediador.Orden = new OrdenMenuLateralDto(guardados, ["alertas", "mi-trabajo", "no-existe"], Guid.NewGuid(), ActorPlataformaId, GuardadoEnUtc);

        var cut = Renderizar();

        GruposEnPantalla(cut).Should().Equal([.. guardados, "plataforma"], "lo nuevo va al final de su lista");
        cut.Find(Fila(TipoDeFila.Grupo, "", "plataforma")).QuerySelector("[data-nuevo]").Should().NotBeNull();
        cut.Find(Fila(TipoDeFila.Grupo, "", "control")).QuerySelector("[data-nuevo]").Should().BeNull();
        EnlacesEnPantalla(cut, "control").Take(2).Should().Equal(["alertas", "mi-trabajo"]);
        cut.Find(Fila(TipoDeFila.Enlace, "control", "facturacion")).QuerySelector("[data-nuevo]").Should().NotBeNull();
        cut.Find(Fila(TipoDeFila.Grupo, "", "control")).QuerySelector("[data-nace-plegado]").Should().NotBeNull(
            "«Control» nace plegado en el catálogo");
    }

    [Fact]
    public void El_ultimo_cambio_nombra_al_Actor_real_y_la_hora_de_Madrid()
    {
        _mediador.Orden = new OrdenMenuLateralDto(GruposPorDefecto, [], Guid.NewGuid(), ActorPlataformaId, GuardadoEnUtc);

        var cut = Renderizar();

        cut.Find("[data-historial='actor']").TextContent.Should().Contain("raiz@talveg.es");
        cut.Find("[data-historial='fecha']").TextContent.Should().Contain("23/09/2026 10:42", "08:42 UTC es 10:42 en Madrid en septiembre");
        cut.Find("[data-historial='cambios']").TextContent.Should().Contain("Vuelve al orden por defecto");
    }

    // ---------------------------------------------------------------- edición y guardado

    [Fact]
    public void Bajar_un_grupo_y_guardar_envia_el_orden_nuevo_con_la_version_leida()
    {
        var version = Guid.NewGuid();
        _mediador.Orden = new OrdenMenuLateralDto(GruposPorDefecto, [], version, ActorPlataformaId, GuardadoEnUtc);
        var cut = Renderizar();

        cut.Find(Fila(TipoDeFila.Grupo, "", "negocio") + " [data-flecha='abajo']").Click();

        GruposEnPantalla(cut).Take(3).Should().Equal(["dashboards", "operacion", "negocio"]);
        cut.Find("[data-estado-titulo]").TextContent.Should().Be("2 cambios sin guardar");
        cut.Find("[data-anuncio]").TextContent.Should().Be("Negocio, posición 3 de 6.");

        cut.Find("[data-accion='guardar']").Click();

        var comando = _mediador.Guardados.Should().ContainSingle().Subject;
        comando.VersionEsperada.Should().Be(version);
        comando.Grupos.Take(3).Should().Equal(["dashboards", "operacion", "negocio"]);
        comando.Enlaces.Should().HaveCount(CatalogoMenuLateral.Enlaces.Count, "se guarda el orden completo de enlaces");
        cut.WaitForAssertion(() => cut.Find("[data-aviso='guardado']"));
        cut.Find("[data-estado-titulo]").TextContent.Should().Be("Sin cambios");
    }

    [Fact]
    public void Un_enlace_se_mueve_dentro_de_su_grupo_y_no_sale_de_el()
    {
        var cut = Renderizar();
        var enlacesControl = EnlacesEnPantalla(cut, "control");

        cut.Find(Fila(TipoDeFila.Enlace, "control", enlacesControl[^1]) + " [data-flecha='abajo']")
            .HasAttribute("disabled").Should().BeTrue("el último de su grupo no baja al siguiente grupo");
        cut.Find(Fila(TipoDeFila.Enlace, "control", enlacesControl[1]) + " [data-flecha='arriba']").Click();

        EnlacesEnPantalla(cut, "control").Take(2).Should().Equal([enlacesControl[1], enlacesControl[0]]);
        EnlacesEnPantalla(cut, "control").Should().BeEquivalentTo(enlacesControl);
    }

    [Fact]
    public void Con_el_teclado_se_coge_se_mueve_y_Escape_devuelve_la_fila_a_su_sitio()
    {
        var cut = Renderizar();
        var asa = Fila(TipoDeFila.Grupo, "", "dashboards") + " [data-asa]";

        cut.Find(asa).Click();
        cut.Find(asa).GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-anuncio]").TextContent.Should().StartWith("Dashboards cogido.");

        cut.Find(asa).KeyDown(key: "ArrowDown");
        cut.Find(asa).KeyDown(key: "ArrowDown");
        GruposEnPantalla(cut).Take(3).Should().Equal(["negocio", "operacion", "dashboards"]);

        cut.Find(asa).KeyDown(key: "Escape");
        GruposEnPantalla(cut).Should().Equal(GruposPorDefecto);
        cut.Find(asa).GetAttribute("aria-pressed").Should().Be("false");
        cut.Find("[data-anuncio]").TextContent.Should().Be("Movimiento cancelado. Dashboards vuelve a su posición.");
    }

    [Fact]
    public void Sin_coger_la_fila_las_flechas_no_la_mueven()
    {
        var cut = Renderizar();

        cut.Find(Fila(TipoDeFila.Grupo, "", "dashboards") + " [data-asa]").KeyDown(key: "ArrowDown");

        GruposEnPantalla(cut).Should().Equal(GruposPorDefecto);
    }

    [Fact]
    public void Arrastrar_una_fila_sobre_otra_de_la_misma_lista_la_recoloca()
    {
        var cut = Renderizar();

        cut.Find(Fila(TipoDeFila.Grupo, "", "plataforma")).DragStart();
        cut.Find(Fila(TipoDeFila.Grupo, "", "dashboards")).Drop();

        GruposEnPantalla(cut)[0].Should().Be("plataforma");
    }

    [Fact]
    public void Arrastrar_un_enlace_a_otro_grupo_no_hace_nada()
    {
        var cut = Renderizar();

        cut.Find(Fila(TipoDeFila.Enlace, "control", "alertas")).DragStart();
        cut.Find(Fila(TipoDeFila.Enlace, "negocio", "empresas")).Drop();

        EnlacesEnPantalla(cut, "negocio").Should().NotContain("alertas");
        cut.Find("[data-estado-titulo]").TextContent.Should().Be("Sin cambios");
    }

    [Fact]
    public void Descartar_vuelve_al_orden_guardado()
    {
        var cut = Renderizar();
        cut.Find(Fila(TipoDeFila.Grupo, "", "negocio") + " [data-flecha='arriba']").Click();

        cut.Find("[data-accion='descartar']").Click();

        GruposEnPantalla(cut).Should().Equal(GruposPorDefecto);
        _mediador.Guardados.Should().BeEmpty();
    }

    [Fact]
    public void Un_conflicto_no_pisa_el_orden_ajeno_dice_quien_guardo_y_permite_cargarlo()
    {
        var suyo = new OrdenMenuLateralDto(["control", "dashboards"], [], Guid.NewGuid(), OtroActorId, GuardadoEnUtc);
        _mediador.AlGuardar = _ =>
        {
            _mediador.Orden = suyo;
            return Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "conflicto"));
        };
        var cut = Renderizar();
        cut.Find(Fila(TipoDeFila.Grupo, "", "negocio") + " [data-flecha='arriba']").Click();

        cut.Find("[data-accion='guardar']").Click();

        cut.WaitForAssertion(() => cut.Find("[data-aviso='conflicto']").TextContent
            .Should().Contain("soporte@talveg.es").And.Contain("23/09/2026 10:42"));
        GruposEnPantalla(cut)[0].Should().Be("negocio", "los cambios propios siguen en pantalla");

        cut.Find("[data-aviso='conflicto'] button").Click();

        GruposEnPantalla(cut).Take(2).Should().Equal(["control", "dashboards"]);
        cut.Find("[data-historial='actor']").TextContent.Should().Contain("soporte@talveg.es");
        cut.FindAll("[data-aviso]").Should().BeEmpty();
    }

    [Fact]
    public void Un_error_al_guardar_conserva_los_cambios_y_Reintentar_vuelve_a_enviar()
    {
        var intentos = 0;
        _mediador.AlGuardar = _ => ++intentos == 1
            ? Result.Fallo(Error.Crear("OrdenMenu.NoValido", "no válido"))
            : Result.Exito();
        var cut = Renderizar();
        cut.Find(Fila(TipoDeFila.Grupo, "", "negocio") + " [data-flecha='arriba']").Click();

        cut.Find("[data-accion='guardar']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-aviso='error']"));
        GruposEnPantalla(cut)[0].Should().Be("negocio");

        cut.Find("[data-aviso='error'] button").Click();

        cut.WaitForAssertion(() => cut.Find("[data-aviso='guardado']"));
        _mediador.Guardados.Should().HaveCount(2);
        _mediador.Guardados[1].Grupos[0].Should().Be("negocio");
    }

    [Fact]
    public void Restablecer_pide_confirmacion_y_guarda_listas_vacias()
    {
        var version = Guid.NewGuid();
        _mediador.Orden = new OrdenMenuLateralDto(GruposPorDefecto.Reverse().ToList(), [], version, ActorPlataformaId, GuardadoEnUtc);
        var cut = Renderizar();

        cut.Find("[data-accion='restablecer']").Click();
        cut.Find("[role='dialog']").TextContent.Should().Contain("¿Restablecer el orden por defecto?");
        _mediador.Guardados.Should().BeEmpty("abrir el diálogo no guarda nada");

        cut.Find("[data-dialogo='restablecer']").Click();

        var comando = _mediador.Guardados.Should().ContainSingle().Subject;
        comando.Grupos.Should().BeEmpty();
        comando.Enlaces.Should().BeEmpty();
        comando.VersionEsperada.Should().Be(version);
        cut.WaitForAssertion(() => GruposEnPantalla(cut).Should().Equal(GruposPorDefecto));
        cut.FindAll("[role='dialog']").Should().BeEmpty();
    }

    [Fact]
    public void Cancelar_el_dialogo_de_restablecer_no_guarda()
    {
        var cut = Renderizar();

        cut.Find("[data-accion='restablecer']").Click();
        cut.Find("[data-dialogo='cancelar']").Click();

        cut.FindAll("[role='dialog']").Should().BeEmpty();
        _mediador.Guardados.Should().BeEmpty();
    }

    // ---------------------------------------------------------------- vista previa

    [Fact]
    public void La_vista_previa_sigue_el_orden_editado_y_ordenar_no_cambia_lo_que_ve_cada_rol()
    {
        var cut = Renderizar();
        cut.Find("[data-rol-previa]").Change(Roles.Consulta);

        var previa = cut.FindAll("[data-grupo-previa]").Select(g => g.GetAttribute("data-grupo-previa")).ToList();
        previa.Should().NotContain(["administracion", "plataforma"], "Consulta no ve esos grupos por su rol");
        var enlacesVistos = cut.FindAll("[data-enlace-previa]").Count;

        cut.Find(Fila(TipoDeFila.Grupo, "", "control") + " [data-flecha='arriba']").Click();

        cut.FindAll("[data-grupo-previa]").Select(g => g.GetAttribute("data-grupo-previa")).Take(3)
            .Should().Equal(["dashboards", "negocio", "control"]);
        cut.FindAll("[data-enlace-previa]").Should().HaveCount(enlacesVistos);

        var esperados = CatalogoMenuLateral.Visibles(new ContextoMenuLateral(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, Roles.Consulta)], "prueba")),
            null, true, false, PerfilVocabularioTenant.Consultora, false)).Sum(g => g.Enlaces.Count);
        enlacesVistos.Should().Be(esperados, "la vista previa es CatalogoMenuLateral.Visibles, el mismo del menú real");
        cut.Find("[data-previa-texto]").TextContent.Should()
            .Be($"Ve {esperados} de {CatalogoMenuLateral.Enlaces.Count} enlaces; los demás no los ve por su rol, no por el orden.");
    }
}

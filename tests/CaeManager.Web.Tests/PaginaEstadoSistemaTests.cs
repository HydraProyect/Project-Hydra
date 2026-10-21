using Bunit;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// PaginaEstadoSistema, el shell de Error/not-found/acceso-denegado (Estados
/// Sistema TALVEG.dc.html). Cubre las dos propiedades que la revisión de
/// 2026-09-12 encontró sin test: "Reintentar" solo se ofrece cuando de
/// verdad puede repetir la petición que falló, y "acceso-denegado" nunca
/// ofrece un destino al que el usuario denegado no pueda llegar.
///
/// <para>
/// El reload de "Reintentar" (<c>window.location.reload()</c>) vivía en JS
/// nativo — bUnit con <see cref="JSRuntimeMode.Loose"/> no ejecuta interop,
/// así que ese comportamiento nunca fue observable aquí. Ahora "Reintentar"
/// es una ancla real (<c>Boton.Href</c>): se puede comprobar leyendo el
/// <c>href</c> del marcado, sin JS de por medio.
/// </para>
/// </summary>
public class PaginaEstadoSistemaTests : BunitContext
{
    public PaginaEstadoSistemaTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Fact]
    public void El_error_muestra_el_codigo_y_la_ayuda_para_soporte()
    {
        var cut = Renderizar(TipoEstadoSistema.Error, codigo: "codigo-prueba");

        cut.Find(".referencia-tecnica-etiqueta").TextContent.Should().Be("Código para soporte");
        cut.Find(".referencia-tecnica-valor").TextContent.Should().Be("codigo-prueba");
        cut.Find(".referencia-tecnica-ayuda").TextContent.Trim().Should().Be("Si se repite, envía este código a soporte.");
    }

    [Fact]
    public void Sin_codigo_no_se_muestra_la_ayuda_para_soporte()
    {
        var cut = Renderizar(TipoEstadoSistema.Error);

        cut.FindAll(".referencia-tecnica-etiqueta, .referencia-tecnica-ayuda").Should().BeEmpty();
    }

    // ------------------------------------------------------------ Reintentar

    [Fact]
    public void Sin_ruta_de_origen_conocida_no_ofrece_Reintentar()
    {
        var cut = Renderizar(TipoEstadoSistema.Error, rutaReintentar: null);

        cut.Markup.Should().NotContain("Reintentar",
            "sin ruta de origen no hay nada que repetir de verdad — prometer un reintento que solo recarga /Error induce a error");
    }

    [Theory]
    [InlineData("https://evil.example/panel")]
    [InlineData("//evil.example/panel")]
    [InlineData("/\\evil.example")]
    public void Una_ruta_de_origen_no_local_no_se_ofrece_como_Reintentar(string rutaNoLocal)
    {
        var cut = Renderizar(TipoEstadoSistema.Error, rutaReintentar: rutaNoLocal);

        cut.Markup.Should().NotContain("Reintentar",
            "una ruta absoluta o protocol-relative convertiría \"Reintentar\" en un redirect abierto");
    }

    [Fact]
    public void Con_ruta_de_origen_local_conocida_Reintentar_navega_ahi_de_verdad()
    {
        var cut = Renderizar(TipoEstadoSistema.Error, rutaReintentar: "/documentos/subir");

        var enlace = cut.Find("a.boton-secundario");
        enlace.TextContent.Should().Be("Reintentar");
        enlace.GetAttribute("href").Should().Be("/documentos/subir",
            "Reintentar debe repetir la petición que de verdad falló, no recargar /Error");
    }

    [Theory]
    [InlineData(TipoEstadoSistema.NoEncontrado)]
    [InlineData(TipoEstadoSistema.AccesoDenegado)]
    public void Solo_Error_ofrece_Reintentar_aunque_llegue_una_ruta_de_origen(TipoEstadoSistema tipo)
    {
        var cut = Renderizar(tipo, rutaReintentar: "/documentos");

        cut.Markup.Should().NotContain("Reintentar");
    }

    // ------------------------------------------------------- Acceso denegado

    [Fact]
    public void AccesoDenegado_no_atribuye_una_causa_que_no_conoce()
    {
        var cut = Renderizar(TipoEstadoSistema.AccesoDenegado, rol: Roles.Cliente);

        cut.Markup.Should().NotContain("cartera",
            "la pantalla no sabe si la cartera es la causa — no debe inventarla");
        cut.Markup.Should().NotContain("administrador",
            "no debe nombrar una figura concreta que quizá no es quien concede el acceso, ni incumplir el contrato de terminología");
    }

    [Fact]
    public void AccesoDenegado_no_ofrece_Comunicaciones_a_quien_no_puede_entrar_ahi()
    {
        // RolesComunicaciones.Gestion excluye a propósito el rol Cliente — un
        // contacto de una empresa cliente externa no puede leer el correo de
        // las demás. Ofrecerle este destino sería una segunda denegación.
        var cut = Renderizar(TipoEstadoSistema.AccesoDenegado, rol: Roles.Cliente);

        cut.Markup.Should().NotContain("/comunicaciones",
            "el rol Cliente no está en RolesComunicaciones.Gestion: el enlace lo dejaría en otra pantalla de acceso denegado");
    }

    [Fact]
    public void AccesoDenegado_ofrece_Comunicaciones_a_quien_si_puede_entrar_ahi()
    {
        var cut = Renderizar(TipoEstadoSistema.AccesoDenegado, rol: Roles.GestorCae, comunicacionesActivo: true);

        cut.Markup.Should().Contain("/comunicaciones",
            "un Gestor CAE sí está en RolesComunicaciones.Gestion y el módulo está activo: el destino es alcanzable de verdad");
    }

    [Fact]
    public void AccesoDenegado_no_ofrece_Comunicaciones_si_el_modulo_esta_congelado()
    {
        // Bandeja.razor redirige a /not-found cuando ComunicacionesOptions.Activo
        // es false (P2 #26): ofrecer el enlace aquí llevaría a esa misma ruta muerta
        // aunque el rol sea el correcto.
        var cut = Renderizar(TipoEstadoSistema.AccesoDenegado, rol: Roles.GestorCae, comunicacionesActivo: false);

        cut.Markup.Should().NotContain("/comunicaciones",
            "con el módulo congelado, /comunicaciones no es un destino alcanzable aunque el rol sea el correcto");
    }

    // ---- Pantalla principal (demo del piloto): texto de producto y salida que el rol admite

    [Fact]
    public void AccesoDenegado_habla_de_la_pantalla_y_del_rol_sin_revelar_el_recurso()
    {
        var cut = Renderizar(TipoEstadoSistema.AccesoDenegado, rol: Roles.GestorCae);

        cut.Find("h2, h1, h3").TextContent.Should().Be("Tu rol actual no incluye esta pantalla");
        cut.Markup.Should().NotContain("No tienes permiso");
        cut.Markup.Should().NotContain("Tenant");
    }

    /// <summary>
    /// Decisión del propietario, 2026-10-21: la pantalla principal es Inicio para todos los roles, también
    /// para el Gestor CAE con varios Tenants autorizados (antes, Mi trabajo).
    /// </summary>
    [Theory]
    [InlineData(Roles.GestorCae, 1)]
    [InlineData(Roles.GestorCae, 2)]
    [InlineData(Roles.CoordinadorCae, 2)]
    [InlineData(Roles.Consulta, 1)]
    [InlineData(Roles.Cliente, 1)]
    public void AccesoDenegado_lleva_a_Inicio_como_pantalla_principal(string rol, int tenantsAutorizados)
    {
        Services.AddScoped<IMediator>(_ => new MediatorConClientesAutorizados(tenantsAutorizados));

        var cut = Renderizar(TipoEstadoSistema.AccesoDenegado, rol: rol);

        var boton = cut.Find("a.boton-primario");
        boton.TextContent.Should().Be("Ir a tu pantalla principal");
        boton.GetAttribute("href").Should().Be("/");
    }

    [Fact]
    public void AccesoDenegado_sin_sesion_conocida_lleva_a_Inicio()
    {
        var cut = Renderizar(TipoEstadoSistema.AccesoDenegado);

        cut.Find("a.boton-primario").GetAttribute("href").Should().Be("/");
    }

    [Theory]
    [InlineData(TipoEstadoSistema.Error)]
    [InlineData(TipoEstadoSistema.NoEncontrado)]
    public void Error_y_no_encontrado_conservan_Ir_al_Inicio(TipoEstadoSistema tipo)
    {
        var cut = Renderizar(tipo, rol: Roles.GestorCae);

        var boton = cut.Find("a.boton-primario");
        boton.TextContent.Should().Be("Ir al Inicio");
        boton.GetAttribute("href").Should().Be("/");
    }

    [Theory]
    [InlineData("trabajadores", "trabajadores")]
    [InlineData("empresas", "empresas")]
    [InlineData("centros", "centros de trabajo")]
    [InlineData("clientes", "clientes")]
    [InlineData("subcontratas", "subcontratas")]
    [InlineData("vehiculos", "vehículos")]
    [InlineData("proyectos", "proyectos")]
    [InlineData("documentos", "documentos")]
    [InlineData("visitas", "visitas")]
    [InlineData("gestiones", "gestiones")]
    [InlineData("alertas", "alertas")]
    public void NoEncontrado_con_sesion_ofrece_la_lista_con_texto_legible(string segmento, string nombre)
    {
        var cut = Renderizar(TipoEstadoSistema.NoEncontrado, rol: Roles.GestorCae,
            rutaOriginal: $"/{segmento}/no-existe?volver=https://evil.example#detalle");

        var boton = cut.Find("a.boton-primario");
        boton.GetAttribute("href").Should().Be($"/{segmento}");
        boton.TextContent.Should().Be($"Volver a la lista de {nombre}");
        cut.Find("a.boton-secundario").GetAttribute("href").Should().Be("/");
        cut.Markup.Should().NotContain("onclick=");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/not-found")]
    [InlineData("/desconocido/trabajadores")]
    [InlineData("/trabajadores-falso/123")]
    [InlineData("//trabajadores/123")]
    [InlineData("https://evil.example/trabajadores")]
    [InlineData("/trabajadores%2fmalicioso/123")]
    [InlineData("/\\trabajadores/123")]
    public void NoEncontrado_no_construye_destinos_fuera_del_catalogo(string? ruta)
    {
        var cut = Renderizar(TipoEstadoSistema.NoEncontrado, rol: Roles.GestorCae, rutaOriginal: ruta);

        cut.Find("a.boton-primario").GetAttribute("href").Should().Be("/");
        cut.Markup.Should().NotContain("Volver a la lista");
    }

    [Theory]
    [InlineData(TipoEstadoSistema.NoEncontrado, null)]
    [InlineData(TipoEstadoSistema.Error, Roles.GestorCae)]
    [InlineData(TipoEstadoSistema.AccesoDenegado, Roles.GestorCae)]
    public void Solo_404_con_sesion_ofrece_la_lista(TipoEstadoSistema tipo, string? rol)
    {
        var cut = Renderizar(tipo, rol: rol, rutaOriginal: "/trabajadores/123");

        cut.Markup.Should().NotContain("Volver a la lista");
    }

    [Fact]
    public void NotFound_usa_la_ruta_original_de_la_reejecucion()
    {
        ConfigurarServicios(Roles.GestorCae, true);
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/not-found");
        var contexto = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        contexto.Features.Set<Microsoft.AspNetCore.Diagnostics.IStatusCodeReExecuteFeature>(
            new Microsoft.AspNetCore.Diagnostics.StatusCodeReExecuteFeature { OriginalPath = "/vehiculos/no-existe" });

        var cut = Render<NotFound>(p => p.AddCascadingValue<Microsoft.AspNetCore.Http.HttpContext>(contexto));

        cut.Find("a.boton-primario").GetAttribute("href").Should().Be("/vehiculos");
    }

    [Fact]
    public void NotFound_sin_reejecucion_usa_la_ruta_de_navegacion()
    {
        ConfigurarServicios(Roles.GestorCae, true);
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("/empresas/no-existe");

        var cut = Render<NotFound>();

        cut.Find("a.boton-primario").GetAttribute("href").Should().Be("/empresas");
    }

    private sealed class MediatorConClientesAutorizados(int cuantos) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is ObtenerClientesAutorizadosQuery
                ? Task.FromResult((TResponse)(object)(IReadOnlyList<ClienteAutorizadoDto>)
                    [.. Enumerable.Range(0, cuantos).Select(i => new ClienteAutorizadoDto(Guid.NewGuid(), $"Tenant {i}", EsOrigen: i == 0))])
                : throw new NotSupportedException(request.GetType().Name);

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

    // ---------------------------------------------------------------- arnés

    private IRenderedComponent<PaginaEstadoSistema> Renderizar(
        TipoEstadoSistema tipo, string? rutaReintentar = null, string? rol = null, bool comunicacionesActivo = true,
        string? rutaOriginal = null, string? codigo = null)
    {
        ConfigurarServicios(rol, comunicacionesActivo);
        return Render<PaginaEstadoSistema>(parametros => parametros
            .Add(p => p.Tipo, tipo)
            .Add(p => p.RutaReintentar, rutaReintentar)
            .Add(p => p.RutaOriginal, rutaOriginal)
            .Add(p => p.ReferenciaTecnica, codigo));
    }

    private void ConfigurarServicios(string? rol, bool comunicacionesActivo)
    {
        Services.AddLocalization();
        Services.AddSingleton<ToastService>();
        Services.AddSingleton<IOptions<ComunicacionesOptions>>(
            Options.Create(new ComunicacionesOptions { Activo = comunicacionesActivo }));

        // AuthorizeView exige IAuthorizationPolicyProvider aunque la prueba no
        // necesite comprobar ningún rol — sin esto, el render de AccesoDenegado
        // falla por un servicio que falta, no por el producto.
        var autorizacion = AddAuthorization();
        if (rol is not null)
            autorizacion.SetAuthorized("usuaria-de-prueba").SetRoles(rol);
    }
}


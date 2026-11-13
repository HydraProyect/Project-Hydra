using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests.Observabilidad;

/// <summary>
/// La cabecera Server-Timing con el coste de la petición: presente solo con la opción
/// activa, usuario autenticado y respuesta HTML; y con solo números y nombres fijos
/// (OWASP ASVS 4.0.3 V14.3).
/// </summary>
public class ServerTimingCosteCargaTests
{
    [Fact]
    public async Task Con_la_opcion_activa_y_usuario_autenticado_la_pagina_lleva_la_cabecera_con_el_coste()
    {
        var (contexto, respuesta) = await Ejecutar(habilitado: true, autenticado: true, tipo: "text/html; charset=utf-8",
            coste =>
            {
                coste.RegistrarComando(TimeSpan.FromMilliseconds(12.34));
                coste.RegistrarComando(TimeSpan.FromMilliseconds(1));
                coste.RegistrarConexion();
                coste.RegistrarEsperaPuerta(TimeSpan.FromMilliseconds(3));
                coste.RegistrarDespacho();
            });

        respuesta.DispararOnStarting();

        contexto.Response.Headers[CabeceraServerTiming.Nombre].ToString()
            .Should().Be("db;dur=13.3, cmd;desc=\"2\", conn;desc=\"1\", puerta;dur=3.0, med;desc=\"1\"");
    }

    [Fact]
    public async Task Con_la_opcion_inactiva_no_hay_cabecera()
    {
        var (contexto, respuesta) = await Ejecutar(habilitado: false, autenticado: true, tipo: "text/html");

        respuesta.DispararOnStarting();

        contexto.Response.Headers.ContainsKey(CabeceraServerTiming.Nombre).Should().BeFalse();
        respuesta.Callbacks.Should().BeEmpty("apagada no debe ni registrar el OnStarting");
    }

    [Fact]
    public async Task Un_usuario_anonimo_no_recibe_la_cabecera()
    {
        var (contexto, respuesta) = await Ejecutar(habilitado: true, autenticado: false, tipo: "text/html");

        respuesta.DispararOnStarting();

        contexto.Response.Headers.ContainsKey(CabeceraServerTiming.Nombre).Should().BeFalse();
    }

    [Fact]
    public async Task Un_html_que_no_sale_de_una_pagina_de_componentes_no_recibe_la_cabecera()
    {
        var (contexto, respuesta) = await Ejecutar(habilitado: true, autenticado: true, tipo: "text/html",
            paginaSsr: false);

        respuesta.DispararOnStarting();

        contexto.Response.Headers.ContainsKey(CabeceraServerTiming.Nombre).Should().BeFalse();
    }

    [Fact]
    public async Task Si_no_se_puede_resolver_el_acumulador_la_peticion_sigue_sin_cabecera()
    {
        var contexto = new DefaultHttpContext { RequestServices = new ProveedorQueFalla() };
        var siguienteLlamado = false;
        var middleware = new ServerTimingCosteCargaMiddleware(_ => { siguienteLlamado = true; return Task.CompletedTask; },
            Options.Create(new OpcionesServerTiming { Habilitado = true }));

        var invocar = () => middleware.InvokeAsync(contexto);

        await invocar.Should().NotThrowAsync();
        siguienteLlamado.Should().BeTrue();
    }

    private sealed class ProveedorQueFalla : IServiceProvider
    {
        public object? GetService(Type serviceType) => throw new ObjectDisposedException("scope");
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/css")]
    [InlineData("text/html-extra")]
    [InlineData(null)]
    public async Task Una_respuesta_que_no_es_pagina_no_recibe_la_cabecera(string? tipo)
    {
        var (contexto, respuesta) = await Ejecutar(habilitado: true, autenticado: true, tipo: tipo);

        respuesta.DispararOnStarting();

        contexto.Response.Headers.ContainsKey(CabeceraServerTiming.Nombre).Should().BeFalse();
    }

    [Fact]
    public async Task La_cabecera_no_lleva_nada_identificable_aunque_la_peticion_si()
    {
        var tenant = Guid.NewGuid().ToString();
        var (contexto, respuesta) = await Ejecutar(habilitado: true, autenticado: true, tipo: "text/html",
            coste => coste.RegistrarComando(TimeSpan.FromMilliseconds(1)),
            ruta: $"/empresas/{tenant}", usuario: "gestora@example.test");

        respuesta.DispararOnStarting();

        var cabecera = contexto.Response.Headers[CabeceraServerTiming.Nombre].ToString();
        cabecera.Should().NotContain(tenant).And.NotContain("gestora").And.NotContain("empresas");
        // Gramática cerrada: cinco métricas con nombre fijo y valor numérico, nada más.
        cabecera.Should().MatchRegex(
            "^db;dur=\\d+\\.\\d, cmd;desc=\"\\d+\", conn;desc=\"\\d+\", puerta;dur=\\d+\\.\\d, med;desc=\"\\d+\"$");
    }

    [Fact]
    public void El_formato_usa_cultura_invariante()
    {
        var cultura = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("es-ES");
            CabeceraServerTiming.Formatear(new InstantaneaCosteCarga(TimeSpan.FromMilliseconds(1.25), 0, 0, TimeSpan.Zero, 0))
                .Should().MatchRegex("^db;dur=1\\.[23], ");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = cultura;
        }
    }

    [Fact]
    public async Task La_autenticacion_se_decide_al_empezar_la_respuesta_no_al_entrar()
    {
        // En la canalización real este middleware va antes de UseAuthentication: al entrar el
        // usuario aún es anónimo y solo después queda autenticado.
        var (contexto, respuesta) = await Ejecutar(habilitado: true, autenticado: false, tipo: "text/html",
            autenticarDespues: true);

        respuesta.DispararOnStarting();

        contexto.Response.Headers.ContainsKey(CabeceraServerTiming.Nombre).Should().BeTrue();
    }

    [Fact]
    public async Task Una_reejecucion_con_scope_nuevo_no_duplica_la_cabecera_y_cuenta_el_scope_original()
    {
        var (contexto, respuesta) = await Ejecutar(habilitado: true, autenticado: true, tipo: "text/html",
            coste => coste.RegistrarComando(TimeSpan.FromMilliseconds(1)));

        // UseExceptionHandler/UseStatusCodePagesWithReExecute con createScope: mismo HttpContext, scope nuevo.
        var servicios = new ServiceCollection();
        servicios.AddScoped<CosteCarga>();
        contexto.RequestServices = servicios.BuildServiceProvider().CreateScope().ServiceProvider;
        var middleware = new ServerTimingCosteCargaMiddleware(_ => Task.CompletedTask,
            Options.Create(new OpcionesServerTiming { Habilitado = true }));
        await middleware.InvokeAsync(contexto);

        respuesta.DispararOnStarting();

        respuesta.Callbacks.Should().ContainSingle();
        contexto.Response.Headers[CabeceraServerTiming.Nombre].ToString().Should().Contain("cmd;desc=\"1\"");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Una_reejecucion_de_codigo_de_estado_solo_lleva_cabecera_si_la_peticion_original_era_una_pagina(
        bool originalEsPagina, bool esperaCabecera)
    {
        // Un 404 de una API autenticada re-ejecutado como /not-found: el endpoint actual es una
        // página, el original no.
        var (contexto, respuesta) = await Ejecutar(habilitado: true, autenticado: true, tipo: "text/html",
            coste => coste.RegistrarComando(TimeSpan.FromMilliseconds(1)));
        contexto.Features.Set<Microsoft.AspNetCore.Diagnostics.IStatusCodeReExecuteFeature>(
            new Microsoft.AspNetCore.Diagnostics.StatusCodeReExecuteFeature
            {
                Endpoint = originalEsPagina ? EndpointDePagina("/empresas") : new Endpoint(_ => Task.CompletedTask, EndpointMetadataCollection.Empty, "api"),
            });

        respuesta.DispararOnStarting();

        contexto.Response.Headers.ContainsKey(CabeceraServerTiming.Nombre).Should().Be(esperaCabecera);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Una_reejecucion_por_excepcion_solo_lleva_cabecera_si_la_peticion_original_era_una_pagina(
        bool originalEsPagina, bool esperaCabecera)
    {
        var (contexto, respuesta) = await Ejecutar(habilitado: true, autenticado: true, tipo: "text/html");
        contexto.Features.Set<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>(
            new Microsoft.AspNetCore.Diagnostics.ExceptionHandlerFeature
            {
                Error = new InvalidOperationException(),
                Path = "/",
                Endpoint = originalEsPagina ? EndpointDePagina("/empresas") : null,
            });

        respuesta.DispararOnStarting();

        contexto.Response.Headers.ContainsKey(CabeceraServerTiming.Nombre).Should().Be(esperaCabecera);
    }

    private static Endpoint EndpointDePagina(string nombre) => new(_ => Task.CompletedTask,
        new EndpointMetadataCollection(new Microsoft.AspNetCore.Components.Endpoints.ComponentTypeMetadata(typeof(PaginaDePrueba))), nombre);

    private static async Task<(HttpContext Contexto, RespuestaQueCapturaOnStarting Respuesta)> Ejecutar(
        bool habilitado, bool autenticado, string? tipo, Action<CosteCarga>? gastar = null,
        string ruta = "/", string usuario = "u", bool autenticarDespues = false, bool paginaSsr = true)
    {
        var servicios = new ServiceCollection();
        servicios.AddScoped<CosteCarga>();
        var proveedor = servicios.BuildServiceProvider().CreateScope().ServiceProvider;

        var respuesta = new RespuestaQueCapturaOnStarting();
        var contexto = new DefaultHttpContext { RequestServices = proveedor };
        contexto.Features.Set<IHttpResponseFeature>(respuesta);
        contexto.Request.Path = ruta;
        contexto.SetEndpoint(new Endpoint(_ => Task.CompletedTask, paginaSsr
            ? new EndpointMetadataCollection(new Microsoft.AspNetCore.Components.Endpoints.ComponentTypeMetadata(typeof(PaginaDePrueba)))
            : EndpointMetadataCollection.Empty, ruta));
        contexto.User = autenticado
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, usuario)], "Prueba"))
            : new ClaimsPrincipal(new ClaimsIdentity());

        var middleware = new ServerTimingCosteCargaMiddleware(
            ctx =>
            {
                gastar?.Invoke(ctx.RequestServices.GetRequiredService<CosteCarga>());
                ctx.Response.ContentType = tipo;
                if (autenticarDespues)
                    ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, usuario)], "Prueba"));
                return Task.CompletedTask;
            },
            Options.Create(new OpcionesServerTiming { Habilitado = habilitado }));

        await middleware.InvokeAsync(contexto);
        return (contexto, respuesta);
    }

    private sealed class PaginaDePrueba : Microsoft.AspNetCore.Components.ComponentBase;

    private sealed class RespuestaQueCapturaOnStarting : HttpResponseFeature
    {
        public List<(Func<object, Task> Callback, object Estado)> Callbacks { get; } = [];

        public override void OnStarting(Func<object, Task> callback, object state) => Callbacks.Add((callback, state));

        public void DispararOnStarting()
        {
            foreach (var (callback, estado) in Callbacks)
                callback(estado).GetAwaiter().GetResult();
        }
    }
}

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

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/css")]
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
                .Should().StartWith("db;dur=1.2").Or.StartWith("db;dur=1.3");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = cultura;
        }
    }

    private static async Task<(HttpContext Contexto, RespuestaQueCapturaOnStarting Respuesta)> Ejecutar(
        bool habilitado, bool autenticado, string? tipo, Action<CosteCarga>? gastar = null,
        string ruta = "/", string usuario = "u")
    {
        var servicios = new ServiceCollection();
        servicios.AddScoped<CosteCarga>();
        var proveedor = servicios.BuildServiceProvider().CreateScope().ServiceProvider;

        var respuesta = new RespuestaQueCapturaOnStarting();
        var contexto = new DefaultHttpContext { RequestServices = proveedor };
        contexto.Features.Set<IHttpResponseFeature>(respuesta);
        contexto.Request.Path = ruta;
        contexto.User = autenticado
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, usuario)], "Prueba"))
            : new ClaimsPrincipal(new ClaimsIdentity());

        var middleware = new ServerTimingCosteCargaMiddleware(
            ctx =>
            {
                gastar?.Invoke(ctx.RequestServices.GetRequiredService<CosteCarga>());
                ctx.Response.ContentType = tipo;
                return Task.CompletedTask;
            },
            Options.Create(new OpcionesServerTiming { Habilitado = habilitado }));

        await middleware.InvokeAsync(contexto);
        return (contexto, respuesta);
    }

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

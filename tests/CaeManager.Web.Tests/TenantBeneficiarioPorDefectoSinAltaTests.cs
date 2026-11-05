using System.Reflection;
using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Net.Http.Headers;

namespace CaeManager.Web.Tests;

/// <summary>
/// Una sesión iniciada que abre el enlace de verificación del alta autónoma no recibe la redirección del Tenant
/// beneficiario por defecto: esa redirección copia la consulta entera en <c>Location</c>, y en <c>/alta/verificar</c> la
/// consulta lleva el token. El control sobre <c>/</c> prueba que, con la misma sesión, el middleware sí evaluaría.
/// </summary>
public class TenantBeneficiarioPorDefectoSinAltaTests
{
    public class Delator : DispatchProxy
    {
        public int Llamadas;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Interlocked.Increment(ref Llamadas);
            throw new InvalidOperationException($"No debía consultarse: {targetMethod?.Name}");
        }
    }

    private static DefaultHttpContext NavegacionConSesion(string ruta, string consulta)
    {
        var contexto = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "usuario-1")], "Cookies")),
        };
        contexto.Request.Method = HttpMethods.Get;
        contexto.Request.Path = ruta;
        contexto.Request.QueryString = new QueryString(consulta);
        contexto.Request.Headers.Accept = "text/html";
        return contexto;
    }

    private static async Task<(DefaultHttpContext Contexto, bool SiguienteLlamado, int Consultas)> InvocarAsync(string ruta, string consulta)
    {
        var contexto = NavegacionConSesion(ruta, consulta);
        var siguienteLlamado = false;
        var middleware = new TenantBeneficiarioPorDefectoMiddleware(_ => { siguienteLlamado = true; return Task.CompletedTask; });

        var mediator = DispatchProxy.Create<IMediator, Delator>();
        var usuario = DispatchProxy.Create<ICurrentUserService, Delator>();
        var operaciones = DispatchProxy.Create<IOperacionesQueryContext, Delator>();
        var protector = DispatchProxy.Create<IDataProtectionProvider, Delator>();

        await middleware.InvokeAsync(contexto, mediator, usuario, operaciones, protector,
            NullLogger<TenantBeneficiarioPorDefectoMiddleware>.Instance);

        var consultas = new object[] { mediator, usuario, operaciones, protector }.Sum(p => ((Delator)p).Llamadas);
        return (contexto, siguienteLlamado, consultas);
    }

    [Theory]
    [InlineData("/alta/verificar")]
    [InlineData("/ALTA/verificar")]
    [InlineData("/alta")]
    public void El_alta_no_se_evalua_y_la_misma_sesion_en_otra_pagina_si(string ruta)
    {
        TenantBeneficiarioPorDefectoMiddleware.DebeEvaluarse(NavegacionConSesion(ruta, "?token=secreto")).Should().BeFalse();
        TenantBeneficiarioPorDefectoMiddleware.DebeEvaluarse(NavegacionConSesion("/", "?token=secreto")).Should().BeTrue(
            "el control: con la misma sesión, fuera del alta, el middleware evalúa y podría redirigir");
    }

    [Fact]
    public async Task Una_sesion_iniciada_que_abre_el_enlace_de_verificacion_no_recibe_redireccion_con_el_token()
    {
        var (contexto, siguienteLlamado, consultas) = await InvocarAsync("/alta/verificar", "?token=secreto-de-un-solo-uso");

        siguienteLlamado.Should().BeTrue("la página de verificación se sirve tal cual");
        consultas.Should().Be(0, "ni siquiera busca un Tenant beneficiario por defecto");
        contexto.Response.Headers.ContainsKey(HeaderNames.Location).Should().BeFalse();
        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Theory]
    [InlineData("/alta/verificar", "no-referrer")]
    [InlineData("/ALTA/verificar", "no-referrer")]
    [InlineData("/alta", "no-referrer")]
    [InlineData("/altas", "strict-origin-when-cross-origin")]
    [InlineData("/", "strict-origin-when-cross-origin")]
    public void La_pagina_del_alta_no_manda_Referer_ni_siquiera_al_mismo_origen(string ruta, string politica)
    {
        // Con strict-origin-when-cross-origin, las hojas de estilo y el POST de /alta/verificar?token=… llevarían la
        // URL entera, con el token, en el Referer.
        CabecerasSeguridadExtensions.PoliticaReferer(NavegacionConSesion(ruta, "?token=secreto").Request).Should().Be(politica);
    }

    [Fact]
    public async Task El_control_fuera_del_alta_si_busca_el_Tenant_por_defecto()
    {
        var (_, siguienteLlamado, consultas) = await InvocarAsync("/", "?token=secreto");

        consultas.Should().BeGreaterThan(0, "con la misma sesión, fuera del alta, el middleware sí consulta");
        siguienteLlamado.Should().BeTrue("un fallo de la consulta deja la petición en el Tenant de origen");
    }
}

using Bunit;
using CaeManager.Application.AltaAutonoma;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Web.Features.AltaAutonoma.Antiabuso;
using CaeManager.Web.Features.AltaAutonoma.Recursos;
using CaeManager.Web.Features.AltaAutonoma.Registro;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.AltaAutonoma;

/// <summary>
/// Paso «Empresa» del alta autónoma (A4) en la Web: bandera, credencial de continuación en cookie, límite por CIF y el
/// mensaje genérico cuando el CIF ya tiene Tenant propietario (RQ-13, RQ-28). La transición la decide la base; aquí la
/// sustituye un falso.
/// </summary>
public class OrganizacionAltaTests : BunitContext
{
    private const string RazonSocial = "Andamios del Sur SL";
    private const string Cif = "B12345674";

    private readonly RegistroYVerificacionAltaTests.AccesoQueCuenta _acceso = new();
    private readonly DefaultHttpContext _http = new();

    public OrganizacionAltaTests()
    {
        Services.AddLocalization();
        Services.AddSingleton<IAccesoSolicitudesAlta>(_acceso);
        Services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));
        Services.AddSingleton<ServicioDatosOrganizacionAlta>();
        _http.Request.IsHttps = true;
    }

    [Fact]
    public void Con_la_bandera_apagada_la_pagina_no_pinta_nada()
    {
        Configurar(habilitada: false, consultasPorCif: 3);
        ConCookie(ServicioRegistroAlta.NuevoToken());

        var corte = Pintar();

        corte.FindAll("form").Should().BeEmpty();
        corte.Markup.Should().NotContain(Texto("OrganizacionTitulo"));
    }

    [Fact]
    public void Sin_credencial_de_continuacion_no_hay_formulario_sino_volver_a_empezar()
    {
        Configurar(habilitada: true, consultasPorCif: 3);

        var corte = Pintar();

        corte.FindAll("form").Should().BeEmpty();
        corte.Markup.Should().Contain(Texto("ContinuacionNoValidaTitulo"));
        corte.Find("a[href='/alta']").Should().NotBeNull();
    }

    [Fact]
    public void Con_credencial_pinta_el_formulario_en_el_paso_Empresa()
    {
        Configurar(habilitada: true, consultasPorCif: 3);
        ConCookie(ServicioRegistroAlta.NuevoToken());

        var corte = Pintar();

        corte.Find("form[method=post]").Should().NotBeNull();
        corte.Find("li[aria-current=step]").TextContent.Should().Contain(Texto("PasoEmpresa"));
        corte.FindAll("#alta-tramo option").Should().HaveCount(Enum.GetValues<TramoTrabajadoresActivos>().Length + 1);
    }

    [Fact]
    public void Un_CIF_con_Tenant_propietario_da_un_mensaje_generico_sin_nada_del_Tenant_y_borra_la_cookie()
    {
        Configurar(habilitada: true, consultasPorCif: 3);
        ConCookie(ServicioRegistroAlta.NuevoToken());
        _acceso.ResultadoDatos = ResultadoDatosOrganizacionAlta.DerivadaAAcceso;

        var corte = Pintar();
        Rellenar(corte);
        corte.Find("form").Submit();

        _acceso.Completados.Should().ContainSingle().Which.CifNif.Should().Be(Cif);
        corte.FindAll("form").Should().BeEmpty();
        corte.Markup.Should().Contain(Texto("DerivadaTitulo")).And.Contain(Texto("DerivadaExplica"));
        // Lo único que la página sabe es el resultado: no puede pintar el Tenant, y tampoco repite lo que se envió.
        corte.Markup.Should().NotContain(Cif).And.NotContain(RazonSocial);
        _http.Response.Headers.SetCookie.ToString().Should().Contain(CookieContinuacionAlta.Nombre + "=;")
            .And.Contain("expires=Thu, 01 Jan 1970", "la cookie del credencial se borra en un estado final");
    }

    [Fact]
    public void Un_CIF_libre_guarda_los_datos_y_conserva_la_cookie_para_el_paso_siguiente()
    {
        Configurar(habilitada: true, consultasPorCif: 3);
        ConCookie(ServicioRegistroAlta.NuevoToken());

        var corte = Pintar();
        Rellenar(corte);
        corte.Find("form").Submit();

        var datos = _acceso.Completados.Should().ContainSingle().Subject;
        datos.Tramo.Should().Be(TramoTrabajadoresActivos.De11A40);
        datos.RazonSocial.Should().Be(RazonSocial);
        corte.Markup.Should().Contain(Texto("DatosGuardadosTitulo"));
        _http.Response.Headers.SetCookie.ToString().Should().NotContain(CookieContinuacionAlta.Nombre);
    }

    [Fact]
    public void Un_CIF_no_valido_no_llega_a_la_base_ni_gasta_cupo()
    {
        Configurar(habilitada: true, consultasPorCif: 1);
        ConCookie(ServicioRegistroAlta.NuevoToken());

        var corte = Pintar();
        Rellenar(corte, cif: "B12345670");
        corte.Find("form").Submit();

        _acceso.Completados.Should().BeEmpty();
        corte.Find("[role=alert]").TextContent.Should().Be(Texto("ErrorCifNif"));

        var otro = Pintar();
        Rellenar(otro);
        otro.Find("form").Submit();
        _acceso.Completados.Should().ContainSingle("el envío con el CIF mal escrito no consumió la consulta del día");
    }

    [Fact]
    public void Un_tramo_que_no_es_de_la_lista_se_rechaza_aunque_sea_un_numero_del_enum()
    {
        Configurar(habilitada: true, consultasPorCif: 3);
        ConCookie(ServicioRegistroAlta.NuevoToken());

        var corte = Pintar();
        Rellenar(corte);
        corte.Find("#alta-tramo").Change("0");
        corte.Find("form").Submit();

        _acceso.Completados.Should().BeEmpty();
        corte.Find("[role=alert]").TextContent.Should().Be(Texto("ErrorTramo"));
    }

    [Fact]
    public void Un_NIF_de_persona_fisica_exige_la_declaracion_de_actividad_profesional()
    {
        Configurar(habilitada: true, consultasPorCif: 3);
        ConCookie(ServicioRegistroAlta.NuevoToken());

        var corte = Pintar();
        Rellenar(corte, cif: "12345678Z");
        corte.Find("form").Submit();

        _acceso.Completados.Should().BeEmpty();
        corte.Find("[role=alert]").TextContent.Should().Be(Texto("ErrorDeclaraActividad"));
    }

    [Fact]
    public void El_mismo_CIF_no_se_consulta_mas_veces_al_dia_que_el_cupo_aunque_cambie_su_escritura()
    {
        Configurar(habilitada: true, consultasPorCif: 2);
        ConCookie(ServicioRegistroAlta.NuevoToken());

        foreach (var escritura in new[] { Cif, "b-12345674", "B 12345674" })
        {
            var corte = Pintar();
            Rellenar(corte, cif: escritura);
            corte.Find("form").Submit();
        }

        _acceso.Completados.Should().HaveCount(2);
    }

    [Fact]
    public void Cuando_el_cupo_del_CIF_se_agota_se_dice_sin_preguntar_a_la_base()
    {
        Configurar(habilitada: true, consultasPorCif: 1);
        ConCookie(ServicioRegistroAlta.NuevoToken());

        var primero = Pintar();
        Rellenar(primero);
        primero.Find("form").Submit();
        var segundo = Pintar();
        Rellenar(segundo);
        segundo.Find("form").Submit();

        _acceso.Completados.Should().ContainSingle();
        segundo.Find("[role=alert]").TextContent.Should().Be(Texto("ErrorLimiteCif"));
    }

    private void Configurar(bool habilitada, int consultasPorCif)
    {
        Services.AddSingleton(Options.Create(new AltaAutonomaOptions { Habilitada = habilitada }));
        Services.AddSingleton(new CupoConsultasCifAlta(consultasPorCif, TimeSpan.FromDays(1)));
    }

    private void ConCookie(string credencial) =>
        _http.Request.Headers.Cookie = $"{CookieContinuacionAlta.Nombre}={credencial}";

    private IRenderedComponent<OrganizacionAlta> Pintar() =>
        Render<OrganizacionAlta>(p => p.AddCascadingValue<HttpContext>(_http));

    private static void Rellenar(IRenderedComponent<OrganizacionAlta> corte, string cif = Cif)
    {
        corte.Find("#alta-razon-social").Change(RazonSocial);
        corte.Find("#alta-cif").Change(cif);
        corte.Find("#alta-domicilio").Change("Calle Mayor 1, 41001 Sevilla");
        corte.Find("#alta-tramo").Change(nameof(TramoTrabajadoresActivos.De11A40));
        corte.Find("#alta-declara-representacion").Change(true);
    }

    private string Texto(string clave) =>
        Services.GetRequiredService<IStringLocalizer<TextosAltaAutonoma>>()[clave].Value;
}

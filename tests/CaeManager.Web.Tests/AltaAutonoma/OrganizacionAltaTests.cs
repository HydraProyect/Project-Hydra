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
/// Paso «Empresa» del alta autónoma (A4) en la Web: bandera, credencial de continuación en cookie, alerta sin bloqueo por CIF y el
/// mensaje genérico cuando el CIF ya tiene Tenant propietario (RQ-13, RQ-28). La transición la decide la base; aquí la
/// sustituye un falso.
/// </summary>
public class OrganizacionAltaTests : BunitContext
{
    private const string RazonSocial = "Andamios del Sur SL";
    private const string Cif = "B12345674";

    private readonly RegistroYVerificacionAltaTests.AccesoQueCuenta _acceso = new();
    private readonly DefaultHttpContext _http = new();
    private readonly AlertaConsultasCifAltaTests.RegistroCapturado _registro = new();

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
        Configurar(habilitada: false);
        ConCookie(ServicioRegistroAlta.NuevoToken());

        var corte = Pintar();

        corte.FindAll("form").Should().BeEmpty();
        corte.Markup.Should().NotContain(Texto("OrganizacionTitulo"));
    }

    [Fact]
    public void Sin_credencial_de_continuacion_no_hay_formulario_sino_volver_a_empezar()
    {
        Configurar(habilitada: true);

        var corte = Pintar();

        corte.FindAll("form").Should().BeEmpty();
        corte.Markup.Should().Contain(Texto("ContinuacionNoValidaTitulo"));
        corte.Find("a[href='/alta']").Should().NotBeNull();
    }

    [Fact]
    public void Con_credencial_pinta_el_formulario_en_el_paso_Empresa()
    {
        Configurar(habilitada: true);
        ConCookie(ServicioRegistroAlta.NuevoToken());

        var corte = Pintar();

        corte.Find("form[method=post]").Should().NotBeNull();
        corte.Find("li[aria-current=step]").TextContent.Should().Contain(Texto("PasoEmpresa"));
        corte.FindAll("#alta-tramo option").Should().HaveCount(Enum.GetValues<TramoTrabajadoresActivos>().Length + 1);
    }

    [Fact]
    public void Un_CIF_con_Tenant_propietario_da_un_mensaje_generico_sin_nada_del_Tenant_y_borra_la_cookie()
    {
        Configurar(habilitada: true);
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
        Configurar(habilitada: true);
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
    public void Un_CIF_no_valido_no_llega_a_la_base_ni_cuenta_para_la_alerta()
    {
        Configurar(habilitada: true);
        ConCookie(ServicioRegistroAlta.NuevoToken());

        foreach (var _ in Enumerable.Range(0, AlertaConsultasCifAlta.Umbral + 2))
        {
            var corte = Pintar();
            Rellenar(corte, cif: "B12345670");
            corte.Find("form").Submit();
            corte.Find("[role=alert]").TextContent.Should().Be(Texto("ErrorCifNif"));
        }

        _acceso.Completados.Should().BeEmpty();
        _registro.Entradas.Should().BeEmpty();
    }

    [Fact]
    public void Un_tramo_que_no_es_de_la_lista_se_rechaza_aunque_sea_un_numero_del_enum()
    {
        Configurar(habilitada: true);
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
        Configurar(habilitada: true);
        ConCookie(ServicioRegistroAlta.NuevoToken());

        var corte = Pintar();
        Rellenar(corte, cif: "12345678Z");
        corte.Find("form").Submit();

        _acceso.Completados.Should().BeEmpty();
        corte.Find("[role=alert]").TextContent.Should().Be(Texto("ErrorDeclaraActividad"));
    }

    // ── Revisión de A4: sin cupo por CIF que bloquee; las consultas repetidas solo avisan ──

    /// <summary>
    /// Un contador por CIF compartido entre Solicitudes de alta distintas que bloqueara sería un oráculo de actividad
    /// ajena: quien se registra con su propio credencial recibiría otra respuesta porque otras Solicitudes consultaron ese
    /// CIF. La Solicitud nueva recibe exactamente lo mismo que la primera, por muchas que la precedan.
    /// </summary>
    [Theory]
    [InlineData(ResultadoDatosOrganizacionAlta.DatosCompletos)]
    [InlineData(ResultadoDatosOrganizacionAlta.DerivadaAAcceso)]
    public void La_respuesta_a_una_Solicitud_nueva_no_depende_de_cuantas_otras_consultaron_ese_CIF(ResultadoDatosOrganizacionAlta resultado)
    {
        Configurar(habilitada: true);
        _acceso.ResultadoDatos = resultado;
        var otras = AlertaConsultasCifAlta.Umbral * 4;

        string? primera = null;
        foreach (var escritura in Enumerable.Range(0, otras).Select(i => (i % 3) switch { 0 => Cif, 1 => "b-12345674", _ => "B 12345674" }))
        {
            ConCookie(ServicioRegistroAlta.NuevoToken());
            var otra = Pintar();
            Rellenar(otra, cif: escritura);
            otra.Find("form").Submit();
            primera ??= otra.Markup;
        }

        ConCookie(ServicioRegistroAlta.NuevoToken());
        var nueva = Pintar();
        Rellenar(nueva);
        nueva.Find("form").Submit();

        _acceso.Completados.Should().HaveCount(otras + 1, "cada Solicitud de alta llega a la base, sin cupo por CIF delante");
        nueva.FindAll("[role=alert]").Should().BeEmpty();
        nueva.Markup.Should().Be(primera, "la respuesta no cambia por lo que hicieran otras Solicitudes de alta");
        _registro.Entradas.Should().NotBeEmpty("las consultas repetidas sí se avisan, solo en el registro");
    }

    [Fact]
    public void Las_consultas_repetidas_de_un_mismo_CIF_avisan_en_el_registro_sin_el_CIF_aunque_cambie_su_escritura()
    {
        Configurar(habilitada: true);

        foreach (var escritura in new[] { Cif, "b-12345674", "B 12345674", "b12345674" })
        {
            _registro.Entradas.Should().BeEmpty("hasta pasar el umbral no hay aviso");
            ConCookie(ServicioRegistroAlta.NuevoToken());
            var corte = Pintar();
            Rellenar(corte, cif: escritura);
            corte.Find("form").Submit();
        }

        var aviso = _registro.Entradas.Should().ContainSingle().Subject;
        aviso.Evento.Should().Be(AlertaConsultasCifAlta.EventoConsultasRepetidas);
        aviso.Texto.Should().NotContain("12345674");
    }

    [Fact]
    public void Un_credencial_que_la_base_no_acepta_no_cuenta_para_la_alerta()
    {
        Configurar(habilitada: true);
        ConCookie(ServicioRegistroAlta.NuevoToken());
        _acceso.ResultadoDatos = ResultadoDatosOrganizacionAlta.NoValido;

        foreach (var _ in Enumerable.Range(0, AlertaConsultasCifAlta.Umbral + 2))
        {
            var ajeno = Pintar();
            Rellenar(ajeno);
            ajeno.Find("form").Submit();
        }

        _acceso.Completados.Should().HaveCount(AlertaConsultasCifAlta.Umbral + 2);
        _registro.Entradas.Should().BeEmpty("sin transición en la base no hubo consulta con resultado");
    }

    // ── M2: recargar o reenviar tras DatosCompletos no transiciona ni borra la cookie ──

    [Fact]
    public void Recargar_tras_DatosCompletos_muestra_lo_guardado_sin_transicionar_ni_borrar_la_cookie()
    {
        Configurar(habilitada: true);
        ConCookie(ServicioRegistroAlta.NuevoToken());
        _acceso.Continuacion = EstadoContinuacionAlta.DatosCompletos;

        var corte = Pintar();

        corte.FindAll("form").Should().BeEmpty();
        corte.Markup.Should().Contain(Texto("DatosGuardadosTitulo"));
        _acceso.Completados.Should().BeEmpty();
        _http.Response.Headers.SetCookie.ToString().Should().NotContain(CookieContinuacionAlta.Nombre, "A5 necesita la cookie");
    }

    [Fact]
    public void Reenviar_cuando_la_base_ya_tiene_los_datos_muestra_lo_guardado_y_conserva_la_cookie()
    {
        Configurar(habilitada: true);
        ConCookie(ServicioRegistroAlta.NuevoToken());
        _acceso.ResultadoDatos = ResultadoDatosOrganizacionAlta.NoValido;
        _acceso.ContinuacionTrasCompletar = EstadoContinuacionAlta.DatosCompletos;

        var corte = Pintar();
        Rellenar(corte);
        corte.Find("form").Submit();

        corte.Markup.Should().Contain(Texto("DatosGuardadosTitulo")).And.NotContain(Texto("ContinuacionNoValidaTitulo"));
        _http.Response.Headers.SetCookie.ToString().Should().NotContain(CookieContinuacionAlta.Nombre, "A5 necesita la cookie");
    }

    [Fact]
    public void Un_credencial_caducado_pide_volver_a_empezar_y_borra_la_cookie()
    {
        Configurar(habilitada: true);
        ConCookie(ServicioRegistroAlta.NuevoToken());
        _acceso.Continuacion = EstadoContinuacionAlta.Caducada;

        var corte = Pintar();

        corte.FindAll("form").Should().BeEmpty();
        corte.Markup.Should().Contain(Texto("ContinuacionNoValidaTitulo"));
        _http.Response.Headers.SetCookie.ToString().Should().Contain(CookieContinuacionAlta.Nombre + "=;")
            .And.Contain("path=/alta");
    }

    [Fact]
    public void Un_credencial_que_la_base_no_reconoce_pide_volver_a_empezar_sin_borrar_la_cookie()
    {
        Configurar(habilitada: true);
        ConCookie(ServicioRegistroAlta.NuevoToken());
        _acceso.Continuacion = EstadoContinuacionAlta.NoValido;

        var corte = Pintar();

        corte.FindAll("form").Should().BeEmpty();
        corte.Markup.Should().Contain(Texto("ContinuacionNoValidaTitulo"));
        _http.Response.Headers.SetCookie.ToString().Should().NotContain(CookieContinuacionAlta.Nombre);
    }

    // ── M3: la pantalla de CIF ocupado no dice que alguien gestiona la organización ──

    [Theory]
    [InlineData("es-ES", "Soporte TALVEG")]
    [InlineData("ca-ES", "Suport TALVEG")]
    public void La_pantalla_de_CIF_ocupado_ofrece_escribir_a_Soporte_sin_mencionar_a_quien_la_gestiona(string cultura, string soporte)
    {
        var titulo = EnCultura(cultura, "DerivadaTitulo");
        var explica = EnCultura(cultura, "DerivadaExplica");

        foreach (var texto in new[] { titulo, explica })
            texto.Should().NotContainAny("gestion", "gestor", "gestora", "Operador", "contactará", "contactarà");
        explica.Should().Contain(soporte);
        titulo.Should().Contain("TALVEG");
    }

    private string EnCultura(string cultura, string clave)
    {
        var anterior = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(cultura);
            return Services.GetRequiredService<IStringLocalizer<TextosAltaAutonoma>>()[clave].Value;
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = anterior;
        }
    }

    private void Configurar(bool habilitada)
    {
        Services.AddSingleton(Options.Create(new AltaAutonomaOptions { Habilitada = habilitada }));
        Services.AddSingleton(new AlertaConsultasCifAlta(_registro));
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

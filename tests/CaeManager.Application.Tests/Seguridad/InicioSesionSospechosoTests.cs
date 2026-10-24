using System.Net;
using CaeManager.Application.Seguridad.InicioSesion;
using CaeManager.Domain.Seguridad;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Seguridad;

public class DetectorInicioSesionSospechosoTests
{
    private static readonly OpcionesDeteccionInicioSesion Opciones = new();
    private static readonly DateTime Ahora = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    // Coordenadas aproximadas (redondeadas a 0,1°, como se guardan).
    private const double MadridLat = 40.4, MadridLon = -3.7;
    private const double SevillaLat = 37.4, SevillaLon = -6.0;
    private const double LisboaLat = 38.7, LisboaLon = -9.1;
    private const double BucarestLat = 44.4, BucarestLon = 26.1;

    private static InicioSesionPrevio En(DateTime momento, string? pais, double? lat = null, double? lon = null, string? huella = "h1")
        => new(momento, pais, lat, lon, huella);

    [Fact]
    public void Un_inicio_desde_el_pais_y_dispositivo_de_siempre_no_es_sospechoso()
    {
        var historial = new[] { En(Ahora.AddDays(-1), "ES", MadridLat, MadridLon), En(Ahora.AddDays(-5), "ES", MadridLat, MadridLon) };

        DetectorInicioSesionSospechoso.Evaluar(En(Ahora, "ES", SevillaLat, SevillaLon), historial, Opciones)
            .Should().Be(SenalesInicioSesion.Ninguna);
    }

    [Fact]
    public void Un_pais_no_visto_en_los_ultimos_90_dias_es_pais_nuevo()
    {
        var historial = new[] { En(Ahora.AddDays(-2), "ES"), En(Ahora.AddDays(-40), "ES") };

        DetectorInicioSesionSospechoso.Evaluar(En(Ahora, "RO"), historial, Opciones)
            .Should().HaveFlag(SenalesInicioSesion.PaisNuevo);
    }

    [Fact]
    public void Un_pais_visto_dentro_de_los_90_dias_no_es_nuevo()
    {
        var historial = new[] { En(Ahora.AddDays(-2), "ES"), En(Ahora.AddDays(-89), "FR") };

        DetectorInicioSesionSospechoso.Evaluar(En(Ahora, "FR"), historial, Opciones)
            .Should().NotHaveFlag(SenalesInicioSesion.PaisNuevo);
    }

    [Fact]
    public void Un_pais_visto_solo_hace_mas_de_90_dias_vuelve_a_ser_nuevo()
    {
        var historial = new[] { En(Ahora.AddDays(-2), "ES"), En(Ahora.AddDays(-91), "FR") };

        DetectorInicioSesionSospechoso.Evaluar(En(Ahora, "FR"), historial, Opciones)
            .Should().HaveFlag(SenalesInicioSesion.PaisNuevo);
    }

    [Fact]
    public void Sin_historial_el_primer_inicio_fuera_del_pais_habitual_es_pais_nuevo_y_dentro_no()
    {
        DetectorInicioSesionSospechoso.Evaluar(En(Ahora, "MA"), [], Opciones).Should().HaveFlag(SenalesInicioSesion.PaisNuevo);
        DetectorInicioSesionSospechoso.Evaluar(En(Ahora, "ES"), [], Opciones).Should().Be(SenalesInicioSesion.Ninguna);
    }

    [Fact]
    public void Sin_historial_reciente_la_referencia_es_el_pais_mas_frecuente_del_historial()
    {
        var historial = new[] { En(Ahora.AddDays(-200), "PT"), En(Ahora.AddDays(-210), "PT"), En(Ahora.AddDays(-300), "ES") };

        DetectorInicioSesionSospechoso.Evaluar(En(Ahora, "PT"), historial, Opciones).Should().NotHaveFlag(SenalesInicioSesion.PaisNuevo);
        DetectorInicioSesionSospechoso.Evaluar(En(Ahora, "ES"), historial, Opciones).Should().HaveFlag(SenalesInicioSesion.PaisNuevo);
    }

    [Fact]
    public void Un_pais_desconocido_nunca_es_nuevo()
    {
        DetectorInicioSesionSospechoso.Evaluar(En(Ahora, null), [En(Ahora.AddDays(-1), "ES")], Opciones)
            .Should().NotHaveFlag(SenalesInicioSesion.PaisNuevo);
    }

    [Fact]
    public void Una_huella_no_vista_en_ningun_inicio_anterior_es_dispositivo_nuevo()
    {
        var historial = new[] { En(Ahora.AddDays(-1), "ES", huella: "h1"), En(Ahora.AddDays(-3), "ES", huella: "h2") };

        DetectorInicioSesionSospechoso.Evaluar(En(Ahora, "ES", huella: "h3"), historial, Opciones)
            .Should().Be(SenalesInicioSesion.DispositivoNuevo);
        DetectorInicioSesionSospechoso.Evaluar(En(Ahora, "ES", huella: "h2"), historial, Opciones)
            .Should().Be(SenalesInicioSesion.Ninguna);
    }

    [Fact]
    public void El_primer_inicio_de_una_cuenta_no_es_dispositivo_nuevo()
    {
        DetectorInicioSesionSospechoso.Evaluar(En(Ahora, "ES", huella: "h9"), [], Opciones)
            .Should().NotHaveFlag(SenalesInicioSesion.DispositivoNuevo);
    }

    [Fact]
    public void Madrid_y_Bucarest_en_una_hora_es_viaje_imposible()
    {
        var historial = new[] { En(Ahora.AddHours(-1), "ES", MadridLat, MadridLon) };

        DetectorInicioSesionSospechoso.Evaluar(En(Ahora, "RO", BucarestLat, BucarestLon), historial, Opciones)
            .Should().HaveFlag(SenalesInicioSesion.ViajeImposible);
    }

    [Fact]
    public void Madrid_y_Bucarest_con_un_dia_de_por_medio_no_es_viaje_imposible()
    {
        var historial = new[] { En(Ahora.AddHours(-24), "ES", MadridLat, MadridLon) };

        DetectorInicioSesionSospechoso.Evaluar(En(Ahora, "RO", BucarestLat, BucarestLon), historial, Opciones)
            .Should().NotHaveFlag(SenalesInicioSesion.ViajeImposible);
    }

    [Fact]
    public void Paises_vecinos_por_debajo_de_la_distancia_minima_no_son_viaje_imposible()
    {
        // Sevilla–Lisboa: unos 310 km, por debajo de los 500 km mínimos aunque sea en 10 minutos.
        var historial = new[] { En(Ahora.AddMinutes(-10), "ES", SevillaLat, SevillaLon) };

        DetectorInicioSesionSospechoso.Evaluar(En(Ahora, "PT", LisboaLat, LisboaLon), historial, Opciones)
            .Should().NotHaveFlag(SenalesInicioSesion.ViajeImposible);
    }

    [Fact]
    public void Sin_coordenadas_no_se_evalua_el_viaje_imposible()
    {
        var historial = new[] { En(Ahora.AddMinutes(-5), "ES") };

        DetectorInicioSesionSospechoso.Evaluar(En(Ahora, "RO"), historial, Opciones)
            .Should().NotHaveFlag(SenalesInicioSesion.ViajeImposible);
    }

    [Fact]
    public void La_distancia_haversine_es_la_esperada()
        => DetectorInicioSesionSospechoso.DistanciaKm(MadridLat, MadridLon, BucarestLat, BucarestLon).Should().BeInRange(2400, 2550);
}

public class IpClienteDeConfianzaTests
{
    private static readonly IReadOnlyList<IPNetwork> RedesPrivadas =
        IpClienteDeConfianza.LeerRedes(["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"]);

    private static readonly IPAddress Caddy = IPAddress.Parse("172.18.0.5");

    [Fact]
    public void Detras_del_proxy_de_confianza_la_ip_es_la_que_anadio_el_proxy()
        => IpClienteDeConfianza.Resolver(Caddy, "203.0.113.7", RedesPrivadas).Should().Be(IPAddress.Parse("203.0.113.7"));

    [Fact]
    public void Una_ip_falsificada_por_el_cliente_queda_a_la_izquierda_y_no_se_lee()
        // El cliente envía «X-Forwarded-For: 8.8.8.8»; el proxy añade a la derecha la IP real.
        => IpClienteDeConfianza.Resolver(Caddy, "8.8.8.8, 203.0.113.7", RedesPrivadas).Should().Be(IPAddress.Parse("203.0.113.7"));

    [Fact]
    public void Si_el_par_no_es_de_confianza_la_cabecera_se_ignora()
        // Conexión directa desde Internet con una cabecera inventada.
        => IpClienteDeConfianza.Resolver(IPAddress.Parse("198.51.100.20"), "8.8.8.8", RedesPrivadas)
            .Should().Be(IPAddress.Parse("198.51.100.20"));

    [Fact]
    public void Sin_proxies_configurados_nunca_se_cree_la_cabecera()
        => IpClienteDeConfianza.Resolver(Caddy, "203.0.113.7", []).Should().Be(Caddy);

    [Fact]
    public void Una_falsificacion_con_ip_privada_no_hace_saltar_al_cliente()
        // El cliente escribe una IP privada (de confianza) para que se siga leyendo a su izquierda:
        // la entrada del proxy, la más a la derecha, ya es pública y corta el recorrido.
        => IpClienteDeConfianza.Resolver(Caddy, "1.1.1.1, 10.0.0.9, 203.0.113.7", RedesPrivadas)
            .Should().Be(IPAddress.Parse("203.0.113.7"));

    [Fact]
    public void Una_entrada_ilegible_corta_el_recorrido()
        => IpClienteDeConfianza.Resolver(Caddy, "203.0.113.7, basura", RedesPrivadas).Should().Be(Caddy);

    [Theory]
    [InlineData("203.0.113.7:51234", "203.0.113.7")]
    [InlineData("[2001:db8::1]:443", "2001:db8::1")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    public void Acepta_puerto_e_ipv6(string cabecera, string esperada)
        => IpClienteDeConfianza.Resolver(Caddy, cabecera, RedesPrivadas).Should().Be(IPAddress.Parse(esperada));

    [Fact]
    public void Una_ipv4_mapeada_en_ipv6_se_normaliza()
        => IpClienteDeConfianza.Resolver(IPAddress.Parse("::ffff:172.18.0.5"), "203.0.113.7", RedesPrivadas)
            .Should().Be(IPAddress.Parse("203.0.113.7"));

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("203.0.113.7", false)]
    [InlineData("80.58.61.250", false)]
    public void Distingue_ips_no_publicas(string ip, bool noPublica)
        => IpClienteDeConfianza.EsNoPublica(IPAddress.Parse(ip)).Should().Be(noPublica);
}

public class DispositivoDeInicioSesionTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36", "Chrome · Windows")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36 Edg/141.0", "Edge · Windows")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "Safari · iOS")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0", "Firefox · Linux")]
    [InlineData(null, "Navegador desconocido")]
    public void Describe_familia_de_navegador_y_sistema_sin_version(string? userAgent, string esperado)
        => DispositivoDeInicioSesion.Describir(userAgent).Should().Be(esperado);

    [Fact]
    public void La_huella_es_estable_y_no_contiene_el_identificador()
    {
        var id = DispositivoDeInicioSesion.NuevoIdentificador();

        DispositivoDeInicioSesion.EsIdentificadorValido(id).Should().BeTrue();
        DispositivoDeInicioSesion.Huella(id).Should().Be(DispositivoDeInicioSesion.Huella(id)).And.HaveLength(32).And.NotContain(id);
        DispositivoDeInicioSesion.Huella(id).Should().NotBe(DispositivoDeInicioSesion.Huella(DispositivoDeInicioSesion.NuevoIdentificador()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("corto")]
    [InlineData("<script>alert(1)</script>xx")]
    public void Una_cookie_manipulada_no_es_un_identificador_valido(string? valor)
        => DispositivoDeInicioSesion.EsIdentificadorValido(valor).Should().BeFalse();
}

public class RafagaFallosPorIpTests
{
    private static readonly DateTime T0 = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Avisa_una_vez_al_alcanzar_el_umbral_dentro_de_la_ventana()
    {
        var rafagas = new RafagaFallosPorIp(new OpcionesDeteccionInicioSesion { UmbralRafagaFallos = 10, MinutosVentanaRafaga = 15 });

        var avisos = Enumerable.Range(0, 25).Select(i => rafagas.RegistrarFallo("203.0.113.7", T0.AddSeconds(i * 10))).ToList();

        avisos.Count(a => a).Should().Be(1);
        avisos.IndexOf(true).Should().Be(9, "el décimo fallo es el que alcanza el umbral");
    }

    [Fact]
    public void Fallos_espaciados_mas_que_la_ventana_no_forman_rafaga()
    {
        var rafagas = new RafagaFallosPorIp(new OpcionesDeteccionInicioSesion { UmbralRafagaFallos = 10, MinutosVentanaRafaga = 15 });

        Enumerable.Range(0, 30).Select(i => rafagas.RegistrarFallo("203.0.113.7", T0.AddMinutes(i * 2))).Should().NotContain(true);
    }

    [Fact]
    public void Cada_ip_cuenta_por_separado_y_vuelve_a_avisar_en_otra_ventana()
    {
        var rafagas = new RafagaFallosPorIp(new OpcionesDeteccionInicioSesion { UmbralRafagaFallos = 3, MinutosVentanaRafaga = 15 });

        Enumerable.Range(0, 2).Select(_ => rafagas.RegistrarFallo("198.51.100.1", T0)).Should().NotContain(true);
        Enumerable.Range(0, 2).Select(_ => rafagas.RegistrarFallo("198.51.100.2", T0)).Should().NotContain(true);
        rafagas.RegistrarFallo("198.51.100.1", T0).Should().BeTrue();

        var despues = T0.AddMinutes(40);
        Enumerable.Range(0, 3).Select(_ => rafagas.RegistrarFallo("198.51.100.1", despues)).Should().ContainSingle(a => a);
    }

    [Fact]
    public void Los_fallos_contra_cuentas_inexistentes_cuentan_pero_no_gastan_el_aviso()
    {
        var rafagas = new RafagaFallosPorIp(new OpcionesDeteccionInicioSesion { UmbralRafagaFallos = 3, MinutosVentanaRafaga = 15 });

        // Un barrido con correos inventados cruza el umbral sin nadie a quien avisar...
        Enumerable.Range(0, 5).Select(i => rafagas.RegistrarFallo("203.0.113.9", T0.AddSeconds(i), hayAQuienAvisar: false))
            .Should().NotContain(true);

        // ...y el primer intento contra una cuenta real dentro de la ventana avisa.
        rafagas.RegistrarFallo("203.0.113.9", T0.AddSeconds(10)).Should().BeTrue();
        rafagas.RegistrarFallo("203.0.113.9", T0.AddSeconds(11)).Should().BeFalse("ya avisó en esta ventana");
    }

    [Fact]
    public void Con_el_tope_lleno_de_ips_recientes_una_ip_nueva_no_se_vigila()
    {
        var rafagas = new RafagaFallosPorIp(new OpcionesDeteccionInicioSesion { UmbralRafagaFallos = 1, MinutosVentanaRafaga = 15 });
        for (var i = 0; i < RafagaFallosPorIp.MaximoIps; i++)
            rafagas.RegistrarFallo($"10.{i >> 16 & 255}.{i >> 8 & 255}.{i & 255}", T0, hayAQuienAvisar: false);

        rafagas.RegistrarFallo("203.0.113.50", T0.AddSeconds(1)).Should().BeFalse("el tope es duro: la memoria no crece");
        rafagas.RegistrarFallo("10.0.0.1", T0.AddSeconds(1)).Should().BeTrue("las IPs ya vigiladas siguen contando");
        rafagas.RegistrarFallo("203.0.113.50", T0.AddMinutes(20)).Should().BeTrue("pasada la ventana, la poda libera sitio");
    }
}

public class PoliticaPaisesInicioSesionTests
{
    [Fact]
    public void Desactivada_por_defecto_lo_permite_todo()
    {
        var politica = new PoliticaPaisesInicioSesion(activa: false, []);

        politica.Permite("RU").Should().BeTrue();
        politica.Permite(null).Should().BeTrue();
    }

    [Fact]
    public void Activa_solo_permite_su_lista_y_el_pais_desconocido()
    {
        var politica = new PoliticaPaisesInicioSesion(activa: true, ["es", "PT"]);

        politica.PaisesPermitidos.Should().Equal("ES", "PT");
        politica.Permite("ES").Should().BeTrue();
        politica.Permite("pt").Should().BeTrue();
        politica.Permite("FR").Should().BeFalse();
        politica.Permite(null).Should().BeTrue("sin base de geolocalización la aplicación debe funcionar igual");
    }

    [Fact]
    public void No_se_puede_activar_sin_paises_ni_con_codigos_invalidos()
    {
        var sinPaises = () => new PoliticaPaisesInicioSesion(activa: true, []);
        var invalido = () => new PoliticaPaisesInicioSesion(activa: false, ["ESP"]);

        sinPaises.Should().Throw<ArgumentException>();
        invalido.Should().Throw<ArgumentException>();
    }
}

public class RegistroInicioSesionTests
{
    [Fact]
    public void Redondea_las_coordenadas_y_separa_actor_real_de_la_cuenta()
    {
        var usuario = Guid.NewGuid();
        var soporte = Guid.NewGuid();

        var registro = new RegistroInicioSesion(usuario, soporte, DateTime.UtcNow, MetodoInicioSesion.Contrasena,
            ResultadoInicioSesion.Correcto, "203.0.113.7", "ES", "Madrid", 40.41678, -3.70379, "abc", "Chrome · Windows",
            SenalesInicioSesion.Ninguna);

        registro.Latitud.Should().Be(40.4);
        registro.Longitud.Should().Be(-3.7);
        registro.UsuarioId.Should().Be(usuario);
        registro.ActorRealUsuarioId.Should().Be(soporte);
        registro.EsSospechoso.Should().BeFalse();
    }

    [Fact]
    public void Un_actor_real_igual_a_la_cuenta_no_se_guarda_como_actor_separado()
    {
        var usuario = Guid.NewGuid();

        new RegistroInicioSesion(usuario, usuario, DateTime.UtcNow, MetodoInicioSesion.Microsoft, ResultadoInicioSesion.Correcto,
                null, null, null, null, null, null, null, SenalesInicioSesion.Ninguna)
            .ActorRealUsuarioId.Should().BeNull();
    }

    [Fact]
    public void Rechaza_pais_que_no_es_iso_alfa_2()
    {
        var crear = () => new RegistroInicioSesion(Guid.NewGuid(), null, DateTime.UtcNow, MetodoInicioSesion.Contrasena,
            ResultadoInicioSesion.Fallido, null, "Spain", null, null, null, null, null, SenalesInicioSesion.Ninguna);

        crear.Should().Throw<ArgumentException>();
    }
}

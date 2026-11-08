using System.Net;
using CaeManager.Application.Contacto;
using FluentAssertions;

namespace CaeManager.Application.Tests.Contacto;

/// <summary>
/// Validación en servidor y composición del correo del formulario de demo de
/// talveg.es (ASVS 4.0.3 V5.1.3, V5.1.4, V5.2.3, V5.3.1). Lo que valide el
/// navegador no cuenta: estos casos son los que un cliente que no sea
/// <c>contacto.js</c> puede mandar tal cual.
/// </summary>
public class SolicitudDemoValidacionTests
{
    private static SolicitudDemoPeticion Buena() => new()
    {
        Nombre = "  Ana Pérez ",
        Correo = " ana@empresa.es ",
        Telefono = "+34 600 11 22 33",
        Perfil = 1,
        Tramo = 4,
        Intereses = [3, 1],
        Otra = null,
    };

    [Fact]
    public void Una_peticion_buena_sale_normalizada()
    {
        var (solicitud, campos) = ValidadorSolicitudDemo.Validar(Buena());

        campos.Should().BeEmpty();
        solicitud.Should().NotBeNull();
        solicitud!.Nombre.Should().Be("Ana Pérez");
        solicitud.Correo.Should().Be("ana@empresa.es");
        solicitud.Intereses.Should().Equal(1, 3);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" A ")]
    [InlineData("Ana\r\nBcc: x@y.es")]
    [InlineData("Ana‮abc")]
    public void Rechaza_un_nombre_vacio_corto_o_con_controles(string? nombre)
    {
        var peticion = Buena();
        peticion.Nombre = nombre;

        ValidadorSolicitudDemo.Validar(peticion).CamposNoValidos.Should().Equal("nombre");
    }

    [Fact]
    public void Rechaza_un_nombre_demasiado_largo()
    {
        var peticion = Buena();
        peticion.Nombre = new string('a', ValidadorSolicitudDemo.NombreMaximo + 1);

        ValidadorSolicitudDemo.Validar(peticion).CamposNoValidos.Should().Equal("nombre");
    }

    [Theory]
    [InlineData("")]
    [InlineData("ana")]
    [InlineData("ana@empresa")]
    [InlineData("ana@empresa.e")]
    [InlineData("ana @empresa.es")]
    [InlineData("ana@empresa.es\r\nBcc: otro@x.es")]
    [InlineData("\"Ana\" <ana@empresa.es>")]
    [InlineData("ana@empresa.es, otro@x.es")]
    public void Rechaza_un_correo_que_no_es_una_direccion_exacta(string correo)
    {
        var peticion = Buena();
        peticion.Correo = correo;

        ValidadorSolicitudDemo.Validar(peticion).CamposNoValidos.Should().Contain("correo",
            "el correo va a la cabecera Reply-To: solo una dirección exacta, sin nombre visible ni listas");
    }

    [Fact]
    public void Rechaza_un_correo_de_mas_de_254_caracteres()
    {
        var peticion = Buena();
        peticion.Correo = new string('a', 250) + "@x.es";

        ValidadorSolicitudDemo.Validar(peticion).CamposNoValidos.Should().Equal("correo");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void El_telefono_es_opcional(string? telefono)
    {
        var peticion = Buena();
        peticion.Telefono = telefono;

        var (solicitud, campos) = ValidadorSolicitudDemo.Validar(peticion);
        campos.Should().BeEmpty();
        solicitud!.Telefono.Should().BeNull();
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("600-ABC-123")]
    [InlineData("+34 600 11 22 33 44 55 66 77 88 99")]
    [InlineData("600\n112233")]
    [InlineData("600112233\nBcc")]
    public void Rechaza_un_telefono_con_formato_o_longitud_fuera_de_rango(string telefono)
    {
        var peticion = Buena();
        peticion.Telefono = telefono;

        ValidadorSolicitudDemo.Validar(peticion).CamposNoValidos.Should().Equal("telefono");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void Rechaza_un_perfil_fuera_de_la_lista(int perfil)
    {
        var peticion = Buena();
        peticion.Perfil = perfil;

        ValidadorSolicitudDemo.Validar(peticion).CamposNoValidos.Should().Contain("perfil");
    }

    [Theory]
    [InlineData(1, 0, false)]
    [InlineData(1, 8, false)]
    [InlineData(1, 6, true)]
    [InlineData(5, 4, false)]
    [InlineData(5, 6, false)]
    [InlineData(5, 3, true)]
    [InlineData(5, 7, true)]
    public void El_tramo_depende_de_la_escala_del_perfil(int perfil, int tramo, bool valido)
    {
        var peticion = Buena();
        peticion.Perfil = perfil;
        peticion.Tramo = tramo;

        var campos = ValidadorSolicitudDemo.Validar(peticion).CamposNoValidos;
        if (valido)
            campos.Should().BeEmpty();
        else
            campos.Should().Equal("tramo");
    }

    [Fact]
    public void Quien_coordina_contratistas_no_necesita_intereses_y_los_que_lleguen_no_cuentan()
    {
        var peticion = Buena();
        peticion.Perfil = ValidadorSolicitudDemo.PerfilValida;
        peticion.Tramo = 2;
        peticion.Intereses = [99];
        peticion.Otra = "texto de otro paso";

        var (solicitud, campos) = ValidadorSolicitudDemo.Validar(peticion);

        campos.Should().BeEmpty("contacto.js salta el paso de intereses para el perfil 5");
        solicitud!.Intereses.Should().BeEmpty();
        solicitud.Otra.Should().BeNull();
    }

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 7 })]
    [InlineData(new[] { 1, 1 })]
    [InlineData(new[] { 0, 2 })]
    public void Rechaza_intereses_vacios_desconocidos_o_repetidos(int[] intereses)
    {
        var peticion = Buena();
        peticion.Intereses = intereses;

        ValidadorSolicitudDemo.Validar(peticion).CamposNoValidos.Should().Equal("intereses");
    }

    [Fact]
    public void El_texto_de_otra_solo_cuenta_si_se_eligio_otra()
    {
        var peticion = Buena();
        peticion.Otra = "Los accesos a obra";

        ValidadorSolicitudDemo.Validar(peticion).Solicitud!.Otra.Should().BeNull();

        peticion.Intereses = [6];
        ValidadorSolicitudDemo.Validar(peticion).Solicitud!.Otra.Should().Be("Los accesos a obra");
    }

    [Fact]
    public void Rechaza_un_texto_de_otra_demasiado_largo()
    {
        var peticion = Buena();
        peticion.Intereses = [6];
        peticion.Otra = new string('x', ValidadorSolicitudDemo.OtraMaximo + 1);

        ValidadorSolicitudDemo.Validar(peticion).CamposNoValidos.Should().Equal("otra");
    }

    [Fact]
    public void Un_texto_de_otra_que_no_aplica_no_tumba_el_envio_aunque_sea_largo()
    {
        // contacto.js conserva el texto al desmarcar «Otra»: la persona ya no lo ve.
        var peticion = Buena();
        peticion.Intereses = [1];
        peticion.Otra = new string('x', ValidadorSolicitudDemo.OtraMaximo + 1) + "\r\n";

        var (solicitud, campos) = ValidadorSolicitudDemo.Validar(peticion);

        campos.Should().BeEmpty();
        solicitud!.Otra.Should().BeNull();
    }

    [Theory]
    [InlineData("Ana\u2028Bcc: x@y.es")]
    [InlineData("Ana\u2029B")]
    [InlineData("Ana\u200BPérez")]
    public void Rechaza_separadores_de_linea_y_caracteres_de_formato_invisibles(string nombre)
    {
        var peticion = Buena();
        peticion.Nombre = nombre;

        ValidadorSolicitudDemo.Validar(peticion).CamposNoValidos.Should().Equal("nombre");
    }

    [Theory]
    [InlineData("\u0645\u06CC\u200C\u0646\u0627")]
    [InlineData("\u0915\u094D\u200D\u0937")]
    public void Admite_zwnj_y_zwj_que_forman_parte_de_la_escritura_de_un_nombre(string nombre)
    {
        var peticion = Buena();
        peticion.Nombre = nombre;

        ValidadorSolicitudDemo.Validar(peticion).CamposNoValidos.Should().BeEmpty();
    }

    [Theory]
    [InlineData("añá@empresa.es")]
    [InlineData("ana@empresá.es")]
    public void Rechaza_un_correo_no_ascii(string correo)
    {
        var peticion = Buena();
        peticion.Correo = correo;

        ValidadorSolicitudDemo.Validar(peticion).CamposNoValidos.Should().BeEquivalentTo(["correo"],
            "va a la cabecera Reply-To y una dirección no ASCII exige SMTPUTF8 al servidor de salida");
    }

    [Theory]
    [InlineData("91 123 45 67 / 600 11 22 33")]
    [InlineData("+34 (600) 11.22.33")]
    public void Admite_los_separadores_habituales_del_telefono(string telefono)
    {
        var peticion = Buena();
        peticion.Telefono = telefono;

        ValidadorSolicitudDemo.Validar(peticion).CamposNoValidos.Should().BeEmpty();
    }

    [Fact]
    public void Devuelve_todos_los_campos_que_fallan_a_la_vez()
    {
        var peticion = new SolicitudDemoPeticion();

        ValidadorSolicitudDemo.Validar(peticion).CamposNoValidos
            .Should().BeEquivalentTo(["nombre", "correo", "perfil", "tramo", "intereses"]);
    }

    [Fact]
    public void El_correo_codifica_para_html_todo_lo_que_viene_del_navegador()
    {
        var peticion = Buena();
        peticion.Nombre = "<script>alert(1)</script>";
        peticion.Intereses = [6];
        peticion.Otra = "<img src=x onerror=alert(1)> & \"comillas\"";
        var solicitud = ValidadorSolicitudDemo.Validar(peticion).Solicitud!;

        var cuerpo = CorreoSolicitudDemo.CuerpoHtml(solicitud);

        cuerpo.Should().NotContain("<script>").And.NotContain("<img");
        cuerpo.Should().Contain(WebUtility.HtmlEncode("<script>alert(1)</script>"));
        cuerpo.Should().Contain(WebUtility.HtmlEncode(peticion.Otra));
    }

    [Fact]
    public void El_asunto_no_lleva_nada_escrito_por_quien_envia()
    {
        var peticion = Buena();
        peticion.Nombre = "Nombre Que No Debe Ir";
        var solicitud = ValidadorSolicitudDemo.Validar(peticion).Solicitud!;

        CorreoSolicitudDemo.Asunto(solicitud).Should()
            .Be("Solicitud de demo desde talveg.es: Empresa contratista");
    }

    [Fact]
    public void El_correo_pone_los_textos_de_la_tabla_del_servidor()
    {
        var solicitud = ValidadorSolicitudDemo.Validar(Buena()).Solicitud!;

        var cuerpo = CorreoSolicitudDemo.CuerpoHtml(solicitud);

        cuerpo.Should().Contain("81 a 150")
            .And.Contain(WebUtility.HtmlEncode("Pasar las auditorías, Lo que me piden mis Clientes"));
    }
}

using CaeManager.Domain.Configuracion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Configuracion;

public class OrdenCajasFichaTests
{
    private static readonly DateTime Ahora = new(2026, 10, 9, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Se_crea_con_los_datos_dados_y_conserva_el_orden()
    {
        var usuarioId = Guid.NewGuid();

        var orden = new OrdenCajasFicha(usuarioId, "Empresa", ["contacto", "datos-fiscales", "notas"], Ahora);

        orden.UsuarioId.Should().Be(usuarioId);
        orden.TipoFicha.Should().Be("Empresa");
        orden.Claves.Should().Equal("contacto", "datos-fiscales", "notas");
        orden.ActualizadoEnUtc.Should().Be(Ahora);
    }

    [Fact]
    public void No_se_puede_crear_sin_usuario()
    {
        var accion = () => new OrdenCajasFicha(Guid.Empty, "Empresa", ["contacto"], Ahora);

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void No_se_puede_crear_sin_tipo_de_ficha(string tipoFicha)
    {
        var accion = () => new OrdenCajasFicha(Guid.NewGuid(), tipoFicha, ["contacto"], Ahora);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reordenar_sustituye_la_lista_entera_y_mueve_la_fecha()
    {
        var orden = new OrdenCajasFicha(Guid.NewGuid(), "Empresa", ["contacto", "notas"], Ahora);

        orden.Reordenar(["notas", "contacto", "datos-fiscales"], Ahora.AddHours(1));

        orden.Claves.Should().Equal("notas", "contacto", "datos-fiscales");
        orden.ActualizadoEnUtc.Should().Be(Ahora.AddHours(1));
    }

    [Fact]
    public void La_lista_guardada_no_comparte_instancia_con_la_recibida()
    {
        var recibida = new List<string> { "contacto", "notas" };
        var orden = new OrdenCajasFicha(Guid.NewGuid(), "Empresa", recibida, Ahora);

        recibida.Reverse();

        orden.Claves.Should().Equal("contacto", "notas");
    }

    [Fact]
    public void Una_lista_con_buena_forma_no_da_error()
    {
        OrdenCajasFicha.ErrorDeForma(["contacto", "datos-fiscales", "caja2"]).Should().BeNull();
    }

    [Fact]
    public void Una_lista_vacia_no_se_guarda_restablecer_es_borrar_la_fila()
    {
        OrdenCajasFicha.ErrorDeForma([]).Should().NotBeNull();
        OrdenCajasFicha.ErrorDeForma(null).Should().NotBeNull();

        var accion = () => new OrdenCajasFicha(Guid.NewGuid(), "Empresa", [], Ahora);
        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("Contacto")]
    [InlineData("datos fiscales")]
    [InlineData("datos_fiscales")]
    [InlineData("-contacto")]
    [InlineData("contacto-")]
    [InlineData("datos--fiscales")]
    [InlineData("año")]
    [InlineData("")]
    [InlineData("notas\n")]
    [InlineData(null)]
    public void Una_clave_que_no_va_en_minusculas_con_guiones_se_rechaza(string? clave)
    {
        OrdenCajasFicha.ErrorDeForma(["contacto", clave!]).Should().Contain("formato");
    }

    [Fact]
    public void Una_clave_mas_larga_que_el_tope_se_rechaza_y_la_del_tope_justo_no()
    {
        OrdenCajasFicha.ErrorDeForma([new string('a', OrdenCajasFicha.LongitudMaximaClave)]).Should().BeNull();
        OrdenCajasFicha.ErrorDeForma([new string('a', OrdenCajasFicha.LongitudMaximaClave + 1)]).Should().Contain("formato");
    }

    [Fact]
    public void Una_clave_repetida_se_rechaza()
    {
        OrdenCajasFicha.ErrorDeForma(["contacto", "notas", "contacto"]).Should().Contain("repite");
    }

    [Fact]
    public void Mas_claves_que_el_tope_se_rechaza_y_el_tope_justo_no()
    {
        var justo = Enumerable.Range(0, OrdenCajasFicha.MaximoClaves).Select(i => $"caja-{i}").ToList();

        OrdenCajasFicha.ErrorDeForma(justo).Should().BeNull();
        OrdenCajasFicha.ErrorDeForma([.. justo, "una-mas"]).Should().Contain("demasiadas");
    }

    [Fact]
    public void Conciliar_pone_las_cajas_en_el_orden_guardado()
    {
        OrdenCajasFicha.Conciliar(["notas", "contacto", "datos-fiscales"], ["contacto", "datos-fiscales", "notas"])
            .Should().Equal("notas", "contacto", "datos-fiscales");
    }

    [Fact]
    public void Conciliar_ignora_la_clave_guardada_que_la_ficha_ya_no_tiene()
    {
        OrdenCajasFicha.Conciliar(["notas", "caja-retirada", "contacto"], ["contacto", "notas"])
            .Should().Equal("notas", "contacto");
    }

    [Fact]
    public void Conciliar_manda_al_final_la_caja_nueva_que_no_esta_en_el_orden_guardado()
    {
        OrdenCajasFicha.Conciliar(["notas", "contacto"], ["caja-nueva", "contacto", "otra-nueva", "notas"])
            .Should().Equal("notas", "contacto", "caja-nueva", "otra-nueva");
    }

    [Fact]
    public void Conciliar_sin_orden_guardado_devuelve_las_cajas_como_llegan()
    {
        OrdenCajasFicha.Conciliar([], ["contacto", "notas"]).Should().Equal("contacto", "notas");
    }

    [Fact]
    public void Conciliar_no_duplica_una_caja_aunque_el_orden_guardado_la_repita()
    {
        OrdenCajasFicha.Conciliar(["notas", "notas", "contacto"], ["contacto", "notas"])
            .Should().Equal("notas", "contacto");
    }
}

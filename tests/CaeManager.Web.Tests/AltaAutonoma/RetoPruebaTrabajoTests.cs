using CaeManager.Web.Features.AltaAutonoma.Antiabuso;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.AltaAutonoma;

/// <summary>
/// Reto de prueba de trabajo del alta autónoma (ASVS 4.0.3 V2.2.1 y V11.1.4): el servidor emite un token protegido con
/// Data Protection y acepta una sola vez un nonce con los bits cero pedidos, mientras no caduque. El reloj es manual y la
/// dificultad baja (8 bits, unas 256 pruebas de media) para que la búsqueda del nonce sea instantánea.
/// </summary>
public class RetoPruebaTrabajoTests
{
    private readonly RelojManual _reloj = new();
    private readonly OpcionesAntiabusoAlta _opciones = new() { DificultadBits = 8, VigenciaReto = TimeSpan.FromMinutes(20) };
    private readonly IDataProtectionProvider _proteccion = new EphemeralDataProtectionProvider();
    private readonly RegistroRetosUsados _usados;
    private readonly ServicioRetoPruebaTrabajo _servicio;

    public RetoPruebaTrabajoTests()
    {
        _usados = new RegistroRetosUsados(_reloj);
        _servicio = new ServicioRetoPruebaTrabajo(_proteccion, Options.Create(_opciones), _reloj, _usados);
    }

    [Fact]
    public void Un_reto_resuelto_y_vigente_es_valido()
    {
        var reto = _servicio.Emitir();

        reto.DificultadBits.Should().Be(8, "la dificultad la fija la configuración del servidor");
        _servicio.Verificar(reto.Token, Resolver(reto.Token, 8)).Should().Be(ResultadoVerificacionReto.Valido);
    }

    [Fact]
    public void Un_reto_resuelto_solo_vale_una_vez()
    {
        var reto = _servicio.Emitir();
        var nonce = Resolver(reto.Token, 8);

        _servicio.Verificar(reto.Token, nonce).Should().Be(ResultadoVerificacionReto.Valido);
        _servicio.Verificar(reto.Token, nonce).Should().Be(ResultadoVerificacionReto.Reutilizado);
        _servicio.Verificar(reto.Token, Resolver(reto.Token, 8, desde: long.Parse(nonce) + 1))
            .Should().Be(ResultadoVerificacionReto.Reutilizado, "otra solución del mismo reto tampoco lo reabre");
    }

    [Fact]
    public void Un_reto_caducado_se_rechaza_aunque_este_resuelto()
    {
        var reto = _servicio.Emitir();
        var nonce = Resolver(reto.Token, 8);

        _reloj.Avanzar(_opciones.VigenciaReto - TimeSpan.FromSeconds(1));
        _servicio.Verificar(reto.Token, nonce).Should().Be(ResultadoVerificacionReto.Valido, "control: un segundo antes aún vale");

        var otro = _servicio.Emitir();
        var nonceOtro = Resolver(otro.Token, 8);
        _reloj.Avanzar(_opciones.VigenciaReto);
        _servicio.Verificar(otro.Token, nonceOtro).Should().Be(ResultadoVerificacionReto.Caducado);
    }

    [Fact]
    public void Un_nonce_con_menos_bits_de_los_pedidos_se_rechaza_y_no_consume_el_reto()
    {
        var reto = _servicio.Emitir();
        var flojo = Enumerable.Range(0, 10_000).Select(n => n.ToString())
            .First(n => ServicioRetoPruebaTrabajo.BitsCeroIniciales(reto.Token, n) < 8);

        _servicio.Verificar(reto.Token, flojo).Should().Be(ResultadoVerificacionReto.DificultadInsuficiente);
        _servicio.Verificar(reto.Token, Resolver(reto.Token, 8)).Should().Be(ResultadoVerificacionReto.Valido);
    }

    [Fact]
    public void La_dificultad_que_cuenta_es_la_del_token_y_no_la_que_el_cliente_resuelva()
    {
        _opciones.DificultadBits = 12;
        var reto = _servicio.Emitir();

        // Tras emitir se baja la configuración: si la verificación leyera la configuración y no el token, el nonce
        // intermedio (8 a 11 bits) pasaría. Tiene que seguir rechazado.
        _opciones.DificultadBits = 8;
        var intermedio = Enumerable.Range(0, 1_000_000).Select(n => n.ToString())
            .First(n => ServicioRetoPruebaTrabajo.BitsCeroIniciales(reto.Token, n) is >= 8 and < 12);

        _servicio.Verificar(reto.Token, intermedio).Should().Be(ResultadoVerificacionReto.DificultadInsuficiente);
        _servicio.Verificar(reto.Token, Resolver(reto.Token, 12)).Should().Be(ResultadoVerificacionReto.Valido);
    }

    [Fact]
    public void Un_token_manipulado_se_rechaza()
    {
        var reto = _servicio.Emitir();
        var nonce = Resolver(reto.Token, 8);
        var posicion = reto.Token.Length / 2;
        var manipulado = reto.Token[..posicion] + (reto.Token[posicion] == 'A' ? 'B' : 'A') + reto.Token[(posicion + 1)..];

        _servicio.Verificar(manipulado, Resolver(manipulado, 8)).Should().Be(ResultadoVerificacionReto.Ilegible);
        _servicio.Verificar(reto.Token, nonce).Should().Be(ResultadoVerificacionReto.Valido, "control: el original sigue valiendo");
    }

    [Fact]
    public void Un_token_de_otras_claves_o_de_otro_proposito_se_rechaza()
    {
        var ajeno = new ServicioRetoPruebaTrabajo(
            new EphemeralDataProtectionProvider(), Options.Create(_opciones), _reloj, new RegistroRetosUsados(_reloj)).Emitir();
        _servicio.Verificar(ajeno.Token, Resolver(ajeno.Token, 8)).Should().Be(ResultadoVerificacionReto.Ilegible);

        var sello = new ServicioTrampasFormulario(_proteccion, Options.Create(_opciones), _reloj, _usados).EmitirSello(ajeno.Token);
        _servicio.Verificar(sello, Resolver(sello, 8))
            .Should().Be(ResultadoVerificacionReto.Ilegible, "el sello del formulario usa otro propósito de Data Protection");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("12a")]
    [InlineData("１２")]
    [InlineData("123456789012345678901")]
    public void Un_nonce_vacio_no_decimal_o_demasiado_largo_es_ilegible(string? nonce)
    {
        _servicio.Verificar(_servicio.Emitir().Token, nonce).Should().Be(ResultadoVerificacionReto.Ilegible);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-es-base64url!")]
    [InlineData("AAAA")]
    public void Un_token_ausente_o_basura_es_ilegible(string? token)
    {
        _servicio.Verificar(token, "0").Should().Be(ResultadoVerificacionReto.Ilegible);
    }

    [Fact]
    public void Un_token_desmesurado_se_rechaza_sin_descifrarlo()
    {
        _servicio.Verificar(new string('A', 5_000), "0").Should().Be(ResultadoVerificacionReto.Ilegible);
    }

    [Fact]
    public void La_purga_retira_los_ids_caducados_y_el_reto_sigue_rechazado()
    {
        var reto = _servicio.Emitir();
        var nonce = Resolver(reto.Token, 8);
        _servicio.Verificar(reto.Token, nonce).Should().Be(ResultadoVerificacionReto.Valido);
        _usados.Cantidad.Should().Be(1);

        _reloj.Avanzar(_opciones.VigenciaReto);
        _usados.PurgarCaducados();

        _usados.Cantidad.Should().Be(0);
        _servicio.Verificar(reto.Token, nonce).Should().Be(ResultadoVerificacionReto.Caducado);
    }

    [Fact]
    public void Dos_retos_emitidos_seguidos_son_distintos()
    {
        _servicio.Emitir().Token.Should().NotBe(_servicio.Emitir().Token);
    }

    [Fact]
    public void El_vector_compartido_con_el_worker_da_el_mismo_primer_nonce()
    {
        // El mismo vector fija tests/CaeManager.Web.Tests/js/retoPruebaTrabajo.test.mjs con el worker real.
        Resolver("vector-alta-a2", 12).Should().Be("2413");
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(29)]
    public void Una_dificultad_fuera_de_rango_invalida_las_opciones(int bits)
    {
        new OpcionesAntiabusoAlta { DificultadBits = bits, VigenciaReto = TimeSpan.FromHours(2) }.EsValida()
            .Should().Be(bits is >= 8 and <= 23, "con 2 h de vigencia, el tope conjunto queda en 23 bits");
    }

    [Theory]
    // Vigencia en el guion (la del servidor menos 10 s) frente a 3 s de tiempo mínimo más 20 veces la búsqueda media a
    // 24.000 hashes/s: 20 bits piden 877 s y caben en 1.190 s; 21 bits piden 1.751 s y no; en 1 min (50 s en el
    // guion) caben 15 bits (30 s) y no 16 (58 s).
    [InlineData(20, 20, true)]
    [InlineData(21, 20, false)]
    [InlineData(21, 30, true)]
    [InlineData(15, 1, true)]
    [InlineData(16, 1, false)]
    public void La_vigencia_del_reto_tiene_que_cubrir_su_dificultad(int bits, int minutos, bool valida)
    {
        new OpcionesAntiabusoAlta { DificultadBits = bits, VigenciaReto = TimeSpan.FromMinutes(minutos) }.EsValida()
            .Should().Be(valida);
    }

    [Theory]
    // Con 8 bits la búsqueda es despreciable: en 1 min de vigencia (50 s en el guion) cabe un tiempo mínimo de 45 s,
    // pero no uno de 50 s, que dejaría el reto caducado en el navegador justo cuando el envío empieza a valer.
    [InlineData(45, true)]
    [InlineData(50, false)]
    [InlineData(60, false)]
    public void El_tiempo_minimo_de_envio_tiene_que_caber_en_la_vigencia_del_guion(int segundos, bool valida)
        => OpcionesConTiempoMinimo(segundos).EsValida().Should().Be(valida);

    [Theory]
    [InlineData(3001, false)]
    [InlineData(1_199_999, false)]
    [InlineData(1_200_000, true)]
    public void El_sello_de_tiempo_tiene_que_durar_al_menos_lo_que_el_reto(int milisegundos, bool valida)
    {
        new OpcionesAntiabusoAlta { VigenciaSelloFormulario = TimeSpan.FromMilliseconds(milisegundos) }.EsValida()
            .Should().Be(valida, "el reto dura 20 min por defecto");
    }

    private static OpcionesAntiabusoAlta OpcionesConTiempoMinimo(int segundos) => new()
    {
        DificultadBits = 8,
        VigenciaReto = TimeSpan.FromMinutes(1),
        TiempoMinimoEnvio = TimeSpan.FromSeconds(segundos),
    };

    [Fact]
    public void Las_opciones_por_defecto_son_validas_y_las_del_plan()
    {
        var porDefecto = new OpcionesAntiabusoAlta();

        porDefecto.EsValida().Should().BeTrue();
        porDefecto.IniciosPorHoraPorIp.Should().Be(5);
        porDefecto.IniciosPorDiaPorIp.Should().Be(20);
        porDefecto.TiempoMinimoEnvio.Should().Be(TimeSpan.FromSeconds(3));
        new OpcionesAntiabusoAlta { IniciosPorDiaPorIp = 4 }.EsValida().Should().BeFalse("el día no puede quedar por debajo de la hora");
    }

    private static string Resolver(string token, int bits, long desde = 0)
    {
        for (var n = desde; ; n++)
        {
            var nonce = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (ServicioRetoPruebaTrabajo.BitsCeroIniciales(token, nonce) >= bits)
                return nonce;
        }
    }
}

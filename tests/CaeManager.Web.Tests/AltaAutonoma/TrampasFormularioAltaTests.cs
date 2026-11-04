using CaeManager.Web.Features.AltaAutonoma.Antiabuso;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.AltaAutonoma;

/// <summary>
/// Honeypot y trampa de tiempo del formulario del alta (ASVS 4.0.3 V11.1.4). El sello lleva un nonce, la hora de pintado
/// y el reto con el que se pintó, protegidos con Data Protection: el cliente no puede adelantar la hora ni cambiar de
/// reto, un envío antes de 3 s se rechaza y el sello vale una sola vez.
/// </summary>
public class TrampasFormularioAltaTests
{
    private readonly RelojManual _reloj = new();
    private readonly OpcionesAntiabusoAlta _opciones = new();
    private readonly IDataProtectionProvider _proteccion = new EphemeralDataProtectionProvider();
    private readonly RegistroRetosUsados _usados;
    private readonly ServicioRetoPruebaTrabajo _retos;
    private readonly ServicioTrampasFormulario _servicio;

    public TrampasFormularioAltaTests()
    {
        _usados = new RegistroRetosUsados(_reloj);
        _retos = new ServicioRetoPruebaTrabajo(_proteccion, Options.Create(_opciones), _reloj, _usados);
        _servicio = new ServicioTrampasFormulario(_proteccion, Options.Create(_opciones), _reloj, _usados);
    }

    private string Reto() => _retos.Emitir().Token;

    [Fact]
    public void Un_envio_humano_con_el_campo_trampa_vacio_pasado_el_tiempo_minimo_supera_las_trampas()
    {
        var reto = Reto();
        var sello = _servicio.EmitirSello(reto);
        _reloj.Avanzar(TimeSpan.FromSeconds(3));

        _servicio.Verificar(string.Empty, sello, reto).Should().Be(ResultadoTrampasFormulario.Superadas);
    }

    [Fact]
    public void El_mismo_sello_dos_veces_se_rechaza()
    {
        var reto = Reto();
        var sello = _servicio.EmitirSello(reto);
        _reloj.Avanzar(TimeSpan.FromSeconds(3));

        _servicio.Verificar(null, sello, reto).Should().Be(ResultadoTrampasFormulario.Superadas, "control: la primera vez pasa");
        _servicio.Verificar(null, sello, reto).Should().Be(ResultadoTrampasFormulario.SelloReutilizado);
        _reloj.Avanzar(TimeSpan.FromMinutes(10));
        _servicio.Verificar(null, sello, reto).Should().Be(ResultadoTrampasFormulario.SelloReutilizado, "tampoco más tarde");
    }

    [Fact]
    public void Un_sello_antiguo_con_un_reto_nuevo_se_rechaza()
    {
        // El ataque: pedir un formulario, guardar su sello y, pasada la espera, usarlo con cada reto recién emitido
        // para enviar sin esperar.
        var retoAntiguo = Reto();
        var selloAntiguo = _servicio.EmitirSello(retoAntiguo);
        _reloj.Avanzar(TimeSpan.FromMinutes(5));
        var retoNuevo = Reto();

        _servicio.Verificar(null, selloAntiguo, retoNuevo).Should().Be(ResultadoTrampasFormulario.SelloDeOtroReto);
        _servicio.Verificar(null, selloAntiguo, null).Should().Be(ResultadoTrampasFormulario.SelloDeOtroReto);
        _servicio.Verificar(null, selloAntiguo, new string('A', 1_100)).Should().Be(ResultadoTrampasFormulario.SelloDeOtroReto);
        _servicio.Verificar(null, selloAntiguo, retoAntiguo)
            .Should().Be(ResultadoTrampasFormulario.Superadas, "control: con su reto, y sin haberse quemado por los intentos, pasa");
    }

    [Fact]
    public void Un_sello_rechazado_por_rapido_no_se_quema()
    {
        var reto = Reto();
        var sello = _servicio.EmitirSello(reto);

        _servicio.Verificar(null, sello, reto).Should().Be(ResultadoTrampasFormulario.EnvioDemasiadoRapido);
        _usados.Cantidad.Should().Be(0);
    }

    [Fact]
    public void El_sello_no_admite_un_token_vacio_al_emitirse()
    {
        var emitir = () => _servicio.EmitirSello(string.Empty);

        emitir.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("https://ejemplo.test")]
    [InlineData("x")]
    [InlineData(" ")]
    public void Cualquier_valor_en_el_campo_trampa_lo_delata(string valor)
    {
        var reto = Reto();
        var sello = _servicio.EmitirSello(reto);
        _reloj.Avanzar(TimeSpan.FromMinutes(1));

        _servicio.Verificar(valor, sello, reto).Should().Be(ResultadoTrampasFormulario.CampoTrampaRelleno);
    }

    [Fact]
    public void Un_envio_antes_del_tiempo_minimo_se_rechaza()
    {
        var reto = Reto();
        var sello = _servicio.EmitirSello(reto);

        _servicio.Verificar(null, sello, reto).Should().Be(ResultadoTrampasFormulario.EnvioDemasiadoRapido);
        _reloj.Avanzar(TimeSpan.FromMilliseconds(2_999));
        _servicio.Verificar(null, sello, reto).Should().Be(ResultadoTrampasFormulario.EnvioDemasiadoRapido);
        _reloj.Avanzar(TimeSpan.FromMilliseconds(1));
        _servicio.Verificar(null, sello, reto).Should().Be(ResultadoTrampasFormulario.Superadas, "control: a los 3 s ya pasa");
    }

    [Fact]
    public void Un_sello_emitido_en_el_futuro_no_salta_la_espera()
    {
        var adelantado = new RelojManual();
        adelantado.Avanzar(TimeSpan.FromHours(1));
        var reto = Reto();
        var sello = new ServicioTrampasFormulario(_proteccion, Options.Create(_opciones), adelantado, _usados).EmitirSello(reto);

        _servicio.Verificar(null, sello, reto).Should().Be(ResultadoTrampasFormulario.EnvioDemasiadoRapido);
    }

    [Fact]
    public void Un_sello_caducado_se_rechaza()
    {
        var reto = Reto();
        var vigente = _servicio.EmitirSello(reto);
        var caducado = _servicio.EmitirSello(reto);
        _reloj.Avanzar(_opciones.VigenciaSelloFormulario - TimeSpan.FromMilliseconds(1));
        _servicio.Verificar(null, vigente, reto).Should().Be(ResultadoTrampasFormulario.Superadas, "control: justo antes del límite aún vale");

        _reloj.Avanzar(TimeSpan.FromMilliseconds(1));
        _servicio.Verificar(null, caducado, reto).Should().Be(ResultadoTrampasFormulario.SelloCaducado);
    }

    [Fact]
    public void Un_sello_consumido_no_revive_al_purgarse_el_registro()
    {
        var reto = Reto();
        var sello = _servicio.EmitirSello(reto);
        _reloj.Avanzar(TimeSpan.FromSeconds(3));
        _servicio.Verificar(null, sello, reto).Should().Be(ResultadoTrampasFormulario.Superadas);

        _reloj.Avanzar(_opciones.VigenciaSelloFormulario);
        _usados.PurgarCaducados();

        _usados.Cantidad.Should().Be(0, "control: el nonce ya se ha olvidado");
        _servicio.Verificar(null, sello, reto).Should().Be(ResultadoTrampasFormulario.SelloCaducado);
    }

    [Fact]
    public void Un_sello_manipulado_ajeno_o_de_otro_proposito_es_ilegible()
    {
        var reto = Reto();
        var sello = _servicio.EmitirSello(reto);
        _reloj.Avanzar(TimeSpan.FromMinutes(1));
        var posicion = sello.Length / 2;
        var manipulado = sello[..posicion] + (sello[posicion] == 'A' ? 'B' : 'A') + sello[(posicion + 1)..];

        _servicio.Verificar(null, manipulado, reto).Should().Be(ResultadoTrampasFormulario.SelloIlegible);

        var ajeno = new ServicioTrampasFormulario(
            new EphemeralDataProtectionProvider(), Options.Create(_opciones), _reloj, _usados).EmitirSello(reto);
        _servicio.Verificar(null, ajeno, reto).Should().Be(ResultadoTrampasFormulario.SelloIlegible);

        _servicio.Verificar(null, reto, reto).Should().Be(ResultadoTrampasFormulario.SelloIlegible, "el token del reto usa otro propósito");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("basura!")]
    public void Sin_sello_o_con_basura_es_ilegible(string? sello)
    {
        var reto = Reto();

        _servicio.Verificar(null, sello, reto).Should().Be(ResultadoTrampasFormulario.SelloIlegible);
        _servicio.Verificar(null, new string('A', 600), reto).Should().Be(ResultadoTrampasFormulario.SelloIlegible);
    }
}

using CaeManager.Web.Features.AltaAutonoma.Antiabuso;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.AltaAutonoma;

/// <summary>
/// Honeypot y trampa de tiempo del formulario del alta (ASVS 4.0.3 V11.1.4). El sello lleva la hora de pintado protegida
/// con Data Protection: el cliente no puede adelantarla, y un envío antes de 3 s se rechaza.
/// </summary>
public class TrampasFormularioAltaTests
{
    private readonly RelojManual _reloj = new();
    private readonly OpcionesAntiabusoAlta _opciones = new();
    private readonly IDataProtectionProvider _proteccion = new EphemeralDataProtectionProvider();
    private readonly ServicioTrampasFormulario _servicio;

    public TrampasFormularioAltaTests() =>
        _servicio = new ServicioTrampasFormulario(_proteccion, Options.Create(_opciones), _reloj);

    [Fact]
    public void Un_envio_humano_con_el_campo_trampa_vacio_pasado_el_tiempo_minimo_supera_las_trampas()
    {
        var sello = _servicio.EmitirSello();
        _reloj.Avanzar(TimeSpan.FromSeconds(3));

        _servicio.Verificar(string.Empty, sello).Should().Be(ResultadoTrampasFormulario.Superadas);
        _servicio.Verificar(null, sello).Should().Be(ResultadoTrampasFormulario.Superadas, "el sello no es de un solo uso");
    }

    [Theory]
    [InlineData("https://ejemplo.test")]
    [InlineData("x")]
    [InlineData(" ")]
    public void Cualquier_valor_en_el_campo_trampa_lo_delata(string valor)
    {
        var sello = _servicio.EmitirSello();
        _reloj.Avanzar(TimeSpan.FromMinutes(1));

        _servicio.Verificar(valor, sello).Should().Be(ResultadoTrampasFormulario.CampoTrampaRelleno);
    }

    [Fact]
    public void Un_envio_antes_del_tiempo_minimo_se_rechaza()
    {
        var sello = _servicio.EmitirSello();

        _servicio.Verificar(null, sello).Should().Be(ResultadoTrampasFormulario.EnvioDemasiadoRapido);
        _reloj.Avanzar(TimeSpan.FromMilliseconds(2_999));
        _servicio.Verificar(null, sello).Should().Be(ResultadoTrampasFormulario.EnvioDemasiadoRapido);
        _reloj.Avanzar(TimeSpan.FromMilliseconds(1));
        _servicio.Verificar(null, sello).Should().Be(ResultadoTrampasFormulario.Superadas, "control: a los 3 s ya pasa");
    }

    [Fact]
    public void Un_sello_emitido_en_el_futuro_no_salta_la_espera()
    {
        var adelantado = new RelojManual();
        adelantado.Avanzar(TimeSpan.FromHours(1));
        var sello = new ServicioTrampasFormulario(_proteccion, Options.Create(_opciones), adelantado).EmitirSello();

        _servicio.Verificar(null, sello).Should().Be(ResultadoTrampasFormulario.EnvioDemasiadoRapido);
    }

    [Fact]
    public void Un_sello_caducado_se_rechaza()
    {
        var sello = _servicio.EmitirSello();
        _reloj.Avanzar(_opciones.VigenciaSelloFormulario);
        _servicio.Verificar(null, sello).Should().Be(ResultadoTrampasFormulario.Superadas, "control: justo en el límite aún vale");

        _reloj.Avanzar(TimeSpan.FromMilliseconds(1));
        _servicio.Verificar(null, sello).Should().Be(ResultadoTrampasFormulario.SelloCaducado);
    }

    [Fact]
    public void Un_sello_manipulado_ajeno_o_de_otro_proposito_es_ilegible()
    {
        var sello = _servicio.EmitirSello();
        _reloj.Avanzar(TimeSpan.FromMinutes(1));
        var posicion = sello.Length / 2;
        var manipulado = sello[..posicion] + (sello[posicion] == 'A' ? 'B' : 'A') + sello[(posicion + 1)..];

        _servicio.Verificar(null, manipulado).Should().Be(ResultadoTrampasFormulario.SelloIlegible);

        var ajeno = new ServicioTrampasFormulario(new EphemeralDataProtectionProvider(), Options.Create(_opciones), _reloj).EmitirSello();
        _servicio.Verificar(null, ajeno).Should().Be(ResultadoTrampasFormulario.SelloIlegible);

        var reto = new ServicioRetoPruebaTrabajo(_proteccion, Options.Create(_opciones), _reloj, new RegistroRetosUsados(_reloj)).Emitir();
        _servicio.Verificar(null, reto.Token).Should().Be(ResultadoTrampasFormulario.SelloIlegible, "el token del reto usa otro propósito");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("basura!")]
    public void Sin_sello_o_con_basura_es_ilegible(string? sello)
    {
        _servicio.Verificar(null, sello).Should().Be(ResultadoTrampasFormulario.SelloIlegible);
        _servicio.Verificar(null, new string('A', 600)).Should().Be(ResultadoTrampasFormulario.SelloIlegible);
    }
}

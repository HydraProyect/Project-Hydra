using CaeManager.Web.Features.Extension;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// A quién se le puede ofrecer el código de conexión.
///
/// <para>
/// El defecto que estas pruebas fijan lo encontró Codex el 2026-09-21: la
/// pantalla mandaba a «Conectar a mano» también a las extensiones v0.2.0, que
/// no tienen ese control. Un camino de recuperación que no recupera es peor que
/// no tenerlo, porque consume el intento de quien está bloqueado.
/// </para>
///
/// <para>
/// El valor no está en las tres líneas de <see cref="CompatibilidadExtension"/>
/// sino en <b>dónde se corta</b>: la versión mínima es un dato del paquete de la
/// extensión, que se actualiza en la tienda y no con el despliegue. Si alguien
/// adelanta «Conectar a mano» a otra versión, o lo mueve, esta constante deja de
/// describir el mundo y no hay nada más que lo ponga en rojo.
/// </para>
/// </summary>
public class CompatibilidadExtensionTests
{
    [Fact]
    public void La_version_minima_es_la_que_introdujo_conectar_a_mano()
    {
        // El manifiesto subió a 0.3.0 en el mismo incremento que añadió el
        // control al popup. Si esto cambia, hay que cambiar los dos a la vez.
        CompatibilidadExtension.VersionMinimaConexionManual.Should().Be(new Version(0, 3, 0));
    }

    [Theory]
    [InlineData("0.3.0")]
    [InlineData("0.3.1")]
    [InlineData("0.4.0")]
    [InlineData("1.0.0")]
    public void Una_extension_al_dia_puede_conectarse_a_mano(string version)
    {
        CompatibilidadExtension.AdmiteConexionManual(version).Should().BeTrue();
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("0.2.9")]
    [InlineData("0.1.0")]
    public void Una_extension_anterior_no_tiene_donde_pegar_el_codigo(string version)
    {
        CompatibilidadExtension.AdmiteConexionManual(version).Should().BeFalse();
    }

    [Fact]
    public void Sin_respuesta_de_la_extension_se_ofrece_el_codigo_igualmente()
    {
        // Chrome no distingue «no instalada» de «instalada y anticuada»: no hay
        // versión que leer. Esconder el código aquí dejaría sin salida a quien
        // sí la tiene puesta y al día, que es justo el caso que motivó todo
        // esto. La pantalla compensa escribiendo al lado qué versión hace falta.
        CompatibilidadExtension.AdmiteConexionManual(null).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v0.3.0")]
    [InlineData("beta")]
    public void Una_version_que_no_se_puede_leer_cuenta_como_desconocida_no_como_antigua(string version)
    {
        // Una cadena rara es un fallo de lectura nuestro, no una confesión de
        // la extensión. Tratarla como antigua le quitaría la única salida a
        // alguien que quizá sí la tiene.
        CompatibilidadExtension.AdmiteConexionManual(version).Should().BeTrue();
    }

    [Fact]
    public void La_comparacion_es_numerica_y_no_alfabetica()
    {
        // Control del propio instrumento: "0.10.0" es POSTERIOR a "0.3.0"
        // aunque como texto vaya antes. Si esto se compara con string.Compare,
        // esta prueba es la única que se entera.
        CompatibilidadExtension.AdmiteConexionManual("0.10.0").Should().BeTrue();
    }
}

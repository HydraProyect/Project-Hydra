using CaeManager.Domain.Configuracion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Configuracion;

public class ParametroSistemaTests
{
    [Fact]
    public void El_constructor_admite_valores_por_defecto_para_los_umbrales_de_visita()
    {
        var parametros = new ParametroSistema(30, 15);

        parametros.HorasAvisoVisita.Should().Be(48);
        parametros.HorasCriticasVisita.Should().Be(24);
    }

    [Fact]
    public void ActualizarUmbralesVisita_rechaza_horas_criticas_mayores_o_iguales_que_las_de_aviso()
    {
        var parametros = new ParametroSistema(30, 15);

        var accion = () => parametros.ActualizarUmbralesVisita(horasAvisoVisita: 24, horasCriticasVisita: 24);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ActualizarUmbralesVisita_rechaza_horas_criticas_no_positivas()
    {
        var parametros = new ParametroSistema(30, 15);

        var accion = () => parametros.ActualizarUmbralesVisita(horasAvisoVisita: 48, horasCriticasVisita: 0);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ActualizarUmbralesVisita_acepta_valores_validos()
    {
        var parametros = new ParametroSistema(30, 15);

        parametros.ActualizarUmbralesVisita(horasAvisoVisita: 72, horasCriticasVisita: 12);

        parametros.HorasAvisoVisita.Should().Be(72);
        parametros.HorasCriticasVisita.Should().Be(12);
    }
}

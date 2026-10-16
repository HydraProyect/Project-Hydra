using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Trabajadores;

public class TipoContratacionTests
{
    private static Trabajador Crear() => Trabajador.DeEmpresa(Guid.NewGuid(), "Paula", "Campos", "12345678Z");

    [Fact]
    public void Un_trabajador_nuevo_es_por_cuenta_ajena()
    {
        Crear().TipoContratacion.Should().Be(TipoContratacion.CuentaAjena);
    }

    [Theory]
    [InlineData(TipoContratacion.Autonomo)]
    [InlineData(TipoContratacion.Voluntario)]
    public void Cambia_el_tipo_de_contratacion(TipoContratacion tipo)
    {
        var t = Crear();

        t.CambiarTipoContratacion(tipo);

        t.TipoContratacion.Should().Be(tipo);
    }

    [Fact]
    public void Un_valor_fuera_del_enum_se_rechaza()
    {
        var t = Crear();

        var cambiar = () => t.CambiarTipoContratacion((TipoContratacion)99);

        cambiar.Should().Throw<ArgumentOutOfRangeException>();
        t.TipoContratacion.Should().Be(TipoContratacion.CuentaAjena);
    }

    [Fact]
    public void Un_trabajador_anonimizado_no_cambia_de_tipo()
    {
        var t = Crear();
        t.Anonimizar(DateTime.UtcNow);

        var cambiar = () => t.CambiarTipoContratacion(TipoContratacion.Voluntario);

        cambiar.Should().Throw<InvalidOperationException>();
    }
}

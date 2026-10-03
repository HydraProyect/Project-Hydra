using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// La regla única de bloqueo de acceso (decisión del propietario, 2026-10-03): un documento bloqueante AUSENTE o
/// VENCIDO bloquea por igual; Próximo y Urgente (válidos hoy), «No caduca» y «Sin confirmar» no bloquean. Cada fila
/// es una entrada de la tabla; el día de negocio es fijo para que el límite (vence hoy) sea exacto.
/// </summary>
public class ReglaBloqueoDeAccesoTests
{
    private static readonly DateOnly Hoy = new(2026, 10, 3);

    [Theory]
    [InlineData("Vencido ayer", -1, false)]
    [InlineData("Vencido hace un ano", -365, false)]
    [InlineData("Vence hoy (Urgente, valido hoy)", 0, true)]
    [InlineData("Vence manana (Urgente)", 1, true)]
    [InlineData("Vence en 20 dias (Proximo)", 20, true)]
    [InlineData("Vence en un ano (Vigente)", 365, true)]
    public void Solo_un_documento_con_fecha_anterior_a_hoy_deja_de_ser_valido(string caso, int diasHastaVencer, bool esperado)
    {
        ReglaBloqueoDeAcceso.ValidoHoy(VigenciaDocumento.VenceEl(Hoy.AddDays(diasHastaVencer)), Hoy).Should().Be(esperado, caso);
    }

    [Fact]
    public void Un_documento_sin_fecha_no_esta_vencido_y_por_tanto_no_bloquea()
    {
        ReglaBloqueoDeAcceso.ValidoHoy(VigenciaDocumento.NoCaduca, Hoy).Should().BeTrue("«No caduca» no tiene fecha");
        ReglaBloqueoDeAcceso.ValidoHoy(VigenciaDocumento.SinConfirmar, Hoy).Should().BeTrue(
            "«Sin confirmar» no se decidio para el bloqueo: se conserva lo que ya hacia Mi trabajo");
        ReglaBloqueoDeAcceso.ValidoHoy(default, Hoy).Should().BeTrue("el valor por defecto del struct es «Sin confirmar»");
    }

    [Fact]
    public void Sin_ningun_documento_el_requisito_esta_ausente_y_bloquea()
    {
        var situacion = ReglaBloqueoDeAcceso.Evaluar([], Hoy);

        situacion.Should().Be(SituacionDeRequisitoBloqueante.Ausente);
        ReglaBloqueoDeAcceso.Bloquea(situacion).Should().BeTrue();
    }

    [Fact]
    public void Con_solo_documentos_vencidos_el_requisito_esta_vencido_y_bloquea_igual_que_el_ausente()
    {
        var situacion = ReglaBloqueoDeAcceso.Evaluar(
            [VigenciaDocumento.VenceEl(Hoy.AddDays(-1)), VigenciaDocumento.VenceEl(Hoy.AddDays(-400))], Hoy);

        situacion.Should().Be(SituacionDeRequisitoBloqueante.Vencido);
        ReglaBloqueoDeAcceso.Bloquea(situacion).Should().BeTrue(
            "un vencido y un ausente bloquean de igual manera (decision del propietario, 2026-10-03)");
    }

    [Fact]
    public void Un_vencido_con_su_renovacion_valida_cumple_el_requisito()
    {
        var situacion = ReglaBloqueoDeAcceso.Evaluar(
            [VigenciaDocumento.VenceEl(Hoy.AddDays(-30)), VigenciaDocumento.VenceEl(Hoy.AddDays(335))], Hoy);

        situacion.Should().Be(SituacionDeRequisitoBloqueante.Cumplido);
        ReglaBloqueoDeAcceso.Bloquea(situacion).Should().BeFalse();
    }

    [Theory]
    [InlineData("noCaduca")]
    [InlineData("sinConfirmar")]
    [InlineData("venceHoy")]
    public void Los_documentos_sin_fecha_o_que_vencen_hoy_cumplen_el_requisito(string caso)
    {
        VigenciaDocumento vigencia = caso switch
        {
            "noCaduca" => VigenciaDocumento.NoCaduca,
            "sinConfirmar" => VigenciaDocumento.SinConfirmar,
            _ => VigenciaDocumento.VenceEl(Hoy)
        };

        ReglaBloqueoDeAcceso.Evaluar([vigencia], Hoy).Should().Be(SituacionDeRequisitoBloqueante.Cumplido);
    }

    [Theory]
    [InlineData(AmbitoAplicacion.Trabajador, true)]
    [InlineData(AmbitoAplicacion.Empresa, true)]
    [InlineData(AmbitoAplicacion.Cliente, false)]
    [InlineData(AmbitoAplicacion.Vehiculo, false)]
    [InlineData(AmbitoAplicacion.Proyecto, false)]
    public void Solo_los_tipos_de_Trabajador_y_de_Empresa_tienen_sujeto_del_bloqueo(AmbitoAplicacion ambito, bool puede)
    {
        ReglaBloqueoDeAcceso.AmbitoPuedeBloquear(ambito).Should().Be(puede);
    }

    [Fact]
    public void Cada_ambito_del_enum_esta_decidido_en_la_tabla()
    {
        // Un ambito nuevo hay que decidirlo en AmbitoPuedeBloquear Y en la tabla de arriba.
        Enum.GetValues<AmbitoAplicacion>().Should().BeEquivalentTo(
        [
            AmbitoAplicacion.Trabajador, AmbitoAplicacion.Cliente, AmbitoAplicacion.Empresa,
            AmbitoAplicacion.Vehiculo, AmbitoAplicacion.Proyecto
        ]);
    }
}

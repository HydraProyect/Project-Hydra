using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// La vigencia de un documento en una plataforma CAE tiene TRES estados, no
/// dos. Lo que se prueba aquí no es la aritmética de fechas: es que «nadie lo
/// ha anotado» no pueda volver a colarse como «no caduca», que es la forma en
/// que este defecto se manifiesta —un semáforo verde que nadie confirmó—.
/// </summary>
public class VigenciaEnPlataformaTests
{
    private static readonly DateOnly Hoy = new(2026, 9, 21);

    [Fact]
    public void Sin_confirmar_no_es_lo_mismo_que_no_vence_aqui()
    {
        VigenciaEnPlataforma.SinConfirmar.Should().NotBe(VigenciaEnPlataforma.NoVenceAqui);
        VigenciaEnPlataforma.SinConfirmar.EstaSinConfirmar.Should().BeTrue();
        VigenciaEnPlataforma.NoVenceAqui.EstaSinConfirmar.Should().BeFalse();
    }

    [Fact]
    public void Ninguna_de_las_dos_sin_fecha_esta_vencida()
    {
        // Ojo: que no esté vencida NO significa que se pueda entrar al Centro.
        // Son preguntas distintas y por eso son dos propiedades distintas.
        VigenciaEnPlataforma.SinConfirmar.EstaVencidaEl(Hoy).Should().BeFalse();
        VigenciaEnPlataforma.NoVenceAqui.EstaVencidaEl(Hoy).Should().BeFalse();
    }

    [Theory]
    [InlineData(-1, true)]   // venció ayer
    [InlineData(0, false)]   // vence hoy: hoy todavía vale
    [InlineData(1, false)]   // vence mañana
    public void Vencida_solo_cuando_la_fecha_ya_paso(int diasDesdeHoy, bool esperadoVencida)
    {
        var vigencia = VigenciaEnPlataforma.VenceEl(Hoy.AddDays(diasDesdeHoy));

        vigencia.EstaVencidaEl(Hoy).Should().Be(esperadoVencida);
    }

    [Fact]
    public void Vencer_en_una_fecha_conserva_la_fecha()
    {
        var vigencia = VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 1, 15));

        vigencia.Estado.Should().Be(EstadoVigenciaEnPlataforma.VenceEnFecha);
        vigencia.FechaVencimiento.Should().Be(new DateOnly(2027, 1, 15));
    }

    [Theory]
    [InlineData(EstadoVigenciaEnPlataforma.SinConfirmar)]
    [InlineData(EstadoVigenciaEnPlataforma.NoVenceAqui)]
    public void Los_estados_sin_fecha_no_llevan_fecha(EstadoVigenciaEnPlataforma estado)
    {
        var vigencia = VigenciaEnPlataforma.Rehidratar(estado, null);

        vigencia.FechaVencimiento.Should().BeNull();
    }

    // --- Rehidratación: una fila incoherente revienta donde se lee ---------
    //
    // Es el único sitio donde el estado inválido puede aparecer, porque los
    // constructores no lo permiten. Si la base de datos trae una combinación
    // imposible —una migración a medias, una corrección a mano—, tiene que
    // fallar aquí y no convertirse en un semáforo verde tres capas más arriba.

    [Fact]
    public void Rehidratar_rechaza_vence_en_fecha_sin_fecha()
    {
        var accion = () => VigenciaEnPlataforma.Rehidratar(EstadoVigenciaEnPlataforma.VenceEnFecha, null);

        accion.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(EstadoVigenciaEnPlataforma.SinConfirmar)]
    [InlineData(EstadoVigenciaEnPlataforma.NoVenceAqui)]
    public void Rehidratar_rechaza_una_fecha_que_no_deberia_estar(EstadoVigenciaEnPlataforma estado)
    {
        var accion = () => VigenciaEnPlataforma.Rehidratar(estado, new DateOnly(2027, 1, 1));

        accion.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Rehidratar_devuelve_lo_mismo_que_se_guardo()
    {
        // Control positivo: sin esto, las tres pruebas de arriba pasarían igual
        // si Rehidratar lanzara siempre.
        var original = VigenciaEnPlataforma.VenceEl(new DateOnly(2027, 3, 9));

        var rehidratada = VigenciaEnPlataforma.Rehidratar(original.Estado, original.FechaVencimiento);

        rehidratada.Should().Be(original);
    }
}

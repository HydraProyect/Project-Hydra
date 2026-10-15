using CaeManager.Application.Proyectos.Commands.CrearProyecto;
using CaeManager.Domain.Proyectos;
using FluentAssertions;

namespace CaeManager.Application.Tests.Proyectos;

/// <summary>
/// Reglas de entrada del alta de un Proyecto de tipo Evento (2026-10-15): un evento exige fecha de fin, sus requisitos
/// se copian de otro evento o de una plantilla pero no de los dos, y una obra no copia requisitos de evento. La copia
/// y la visibilidad del origen se prueban contra PostgreSQL en <c>EventosBajoRlsTests</c>.
/// </summary>
public class CrearProyectoEventoValidatorTests
{
    private static readonly CrearProyectoCommandValidator Validador = new();

    private static CrearProyectoCommand Alta(TipoProyecto tipo, DateOnly? fin, Guid? eventoOrigen = null, Guid? plantilla = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Partido de liga — jornada 7", new DateOnly(2026, 11, 22), fin, null,
            tipo, eventoOrigen, plantilla);

    private static readonly DateOnly Fin = new(2026, 11, 22);

    [Fact]
    public void Un_evento_sin_fecha_de_fin_no_pasa() =>
        Validador.Validate(Alta(TipoProyecto.Evento, null)).Errors.Select(e => e.ErrorMessage)
            .Should().ContainSingle().Which.Should().Be("Un evento necesita fecha de fin.");

    [Fact]
    public void Una_obra_sin_fecha_de_fin_pasa() =>
        Validador.Validate(Alta(TipoProyecto.Obra, null)).IsValid.Should().BeTrue();

    [Fact]
    public void Un_evento_con_fin_y_un_solo_origen_pasa()
    {
        Validador.Validate(Alta(TipoProyecto.Evento, Fin, eventoOrigen: Guid.NewGuid())).IsValid.Should().BeTrue();
        Validador.Validate(Alta(TipoProyecto.Evento, Fin, plantilla: Guid.NewGuid())).IsValid.Should().BeTrue();
        Validador.Validate(Alta(TipoProyecto.Evento, Fin)).IsValid.Should().BeTrue("un evento puede nacer sin requisitos");
    }

    [Fact]
    public void Un_evento_con_los_dos_origenes_no_pasa() =>
        Validador.Validate(Alta(TipoProyecto.Evento, Fin, Guid.NewGuid(), Guid.NewGuid())).Errors.Select(e => e.ErrorMessage)
            .Should().ContainSingle().Which.Should().Be("Copia los requisitos de otro evento o de una plantilla, no de los dos.");

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Una_obra_con_origen_de_requisitos_no_pasa(bool conEvento, bool conPlantilla) =>
        Validador.Validate(Alta(TipoProyecto.Obra, null, conEvento ? Guid.NewGuid() : null, conPlantilla ? Guid.NewGuid() : null))
            .Errors.Select(e => e.ErrorMessage).Should().ContainSingle().Which.Should().Be("Solo un evento copia requisitos.");

    [Fact]
    public void Un_tipo_fuera_del_enumerado_no_pasa() =>
        Validador.Validate(Alta((TipoProyecto)7, Fin)).IsValid.Should().BeFalse();
}

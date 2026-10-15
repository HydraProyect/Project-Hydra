using CaeManager.Domain.Proyectos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Proyectos;

public class ProyectoEventoTests
{
    private static readonly DateOnly Inicio = new(2026, 11, 7);
    private static readonly DateOnly Fin = new(2026, 11, 8);

    private static Proyecto Evento() =>
        Proyecto.Crear(Guid.NewGuid(), Guid.NewGuid(), "Partido jornada 12", Inicio, Fin, null, TipoProyecto.Evento);

    [Fact]
    public void Un_proyecto_sin_tipo_es_una_obra_y_puede_no_tener_fin()
    {
        var obra = Proyecto.Crear(Guid.NewGuid(), Guid.NewGuid(), "Ampliación nave 2", Inicio, null, null);

        obra.Tipo.Should().Be(TipoProyecto.Obra);
        obra.EsEvento.Should().BeFalse();
        obra.EventoTerminadoEn(Inicio.AddYears(5)).Should().BeFalse("una obra nunca es un evento terminado");
    }

    [Fact]
    public void Un_evento_exige_fecha_de_fin_al_crearlo()
    {
        var crear = () => Proyecto.Crear(Guid.NewGuid(), Guid.NewGuid(), "Partido", Inicio, null, null, TipoProyecto.Evento);

        crear.Should().Throw<ArgumentException>().WithParameterName("fechaFinPrevista");
    }

    [Fact]
    public void Un_evento_no_puede_quedarse_sin_fecha_de_fin_al_editarlo()
    {
        var evento = Evento();

        var actualizar = () => evento.Actualizar("Partido jornada 12", null, null);

        actualizar.Should().Throw<ArgumentException>();
        evento.FechaFinPrevista.Should().Be(Fin);
    }

    [Fact]
    public void Un_tipo_fuera_del_enum_se_rechaza()
    {
        var crear = () => Proyecto.Crear(Guid.NewGuid(), Guid.NewGuid(), "X", Inicio, Fin, null, (TipoProyecto)7);

        crear.Should().Throw<ArgumentException>().WithParameterName("tipo");
    }

    [Fact]
    public void El_ultimo_dia_el_evento_sigue_vigente_y_al_dia_siguiente_esta_terminado()
    {
        var evento = Evento();

        evento.EventoTerminadoEn(Inicio.AddDays(-30)).Should().BeFalse("un evento futuro no ha terminado");
        evento.EventoTerminadoEn(Fin).Should().BeFalse("el día de fin cuenta como vigente");
        evento.EventoTerminadoEn(Fin.AddDays(1)).Should().BeTrue();
    }

    [Fact]
    public void Un_evento_cerrado_esta_terminado_aunque_no_haya_llegado_su_fin()
    {
        var evento = Evento();
        evento.Cerrar(Inicio);

        evento.EventoTerminadoEn(Inicio).Should().BeTrue();
    }

    [Fact]
    public void La_plantilla_guarda_cada_tipo_una_vez_y_no_admite_quedar_vacia()
    {
        var tipo = Guid.NewGuid();

        var plantilla = PlantillaRequisitosEvento.Crear(Guid.NewGuid(), "  Partidos de liga  ", [tipo, tipo, Guid.NewGuid()]);

        plantilla.Nombre.Should().Be("Partidos de liga");
        plantilla.Tipos.Should().HaveCount(2);
        plantilla.Tipos.Should().OnlyContain(t => t.PlantillaId == plantilla.Id);
        var vacia = () => PlantillaRequisitosEvento.Crear(Guid.NewGuid(), "Vacía", []);
        vacia.Should().Throw<ArgumentException>();
    }
}

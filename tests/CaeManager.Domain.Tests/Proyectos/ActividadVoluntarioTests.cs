using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Proyectos;

/// <summary>
/// Voluntario inactivo (decisión 2026-10-15): un voluntario sin evento vigente ni futuro asignado pasa a
/// inactivo, sin borrarse; se reactiva al asignarlo a otro evento. Es un dato derivado, no un flag.
/// Los requisitos del evento cuentan desde la asignación mientras el evento es futuro o vigente (opción B, decisión
/// 2026-10-16, <see cref="Proyecto.EventoAportaRequisitosEn"/>).
/// </summary>
public class ActividadVoluntarioTests
{
    private static readonly DateOnly Hoy = new(2026, 11, 7);

    private static Proyecto Evento(DateOnly inicio, DateOnly fin) =>
        Proyecto.Crear(Guid.NewGuid(), Guid.NewGuid(), "Partido", inicio, fin, null, TipoProyecto.Evento);

    private static Proyecto Obra() => Proyecto.Crear(Guid.NewGuid(), Guid.NewGuid(), "Nave 2", Hoy.AddDays(-30), null, null);

    [Fact]
    public void Un_voluntario_sin_eventos_esta_inactivo()
    {
        ActividadVoluntario.EstaInactivo(TipoContratacion.Voluntario, [], Hoy).Should().BeTrue();
    }

    [Theory]
    [InlineData(-1, 1, "vigente")]
    [InlineData(5, 6, "futuro")]
    [InlineData(-3, 0, "termina hoy")]
    public void Un_voluntario_con_un_evento_vigente_o_futuro_esta_activo(int desde, int hasta, string caso)
    {
        var evento = Evento(Hoy.AddDays(desde), Hoy.AddDays(hasta));

        ActividadVoluntario.EstaInactivo(TipoContratacion.Voluntario, [evento], Hoy).Should().BeFalse(caso);
    }

    [Fact]
    public void Un_voluntario_cuyo_unico_evento_ya_paso_o_se_cerro_esta_inactivo()
    {
        var pasado = Evento(Hoy.AddDays(-10), Hoy.AddDays(-1));
        var cerrado = Evento(Hoy.AddDays(-2), Hoy.AddDays(3));
        cerrado.Cerrar(Hoy.AddDays(-1));

        ActividadVoluntario.EstaInactivo(TipoContratacion.Voluntario, [pasado, cerrado], Hoy).Should().BeTrue();
    }

    [Fact]
    public void Una_obra_no_mantiene_activo_a_un_voluntario()
    {
        ActividadVoluntario.EstaInactivo(TipoContratacion.Voluntario, [Obra()], Hoy).Should().BeTrue();
    }

    [Fact]
    public void Se_reactiva_al_asignarlo_a_otro_evento()
    {
        var pasado = Evento(Hoy.AddDays(-10), Hoy.AddDays(-1));
        ActividadVoluntario.EstaInactivo(TipoContratacion.Voluntario, [pasado], Hoy).Should().BeTrue();

        ActividadVoluntario.EstaInactivo(TipoContratacion.Voluntario, [pasado, Evento(Hoy.AddDays(14), Hoy.AddDays(14))], Hoy)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(TipoContratacion.CuentaAjena)]
    [InlineData(TipoContratacion.Autonomo)]
    public void Quien_no_es_voluntario_nunca_esta_inactivo(TipoContratacion tipo)
    {
        ActividadVoluntario.EstaInactivo(tipo, [], Hoy).Should().BeFalse();
    }

    [Fact]
    public void Un_evento_aporta_sus_requisitos_mientras_es_futuro_o_vigente_y_no_despues_de_terminar()
    {
        var evento = Evento(Hoy, Hoy.AddDays(1));

        evento.EventoAportaRequisitosEn(Hoy.AddDays(-10)).Should().BeTrue("futuro: cuentan desde la asignación (opción B)");
        evento.EventoAportaRequisitosEn(Hoy).Should().BeTrue();
        evento.EventoAportaRequisitosEn(Hoy.AddDays(1)).Should().BeTrue("el día de fin aún cuenta");
        evento.EventoAportaRequisitosEn(Hoy.AddDays(2)).Should().BeFalse("terminado");
        Obra().EventoAportaRequisitosEn(Hoy).Should().BeFalse("una obra no es un evento");

        var cerrado = Evento(Hoy.AddDays(-2), Hoy.AddDays(6));
        cerrado.Cerrar(Hoy.AddDays(-1));
        cerrado.EventoAportaRequisitosEn(Hoy).Should().BeFalse("cerrado antes de su fecha de fin");
    }

    /// <summary>
    /// El criterio es uno solo (<see cref="VigenciaEvento"/>): la regla en memoria y el predicado que se traduce a SQL dan lo
    /// mismo en todos los casos, así que cambiar de criterio no puede dejar uno de los dos atrás.
    /// </summary>
    [Fact]
    public void La_regla_en_memoria_y_el_predicado_traducible_a_SQL_coinciden_dia_a_dia()
    {
        var abierto = Evento(Hoy, Hoy.AddDays(1));
        var cerrado = Evento(Hoy.AddDays(-3), Hoy.AddDays(3));
        cerrado.Cerrar(Hoy.AddDays(-1));
        var sinTerminarEn = (DateOnly dia) => VigenciaEvento.SinTerminarEn(dia).Compile();
        var aportaRequisitosEn = (DateOnly dia) => VigenciaEvento.AportaRequisitosEn(dia).Compile();

        foreach (var proyecto in new[] { abierto, cerrado, Obra() })
            for (var desplazamiento = -2; desplazamiento <= 3; desplazamiento++)
            {
                var dia = Hoy.AddDays(desplazamiento);
                sinTerminarEn(dia)(proyecto).Should().Be(proyecto.EsEvento && !proyecto.EventoTerminadoEn(dia), $"{proyecto.Nombre} el {dia}");
                aportaRequisitosEn(dia)(proyecto).Should().Be(proyecto.EventoAportaRequisitosEn(dia), $"{proyecto.Nombre} el {dia}");
                aportaRequisitosEn(dia)(proyecto).Should().Be(sinTerminarEn(dia)(proyecto), $"opción B: aporta requisitos mientras está sin terminar, {proyecto.Nombre} el {dia}");
            }

        aportaRequisitosEn(Hoy.AddDays(-1))(abierto).Should().BeTrue("opción B: el evento futuro ya aporta sus requisitos");
        sinTerminarEn(Hoy.AddDays(-1))(abierto).Should().BeTrue("y mantiene activo al voluntario");
    }
}

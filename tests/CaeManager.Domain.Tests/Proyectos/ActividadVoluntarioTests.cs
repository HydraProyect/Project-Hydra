using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Proyectos;

/// <summary>
/// Voluntario inactivo (decisión 2026-10-15): un voluntario sin evento vigente ni futuro asignado pasa a
/// inactivo, sin borrarse; se reactiva al asignarlo a otro evento. Es un dato derivado, no un flag.
/// Vigente = ya empezó y no ha terminado (opción A, aislada en <see cref="Proyecto.EventoVigenteEn"/>).
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
    public void Un_evento_es_vigente_desde_su_inicio_hasta_su_fin_y_no_antes_ni_despues()
    {
        var evento = Evento(Hoy, Hoy.AddDays(1));

        evento.EventoVigenteEn(Hoy.AddDays(-1)).Should().BeFalse("aún no ha empezado (opción A)");
        evento.EventoVigenteEn(Hoy).Should().BeTrue();
        evento.EventoVigenteEn(Hoy.AddDays(1)).Should().BeTrue("el día de fin aún cuenta");
        evento.EventoVigenteEn(Hoy.AddDays(2)).Should().BeFalse();
        Obra().EventoVigenteEn(Hoy).Should().BeFalse("una obra no es un evento");
    }
}

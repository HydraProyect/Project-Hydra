using CaeManager.Domain.Visitas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Visitas;

public class CalculadoraUrgenciaVisitaTests
{
    private static readonly DateOnly Hoy = new(2026, 7, 15);
    private const int HorasAviso = 48;
    private const int HorasCriticas = 24;

    [Fact]
    public void Retorna_EnCurso_cuando_hoy_cae_dentro_del_rango_de_la_visita()
    {
        var nivel = CalculadoraUrgenciaVisita.Calcular(
            Hoy.AddDays(-1), Hoy.AddDays(1), Hoy, HorasAviso, HorasCriticas);

        nivel.Should().Be(NivelUrgenciaVisita.EnCurso);
    }

    [Fact]
    public void Retorna_Normal_cuando_la_visita_ya_termino()
    {
        var nivel = CalculadoraUrgenciaVisita.Calcular(
            Hoy.AddDays(-5), Hoy.AddDays(-2), Hoy, HorasAviso, HorasCriticas);

        nivel.Should().Be(NivelUrgenciaVisita.Normal);
    }

    [Fact]
    public void Retorna_EnCurso_cuando_la_visita_empieza_y_termina_hoy()
    {
        var nivel = CalculadoraUrgenciaVisita.Calcular(
            Hoy, Hoy, Hoy, HorasAviso, HorasCriticas);

        // FechaInicio == hoy también cae en el rango EnCurso (FechaInicio <= hoy <= FechaFin),
        // así que una visita que empieza y termina hoy es "en curso", no "crítica" — es el
        // caso límite de una visita de un solo día.
        nivel.Should().Be(NivelUrgenciaVisita.EnCurso);
    }

    [Fact]
    public void Retorna_Critica_cuando_la_visita_empieza_manana_dentro_de_las_horas_criticas()
    {
        var nivel = CalculadoraUrgenciaVisita.Calcular(
            Hoy.AddDays(1), Hoy.AddDays(1), Hoy, HorasAviso, HorasCriticas);

        // Mañana = 24h desde hoy, igual al umbral crítico.
        nivel.Should().Be(NivelUrgenciaVisita.Critica);
    }

    [Fact]
    public void Retorna_Urgente_cuando_la_visita_empieza_pasado_manana_dentro_de_las_horas_de_aviso()
    {
        var nivel = CalculadoraUrgenciaVisita.Calcular(
            Hoy.AddDays(2), Hoy.AddDays(2), Hoy, HorasAviso, HorasCriticas);

        // Pasado mañana = 48h desde hoy, igual al umbral de aviso.
        nivel.Should().Be(NivelUrgenciaVisita.Urgente);
    }

    [Fact]
    public void Retorna_Normal_cuando_la_visita_empieza_fuera_de_la_ventana_de_aviso()
    {
        var nivel = CalculadoraUrgenciaVisita.Calcular(
            Hoy.AddDays(5), Hoy.AddDays(5), Hoy, HorasAviso, HorasCriticas);

        nivel.Should().Be(NivelUrgenciaVisita.Normal);
    }
}

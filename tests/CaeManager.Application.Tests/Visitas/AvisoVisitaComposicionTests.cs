using CaeManager.Application.Visitas.Queries.ObtenerAvisoVisita;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Visitas;

/// <summary>
/// P1-X2: el texto del aviso de visita a un Centro sin gestión CAE. La
/// ausencia de DNI con datos reales la prueba la integración de la Query
/// (<c>ObtenerAvisoVisitaQueryTests</c>); aquí, el contenido y su forma.
/// </summary>
public class AvisoVisitaComposicionTests
{
    private static DatosAvisoVisita Datos(
        DateOnly? fin = null, TimeOnly? hora = null, IReadOnlyList<TrabajadorAvisoVisita>? trabajadores = null) =>
        new("Oficina Huesca",
            new DateOnly(2026, 9, 28),
            fin ?? new DateOnly(2026, 9, 28),
            hora,
            trabajadores ?? [new TrabajadorAvisoVisita("Ana López", "Instalaciones Norte SL")],
            "Mantenimientos Ebro SL");

    [Fact]
    public void Incluye_centro_fecha_hora_y_quien_acude_con_su_empresa()
    {
        var aviso = ObtenerAvisoVisitaQueryHandler.Componer(Datos(
            hora: new TimeOnly(8, 30),
            trabajadores:
            [
                new TrabajadorAvisoVisita("Ana López", "Instalaciones Norte SL"),
                new TrabajadorAvisoVisita("Luis Pérez", "Subcontrata Sur SL")
            ]));

        aviso.Asunto.Should().Be("Aviso de visita — Oficina Huesca — 28/09/2026");
        var lineas = aviso.Cuerpo.Split('\n');
        lineas.Should().Contain("Os avisamos de una visita a Oficina Huesca.");
        lineas.Should().Contain("Fecha: 28/09/2026");
        lineas.Should().Contain("Hora estimada de llegada: 08:30");
        lineas.Should().Contain("Acuden:");
        lineas.Should().Contain("- Ana López (Instalaciones Norte SL)");
        lineas.Should().Contain("- Luis Pérez (Subcontrata Sur SL)");
        lineas[^1].Should().Be("Mantenimientos Ebro SL");
    }

    [Fact]
    public void Una_visita_de_varios_dias_da_el_intervalo()
    {
        var aviso = ObtenerAvisoVisitaQueryHandler.Componer(Datos(fin: new DateOnly(2026, 9, 30)));

        aviso.Cuerpo.Split('\n').Should().Contain("Fechas: del 28/09/2026 al 30/09/2026");
    }

    [Fact]
    public void Sin_hora_ni_trabajadores_lo_dice_en_vez_de_inventarlo()
    {
        var aviso = ObtenerAvisoVisitaQueryHandler.Componer(Datos(trabajadores: []));

        var lineas = aviso.Cuerpo.Split('\n');
        lineas.Should().Contain("Hora estimada de llegada: por confirmar");
        lineas.Should().Contain("Personas que acuden: por confirmar.");
    }

    [Fact]
    public void Usa_saltos_de_linea_unix_en_cualquier_sistema()
    {
        var aviso = ObtenerAvisoVisitaQueryHandler.Componer(Datos());

        aviso.Cuerpo.Should().NotContain("\r");
    }
}

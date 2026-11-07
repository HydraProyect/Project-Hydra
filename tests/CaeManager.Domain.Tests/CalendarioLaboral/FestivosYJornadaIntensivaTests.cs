using CaeManager.Domain.CalendarioLaboral;
using FluentAssertions;

namespace CaeManager.Domain.Tests.CalendarioLaboral;

/// <summary>
/// Reglas de festivos por ubicación de la persona y de jornada intensiva (Especificación de Ausencias y
/// Coberturas, § 3, § 4.1, D1 y D2) sobre el catálogo oficial sembrado.
/// </summary>
public class FestivosYJornadaIntensivaTests
{
    private static readonly IReadOnlyList<FestivoOficial> Catalogo = CatalogoFestivosOficiales.Todos;

    private static PerfilLaboral En(ComunidadAutonoma comunidad, bool intensiva = true, string? municipio = null) =>
        new(comunidad, municipio, intensiva);

    [Fact]
    public void Un_festivo_autonomico_solo_lo_es_para_quien_trabaja_en_esa_comunidad()
    {
        var difuntos = new DateOnly(2026, 11, 2);

        En(ComunidadAutonoma.Aragon).EsLaborable(difuntos, Catalogo).Should().BeFalse("el 2 de noviembre de 2026 es festivo en Aragón");
        En(ComunidadAutonoma.PaisVasco).EsLaborable(difuntos, Catalogo).Should().BeTrue("en el País Vasco no lo es");
        PerfilLaboral.SinConfigurar.EsLaborable(difuntos, Catalogo).Should().BeTrue("sin comunidad solo cuentan los nacionales");
    }

    [Fact]
    public void Un_festivo_nacional_lo_es_para_todos_incluso_sin_comunidad()
    {
        var hispanidad = new DateOnly(2026, 10, 12);

        foreach (var comunidad in CatalogoFestivosOficiales.ComunidadesCubiertas)
            En(comunidad).EsLaborable(hispanidad, Catalogo).Should().BeFalse($"es nacional también en {comunidad.Nombre()}");
        PerfilLaboral.SinConfigurar.EsLaborable(hispanidad, Catalogo).Should().BeFalse();
    }

    [Fact]
    public void Sin_comunidad_solo_le_aplican_los_festivos_nacionales()
    {
        PerfilLaboral.SinConfigurar.FestivosQueLeAplican(Catalogo)
            .Should().NotBeEmpty()
            .And.OnlyContain(f => f.Ambito == AmbitoFestivo.Nacional);
    }

    [Fact]
    public void Cada_comunidad_cubierta_suma_doce_festivos_retribuidos_por_anio()
    {
        // Estatuto de los Trabajadores, art. 37.2: hasta catorce, de ellos dos locales; el catálogo no siembra
        // locales, así que cada comunidad cubierta debe sumar los doce nacionales y autonómicos.
        foreach (var comunidad in CatalogoFestivosOficiales.ComunidadesCubiertas)
        foreach (var anio in new[] { 2026, 2027 })
        {
            En(comunidad).FestivosQueLeAplican(Catalogo).Count(f => f.Fecha.Year == anio)
                .Should().Be(12, $"{comunidad.Nombre()} {anio}");
        }
    }

    [Fact]
    public void Los_festivos_que_le_aplican_salen_ordenados_por_fecha_y_sin_los_de_otra_comunidad()
    {
        var festivos = En(ComunidadAutonoma.Cataluna).FestivosQueLeAplican(Catalogo);

        festivos.Select(f => f.Fecha).Should().BeInAscendingOrder();
        festivos.Where(f => f.Ambito == AmbitoFestivo.Autonomico).Should().OnlyContain(f => f.Comunidad == ComunidadAutonoma.Cataluna);
        festivos.Should().Contain(f => f.Fecha == new DateOnly(2026, 9, 11), "la Diada es autonómica de Cataluña");
    }

    [Fact]
    public void Un_festivo_local_solo_aplica_al_municipio_de_su_comunidad()
    {
        var pilar = FestivoOficial.Local(new DateOnly(2026, 10, 13), "Traslado del Pilar", ComunidadAutonoma.Aragon, "Zaragoza");

        pilar.AplicaA(En(ComunidadAutonoma.Aragon, municipio: " zaragoza ")).Should().BeTrue("el municipio se compara sin espacios ni mayúsculas");
        pilar.AplicaA(En(ComunidadAutonoma.Aragon, municipio: "Huesca")).Should().BeFalse();
        pilar.AplicaA(En(ComunidadAutonoma.Aragon)).Should().BeFalse("sin municipio no hay locales");
        pilar.AplicaA(En(ComunidadAutonoma.Cataluna, municipio: "Zaragoza")).Should().BeFalse("el municipio va con su comunidad");
    }

    [Fact]
    public void El_fin_de_semana_nunca_es_laborable()
    {
        En(ComunidadAutonoma.Aragon).EsLaborable(new DateOnly(2026, 11, 7), []).Should().BeFalse("sábado");
        En(ComunidadAutonoma.Aragon).EsLaborable(new DateOnly(2026, 11, 8), []).Should().BeFalse("domingo");
        En(ComunidadAutonoma.Aragon).EsLaborable(new DateOnly(2026, 11, 9), []).Should().BeTrue("lunes sin festivo");
    }

    [Theory]
    [InlineData(2027, 7, 1, true)]   // primer día del periodo (jueves)
    [InlineData(2027, 8, 31, true)]  // último día del periodo (martes)
    [InlineData(2027, 6, 30, false)] // la víspera
    [InlineData(2027, 9, 1, false)]  // el día siguiente
    [InlineData(2027, 7, 3, false)]  // sábado dentro del periodo
    public void La_jornada_intensiva_va_del_uno_de_julio_al_treinta_y_uno_de_agosto_en_dias_laborables(int anio, int mes, int dia, bool esperado)
    {
        En(ComunidadAutonoma.PaisVasco)
            .TieneJornadaIntensiva(new DateOnly(anio, mes, dia), Catalogo, PeriodoIntensiva.Verano)
            .Should().Be(esperado);
    }

    [Fact]
    public void Un_festivo_suyo_dentro_del_periodo_no_es_dia_de_intensiva()
    {
        var santiago = new DateOnly(2026, 7, 24); // viernes; el 25 de julio de 2026 cae en sábado
        var asuncion = new DateOnly(2026, 8, 14); // viernes laborable antes del 15 de agosto (sábado)
        var lunesAsuncion2027 = new DateOnly(2027, 8, 16); // festivo autonómico en Aragón en 2027

        En(ComunidadAutonoma.Aragon).TieneJornadaIntensiva(lunesAsuncion2027, Catalogo, PeriodoIntensiva.Verano).Should().BeFalse();
        En(ComunidadAutonoma.Cataluna).TieneJornadaIntensiva(lunesAsuncion2027, Catalogo, PeriodoIntensiva.Verano).Should().BeTrue();
        En(ComunidadAutonoma.Cataluna).TieneJornadaIntensiva(santiago, Catalogo, PeriodoIntensiva.Verano).Should().BeTrue();
        En(ComunidadAutonoma.Cataluna).TieneJornadaIntensiva(asuncion, Catalogo, PeriodoIntensiva.Verano).Should().BeTrue();
    }

    [Fact]
    public void Sin_el_interruptor_aplica_la_persona_no_tiene_dias_de_intensiva()
    {
        En(ComunidadAutonoma.Aragon, intensiva: false)
            .TieneJornadaIntensiva(new DateOnly(2027, 7, 1), Catalogo, PeriodoIntensiva.Verano)
            .Should().BeFalse();
        PerfilLaboral.SinConfigurar.AplicaJornadaIntensiva.Should().BeTrue("la persona sin calendario guardado lleva la intensiva del equipo");
    }

    [Fact]
    public void El_municipio_exige_comunidad_y_se_guarda_limpio()
    {
        var sinComunidad = () => new PerfilLaboral(null, "Bilbao", true);
        sinComunidad.Should().Throw<ArgumentException>();

        new PerfilLaboral(ComunidadAutonoma.PaisVasco, "  Bilbao ", true).Municipio.Should().Be("Bilbao");
        new PerfilLaboral(ComunidadAutonoma.PaisVasco, "   ", true).Municipio.Should().BeNull();

        var largo = () => new PerfilLaboral(ComunidadAutonoma.PaisVasco, new string('x', FestivoOficial.LongitudMaximaMunicipio + 1), true);
        largo.Should().Throw<ArgumentException>();
        var desconocida = () => new PerfilLaboral((ComunidadAutonoma)99, null, true);
        desconocida.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void El_calendario_de_la_persona_guarda_y_devuelve_su_perfil()
    {
        var usuario = Guid.NewGuid();
        var calendario = CalendarioLaboralPersona.Crear(usuario, new PerfilLaboral(ComunidadAutonoma.Aragon, "Zaragoza", true));

        calendario.UsuarioId.Should().Be(usuario);
        calendario.Perfil.Comunidad.Should().Be(ComunidadAutonoma.Aragon);
        calendario.Perfil.Municipio.Should().Be("Zaragoza");

        calendario.Cambiar(new PerfilLaboral(ComunidadAutonoma.Cataluna, null, false));
        calendario.Perfil.Comunidad.Should().Be(ComunidadAutonoma.Cataluna);
        calendario.Perfil.Municipio.Should().BeNull();
        calendario.Perfil.AplicaJornadaIntensiva.Should().BeFalse();
    }

    [Fact]
    public void El_catalogo_no_repite_identificadores_y_son_estables()
    {
        Catalogo.Select(f => f.Id).Should().OnlyHaveUniqueItems();
        // El Id es determinista: volver a construir la misma fila da el mismo Id (HasData no reescribe la tabla).
        FestivoOficial.Nacional(new DateOnly(2026, 1, 1), "Año Nuevo").Id
            .Should().Be(Catalogo.Single(f => f.Fecha == new DateOnly(2026, 1, 1)).Id);
        FestivoOficial.Local(new DateOnly(2026, 10, 13), "X", ComunidadAutonoma.Aragon, "Zaragoza").Id
            .Should().NotBe(FestivoOficial.Local(new DateOnly(2026, 10, 13), "X", ComunidadAutonoma.Aragon, "Huesca").Id);
    }

    [Theory]
    [InlineData(7, 1, 8, 31)]
    public void El_periodo_de_verano_va_del_uno_de_julio_al_treinta_y_uno_de_agosto(int mesDesde, int diaDesde, int mesHasta, int diaHasta)
    {
        PeriodoIntensiva.Verano.MesDesde.Should().Be(mesDesde);
        PeriodoIntensiva.Verano.DiaDesde.Should().Be(diaDesde);
        PeriodoIntensiva.Verano.MesHasta.Should().Be(mesHasta);
        PeriodoIntensiva.Verano.DiaHasta.Should().Be(diaHasta);

        var alReves = () => new PeriodoIntensiva("Mal", 9, 1, 7, 1);
        alReves.Should().Throw<ArgumentException>();
    }
}

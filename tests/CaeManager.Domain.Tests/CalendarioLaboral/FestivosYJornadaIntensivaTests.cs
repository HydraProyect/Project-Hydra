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

    [Theory]
    // Contrastado con el anexo de BOE-A-2026-21254 el 2026-11-07: el 15 de agosto de 2027 es domingo y cada
    // comunidad lo traslada o lo sustituye a su manera; el País Vasco no tiene el 19 de marzo ese año.
    [InlineData(ComunidadAutonoma.Aragon, "2027-03-25,2027-04-23,2027-08-16")]
    [InlineData(ComunidadAutonoma.Cataluna, "2027-03-29,2027-06-24,2027-09-11")]
    [InlineData(ComunidadAutonoma.Extremadura, "2027-03-25,2027-09-08,2027-10-11")]
    [InlineData(ComunidadAutonoma.PaisVasco, "2027-03-25,2027-03-29,2027-10-07")]
    public void Los_autonomicos_de_2027_son_los_del_BOE(ComunidadAutonoma comunidad, string fechas)
    {
        Catalogo.Where(f => f.Ambito == AmbitoFestivo.Autonomico && f.Comunidad == comunidad && f.Fecha.Year == 2027)
            .Select(f => f.Fecha.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))
            .Should().Equal(fechas.Split(','));
    }

    [Fact]
    public void Los_anios_cubiertos_son_los_sembrados_y_cada_uno_tiene_sus_nacionales()
    {
        // Sin reloj a propósito: un trinquete de caducidad pondría en rojo todas las PR el día que venza. La
        // caducidad se ve en /calendario, que avisa en un año sin catálogo. Ampliar el catálogo cambia esta lista.
        CatalogoFestivosOficiales.AniosCubiertos.Should().Equal(2026, 2027);
        foreach (var anio in CatalogoFestivosOficiales.AniosCubiertos)
            Catalogo.Count(f => f.Ambito == AmbitoFestivo.Nacional && f.Fecha.Year == anio)
                .Should().BeGreaterThanOrEqualTo(8, $"los nacionales de {anio}");
    }

    [Theory]
    [InlineData(2026, ComunidadAutonoma.Aragon, true)]
    [InlineData(2027, null, true)]
    [InlineData(2028, ComunidadAutonoma.Aragon, false)]
    [InlineData(2028, null, false)]
    [InlineData(2027, ComunidadAutonoma.Madrid, false)]
    public void Cubre_exige_el_anio_y_la_comunidad_en_el_catalogo(int anio, ComunidadAutonoma? comunidad, bool esperado) =>
        CatalogoFestivosOficiales.Cubre(anio, comunidad is { } c ? En(c) : PerfilLaboral.SinConfigurar).Should().Be(esperado);

    [Theory]
    // Idescat, comarca de Aran (2026-11-07): sus nueve municipios, y formas que alguien puede escribir.
    [InlineData("Vielha e Mijaran")]
    [InlineData("Bossòst")]
    [InlineData("bossost")]
    [InlineData("Naut Aran")]
    [InlineData("Es Bòrdes")]
    [InlineData("Les")]
    [InlineData("Vilamòs")]
    [InlineData("Arres")]
    [InlineData("Bausen")]
    [InlineData("Canejan")]
    [InlineData("  VIELHA  ")]
    [InlineData("Val d’Aran")]
    [InlineData("Alt Aran")]
    [InlineData("Vielha-Mijaran")]
    [InlineData("Vielha i Mijaran")]
    public void La_Val_d_Aran_no_esta_cubierta_aunque_Cataluna_si(string municipio)
    {
        // BOE-A-2025-21667 y BOE-A-2026-21254, nota 2: en el territorio de Arán el 26-12-2026 y el 29-03-2027 se
        // sustituyen por el 17 de junio (Fiesta de Arán). El catálogo no siembra esas sustituciones, así que no da por
        // conocidos sus autonómicos: ni los de Cataluña, ni la intensiva.
        var aran = En(ComunidadAutonoma.Cataluna, municipio: municipio);

        CatalogoFestivosOficiales.EsDeLaValDAran(aran).Should().BeTrue();
        foreach (var anio in CatalogoFestivosOficiales.AniosCubiertos)
            CatalogoFestivosOficiales.Cubre(anio, aran).Should().BeFalse($"{municipio} {anio}");
        aran.FestivosQueLeAplican(Catalogo).Should().NotBeEmpty()
            .And.OnlyContain(f => f.Ambito == AmbitoFestivo.Nacional, "el cuadro general de Cataluña no es el de Arán");
        aran.EsLaborable(new DateOnly(2027, 3, 26), Catalogo).Should().BeFalse("el Viernes Santo es nacional");
        aran.EsLaborable(new DateOnly(2027, 3, 29), Catalogo).Should().BeTrue("el Lunes de Pascua de 2027 está sustituido en Arán");
    }

    [Theory]
    // Espacios que no son el ordinario (copiar y pegar de una hoja o de un PDF): NBSP, tabulador, dobles, espacio
    // estrecho NBSP (U+202F), espacio ideográfico, espacio de ancho cero rodeado de espacios y salto de línea.
    [InlineData("Vielha e Mijaran")]
    [InlineData("Vielha e\tMijaran")]
    [InlineData("Vielha  e   Mijaran")]
    [InlineData(" Naut  Aran ")]
    [InlineData("Naut\tAran")]
    [InlineData("Alt Aran")]
    [InlineData("Vielha　i Mijaran")]
    [InlineData("Vielha \u200Be\u200B Mijaran")]
    // Caracteres de formato (categoría Format) dentro de una palabra: guion blando, ancho cero U+200C y U+200D, unión de
    // palabras U+2060 y BOM. Son invisibles y se eliminan; no deben impedir el reconocimiento.
    [InlineData("Viel\u00ADha e Mijaran")]
    [InlineData("Vielha e Mi\u200Cjaran")]
    [InlineData("Vielha e Mij\u200Daran")]
    [InlineData("Naut Ar\u2060an")]
    [InlineData("Naut Ara\uFEFFn")]
    [InlineData("Vi\u00ADel\u200Bha\u200C e\u2060 Mi\uFEFFj\u200Dar\u00ADan")]
    [InlineData("Naut\r\nAran")]
    [InlineData("Val d’Aran")]
    public void La_Val_d_Aran_se_reconoce_aunque_el_texto_traiga_espacios_no_ordinarios(string municipio)
    {
        var aran = En(ComunidadAutonoma.Cataluna, municipio: municipio);

        CatalogoFestivosOficiales.EsDeLaValDAran(aran).Should().BeTrue();
        foreach (var anio in CatalogoFestivosOficiales.AniosCubiertos)
            CatalogoFestivosOficiales.Cubre(anio, aran).Should().BeFalse($"{municipio} {anio}");
        aran.EsLaborable(new DateOnly(2027, 3, 29), Catalogo).Should().BeTrue("el Lunes de Pascua de 2027 está sustituido en Arán");
    }

    [Theory]
    // Control: los mismos espacios en un municipio que no es de Arán no lo convierten en Arán, y los espacios no unen
    // palabras: «Naut Aran» sí es Arán, pero «NautAran» pegado no.
    [InlineData("Barcelona")]
    [InlineData("Lleida ")]
    [InlineData("L’Hospitalet de Llobregat")]
    [InlineData("Sant\t\tCugat")]
    [InlineData("Salardú ")]
    [InlineData("NautAran")]
    [InlineData("Vielhae Mijaran")]
    // Un carácter de formato (guion blando, ancho cero...) no es un espacio: no une ni separa, solo desaparece.
    [InlineData("Barce\u00ADlona")]
    [InlineData("Naut\u00ADAran")]
    [InlineData("Vielha\u200Be\u200CMijaran")]
    public void Los_espacios_no_ordinarios_no_convierten_en_Aran_un_municipio_que_no_lo_es(string municipio)
    {
        var perfil = En(ComunidadAutonoma.Cataluna, municipio: municipio);

        CatalogoFestivosOficiales.EsDeLaValDAran(perfil).Should().BeFalse(municipio);
        CatalogoFestivosOficiales.Cubre(2027, perfil).Should().BeTrue();
        perfil.EsLaborable(new DateOnly(2027, 3, 29), Catalogo).Should().BeFalse("es Lunes de Pascua en Cataluña");
    }

    [Fact]
    public void El_resto_de_Cataluna_sigue_cubierto_con_su_cuadro_general()
    {
        var barcelona = En(ComunidadAutonoma.Cataluna, municipio: "Barcelona");
        var lleida = En(ComunidadAutonoma.Cataluna, municipio: "Lleida");
        var sinMunicipio = En(ComunidadAutonoma.Cataluna);
        var lesEnAragon = En(ComunidadAutonoma.Aragon, municipio: "Les");

        foreach (var perfil in new[] { barcelona, lleida, sinMunicipio })
        {
            CatalogoFestivosOficiales.EsDeLaValDAran(perfil).Should().BeFalse();
            CatalogoFestivosOficiales.Cubre(2027, perfil).Should().BeTrue();
            perfil.EsLaborable(new DateOnly(2027, 3, 29), Catalogo).Should().BeFalse("es Lunes de Pascua en Cataluña");
        }
        CatalogoFestivosOficiales.EsDeLaValDAran(lesEnAragon).Should().BeFalse("solo cuenta en Cataluña");
    }

    [Fact]
    public void El_catalogo_no_tiene_festivos_locales_y_con_municipio_lo_dice()
    {
        Catalogo.Should().NotContain(f => f.Ambito == AmbitoFestivo.Local, "no se añaden locales sin su resolución oficial");

        CatalogoFestivosOficiales.IncluyeLocales(2026, En(ComunidadAutonoma.Aragon, municipio: "Huesca")).Should().BeFalse(
            "el 10 de agosto es festivo local de Huesca y el catálogo no lo tiene");
        CatalogoFestivosOficiales.IncluyeLocales(2026, En(ComunidadAutonoma.Aragon)).Should().BeFalse(
            "con comunidad y sin municipio no se sabe qué locales le tocan");
        CatalogoFestivosOficiales.IncluyeLocales(2026, PerfilLaboral.SinConfigurar).Should().BeTrue("sin comunidad, § 3 le aplica solo los nacionales");
    }

    [Fact]
    public void Los_festivos_locales_de_un_municipio_se_siembran_los_dos_de_su_anio_o_ninguno()
    {
        // IncluyeLocales da por cubierto un municipio con que tenga uno: solo vale si nunca se siembra uno suelto.
        // Hoy no hay ninguno: vale como trinquete para el primero que se siembre.
        Catalogo.Where(f => f.Ambito == AmbitoFestivo.Local)
            .GroupBy(f => (f.Fecha.Year, f.Comunidad, Municipio: f.Municipio!.ToUpperInvariant()))
            .Where(g => g.Count() != 2)
            .Select(g => g.Key)
            .Should().BeEmpty("el artículo 37.2 del Estatuto de los Trabajadores da dos locales por año");
    }

    [Fact]
    public void Un_festivo_conocido_de_cualquier_ambito_no_es_dia_de_intensiva()
    {
        var jueves = new DateOnly(2027, 7, 15);
        var huesca = En(ComunidadAutonoma.Aragon, municipio: "Huesca");

        huesca.TieneJornadaIntensiva(jueves, [], PeriodoIntensiva.Verano).Should().BeTrue();
        huesca.TieneJornadaIntensiva(jueves, [FestivoOficial.Nacional(jueves, "Nacional")], PeriodoIntensiva.Verano).Should().BeFalse();
        huesca.TieneJornadaIntensiva(jueves, [FestivoOficial.Autonomico(jueves, "Autonómico", ComunidadAutonoma.Aragon)], PeriodoIntensiva.Verano).Should().BeFalse();
        huesca.TieneJornadaIntensiva(jueves, [FestivoOficial.Local(jueves, "Local", ComunidadAutonoma.Aragon, "Huesca")], PeriodoIntensiva.Verano).Should().BeFalse(
            "un local catalogado también la quita");
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

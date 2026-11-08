using Bunit;
using CaeManager.Application.Calendario.Queries;
using CaeManager.Domain.CalendarioLaboral;
using CaeManager.Web.Features.Calendario.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Capas «Festivos» y «Jornada intensiva» de /calendario (maqueta Calendario TALVEG, :102-107 y :141-143): lo que
/// entregan a las celdas según las casillas y «Festivos de», y que una carga fallida no rompe el calendario.
/// </summary>
public class CapasCalendarioTests : BunitContext
{
    private static readonly DateOnly Difuntos = new(2026, 11, 2);
    private static readonly DateOnly Lunes9 = new(2026, 11, 9);

    public CapasCalendarioTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private sealed class MediadorCapas(Func<ObtenerCapasCalendarioQuery, CapasCalendarioDto> responder) : IMediator
    {
        public List<ObtenerCapasCalendarioQuery> Consultas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is not ObtenerCapasCalendarioQuery consulta)
                throw new NotSupportedException(request.GetType().Name);
            Consultas.Add(consulta);
            return Task.FromResult((TResponse)(object)responder(consulta));
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification =>
            Task.CompletedTask;
    }

    private static PersonaCapasCalendarioDto Persona(string? comunidad, IReadOnlyList<FestivoCapaDto> festivos, params DateOnly[] intensiva) =>
        new(comunidad, festivos, intensiva);

    private static readonly FestivoCapaDto DifuntosAragon = new(Difuntos, "Todos los Santos (traslado)", AmbitoFestivo.Autonomico, "Aragón");
    private static readonly FestivoCapaDto Constitucion = new(new DateOnly(2026, 12, 8), "Inmaculada Concepción", AmbitoFestivo.Nacional, null);

    private static CapasCalendarioDto Equipo() => new(true,
    [
        Persona("Aragón", [DifuntosAragon, Constitucion], Lunes9),
        Persona("País Vasco", [Constitucion]),
        Persona("Cataluña", [Constitucion]),
    ]);

    private (IRenderedComponent<CapasCalendario> Componente, Func<MarcasCapasMes> Marcas, MediadorCapas Mediador) Montar(
        Func<ObtenerCapasCalendarioQuery, CapasCalendarioDto> responder, int anio = 2026, int mes = 11)
    {
        var mediador = new MediadorCapas(responder);
        Services.AddScoped<IMediator>(_ => mediador);
        var ultimas = MarcasCapasMes.Vacias;
        var cut = Render<CapasCalendario>(p => p
            .Add(c => c.Anio, anio)
            .Add(c => c.Mes, mes)
            .Add(c => c.MarcasChanged, (MarcasCapasMes m) => ultimas = m));
        return (cut, () => ultimas, mediador);
    }

    [Fact]
    public void Con_las_dos_capas_activas_la_celda_recibe_festivos_con_su_lugar_y_la_intensiva()
    {
        var (cut, marcas, _) = Montar(_ => Equipo());

        cut.Find(".capas-calendario").TextContent.Should().Contain("Capas").And.Contain("Festivos").And.Contain("Jornada intensiva");
        cut.FindAll("input[type=checkbox]").Should().HaveCount(2).And.OnlyContain(c => c.HasAttribute("checked"));

        marcas().Festivos(Difuntos).Should().Equal("Todos los Santos (traslado) · Aragón");
        marcas().Festivos(new DateOnly(2026, 12, 8)).Should().Equal("Inmaculada Concepción · nacional");
        marcas().Intensiva(Lunes9).Should().BeTrue();
        marcas().TextoAccesible(Difuntos).Should().Be(". Festivo: Todos los Santos (traslado) · Aragón");
    }

    [Fact]
    public void Un_festivo_que_comparten_varias_personas_se_pinta_una_sola_vez()
    {
        var (_, marcas, _) = Montar(_ => Equipo());

        marcas().Festivos(new DateOnly(2026, 12, 8)).Should().ContainSingle();
    }

    [Fact]
    public void Festivos_de_una_comunidad_deja_solo_los_de_sus_personas()
    {
        var (cut, marcas, _) = Montar(_ => Equipo());

        cut.FindAll("select option").Select(o => o.TextContent).Should().Equal("Todo el equipo", "Aragón", "Cataluña", "País Vasco");
        cut.Find("select").Change("País Vasco");

        marcas().Festivos(Difuntos).Should().BeEmpty("el traslado de Todos los Santos es de Aragón");
        marcas().Festivos(new DateOnly(2026, 12, 8)).Should().ContainSingle();
        marcas().Intensiva(Lunes9).Should().BeTrue("la intensiva no se filtra por «Festivos de»");

        cut.Find("select").Change("");
        marcas().Festivos(Difuntos).Should().ContainSingle();
    }

    [Fact]
    public void Apagar_una_capa_quita_sus_marcas_y_el_selector()
    {
        var (cut, marcas, _) = Montar(_ => Equipo());

        cut.FindAll("input[type=checkbox]")[0].Change(false);
        marcas().Festivos(Difuntos).Should().BeEmpty();
        cut.FindAll("select").Should().BeEmpty("«Festivos de» solo se ofrece con la capa de festivos activa");
        marcas().Intensiva(Lunes9).Should().BeTrue();

        cut.FindAll("input[type=checkbox]")[1].Change(false);
        marcas().Intensiva(Lunes9).Should().BeFalse();
        marcas().TieneMarcas(Lunes9).Should().BeFalse();
    }

    [Fact]
    public void El_Gestor_CAE_ve_sus_festivos_sin_selector_de_equipo()
    {
        var (cut, marcas, _) = Montar(_ => new CapasCalendarioDto(false, [Persona("Aragón", [DifuntosAragon])]));

        cut.FindAll("select").Should().BeEmpty();
        cut.Find(".capas-nota").TextContent.Should().Be("Ves tus festivos.");
        marcas().Festivos(Difuntos).Should().ContainSingle();
    }

    [Fact]
    public void Un_anio_sin_catalogo_oficial_lo_avisa_en_la_barra()
    {
        var (cut, _, _) = Montar(
            _ => new CapasCalendarioDto(false, [Persona("Aragón", [])]) { CatalogoCubreElAnio = false }, anio: 2028, mes: 7);

        cut.Find(".capas-nota").TextContent.Should().Be(
            "No tenemos el calendario oficial de festivos de 2028: este mes no marca festivos ni jornada intensiva.");
    }

    [Fact]
    public void Si_faltan_festivos_locales_la_barra_lo_avisa()
    {
        var (cut, _, _) = Montar(
            _ => new CapasCalendarioDto(false, [Persona("Aragón", [], new DateOnly(2026, 8, 10))])
            {
                FaltanFestivosLocales = true,
                IntensivaPuedeCaerEnFestivoLocal = true,
            },
            anio: 2026, mes: 8);

        cut.Find(".capas-aviso-locales").TextContent.Should().Be(
            "Los festivos locales no están incluidos: la jornada intensiva puede marcarse en un festivo local.");
        cut.Find(".capas-nota").TextContent.Should().Be("Ves tus festivos.", "el aviso va además de la nota, no en su lugar");

        cut.FindAll("input[type=checkbox]")[1].Change(false);
        cut.Find(".capas-aviso-locales").TextContent.Should().Be("Los festivos locales no están incluidos.",
            "con la capa de intensiva apagada no hay intensiva que pueda caer en un local");
    }

    [Fact]
    public void Sin_intensiva_que_pueda_caer_en_un_local_el_aviso_solo_dice_que_faltan()
    {
        var (cut, _, _) = Montar(
            _ => new CapasCalendarioDto(false, [Persona("Aragón", [])]) { FaltanFestivosLocales = true });

        cut.Find(".capas-aviso-locales").TextContent.Should().Be("Los festivos locales no están incluidos.");
    }

    [Fact]
    public void Una_comunidad_sin_autonomicos_se_avisa_en_la_barra()
    {
        var (cut, _, _) = Montar(
            _ => new CapasCalendarioDto(true, [Persona("Comunidad de Madrid", []), Persona("Aragón", [])])
            {
                ComunidadesSinAutonomicos = ["Comunidad de Madrid"],
            },
            anio: 2027, mes: 7);

        cut.FindAll(".capas-aviso").Select(a => a.TextContent).Should().Contain(
            "Sin festivos autonómicos en el catálogo: Comunidad de Madrid. En esa ubicación no se marcan ni esos festivos ni la jornada intensiva.");
    }

    [Fact]
    public void Sin_municipios_sin_cubrir_no_hay_aviso_de_festivos_locales()
    {
        var (cut, _, _) = Montar(_ => Equipo());

        cut.FindAll(".capas-aviso").Should().BeEmpty();
    }

    [Fact]
    public void Un_anio_sin_catalogo_no_repite_el_aviso_de_festivos_locales()
    {
        var (cut, _, _) = Montar(
            _ => new CapasCalendarioDto(false, [Persona("Aragón", [])]) { CatalogoCubreElAnio = false, FaltanFestivosLocales = true },
            anio: 2028, mes: 7);

        cut.FindAll(".capas-aviso").Should().BeEmpty("sin catálogo del año no se marca nada y ya se avisa de ello");
    }

    [Fact]
    public void Sin_nadie_que_mostrar_no_pinta_la_barra()
    {
        var (cut, marcas, _) = Montar(_ => CapasCalendarioDto.Vacio);

        cut.FindAll(".capas-calendario").Should().BeEmpty();
        marcas().TieneMarcas(Difuntos).Should().BeFalse();
    }

    [Fact]
    public void Si_la_carga_falla_el_calendario_sigue_sin_capas()
    {
        var (cut, marcas, _) = Montar(_ => throw new InvalidOperationException("caída"));

        cut.FindAll(".capas-calendario").Should().BeEmpty();
        marcas().TieneMarcas(Difuntos).Should().BeFalse();
    }

    [Fact]
    public void Cambiar_de_mes_recarga_y_mantener_el_mes_no()
    {
        var (cut, _, mediador) = Montar(_ => Equipo());

        cut.Render(p => p.Add(c => c.Anio, 2026).Add(c => c.Mes, 11));
        cut.Render(p => p.Add(c => c.Anio, 2026).Add(c => c.Mes, 12));

        mediador.Consultas.Should().Equal(new ObtenerCapasCalendarioQuery(2026, 11), new ObtenerCapasCalendarioQuery(2026, 12));
    }

    [Fact]
    public void La_celda_pinta_la_etiqueta_de_tolerancia_y_la_linea_de_intensiva()
    {
        var (_, marcas, _) = Montar(_ => Equipo());

        var celda = Render<MarcasCapasDia>(p => p.Add(c => c.Marcas, marcas()).Add(c => c.Fecha, Difuntos));
        celda.FindAll(".capas-festivo").Select(e => e.TextContent).Should().Equal("Todos los Santos (traslado) · Aragón");
        celda.FindAll(".capas-intensiva").Should().BeEmpty("el 2 de noviembre no es de intensiva");

        var lunes = Render<MarcasCapasDia>(p => p.Add(c => c.Marcas, marcas()).Add(c => c.Fecha, Lunes9));
        lunes.FindAll(".capas-intensiva").Should().ContainSingle();
        lunes.FindAll(".capas-festivo").Should().BeEmpty();
    }

    // En un día sin vencimientos ni visitas la celda no es botón: la línea de intensiva, sin texto, es lo único que
    // la anuncia, y sin nombre accesible un lector de pantalla no la percibe.
    [Theory]
    [InlineData("es-ES", "Jornada intensiva · cuenta como día completo")]
    [InlineData("ca-ES", "Jornada intensiva · compta com a dia complet")]
    public void La_linea_de_intensiva_tiene_nombre_accesible_en_cada_idioma(string cultura, string esperado)
    {
        var anterior = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(cultura);
            var (_, marcas, _) = Montar(_ => Equipo());

            var lunes = Render<MarcasCapasDia>(p => p.Add(c => c.Marcas, marcas()).Add(c => c.Fecha, Lunes9));

            var linea = lunes.Find(".capas-intensiva");
            linea.GetAttribute("role").Should().Be("img");
            linea.GetAttribute("aria-label").Should().Be(esperado);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = anterior;
        }
    }
}

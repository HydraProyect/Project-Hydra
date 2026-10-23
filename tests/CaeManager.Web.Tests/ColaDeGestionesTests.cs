using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Domain.Gestiones;
using CaeManager.Web.Features.Gestiones;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// El orden de la cola de «Gestión rápida»: la primera es la primera del listado; tras despachar una, la siguiente
/// es la primera restante del mismo Tipo de documento y la misma Plataforma CAE, y si no queda ninguna, la primera
/// restante. Sin base de datos ni pantalla: es una regla pura sobre las filas que el listado ya devolvió.
/// </summary>
public class ColaDeGestionesTests
{
    private static readonly Guid Reconocimiento = Guid.NewGuid();
    private static readonly Guid Formacion = Guid.NewGuid();

    private static GestionListaDto G(string trabajador, Guid tipo, string? plataforma, EstadoGestion estado = EstadoGestion.Pendiente) => new(
        Guid.NewGuid(), Guid.NewGuid(), trabajador, Guid.NewGuid(), "Centro Norte", tipo, "Tipo", estado,
        new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
        ClienteEmpresarialId: Guid.NewGuid(), ClienteEmpresarialNombre: "Cliente", PlataformaCaeNombre: plataforma);

    private static IEnumerable<string> Recorrer(ColaDeGestiones cola, DestinoEnCola destino = DestinoEnCola.Completada)
    {
        while (cola.Actual is { } actual)
        {
            yield return actual.TrabajadorNombre;
            cola.Avanzar(destino);
        }
    }

    [Fact]
    public void Sin_pendientes_no_hay_cola()
    {
        ColaDeGestiones.Crear([]).Should().BeNull();
        ColaDeGestiones.Crear([G("Ana", Reconocimiento, "Nalanda", EstadoGestion.Completada)]).Should().BeNull("las completadas no se recorren");
    }

    [Fact]
    public void La_primera_es_la_primera_del_listado_y_las_completadas_no_cuentan()
    {
        var cola = ColaDeGestiones.Crear(
            [G("Hecha", Reconocimiento, "Nalanda", EstadoGestion.Completada), G("Ana", Reconocimiento, "Nalanda"), G("Bea", Formacion, "Dokify")])!;

        cola.Actual!.TrabajadorNombre.Should().Be("Ana");
        cola.Total.Should().Be(2);
        cola.Posicion.Should().Be(1);
    }

    [Fact]
    public void Tras_cada_una_van_primero_las_del_mismo_tipo_y_la_misma_plataforma_y_despues_el_resto_en_orden_de_lista()
    {
        // Orden del listado: Ana(R,Nalanda) Bea(F,Dokify) Cris(R,Dokify) Dani(R,Nalanda) Eva(F,Dokify) Fer(R,Nalanda)
        var cola = ColaDeGestiones.Crear(
        [
            G("Ana", Reconocimiento, "Nalanda"), G("Bea", Formacion, "Dokify"), G("Cris", Reconocimiento, "Dokify"),
            G("Dani", Reconocimiento, "Nalanda"), G("Eva", Formacion, "Dokify"), G("Fer", Reconocimiento, "Nalanda")
        ])!;

        // Ana → sus similares Dani y Fer; agotadas, la primera del resto (Bea) y su similar Eva; al final Cris.
        Recorrer(cola).Should().Equal("Ana", "Dani", "Fer", "Bea", "Eva", "Cris");
    }

    [Fact]
    public void El_mismo_tipo_en_otra_plataforma_no_es_similar_ni_la_misma_plataforma_con_otro_tipo()
    {
        var anterior = G("Ana", Reconocimiento, "Nalanda");
        var mismoTipoOtraPlataforma = G("Bea", Reconocimiento, "Dokify");
        var mismaPlataformaOtroTipo = G("Cris", Formacion, "Nalanda");
        var similar = G("Dani", Reconocimiento, "Nalanda");

        ColaDeGestiones.ElegirSiguiente([mismoTipoOtraPlataforma, mismaPlataformaOtroTipo, similar], anterior)
            .Should().Be(similar);
        ColaDeGestiones.ElegirSiguiente([mismoTipoOtraPlataforma, mismaPlataformaOtroTipo], anterior)
            .Should().Be(mismoTipoOtraPlataforma, "sin similares, la primera restante en el orden del listado");
        ColaDeGestiones.ElegirSiguiente([], anterior).Should().BeNull();
    }

    [Theory]
    [InlineData("Nalanda", "  nalanda ")]
    [InlineData("CTAIMA", "ctaima")]
    [InlineData(null, "")]
    [InlineData(null, "   ")]
    public void La_misma_plataforma_se_compara_sin_mayusculas_ni_espacios_y_sin_plataforma_solo_se_parece_a_sin_plataforma(string? a, string? b)
    {
        ColaDeGestiones.ClaveDeSimilitud(G("Ana", Reconocimiento, a)).Should().Be(ColaDeGestiones.ClaveDeSimilitud(G("Bea", Reconocimiento, b)));
        ColaDeGestiones.ClaveDeSimilitud(G("Ana", Reconocimiento, a)).Should().NotBe(ColaDeGestiones.ClaveDeSimilitud(G("Cris", Reconocimiento, "Otra")));
    }

    [Fact]
    public void Las_gestiones_sin_plataforma_se_agrupan_entre_si_por_tipo()
    {
        var cola = ColaDeGestiones.Crear(
        [
            G("Ana", Reconocimiento, null), G("Bea", Reconocimiento, "Nalanda"), G("Cris", Reconocimiento, null)
        ])!;

        Recorrer(cola).Should().Equal("Ana", "Cris", "Bea");
    }

    [Fact]
    public void El_contador_es_N_de_M_y_avanza_con_cada_despachada_sin_pasar_de_M()
    {
        var cola = ColaDeGestiones.Crear([G("Ana", Reconocimiento, null), G("Bea", Reconocimiento, null), G("Cris", Reconocimiento, null)])!;

        (cola.Posicion, cola.Total).Should().Be((1, 3));
        cola.Avanzar(DestinoEnCola.Completada);
        (cola.Posicion, cola.Total).Should().Be((2, 3));
        cola.Avanzar(DestinoEnCola.Saltada);
        (cola.Posicion, cola.Total).Should().Be((3, 3));
        cola.Avanzar(DestinoEnCola.Completada);

        cola.Terminada.Should().BeTrue();
        cola.Actual.Should().BeNull();
        cola.Posicion.Should().Be(3, "la cola acabada no enseña «4 de 3»");
        (cola.Completadas, cola.Saltadas).Should().Be((2, 1));
    }

    [Fact]
    public void Una_saltada_no_vuelve_a_salir_en_esta_cola()
    {
        var cola = ColaDeGestiones.Crear([G("Ana", Reconocimiento, null), G("Bea", Formacion, null)])!;

        Recorrer(cola, DestinoEnCola.Saltada).Should().Equal("Ana", "Bea");
        cola.Terminada.Should().BeTrue();
    }

    [Fact]
    public void Avanzar_con_la_cola_acabada_no_hace_nada()
    {
        var cola = ColaDeGestiones.Crear([G("Ana", Reconocimiento, null)])!;
        cola.Avanzar(DestinoEnCola.Completada);

        cola.Avanzar(DestinoEnCola.Saltada);

        (cola.Completadas, cola.Saltadas).Should().Be((1, 0));
    }

    [Fact]
    public void Reabrir_una_completada_la_devuelve_a_primer_plano_y_la_que_se_tenia_delante_vuelve_a_su_sitio()
    {
        var ana = G("Ana", Reconocimiento, "Nalanda");
        var bea = G("Bea", Formacion, "Dokify");
        var cris = G("Cris", Reconocimiento, "Nalanda");
        var cola = ColaDeGestiones.Crear([ana, bea, cris])!;
        cola.Avanzar(DestinoEnCola.Completada);   // Ana hecha; delante, Cris (similar)

        cola.Reabrir(ana.Id).Should().BeTrue();

        cola.Actual.Should().Be(ana);
        (cola.Posicion, cola.Completadas).Should().Be((1, 0));
        // Cris, que se tenía delante, vuelve a la cola en su sitio del listado; y siendo similar a Ana, sigue a Ana.
        Recorrer(cola).Should().Equal("Ana", "Cris", "Bea");
    }

    [Fact]
    public void Reabrir_la_ultima_con_la_cola_acabada_la_revive()
    {
        var ana = G("Ana", Reconocimiento, null);
        var cola = ColaDeGestiones.Crear([ana])!;
        cola.Avanzar(DestinoEnCola.Completada);
        cola.Terminada.Should().BeTrue();

        cola.Reabrir(ana.Id).Should().BeTrue();

        cola.Actual.Should().Be(ana);
        cola.Terminada.Should().BeFalse();
    }

    [Fact]
    public void Reabrir_una_que_esta_cola_no_completo_no_toca_la_cola()
    {
        var ana = G("Ana", Reconocimiento, null);
        var bea = G("Bea", Reconocimiento, null);
        var cola = ColaDeGestiones.Crear([ana, bea])!;
        cola.Avanzar(DestinoEnCola.Saltada);

        cola.Reabrir(ana.Id).Should().BeFalse("la saltó, no la completó");
        cola.Reabrir(Guid.NewGuid()).Should().BeFalse();

        cola.Actual.Should().Be(bea);
    }

    [Fact]
    public void Una_cola_se_corta_en_el_tope_y_no_repite_filas()
    {
        var filas = Enumerable.Range(0, ColaDeGestiones.TopeDeGestiones + 25).Select(i => G($"T{i}", Reconocimiento, null)).ToList();
        filas.Add(filas[0]);

        ColaDeGestiones.Crear(filas)!.Total.Should().Be(ColaDeGestiones.TopeDeGestiones);
    }
}

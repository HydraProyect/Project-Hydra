using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Pruebas de la mecánica de <see cref="ListaCongelada"/>, que comparten todos los trinquetes por
/// ubicación: los cuatro desvíos y el formato del fichero. Si esta mecánica se volviera permisiva,
/// todos los trinquetes que dependen de ella darían verde con la regla violada.
/// </summary>
public class ListaCongeladaTests
{
    [Fact]
    public void Un_uso_en_un_fichero_nuevo_un_aumento_una_baja_y_una_entrada_obsoleta_ponen_el_test_en_rojo_cada_uno_por_su_motivo()
    {
        var listado = new Dictionary<Ubicacion, int>
        {
            [new("a.cs", "ClienteId")] = 2,
            [new("b.cs", "ClienteId")] = 3,
            [new("c.cs", "ClienteId")] = 1,
        };
        var medido = new Dictionary<Ubicacion, int>
        {
            [new("a.cs", "ClienteId")] = 3,   // crece
            [new("b.cs", "ClienteId")] = 2,   // baja
            [new("d.cs", "ClienteId")] = 1,   // nueva
            // c.cs ya no se mide: obsoleta
        };

        var desvios = ListaCongelada.Desvios(medido, listado);

        desvios.Should().HaveCount(4);
        desvios.Should().ContainSingle(d => d.StartsWith("CRECE") && d.Contains("a.cs"));
        desvios.Should().ContainSingle(d => d.StartsWith("BAJA") && d.Contains("b.cs"));
        desvios.Should().ContainSingle(d => d.StartsWith("NUEVA") && d.Contains("d.cs"));
        desvios.Should().ContainSingle(d => d.StartsWith("OBSOLETA") && d.Contains("c.cs"));
        ListaCongelada.Desvios(listado, listado).Should().BeEmpty();
    }

    [Fact]
    public void Un_simbolo_nuevo_en_un_fichero_ya_listado_es_una_ubicacion_nueva()
    {
        var listado = new Dictionary<Ubicacion, int> { [new("a.cs", "ClienteId")] = 2 };
        var medido = new Dictionary<Ubicacion, int>
        {
            [new("a.cs", "ClienteId")] = 2,
            [new("a.cs", "OtroClienteId")] = 1,
        };

        ListaCongelada.Desvios(medido, listado).Should().ContainSingle(d => d.StartsWith("NUEVA") && d.Contains("OtroClienteId"));
    }

    [Fact]
    public void Un_escaner_que_se_queda_ciego_pone_en_rojo_todas_las_lineas()
    {
        var listado = new Dictionary<Ubicacion, int>
        {
            [new("a.cs", "X")] = 1,
            [new("b.cs", "X")] = 4,
        };

        ListaCongelada.Desvios(new Dictionary<Ubicacion, int>(), listado)
            .Should().HaveCount(2).And.OnlyContain(d => d.StartsWith("OBSOLETA"));
    }

    [Fact]
    public void El_formato_de_la_lista_rechaza_lo_mal_formado_lo_repetido_y_el_recuento_cero()
    {
        ListaCongelada.Leer("# comentario\n\na.cs :: ClienteId = 2\n").Should().ContainSingle();
        ((Action)(() => ListaCongelada.Leer("a.cs ClienteId 2"))).Should().Throw<FormatException>();
        ((Action)(() => ListaCongelada.Leer("a.cs :: ClienteId = 0"))).Should().Throw<FormatException>();
        ((Action)(() => ListaCongelada.Leer("a.cs :: ClienteId = x"))).Should().Throw<FormatException>();
        ((Action)(() => ListaCongelada.Leer("a.cs :: ClienteId = 1\na.cs :: ClienteId = 2"))).Should().Throw<FormatException>();
    }

    [Fact]
    public void Serializar_y_leer_son_inversos_y_la_salida_es_estable()
    {
        var medido = new Dictionary<Ubicacion, int>
        {
            [new("z/b.cs", "Q -> R")] = 7,
            [new("a/a.cs", "ClienteId")] = 1,
        };

        var texto = ListaCongelada.Serializar(medido);

        texto.Should().Be("a/a.cs :: ClienteId = 1\nz/b.cs :: Q -> R = 7\n");
        ListaCongelada.Leer(texto).Should().Equal(medido);
    }

    // ───────────── Evaluar: el núcleo de Verificar, con lectura y volcado inyectados ─────────────

    private static readonly Dictionary<Ubicacion, int> Medido = new() { [new("a.cs", "X")] = 2 };

    [Fact]
    public void Evaluar_devuelve_null_solo_si_lo_medido_coincide_con_la_lista_leida()
    {
        ListaCongelada.Evaluar("n", "r.txt", Medido, "guia", null, _ => true, _ => "a.cs :: X = 2\n").Should().BeNull();

        var fallo = ListaCongelada.Evaluar("n", "r.txt", Medido, "guia", null, _ => true, _ => "a.cs :: X = 1\n");
        fallo.Should().NotBeNull().And.Contain("CRECE").And.Contain("a.cs :: X").And.Contain("guia").And.Contain("1 desvíos");
    }

    [Fact]
    public void Evaluar_con_la_lista_ausente_es_un_fallo_con_su_nombre_nunca_un_verde()
    {
        ListaCongelada.Evaluar("mi-lista", "r.txt", Medido, "guia", null, _ => false, _ => throw new InvalidOperationException("no debe leer"))
            .Should().Contain("No existe la lista congelada 'mi-lista'");
    }

    [Fact]
    public void Evaluar_trunca_el_mensaje_a_cuarenta_desvios_y_dice_cuantos_faltan()
    {
        var medido = Enumerable.Range(0, 55).ToDictionary(i => new Ubicacion($"f{i:00}.cs", "X"), _ => 1);

        var fallo = ListaCongelada.Evaluar("n", "r.txt", medido, "guia", null, _ => true, _ => "# vacía\n");

        fallo.Should().Contain("55 desvíos").And.Contain("… y 15 más");
        fallo!.Split('\n').Count(l => l.TrimStart().StartsWith("NUEVA")).Should().Be(40);
    }

    [Fact]
    public void Evaluar_vuelca_la_medida_sin_cambiar_el_veredicto_ni_tocar_la_lista()
    {
        var directorio = Path.Combine(Path.GetTempPath(), "volcado-" + Guid.NewGuid().ToString("N"));
        try
        {
            var lista = "a.cs :: X = 2\n";
            var leidas = 0;

            var coincide = ListaCongelada.Evaluar("mi-lista", "r.txt", Medido, "g", directorio, _ => true, _ => { leidas++; return lista; });
            var difiere = ListaCongelada.Evaluar("otra", "r.txt", new Dictionary<Ubicacion, int> { [new("z.cs", "Y")] = 1 }, "g", directorio, _ => true, _ => lista);

            coincide.Should().BeNull("el volcado no cambia el veredicto");
            difiere.Should().NotBeNull("ni lo suaviza");
            leidas.Should().Be(1);
            File.ReadAllText(Path.Combine(directorio, "mi-lista.txt")).Should().Be("a.cs :: X = 2\n");
            File.ReadAllText(Path.Combine(directorio, "otra.txt")).Should().Be("z.cs :: Y = 1\n");
        }
        finally
        {
            if (Directory.Exists(directorio))
                Directory.Delete(directorio, recursive: true);
        }
    }

    [Fact]
    public void Evaluar_sin_directorio_de_volcado_no_escribe_nada()
    {
        // Sin directorio (nulo o vacío) no hay volcado: Directory.CreateDirectory("") lanzaría si el guard
        // desapareciera, así que esta prueba falla por la excepción, que es lo que observa.
        ListaCongelada.Evaluar("n", "r.txt", Medido, "g", "", _ => true, _ => "a.cs :: X = 2\n").Should().BeNull();
        ListaCongelada.Evaluar("n", "r.txt", Medido, "g", "   ", _ => true, _ => "a.cs :: X = 2\n").Should().BeNull();
        ListaCongelada.Evaluar("n", "r.txt", Medido, "g", null, _ => true, _ => "a.cs :: X = 2\n").Should().BeNull();
    }

    [Fact]
    public void Las_listas_versionadas_se_leen_sin_lineas_mal_formadas_ni_repetidas()
    {
        var directorio = Path.GetDirectoryName(ListaCongelada.RutaDeLista("x"))!;
        var listas = Directory.EnumerateFiles(directorio, "*.txt")
            .Where(f => !f.EndsWith("ca-ES-iguales-validos.txt", StringComparison.Ordinal))
            .ToList();

        // Control positivo: hay listas que leer. Una lista puede quedar vacía (deuda a cero) sin
        // dejar de existir; Leer lanza si hay una línea mal formada o repetida.
        listas.Should().HaveCountGreaterThanOrEqualTo(7);
        foreach (var lista in listas)
            ((Action)(() => ListaCongelada.Leer(File.ReadAllText(lista)))).Should().NotThrow($"la lista {Path.GetFileName(lista)} debe seguir el formato");
    }
}

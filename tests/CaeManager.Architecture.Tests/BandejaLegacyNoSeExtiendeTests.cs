using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Trinquete por ubicación de «Bandeja»</b> — S1/S11 del análisis de causas raíz de
/// 2026-10-02 (<c>Project-Hydra-Negocio/tecnico/evaluacion/</c>) y D-27 del recorrido en vivo de
/// staging (2026-10-01).
///
/// <para>
/// <b>Qué es.</b> La pantalla de aterrizaje del Gestor CAE se llama <b>Mi trabajo</b>; «Bandeja» es el
/// nombre histórico que sobrevive en el espacio de nombres <c>Application.Bandeja</c>, en la ruta
/// <c>/bandeja</c>, en los DTO (<c>ItemBandejaDto</c>, <c>TipoItemBandeja</c>) y en algún rótulo. El
/// recorrido encontró «Bandeja del gestor» y «desde la Bandeja» en pantalla (D-27) y fue una de las
/// dos reincidencias que costaron varias PR. Congelar la presencia por <b>ubicación</b> hace que el
/// siguiente uso sea una decisión visible y no un olvido.
/// </para>
///
/// <para>
/// <b>Qué mide.</b> Cada pareja <c>fichero :: palabra</c> cuya palabra contiene «bandeja» (sin
/// distinguir mayúsculas, para ver también <c>_bandeja</c> o la ruta <c>/bandeja</c>) en
/// identificadores de Roslyn y <b>literales de cadena</b> de <c>.cs</c> (a diferencia de
/// <see cref="ClienteIdNoSeExtiendeTests"/>: es vocabulario visible, y el texto de pantalla también
/// vive en C#), texto sin comentarios en <c>.razor</c> y valores en <c>.resx</c> neutral, más una línea
/// <c>(nombre de fichero)</c> por cada fichero de <c>src</c> cuya ruta contiene «bandeja».
/// </para>
///
/// <para>
/// <b>Qué NO decide.</b> «Bandeja» no es siempre deuda: en Comunicaciones puede designar el buzón
/// de correo (la bandeja de entrada), un sentido legítimo y distinto de la antigua pantalla de
/// aterrizaje. El trinquete no separa los dos sentidos —hacerlo es una decisión de vocabulario
/// (S1, <c>Vocabulario.json</c>)—: congela ambos y avisa. Quien necesite el sentido «buzón» en
/// código nuevo, lo añade a la lista con su motivo en la PR.
/// </para>
/// </summary>
public class BandejaLegacyNoSeExtiendeTests
{
    private const string Palabra = "bandeja";
    private const string SimboloNombreDeFichero = "(nombre de fichero)";

    [Fact]
    public void Las_ubicaciones_de_Bandeja_solo_decrecen()
    {
        var fallo = ListaCongelada.Verificar("Bandeja-ubicaciones", Medir(FuentesDeSrc.Analisis),
            "«Bandeja» es el nombre histórico de Mi trabajo (la pantalla de aterrizaje del Gestor CAE); el " +
            "contrato de terminología y D-27 del recorrido en vivo piden «Mi trabajo». No añadas usos nuevos con " +
            "ese sentido. Si el sentido es el buzón de correo (bandeja de entrada), añade la línea en el mismo commit " +
            "y dilo en la PR; si has RETIRADO usos, baja o borra su línea.");

        fallo.Should().BeNull();
    }

    // ───────────── control positivo del detector, con fuentes sintéticas ─────────────

    [Fact]
    public void El_detector_ve_identificadores_de_cualquier_capitalizacion_y_el_nombre_del_fichero()
    {
        var analisis = new Dictionary<string, AnalisisDeFichero>
        {
            ["src/A/Bandeja/Cosa.cs"] = FuentesDeSrc.AnalizarCSharp(
                "namespace X.Bandeja; class ItemBandejaDto { string _bandeja; // Bandeja en comentario\n string s = \"Bandeja\"; }"),
            ["src/A/Otra.razor"] = FuentesDeSrc.AnalizarRazor("@page \"/bandeja\"\n@* Bandeja comentada *@\n<p>Bandeja del gestor</p>"),
            ["src/A/Limpio.cs"] = FuentesDeSrc.AnalizarCSharp("class Limpio { }"),
        };

        var medido = Medir(analisis);

        medido.Should().Contain(new KeyValuePair<Ubicacion, int>(new("src/A/Bandeja/Cosa.cs", "Bandeja"), 2),
            "el espacio de nombres (identificador) y el literal de cadena cuentan; el comentario no");
        medido.Should().Contain(new KeyValuePair<Ubicacion, int>(new("src/A/Bandeja/Cosa.cs", "ItemBandejaDto"), 1));
        medido.Should().Contain(new KeyValuePair<Ubicacion, int>(new("src/A/Bandeja/Cosa.cs", "_bandeja"), 1));
        medido.Should().Contain(new KeyValuePair<Ubicacion, int>(new("src/A/Bandeja/Cosa.cs", SimboloNombreDeFichero), 1));
        medido.Should().Contain(new KeyValuePair<Ubicacion, int>(new("src/A/Otra.razor", "bandeja"), 1));
        medido.Should().Contain(new KeyValuePair<Ubicacion, int>(new("src/A/Otra.razor", "Bandeja"), 1), "el texto visible cuenta; el comentario Razor no");
        medido.Keys.Should().NotContain(u => u.Lugar == "src/A/Limpio.cs");
        medido.Should().HaveCount(6);
    }

    [Fact]
    public void El_nombre_de_fichero_solo_cuenta_cuando_la_ruta_contiene_la_palabra()
    {
        var analisis = new Dictionary<string, AnalisisDeFichero>
        {
            ["src/A/Pages/MiTrabajo.razor"] = FuentesDeSrc.AnalizarRazor("<p>Mi trabajo</p>"),
            ["src/A/Pages/BandejaSala.razor"] = FuentesDeSrc.AnalizarRazor("<p>Mi trabajo</p>"),
        };

        Medir(analisis).Should().ContainSingle()
            .Which.Key.Should().Be(new Ubicacion("src/A/Pages/BandejaSala.razor", SimboloNombreDeFichero));
    }

    private static Dictionary<Ubicacion, int> Medir(IReadOnlyDictionary<string, AnalisisDeFichero> analisis)
    {
        // Con literales: «Bandeja» es vocabulario visible, y el texto de pantalla también vive en literales
        // de C# (notificaciones, paleta de comandos). ClienteId, que es un identificador, no los cuenta.
        var resultado = FuentesDeSrc.UbicacionesDePalabras(analisis,
            p => p.Contains(Palabra, StringComparison.OrdinalIgnoreCase), incluirLiterales: true);

        foreach (var ruta in analisis.Keys.Where(r => r.Contains(Palabra, StringComparison.OrdinalIgnoreCase)))
            resultado[new Ubicacion(ruta, SimboloNombreDeFichero)] = 1;

        return resultado;
    }
}

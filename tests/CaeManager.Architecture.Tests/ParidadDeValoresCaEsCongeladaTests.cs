using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>S7: paridad de valores ca-ES, solo congelar</b> — análisis de causas raíz de 2026-10-02
/// (<c>Project-Hydra-Negocio/tecnico/evaluacion/</c>), clase C8: varias PR del lote UX tuvieron un
/// hallazgo de esa clase.
///
/// <para>
/// <b>El hueco.</b> <c>LocalizacionRecursosYRegistroTests</c> exige que cada <c>X.ca-ES.resx</c> tenga
/// <b>las mismas claves</b> que su neutral; nada mira los <b>valores</b>. Un valor catalán copiado del
/// castellano es una clave presente, así que pasa. Medido el 2026-10-02, la mayoría de los pares con más de
/// diez letras eran castellano copiado: el catalán todavía no se ofrece en el shell, y esa es la
/// razón de que la gravedad sea baja, no de que el instrumento sobre.
/// </para>
///
/// <para>
/// <b>Qué hace este test.</b> Congela, por <c>fichero :: clave</c>, los pares cuyo valor catalán es
/// idéntico al neutral (<see cref="AnalisisDeCatalan"/> define «idéntico» y «largo»). Una clave nueva
/// copiada sin traducir es una línea que falta y pone el test en rojo; traducir una clave existente
/// también lo pone (la lista debe bajar con el trabajo). No traduce nada ni decide qué se traduce:
/// <b>solo congela</b>.
/// </para>
///
/// <para>
/// <b>Lista aparte de los valores legítimamente idénticos</b>
/// (<c>Congelados/ca-ES-iguales-validos.txt</c>): un nombre propio o una cifra que se escribe igual
/// en las dos lenguas. Hoy está vacía —los 41 candidatos revisados a mano el 2026-10-02 («Corriente
/// AEAT», «Descargar PDF», «Plataforma CAE»…) son castellano sin traducir, no nombres propios—, y
/// existe para que el primer falso positivo real tenga dónde ir sin entrar en la lista de deuda.
/// Es una exención <b>global</b> por valor (vale para cualquier clave futura con ese texto), así que
/// <c>Los_valores_validos_declarados_siguen_usandose</c> exige borrar la que ya ningún par idéntico
/// necesita.
/// </para>
/// </summary>
public class ParidadDeValoresCaEsCongeladaTests
{
    [Fact]
    public void Los_valores_ca_ES_identicos_al_neutral_solo_decrecen()
    {
        var validos = AnalisisDeCatalan.LeerValidos(File.ReadAllText(ListaCongelada.RutaDeLista("ca-ES-iguales-validos")));
        var medido = AnalisisDeCatalan.Medir(AnalisisDeCatalan.ParesDeSrc(), validos);

        var fallo = ListaCongelada.Verificar("ca-ES-iguales", medido,
            "Un valor ca-ES idéntico al es-ES (neutral) y de más de 10 letras es castellano sin traducir: " +
            "ResourceManager no se queja y nada más lo detecta (S7, clase C8). Traduce la clave en el .ca-ES.resx. " +
            "Si el valor es legítimamente igual en las dos lenguas (nombre propio, cifra), añade el valor normalizado a " +
            "Congelados/ca-ES-iguales-validos.txt con el motivo; no a la lista de deuda. Si has TRADUCIDO claves, borra " +
            "su línea. Formato: 'Recursos/TextosX.resx :: clave = 1'.");

        fallo.Should().BeNull();
    }

    [Fact]
    public void Los_valores_validos_declarados_siguen_usandose()
    {
        var validos = AnalisisDeCatalan.LeerValidos(File.ReadAllText(ListaCongelada.RutaDeLista("ca-ES-iguales-validos")));

        AnalisisDeCatalan.ValidosSinUso(AnalisisDeCatalan.ParesDeSrc(), validos).Should().BeEmpty(
            "una exención por valor es global: la que ya ningún par idéntico necesita se borra de Congelados/ca-ES-iguales-validos.txt");
    }

    [Fact]
    public void Un_valido_que_ningun_par_identico_necesita_se_detecta_como_sin_uso()
    {
        var neutral = Resx(("A", "Plataforma Coordinación"), ("B", "Otro texto bastante largo"));
        var catalan = Resx(("A", "Plataforma Coordinación"), ("B", "Un altre text prou llarg"));

        AnalisisDeCatalan.ValidosSinUso([("r.resx", neutral, catalan)], new HashSet<string> { "Plataforma Coordinación", "Otro texto bastante largo", "Inventado" })
            .Should().Equal("Inventado", "Otro texto bastante largo");
    }

    [Fact]
    public void El_escaner_encuentra_los_pares_neutral_y_catalan()
    {
        // Control positivo independiente de la lista: hay decenas de pares neutral/catalán en src.
        AnalisisDeCatalan.ParesDeSrc().Count().Should().BeGreaterThan(30);
    }

    // ───────────── control positivo del detector, con .resx sintéticos ─────────────

    private static string Resx(params (string Clave, string Valor)[] datos) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><root>" +
        string.Concat(datos.Select(d => $"<data name=\"{d.Clave}\" xml:space=\"preserve\"><value>{d.Valor}</value></data>")) +
        "</root>";

    [Fact]
    public void Un_valor_catalan_copiado_del_castellano_se_detecta_y_uno_traducido_no()
    {
        var neutral = Resx(("Copiada", "Descargar documentación"), ("Traducida", "Descargar documentación completa"));
        var catalan = Resx(("Copiada", "Descargar documentación"), ("Traducida", "Descarregar documentació completa"));

        var medido = AnalisisDeCatalan.Medir([("src/X/TextosX.resx", neutral, catalan)], new HashSet<string>());

        medido.Should().Equal(new Dictionary<Ubicacion, int> { [new("src/X/TextosX.resx", "Copiada")] = 1 });
    }

    [Fact]
    public void Las_palabras_cortas_y_los_marcadores_no_cuentan_pero_el_texto_con_marcadores_si()
    {
        var neutral = Resx(("Corta", "Inicio"), ("Marcador", "{0}"), ("Numero", "12345678901234"),
            ("ConMarcador", "Documentos de {0} pendientes"));
        var catalan = Resx(("Corta", "Inicio"), ("Marcador", "{0}"), ("Numero", "12345678901234"),
            ("ConMarcador", "Documentos de {0} pendientes"));

        var medido = AnalisisDeCatalan.Medir([("r.resx", neutral, catalan)], new HashSet<string>());

        medido.Keys.Should().ContainSingle().Which.Simbolo.Should().Be("ConMarcador");
    }

    [Fact]
    public void Un_valor_declarado_valido_queda_fuera_de_la_deuda_y_su_ausencia_lo_devuelve()
    {
        var neutral = Resx(("Nombre", "Plataforma Coordinación"));
        var catalan = Resx(("Nombre", "Plataforma Coordinación"));

        AnalisisDeCatalan.Medir([("r.resx", neutral, catalan)], new HashSet<string> { "Plataforma Coordinación" })
            .Should().BeEmpty();
        AnalisisDeCatalan.Medir([("r.resx", neutral, catalan)], new HashSet<string>())
            .Should().HaveCount(1);
    }

    [Fact]
    public void Una_clave_ausente_en_el_catalan_no_es_un_valor_identico()
    {
        // La paridad de claves la exige LocalizacionRecursosYRegistroTests; aquí no se duplica.
        var neutral = Resx(("Solo", "Un texto bastante largo"));
        var catalan = Resx(("Otra", "Un texto bastante largo"));

        AnalisisDeCatalan.Medir([("r.resx", neutral, catalan)], new HashSet<string>()).Should().BeEmpty();
    }

    [Fact]
    public void La_lista_de_validos_ignora_comentarios_y_lineas_vacias()
    {
        AnalisisDeCatalan.LeerValidos("# motivo\n\nTALVEG Plataforma\n  Otro valor  \n")
            .Should().BeEquivalentTo("TALVEG Plataforma", "Otro valor");
    }
}

using Microsoft.Playwright;

namespace CaeManager.E2ETests.Fidelidad360;

/// <summary>
/// Pruebas del propio comparador, con dos páginas sintéticas y sin aplicación: un
/// comparador que da verde solo vale si se ha visto que puede dar rojo, y por el motivo
/// esperado. Cada mutación cambia UNA propiedad de la «ficha» y la prueba exige que la
/// tabla la nombre a ella, con el valor mutado, y a ninguna otra.
/// </summary>
public class ComparadorFidelidad360Tests(NavegadorFidelidadFixture navegador) : IClassFixture<NavegadorFidelidadFixture>
{
    /// <summary>Página construida con los valores decididos para las fichas 360.</summary>
    private sealed record Hoja(
        string TamanoPastilla = "13px", string BordeTarjeta = "#cfd8e3", string AnchoLateral = "300px", string Anillo = "84px",
        string ColumnaEstado = "138px", string DegradadoProblema = "linear-gradient(90deg, transparent 50%, #ffffff 100%)",
        string BordeVencido = "color-mix(in srgb, #b42318 35%, transparent)", string Divisor = "#a9b4c2",
        string Sombra = "none", bool ConTarjetas = true, bool ConFilaProblema = true, bool ConClasesPropias = false, string Cabecera = "envuelta", bool FilasEnvueltas = false);

    private static string Html(Hoja h)
    {
        // Con clases propias la página no lleva ni un data-pieza: es el mockup anterior a la convención.
        string Marca(string pieza, string clase, string? tono = null) => h.ConClasesPropias
            ? $"class=\"{clase}{(tono is null ? null : " " + tono)}\""
            : $"class=\"{clase}\" data-pieza=\"{pieza}\"{(tono is null ? null : $" data-tono=\"{tono}\"")}";

        string Fila(string nombre, string estado, string clase, string? tono = null) => h.FilasEnvueltas ? $"<div class=\"envoltorio\">{FilaSola(nombre, estado, clase, tono)}</div>" : FilaSola(nombre, estado, clase, tono);
        string FilaSola(string nombre, string estado, string clase, string? tono = null) =>
            $"""<div {Marca("fila", "f", tono)}><span><b>{nombre}</b><small {Marca("fila-detalle", "d")}>detalle</small></span><span class="est"><span {Marca("pastilla", "p " + clase)}>{estado}</span></span></div>""";

        var tarjeta = h.ConTarjetas ? Marca("tarjeta", "t") : "class=\"caja\"";
        var lateral = h.ConTarjetas ? Marca("lateral", "l") : "class=\"caja\"";
        var interior = $"""<span {Marca("anillo", "a")}></span><h1>Montajes Skynet S.L.</h1><span {Marca("pastilla", "p g")}>Activa</span>""";
        var cabecera = h.Cabecera switch
        {
            // La cabecera ES la tarjeta: un solo elemento, sin data-pieza="tarjeta".
            "es-tarjeta" => $"""<div class="t c" data-pieza="cabecera-identidad">{interior}</div>""",
            "suelta" => $"""<div class="c" data-pieza="cabecera-identidad">{interior}</div>""",
            _ => $"""<div {tarjeta}><div {Marca("cabecera-identidad", "c")}>{interior}</div></div>""",
        };
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><style>
            body { margin: 0; padding: 24px; background: #e9eef4; font-family: Inter, sans-serif; }
            .t { background: #fff; border: 1px solid {{h.BordeTarjeta}}; border-radius: 14px; padding: 16px; box-shadow: {{h.Sombra}}; }
            .c { display: flex; gap: 20px; align-items: center; margin-bottom: 12px; }
            .a { width: {{h.Anillo}}; height: {{h.Anillo}}; border-radius: 50%; background: #d5dce5; flex: none; }
            .cuerpo { display: grid; grid-template-columns: minmax(0, 1fr) {{h.AnchoLateral}}; gap: 20px; align-items: start; }
            .f { display: grid; grid-template-columns: minmax(0, 1fr) {{h.ColumnaEstado}}; gap: 12px; align-items: center; min-height: 50px; border-top: 1px solid {{h.Divisor}}; }
            .f:first-child { border-top: 0; }
            .f.peligro, .f[data-tono="peligro"] { background-color: #fef3f2; background-image: {{h.DegradadoProblema}}; border-top-color: transparent; }
            .d { display: block; color: #46566c; }
            .p { display: inline-block; font: 600 {{h.TamanoPastilla}}/20px Inter, sans-serif; padding: 0 8px; border-radius: 999px; border: 1px solid transparent; }
            .p.r { background: #fef3f2; color: #b42318; border-color: {{h.BordeVencido}}; }
            .p.g { background: #ecfdf3; color: #067647; border-color: color-mix(in srgb, #067647 35%, transparent); }
            </style></head><body>
            {{cabecera}}
            <div class="cuerpo">
              <div {{tarjeta}}><div class="lista">
                {{(h.ConFilaProblema ? Fila("Formación PRL", "Vencido", "r", "peligro") : null)}}
                {{Fila("Reconocimiento médico", "Vencido", "r")}}
                {{Fila("Seguro de responsabilidad civil", "Vigente", "g")}}
                {{Fila("Certificado de Hacienda", "Vigente", "g")}}
              </div></div>
              <aside {{lateral}}><div {{tarjeta}}>Datos</div></aside>
            </div></body></html>
            """;
    }

    private static readonly SelectoresDeLado SelectoresPropios = SelectoresDeLado.Con("body", new Dictionary<string, string>
    {
        [SelectoresDeLado.Pastilla] = ".p",
        [SelectoresDeLado.Tarjeta] = ".t",
        [SelectoresDeLado.Lateral] = ".l",
        [SelectoresDeLado.Anillo] = ".a",
        [SelectoresDeLado.Cabecera] = ".c",
        [SelectoresDeLado.Fila] = ".f",
        [SelectoresDeLado.FilaDetalle] = ".d",
        [SelectoresDeLado.FilaProblemaPeligro] = ".f.peligro",
    });

    private async Task<Medicion> MedirAsync(string lado, Hoja hoja, SelectoresDeLado? selectores = null)
    {
        await using var contexto = await Fidelidad360.NuevoContextoAsync(navegador.Browser);
        var page = await contexto.NewPageAsync();
        await page.SetContentAsync(Html(hoja));
        await Fidelidad360.AsentarAsync(page);
        return await MedidorPiezas360.MedirAsync(page, lado, selectores ?? SelectoresDeLado.Convencion());
    }

    private async Task<InformeFidelidad> CompararAsync(Hoja mockup, Hoja ficha) =>
        ComparadorFidelidad360.Comparar("sintética", "claro", await MedirAsync("mockup", mockup), await MedirAsync("ficha", ficha));

    [Fact]
    public async Task Dos_lados_identicos_no_dan_diferencias_y_el_medidor_ha_visto_todas_las_piezas()
    {
        var informe = await CompararAsync(new Hoja(), new Hoja());

        Assert.True(informe.SinDiferencias, informe.ATablaMarkdown());

        // Control positivo: cero diferencias midiendo de verdad, no por no haber medido nada.
        Assert.Equal(5, informe.Ficha.PiezasVistas["pastilla"]);
        Assert.Equal(4, informe.Ficha.PiezasVistas["pastilla-de-fila"]);
        Assert.Equal(3, informe.Ficha.PiezasVistas["tarjeta"]);
        Assert.Equal(1, informe.Ficha.PiezasVistas["fila-problema-peligro"]);
        Assert.Equal("13", informe.Ficha.Magnitudes["pastilla-de-fila.font-size"].Valor);
        Assert.Equal("20", informe.Ficha.Magnitudes["pastilla-de-fila.line-height"].Valor);
        Assert.Equal("#cfd8e3ff", informe.Ficha.Magnitudes["tarjeta.border-top-color"].Valor);
        Assert.Equal("300", informe.Ficha.Magnitudes["lateral.width"].Valor);
        Assert.Equal("84", informe.Ficha.Magnitudes["anillo.width"].Valor);
        Assert.Equal("#e9eef4ff", informe.Ficha.Magnitudes["fondo-pagina.background-color"].Valor);
        Assert.Equal("#a9b4c2ff", informe.Ficha.Magnitudes["fila.border-top-color"].Valor);
        Assert.Equal("#46566cff", informe.Ficha.Magnitudes["fila-detalle.color"].Valor);
        Assert.Equal("0", informe.Ficha.Magnitudes["pastilla-de-fila.dispersion-izquierda"].Valor);
        Assert.Equal("sí", informe.Ficha.Magnitudes["fila-con-problema[peligro].degradado"].Valor);
        Assert.Equal("sí", informe.Ficha.Magnitudes["cabecera-identidad.en-tarjeta"].Valor);
        // color-mix al 35 %: el borde de «Vencido» es su color de texto con alfa 0x59.
        Assert.Equal("#b4231859", informe.Ficha.Magnitudes["pastilla[Vencido].border-top-color"].Valor);
    }

    public static TheoryData<string, string[], string> Mutaciones() => new()
    {
        { "pastilla", ["pastilla-de-fila.font-size", "pastilla.font-size"], "14" },
        { "borde-tarjeta", ["tarjeta.border-top-color"], "#cfd8e8ff" },
        { "lateral", ["lateral.width"], "320" },
        { "anillo", ["anillo.height", "anillo.width"], "88" },
        { "divisor", ["fila.border-top-color"], "#cfd8e3ff" },
        { "sombra", ["tarjeta.box-shadow"], "rgba(0, 0, 0, 0.1) 0px 1px 2px 0px" },
        { "sin-degradado", ["fila-con-problema[peligro].degradado"], "no" },
        { "borde-pastilla", ["pastilla[Vencido].border-top-color"], "#b42318ff" },
    };

    [Theory]
    [MemberData(nameof(Mutaciones))]
    public async Task Una_mutacion_de_la_ficha_pone_en_rojo_su_fila_y_solo_la_suya(string mutacion, string[] clavesEsperadas, string valorMutado)
    {
        var ficha = mutacion switch
        {
            "pastilla" => new Hoja(TamanoPastilla: "14px"),
            // Cinco niveles en un canal: por encima de la tolerancia de dos.
            "borde-tarjeta" => new Hoja(BordeTarjeta: "#cfd8e8"),
            "lateral" => new Hoja(AnchoLateral: "320px"),
            "anillo" => new Hoja(Anillo: "88px"),
            "divisor" => new Hoja(Divisor: "#cfd8e3"),
            "sombra" => new Hoja(Sombra: "0 1px 2px rgba(0,0,0,.1)"),
            "sin-degradado" => new Hoja(DegradadoProblema: "none"),
            "borde-pastilla" => new Hoja(BordeVencido: "#b42318"),
            _ => throw new ArgumentOutOfRangeException(nameof(mutacion)),
        };

        var informe = await CompararAsync(new Hoja(), ficha);

        Assert.False(informe.SinDiferencias);
        Assert.Equal(clavesEsperadas, informe.Diferencias.Select(d => d.Clave).Order(StringComparer.Ordinal));
        Assert.All(informe.Diferencias, d =>
        {
            Assert.Equal(VeredictoFila.Distinto, d.Veredicto);
            Assert.Equal(valorMutado, d.Ficha);
        });
    }

    [Fact]
    public async Task Una_pastilla_fuera_de_su_columna_se_ve_como_dispersion()
    {
        // Columna «auto»: cada pastilla se coloca según su propio ancho, como hace hoy una fila en flex.
        var informe = await CompararAsync(new Hoja(), new Hoja(ColumnaEstado: "auto"));

        var fila = Assert.Single(informe.Diferencias, d => d.Clave == "pastilla-de-fila.dispersion-izquierda");
        Assert.Equal("0", fila.Mockup);
        Assert.True(double.Parse(fila.Ficha, System.Globalization.CultureInfo.InvariantCulture) > 1, fila.Ficha);
    }

    [Fact]
    public async Task Las_filas_envueltas_una_a_una_siguen_siendo_una_lista_para_medir_la_columna()
    {
        // Fila desplegable o envoltorio de plantilla: el padre de cada fila solo la contiene a ella.
        var alineada = await MedirAsync("mockup", new Hoja(FilasEnvueltas: true));
        Assert.Equal("0", alineada.Magnitudes["pastilla-de-fila.dispersion-izquierda"].Valor);

        var desalineada = await MedirAsync("ficha", new Hoja(FilasEnvueltas: true, ColumnaEstado: "auto"));
        var informe = ComparadorFidelidad360.Comparar("sintética", "claro", alineada, desalineada);
        Assert.Single(informe.Diferencias, d => d.Clave == "pastilla-de-fila.dispersion-izquierda");
    }

    [Fact]
    public async Task Lo_que_cae_dentro_de_la_tolerancia_no_es_diferencia()
    {
        // Un píxel de lateral (tolerancia 1) y dos niveles de un canal (tolerancia 2).
        var informe = await CompararAsync(new Hoja(), new Hoja(AnchoLateral: "301px", BordeTarjeta: "#cfd8e5"));

        Assert.True(informe.SinDiferencias, informe.ATablaMarkdown());
        Assert.Equal("301", informe.Ficha.Magnitudes["lateral.width"].Valor);
        Assert.Equal("#cfd8e5ff", informe.Ficha.Magnitudes["tarjeta.border-top-color"].Valor);
    }

    [Fact]
    public async Task Una_pieza_que_falta_en_la_ficha_se_dice_ausente_y_no_se_calla()
    {
        var informe = await CompararAsync(new Hoja(), new Hoja(ConFilaProblema: false));

        Assert.False(informe.SinDiferencias);
        Assert.Contains(informe.Diferencias, d => d is { Clave: "fila-con-problema[peligro].background-color", Veredicto: VeredictoFila.AusenteEnFicha });
        Assert.Contains(informe.Diferencias, d => d is { Clave: "fila-con-problema[peligro].degradado", Veredicto: VeredictoFila.AusenteEnFicha });
    }

    [Fact]
    public async Task Dos_lados_sin_tarjetas_ni_lateral_son_un_instrumento_ciego_no_un_verde()
    {
        var informe = await CompararAsync(new Hoja(ConTarjetas: false), new Hoja(ConTarjetas: false));

        Assert.Empty(informe.Diferencias);
        Assert.True(informe.Ciego);
        Assert.False(informe.SinDiferencias);
        Assert.Contains("lateral.width", informe.NucleoNoObservado);
        Assert.Contains("INSTRUMENTO CIEGO", informe.ATablaMarkdown(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Un_mockup_con_clases_propias_mide_lo_mismo_que_la_ficha_con_data_pieza()
    {
        var mockup = await MedirAsync("mockup", new Hoja(ConClasesPropias: true), SelectoresPropios);
        var ficha = await MedirAsync("ficha", new Hoja());

        var informe = ComparadorFidelidad360.Comparar("sintética", "claro", mockup, ficha);

        Assert.True(informe.SinDiferencias, informe.ATablaMarkdown());
        Assert.Equal(ficha.PiezasVistas, mockup.PiezasVistas);

        // Y la convención no ve nada en una página que no la sigue: no hay «probar los dos selectores».
        var aCiegas = await MedirAsync("mockup", new Hoja(ConClasesPropias: true));
        Assert.All(aCiegas.PiezasVistas.Values, vistas => Assert.Equal(0, vistas));
    }

    [Fact]
    public async Task Una_cabecera_que_es_la_tarjeta_mide_igual_que_una_cabecera_dentro_de_una_tarjeta()
    {
        var informe = await CompararAsync(new Hoja(Cabecera: "es-tarjeta"), new Hoja());

        Assert.True(informe.SinDiferencias, informe.ATablaMarkdown());
        Assert.Equal("sí", informe.Mockup.Magnitudes["cabecera-identidad.en-tarjeta"].Valor);
        Assert.Equal(3, informe.Mockup.PiezasVistas["tarjeta"]);

        // Y una cabecera sin caja, que es la del producto hoy, sigue saliendo como diferencia.
        var suelta = await CompararAsync(new Hoja(Cabecera: "es-tarjeta"), new Hoja(Cabecera: "suelta"));
        var fila = Assert.Single(suelta.Diferencias);
        Assert.Equal(("cabecera-identidad.en-tarjeta", "sí", "no"), (fila.Clave, fila.Mockup, fila.Ficha));
    }

    /// <summary>Los mockups «… 360 página» que ya marcan sus piezas con <c>data-pieza</c>.</summary>
    private static readonly string[] MockupsPorConvencion =
    [
        "Subcontrata 360 página TALVEG.dc.html",
        "Vehiculo 360 página TALVEG.dc.html",
        "Tipo Documento 360 página TALVEG.dc.html",
        "Proyecto 360 página TALVEG.dc.html",
        "Visita 360 página TALVEG.dc.html",
    ];

    public static TheoryData<string, string> MockupsPorTema()
    {
        var datos = new TheoryData<string, string>();
        foreach (var fichero in MockupsPorConvencion)
        {
            foreach (var tema in Fidelidad360.Temas)
                datos.Add(fichero, tema);
        }

        return datos;
    }

    /// <summary>
    /// El mockup solo, sin ficha: ¿enseña las piezas del catálogo, tiene el tema pedido y
    /// cumple los valores decididos? Sirve mientras la página aún no existe en el producto,
    /// y para que un mockup no llegue a un constructor por detrás de la norma.
    /// </summary>
    [TeoriaConMockups]
    [MemberData(nameof(MockupsPorTema))]
    public async Task Un_mockup_por_convencion_ensena_sus_piezas_y_cumple_la_norma(string fichero, string tema)
    {
        var directorio = Mockup360.Directorio!;
        Assert.True(File.Exists(Path.Combine(directorio, fichero)), $"No existe el mockup «{fichero}» en {directorio}.");
        var mockup = Mockup360.PorConvencion(
            fichero,
            "[data-pieza=\"cabecera-identidad\"]",
            t => t == "oscuro" ? "document.documentElement.dataset.theme = 'oscuro'" : "delete document.documentElement.dataset.theme");
        var salida = Path.Combine(Mockup360.DirectorioSalida, "mockups", Path.GetFileNameWithoutExtension(fichero), tema);

        await using var contexto = await Fidelidad360.NuevoContextoAsync(navegador.Browser);
        var page = await contexto.NewPageAsync();
        await mockup.AbrirAsync(page, directorio, "claro");
        var enClaro = await Fidelidad360.MedirAsentadoAsync(page, "mockup", mockup.Selectores);
        var medicion = enClaro;
        if (tema != "claro")
        {
            await mockup.AbrirAsync(page, directorio, tema);
            medicion = await Fidelidad360.MedirAsentadoAsync(page, "mockup", mockup.Selectores);
        }

        await Fidelidad360.CapturarAsync(page, Path.Combine(salida, "mockup.png"));
        var norma = Norma360.Evaluar(medicion, tema);
        var tabla = Norma360.ATablaMarkdown($"{fichero} ({tema})", norma);
        var vistas = string.Join(", ", medicion.PiezasVistas.Select(p => $"{p.Key} {p.Value}"));
        await File.WriteAllTextAsync(Path.Combine(salida, "informe.md"), $"Piezas vistas: {vistas}.\n\n{tabla}");

        // Control positivo antes de leer la norma: sin piezas, «nada incumple» no dice nada.
        foreach (var pieza in new[] { "tarjeta", "lateral", "anillo", "cabecera-identidad", "pastilla", "pastilla-de-fila", "fila", "fila-detalle" })
            Assert.True(medicion.PiezasVistas[pieza] >= 1, $"«{fichero}» no enseña ninguna pieza «{pieza}». Piezas vistas: {vistas}.");
        if (tema != "claro")
            Assert.True(Fidelidad360.TemaAplicado(enClaro, medicion), $"«{fichero}» no tiene tema {tema}: pedirlo no cambió el fondo de página.");

        Assert.True(norma.All(f => f.Cumple != false), $"Informe y captura en {salida}\n\nPiezas vistas: {vistas}.\n\n{tabla}");
    }

    [Fact]
    public async Task La_norma_se_cumple_en_la_pagina_de_referencia_y_falla_en_la_fila_mutada()
    {
        var referencia = Norma360.Evaluar(await MedirAsync("ficha", new Hoja()), "claro");
        Assert.All(referencia, f => Assert.True(f.Cumple == true || f.Clave == "fila-con-problema[advertencia].degradado",
            Norma360.ATablaMarkdown("referencia", referencia)));
        Assert.Null(Assert.Single(referencia, f => f.Clave == "fila-con-problema[advertencia].degradado").Cumple);

        var mutada = Norma360.Evaluar(await MedirAsync("ficha", new Hoja(Anillo: "88px", TamanoPastilla: "12px")), "claro");
        Assert.Equal(
            ["anillo.height", "anillo.width", "pastilla-de-fila.font-size", "pastilla.font-size"],
            mutada.Where(f => f.Cumple == false).Select(f => f.Clave).Order(StringComparer.Ordinal));
    }
}

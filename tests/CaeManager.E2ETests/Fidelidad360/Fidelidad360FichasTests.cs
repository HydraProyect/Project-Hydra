using System.Globalization;
using System.Text;
using Microsoft.Playwright;

namespace CaeManager.E2ETests.Fidelidad360;

/// <summary>
/// El comparador contra las fichas 360 del producto. Dos familias de prueba:
/// <list type="bullet">
/// <item>sin mockups (corre en CI): la ficha real se mide dos veces igual, y una regla CSS
/// inyectada sobre una pieza pone en rojo su fila. Fija que los componentes compartidos
/// siguen emitiendo <c>data-pieza</c> y que el medidor ve a través de ellos;</item>
/// <item>con mockups (solo en local, ver <see cref="Mockup360.VariableDirectorio"/>): cada
/// pareja ficha ↔ mockup, en claro y en oscuro, con tabla de diferencias, capturas e
/// indicador de píxeles en <see cref="Mockup360.DirectorioSalida"/>. Falla mientras haya
/// diferencias: esa es la medida de lo que le falta a la ficha.</item>
/// </list>
/// </summary>
[Collection("AppCollectionFichas360")]
public class Fidelidad360FichasTests(WebAppFixtureFichas360 fixture)
{
    private const string TenantPizzaPlanet = "Pizza Planet S.L.";
    private const string EmpresaDeLaMaqueta = "Montajes Skynet S.L.";
    private const string ClienteEmpresarialDeLaMaqueta = "Cyberdyne Ibérica S.A.";
    private const string VehiculoDeLaMaqueta = "Camión grúa";
    private const string ProyectoDeLaMaqueta = "Reforma nave Sevilla";

    /// <summary>Tabla y columna por las que se resuelve el id de la ficha cuando no es una Empresa.</summary>
    private const string VehiculosPorNombre = "\"Vehiculos\".\"Nombre\"";
    private const string ProyectosPorNombre = "\"Proyectos\".\"Nombre\"";

    /// <summary>
    /// Pareja ficha ↔ mockup. La ruta se resuelve por el nombre sembrado: la razón social de una
    /// Empresa, salvo que <paramref name="BuscarEn"/> nombre otra tabla y columna.
    /// </summary>
    private sealed record Pareja(string Clave, string Ruta, string RazonSocial, Mockup360 Mockup, string? BuscarEn = null);

    private static readonly Pareja[] Parejas =
    [
        new("empresa-360", "/empresas/", EmpresaDeLaMaqueta, Mockup360.PaginaDc("Empresa 360 página TALVEG.dc.html")),
        new("cliente-empresarial-360", "/clientes/", ClienteEmpresarialDeLaMaqueta, Mockup360.PaginaDc("Cliente 360 página TALVEG.dc.html")),
        new("vehiculo-360", "/vehiculos/", VehiculoDeLaMaqueta,
            Mockup360.PorConvencion(
                "Vehiculo 360 página TALVEG.dc.html", "[data-pieza=\"cabecera-identidad\"]",
                tema => tema == "oscuro" ? "document.documentElement.dataset.theme = 'oscuro'" : "delete document.documentElement.dataset.theme"),
            BuscarEn: VehiculosPorNombre),
        new("proyecto-360", "/proyectos/", ProyectoDeLaMaqueta,
            Mockup360.PorConvencion(
                "Proyecto 360 página TALVEG.dc.html", "[data-pieza=\"lateral\"] [data-pieza=\"tarjeta\"]",
                tema => tema == "oscuro" ? "document.documentElement.dataset.theme = 'oscuro'" : "delete document.documentElement.dataset.theme"),
            BuscarEn: ProyectosPorNombre),
    ];

    public static TheoryData<string, string> ParejasPorTema()
    {
        var datos = new TheoryData<string, string>();
        foreach (var pareja in Parejas)
        {
            foreach (var tema in Fidelidad360.Temas)
                datos.Add(pareja.Clave, tema);
        }

        return datos;
    }

    private async Task<IPage> AbrirFichaAsync(IBrowserContext contexto, string ruta, string razonSocial, string tema, string? buscarEn = null)
    {
        var email = await fixture.LeerValorSqlAsync(
            """SELECT "Email" FROM "AspNetUsers" WHERE "Email" LIKE 'coordinador1.%@caemanager.local' """);
        // «buscarEn» solo llega de las constantes de esta clase ("Tabla"."Columna"): no es entrada externa.
        var (tabla, columna) = (buscarEn ?? "\"Empresas\".\"RazonSocial\"").Split('.') is [var t, var c]
            ? (t, c)
            : throw new ArgumentException($"Se esperaba \"Tabla\".\"Columna\" y llegó «{buscarEn}».", nameof(buscarEn));
        var id = await fixture.LeerValorSqlAsync(
            $"""
            SELECT e."Id"::text FROM {tabla} e JOIN "Tenants" t ON t."Id" = e."TenantId"
            WHERE e.{columna} = @razon AND t."Nombre" = @tenant
            """, ("razon", razonSocial), ("tenant", TenantPizzaPlanet));

        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, email, Ayudas.ContrasenaUsuariosPrueba);
        await Ayudas.CambiarClienteActivoAsync(page, fixture, TenantPizzaPlanet);
        await page.GotoAsync(fixture.BaseUrl + ruta + id);
        await page.WaitForSelectorAsync("[data-pieza=\"lateral\"] [data-pieza=\"tarjeta\"]", new PageWaitForSelectorOptions { Timeout = 30_000 });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Fidelidad360.PonerTemaDeFichaAsync(page, tema);
        return page;
    }

    [Theory]
    [InlineData("claro")]
    [InlineData("oscuro")]
    public async Task La_ficha_de_Empresa_se_mide_dos_veces_igual_y_cada_regla_inyectada_pone_en_rojo_su_fila(string tema)
    {
        await using var contexto = await Fidelidad360.NuevoContextoAsync(fixture.Browser);
        var page = await AbrirFichaAsync(contexto, "/empresas/", EmpresaDeLaMaqueta, tema);
        var selectores = SelectoresDeLado.Convencion();

        var referencia = await Fidelidad360.MedirAsentadoAsync(page, "ficha (referencia)", selectores);
        var repetida = await MedidorPiezas360.MedirAsync(page, "ficha", selectores);
        var igual = ComparadorFidelidad360.Comparar("empresa-360 consigo misma", tema, referencia, repetida);

        Assert.True(igual.SinDiferencias, igual.ATablaMarkdown());
        // Control positivo: la ficha real enseña las piezas, así que el cero de arriba es una medida.
        Assert.True(referencia.PiezasVistas["tarjeta"] >= 2, igual.ATablaMarkdown());
        Assert.Equal(1, referencia.PiezasVistas["lateral"]);
        Assert.Equal(1, referencia.PiezasVistas["cabecera-identidad"]);
        Assert.True(referencia.PiezasVistas["pastilla"] >= 1, igual.ATablaMarkdown());

        (string Regla, string Clave, string ValorMutado)[] mutaciones =
        [
            ("[data-pieza=\"pastilla\"] { font-size: 17px !important; }", "pastilla.font-size", "17"),
            ("[data-pieza=\"tarjeta\"] { border-color: rgb(255, 0, 0) !important; }", "tarjeta.border-top-color", "#ff0000ff"),
            ("[data-pieza=\"lateral\"] { width: 340px !important; }", "lateral.width", "340"),
        ];

        foreach (var (regla, clave, valorMutado) in mutaciones)
        {
            var etiqueta = await page.AddStyleTagAsync(new PageAddStyleTagOptions { Content = regla });
            await Fidelidad360.AsentarAsync(page);
            var mutada = await MedidorPiezas360.MedirAsync(page, "ficha (mutada)", selectores);
            var informe = ComparadorFidelidad360.Comparar($"empresa-360 con «{regla}»", tema, referencia, mutada);

            var fila = Assert.Single(informe.Diferencias, d => d.Clave == clave);
            Assert.Equal(VeredictoFila.Distinto, fila.Veredicto);
            Assert.Equal(valorMutado, fila.Ficha);

            await etiqueta.EvaluateAsync("e => e.remove()");
            await Fidelidad360.AsentarAsync(page);
            var revertida = await MedidorPiezas360.MedirAsync(page, "ficha (revertida)", selectores);
            Assert.True(ComparadorFidelidad360.Comparar("empresa-360 revertida", tema, referencia, revertida).SinDiferencias,
                $"Tras retirar «{regla}» la ficha no volvió a su medida.");
        }
    }

    [Fact]
    public async Task El_tema_oscuro_de_la_ficha_cambia_lo_que_se_mide()
    {
        await using var contexto = await Fidelidad360.NuevoContextoAsync(fixture.Browser);
        var page = await AbrirFichaAsync(contexto, "/empresas/", EmpresaDeLaMaqueta, "claro");
        var claro = await Fidelidad360.MedirAsentadoAsync(page, "ficha", SelectoresDeLado.Convencion());

        await Fidelidad360.PonerTemaDeFichaAsync(page, "oscuro");
        var oscuro = await Fidelidad360.MedirAsentadoAsync(page, "ficha", SelectoresDeLado.Convencion());

        Assert.True(Fidelidad360.TemaAplicado(claro, oscuro), "Poner data-theme=oscuro no cambió el fondo de página medido.");
    }

    [TeoriaConMockups]
    [MemberData(nameof(ParejasPorTema))]
    public async Task La_ficha_coincide_con_su_mockup(string clavePareja, string tema)
    {
        var pareja = Parejas.Single(p => p.Clave == clavePareja);
        var directorio = Mockup360.Directorio!;
        Assert.True(File.Exists(Path.Combine(directorio, pareja.Mockup.Fichero)), $"No existe el mockup «{pareja.Mockup.Fichero}» en {directorio}.");
        var salida = Path.Combine(Mockup360.DirectorioSalida, pareja.Clave, tema);
        Directory.CreateDirectory(salida);

        await using var contexto = await Fidelidad360.NuevoContextoAsync(fixture.Browser);

        var paginaMockup = await contexto.NewPageAsync();
        await pareja.Mockup.AbrirAsync(paginaMockup, directorio, "claro");
        var mockupEnClaro = await Fidelidad360.MedirAsentadoAsync(paginaMockup, "mockup", pareja.Mockup.Selectores);
        var mockup = mockupEnClaro;
        if (tema != "claro")
        {
            await pareja.Mockup.AbrirAsync(paginaMockup, directorio, tema);
            mockup = await Fidelidad360.MedirAsentadoAsync(paginaMockup, "mockup", pareja.Mockup.Selectores);
        }

        await Fidelidad360.CapturarAsync(paginaMockup, Path.Combine(salida, "mockup.png"));

        var paginaFicha = await AbrirFichaAsync(contexto, pareja.Ruta, pareja.RazonSocial, tema, pareja.BuscarEn);
        var ficha = await Fidelidad360.MedirAsentadoAsync(paginaFicha, "ficha", SelectoresDeLado.Convencion());
        await Fidelidad360.CapturarAsync(paginaFicha, Path.Combine(salida, "ficha.png"));

        var fraccion = await Fidelidad360.DiferenciaDePixelesAsync(
            contexto, Path.Combine(salida, "mockup.png"), Path.Combine(salida, "ficha.png"), Path.Combine(salida, "diferencia.png"));

        var informe = ComparadorFidelidad360.Comparar(pareja.Clave, tema, mockup, ficha);
        var mockupSinEsteTema = tema != "claro" && !Fidelidad360.TemaAplicado(mockupEnClaro, mockup);

        var texto = new StringBuilder();
        texto.AppendLine(CultureInfo.InvariantCulture, $"## {pareja.Clave} · tema {tema}");
        texto.AppendLine();
        texto.AppendLine(CultureInfo.InvariantCulture, $"Mockup: `{pareja.Mockup.Fichero}`. Ficha: `{pareja.Ruta}{{id}}` de «{pareja.RazonSocial}». Ventana {Fidelidad360.Ancho}×{Fidelidad360.Alto}.");
        texto.AppendLine(CultureInfo.InvariantCulture, $"Píxeles distintos entre capturas: {fraccion.ToString("P1", CultureInfo.InvariantCulture)} (indicador; ver `diferencia.png`).");
        if (mockupSinEsteTema)
            texto.AppendLine().AppendLine("**EL MOCKUP NO TIENE TEMA OSCURO**: pedirlo no cambió su fondo. La tabla compara la ficha en oscuro con un mockup en claro y no mide fidelidad.");
        texto.AppendLine();
        texto.AppendLine(informe.ATablaMarkdown());
        texto.AppendLine(Norma360.ATablaMarkdown($"mockup ({tema})", Norma360.Evaluar(mockup, tema)));
        texto.AppendLine(Norma360.ATablaMarkdown($"ficha ({tema})", Norma360.Evaluar(ficha, tema)));
        await File.WriteAllTextAsync(Path.Combine(salida, "informe.md"), texto.ToString());

        Assert.False(mockupSinEsteTema, $"El mockup «{pareja.Mockup.Fichero}» no tiene tema {tema}: no hay con qué comparar. Informe en {salida}.");
        Assert.True(informe.SinDiferencias, $"Informe y capturas en {salida}\n\n{informe.ATablaMarkdown()}");
    }
}

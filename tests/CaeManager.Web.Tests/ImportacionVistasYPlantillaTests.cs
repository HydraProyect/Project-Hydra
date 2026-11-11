using System.Reflection;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Queries.ObtenerHistorialImportaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Importacion;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Importacion;
using ClosedXML.Excel;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using PaginaImportacion = CaeManager.Web.Features.Importacion.Pages.Importacion;

namespace CaeManager.Web.Tests;

/// <summary>
/// La pantalla de Importación con el Paso 1 · Estructura (incremento I-2): qué conserva de la vista de siempre, cuándo
/// ofrece la vista nueva y cómo se entra en ella; el CSV de los informes (contra la inyección de fórmulas) y la
/// plantilla maestra en blanco que se descarga.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> los elementos de la vista de siempre que el encargo manda conservar (migas, cabecera,
/// pasos, plantillas con su enlace en blanco, Historial), que la vista nueva solo aparece con <c>?vista=estructura</c>
/// y nunca dentro de los flujos de Clientes ni de Documentos, el atributo de autorización de la página, el contenido del
/// libro en blanco (hojas, cabeceras que el lector exige, ejemplo marcado) y el CSV.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> que el endpoint de la plantilla exija el rol (eso lo cubre el catálogo de endpoints de
/// Architecture.Tests y, con servidor real, CI), que el lector real acepte la plantilla recién descargada (integración,
/// en CI) ni el aspecto.
/// </para>
/// </summary>
public class ImportacionVistasYPlantillaTests : BunitContext
{
    public ImportacionVistasYPlantillaTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediadorVacio : IMediator
    {
        public List<object> Recibidas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Recibidas.Add(request);
            object respuesta = request is ObtenerHistorialImportacionesQuery
                ? (IReadOnlyList<HistorialImportacionDto>)[]
                : throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
            return Task.FromResult((TResponse)respuesta);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class PlantillaCaeConfigurada : IPlantillaCaeCompletaConfigurada
    {
        public bool EstaConfigurada => true;
    }

    private IRenderedComponent<PaginaImportacion> Renderizar(string url)
    {
        var mediador = new MediadorVacio();
        Services.AddOptions<OpcionesLimitesImportacion>();
        Services.AddSingleton<LimitadorAnalisisImportacion>();
        Services.AddSingleton<ITenantActual>(new TenantEfectivoDePrueba(Guid.NewGuid()));
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddSingleton<IPlantillaCaeCompletaConfigurada>(new PlantillaCaeConfigurada());
        Services.AddScoped<ToastService>();
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddLogging();
        Services.AddLocalization();
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        return Render<PaginaImportacion>();
    }

    private static string Texto(IElement elemento) => Regex.Replace(elemento.TextContent, @"\s+", " ").Trim();

    // ---------------------------------------------------------------- lo que se conserva de la vista de siempre

    [Fact]
    public void SinVista_SigueSiendoLaPantallaDeSiempre()
    {
        var cut = Renderizar("importacion");

        cut.FindAll(".estructura-importacion").Should().BeEmpty();
        cut.FindAll("nav.pasos-importacion button.paso-importacion").Should().HaveCount(5);
        cut.FindAll("[role=radiogroup] [role=radio]").Should().HaveCount(4);
        cut.FindAll("[role=radiogroup] .tarjeta-plantilla").Select(t => t.GetAttribute("data-plantilla"))
            .Should().Equal("cae", "clientes", "combinada", "documentos");
        cut.FindAll(".enlace-plantilla-blanco").Should().NotBeEmpty("cada plantilla clásica conserva su enlace a la plantilla en blanco");
        Texto(cut.Find(".panel-historial-importacion .cabecera-panel-historial")).Should().NotBeEmpty();
        cut.FindAll(".panel-historial-importacion .vacio-historial-importacion").Should().ContainSingle("Historial sin entradas");
        cut.FindAll(".ruta-importacion").Should().BeEmpty("las migas son de los flujos de Documentos y de Clientes");
        cut.FindAll("h1").Should().NotBeEmpty("la cabecera de página sigue pintándose");
    }

    [Fact]
    public void SinVista_SeOfreceLaNavegacionEntreVistasConElPaso2Deshabilitado()
    {
        var cut = Renderizar("importacion");

        var nav = cut.Find("nav.nav-vistas-importacion");
        var paso1 = nav.QuerySelector("a[href='?vista=estructura']")!;
        paso1.GetAttribute("aria-current").Should().BeNull();
        var paso2 = nav.QuerySelector("button")!;
        paso2.HasAttribute("disabled").Should().BeTrue("el Paso 2 (PDF) no entra en este incremento");
        Texto(paso2).Should().Contain("próximamente");
        var historial = nav.QuerySelector("a[href='?']")!;
        historial.GetAttribute("aria-current").Should().Be("page");
        nav.QuerySelectorAll("a, button").Should().HaveCount(3);
    }

    [Fact]
    public void ConVistaEstructura_SeVeElPaso1YNoLaVistaDeSiempre()
    {
        var cut = Renderizar("importacion?vista=estructura");

        cut.FindAll(".estructura-importacion").Should().ContainSingle();
        cut.FindAll("nav.pasos-importacion").Should().BeEmpty();
        cut.FindAll("[role=radiogroup]").Should().BeEmpty();
        cut.FindAll(".panel-historial-importacion").Should().BeEmpty();
        cut.Find("nav.nav-vistas-importacion a[href='?vista=estructura']").GetAttribute("aria-current").Should().Be("page");
        cut.Find("nav.nav-vistas-importacion a[href='?']").GetAttribute("aria-current").Should().BeNull();
        cut.Find("a[download]").GetAttribute("href").Should().Be(ImportacionEndpoints.RutaPlantillaMaestra);
    }

    [Fact]
    public void ConUnaVistaDesconocida_SeVeLaPantallaDeSiempre()
    {
        var cut = Renderizar("importacion?vista=otra");

        cut.FindAll(".estructura-importacion").Should().BeEmpty();
        cut.FindAll("nav.pasos-importacion").Should().ContainSingle();
    }

    [Fact]
    public void DesdeClientes_ConservaLaRutaDeVueltaYNoOfreceLaVistaNueva()
    {
        var cut = Renderizar("importacion?plantilla=clientes&vista=estructura");

        cut.Find(".ruta-importacion a.enlace-volver-importacion").GetAttribute("href").Should().Be("/clientes");
        cut.FindAll("nav.nav-vistas-importacion").Should().BeEmpty();
        cut.FindAll(".estructura-importacion").Should().BeEmpty();
        cut.FindAll("nav.pasos-importacion").Should().ContainSingle();
    }

    [Fact]
    public void ImportacionDeDocumentos_ConservaMigasYAccionesYNoOfreceLaVistaNueva()
    {
        var cut = Renderizar("importacion?plantilla=documentos&flujo=documentos&vista=estructura");

        cut.Find(".ruta-importacion-documentos a.enlace-volver-importacion").GetAttribute("href").Should().Be("/documentos");
        var acciones = cut.Find(".acciones-contexto-documentos");
        acciones.QuerySelector("a[href='/importacion']").Should().NotBeNull();
        acciones.QuerySelector("a.enlace-subida-multiple").Should().NotBeNull();
        cut.Find("a[href='/documentos/plantilla.xlsx']").Should().NotBeNull();
        cut.FindAll("nav.nav-vistas-importacion").Should().BeEmpty();
        cut.FindAll(".estructura-importacion").Should().BeEmpty();
    }

    [Fact]
    public void LaAutorizacionDeLaPaginaNoCambia()
    {
        var atributo = typeof(PaginaImportacion).GetCustomAttribute<AuthorizeAttribute>();

        atributo.Should().NotBeNull();
        atributo!.Roles.Should().Be(Roles.ConCargaMasivaCsv);
    }

    [Fact]
    public void LaRutaDeLaPlantillaEsLaDelEnlace()
    {
        ImportacionEndpoints.RutaPlantillaMaestra.Should().Be("/importacion/plantilla-maestra.xlsx");
    }

    // ---------------------------------------------------------------- CSV de los informes

    [Theory]
    [InlineData("=SUMA(A1:A2)", "\"'=SUMA(A1:A2)\"")]
    [InlineData("+34 600 000 010", "\"'+34 600 000 010\"")]
    [InlineData("-5", "\"'-5\"")]
    [InlineData("@usuario", "\"'@usuario\"")]
    [InlineData("\tcelda", "\"'\tcelda\"")]
    [InlineData("\rcelda", "\"'\rcelda\"")]
    public void Csv_UnaCeldaQueEmpiezaPorCaracterDeFormulaSeEscribeComoTexto(string valor, string esperado)
    {
        InformeCsv.Celda(valor).Should().Be(esperado);
    }

    [Fact]
    public void Csv_LasComillasSeDuplicanYLosSeparadoresQuedanDentro()
    {
        InformeCsv.Celda("dijo \"hola\"; adiós").Should().Be("\"dijo \"\"hola\"\"; adiós\"");
        InformeCsv.Celda("Industrias Ficticias, S.L.").Should().Be("\"Industrias Ficticias, S.L.\"");
    }

    [Fact]
    public void Csv_UnValorNuloOVacioEsUnaCeldaVaciaYUnTextoNormalNoSeAltera()
    {
        InformeCsv.Celda(null).Should().Be("\"\"");
        InformeCsv.Celda(string.Empty).Should().Be("\"\"");
        InformeCsv.Celda("Planta Norte").Should().Be("\"Planta Norte\"");
    }

    [Fact]
    public void Csv_CabeceraYFilasVanSeparadasPorPuntoYComaYTerminanEnSaltoDeLinea()
    {
        var csv = InformeCsv.Construir(["Hoja", "Fila"], [["Empresas", "4"], ["Centros", null]]);

        csv.Should().Be("\"Hoja\";\"Fila\"\r\n\"Empresas\";\"4\"\r\n\"Centros\";\"\"\r\n");
    }

    // ---------------------------------------------------------------- plantilla maestra en blanco

    private static readonly string[] HojasDeDatos =
        ["Empresas", "Subcontratas", "Clientes empresariales", "Centros", "Accesos por Centro", "Trabajadores", "Asignaciones", "Vehículos", "Tipos de documento"];

    /// <summary>Lo que el lector <c>ClosedXmlPlantillaMaestraService</c> exige por hoja (sin el «*», que es del usuario).</summary>
    private static readonly Dictionary<string, string[]> ColumnasQueExigeElLector = new()
    {
        ["Empresas"] = ["Razón social", "CIF"],
        ["Clientes empresariales"] = ["Razón social", "CIF", "Crítico (C/N)", "CIF de la Empresa gestionada"],
        ["Centros"] = ["Nombre del Centro", "CIF del Cliente empresarial", "CIF de la Empresa gestionada", "Dirección", "Modalidad de gestión CAE"],
        ["Trabajadores"] =
        [
            "Nombre", "Apellidos", "Tipo de identidad", "Número de identidad", "CIF de la Empresa", "Carpeta actual",
            "Categoría / Puesto", "Fecha de nacimiento", "Nacionalidad", "Tipo de contratación", "Fecha de alta en la Empresa"
        ]
    };

    private static XLWorkbook Plantilla() => new(new MemoryStream(PlantillaMaestraEnBlanco.Generar()));

    private static string Cabecera(IXLWorksheet hoja, int columna) => hoja.Cell(1, columna).GetString();

    private static IEnumerable<string> Cabeceras(IXLWorksheet hoja) =>
        Enumerable.Range(1, hoja.LastColumnUsed()!.ColumnNumber()).Select(c => Cabecera(hoja, c));

    private static string SinAsterisco(string cabecera) => cabecera.Trim().TrimEnd('*').Trim();

    [Fact]
    public void Plantilla_TrasInstruccionesTraeUnaHojaPorEntidadEnSuOrden()
    {
        using var libro = Plantilla();

        libro.Worksheets.Select(h => h.Name).Should().Equal(["Instrucciones", .. HojasDeDatos]);
        PlantillaMaestraEnBlanco.NombresDeHojas.Should().Equal(HojasDeDatos);
    }

    [Fact]
    public void Plantilla_CadaHojaTraeLasColumnasQueElLectorExigeYLasMarcaComoObligatorias()
    {
        using var libro = Plantilla();

        foreach (var (nombre, exigidas) in ColumnasQueExigeElLector)
        {
            var cabeceras = Cabeceras(libro.Worksheet(nombre)).ToList();
            foreach (var columna in exigidas)
            {
                var encontrada = cabeceras.Where(c => SinAsterisco(c) == columna).ToList();
                encontrada.Should().ContainSingle($"la hoja «{nombre}» trae «{columna}» una sola vez (el lector la localiza por nombre)");
                encontrada[0].Should().EndWith("*", $"«{columna}» es obligatoria en «{nombre}»");
            }
        }
    }

    [Fact]
    public void Plantilla_LaColumnaDeControlSeIgnoraYNoTieneAsterisco()
    {
        using var libro = Plantilla();

        foreach (var nombre in HojasDeDatos.Where(n => n is not "Tipos de documento"))
            Cabeceras(libro.Worksheet(nombre)).Should().Contain("Control (no rellenar)", nombre);
    }

    [Fact]
    public void Plantilla_LaFila2EsUnEjemploFicticioMarcadoQueElLectorSalta()
    {
        using var libro = Plantilla();

        foreach (var nombre in HojasDeDatos)
        {
            var hoja = libro.Worksheet(nombre);
            hoja.Cell(2, 1).GetString().Should().StartWith("EJEMPLO", $"la fila 2 de «{nombre}» es el ejemplo");
            hoja.Cell(3, 1).IsEmpty().Should().BeTrue($"«{nombre}» no trae más filas que el ejemplo");
        }
    }

    [Fact]
    public void Plantilla_NoTieneColumnasDeCredencialesNiDeSalud()
    {
        using var libro = Plantilla();

        var prohibidas = new Regex("contraseñ|password|usuario|clave de acceso|diagn|médic|salud|baja médica", RegexOptions.IgnoreCase);
        foreach (var nombre in HojasDeDatos)
            Cabeceras(libro.Worksheet(nombre)).Where(c => prohibidas.IsMatch(c)).Should().BeEmpty(nombre);
    }

    [Fact]
    public void Plantilla_LasInstruccionesAdviertenDeCredencialesYDatosMedicos()
    {
        using var libro = Plantilla();

        var texto = string.Join('\n', libro.Worksheet("Instrucciones").CellsUsed().Select(c => c.GetString()));
        texto.Should().Contain("Sin credenciales").And.Contain("Nada médico").And.Contain("EJEMPLO");
    }
}

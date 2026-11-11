using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacionMaestra;
using CaeManager.Application.Importacion.Commands.RegistrarHistorialImportacion;
using CaeManager.Application.Importacion.Maestra;
using CaeManager.Application.Importacion.Queries;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Importacion;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Importacion;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Paso 1 del Importador (incremento I-2): la vista de la plantilla maestra, probada contra su doble de mediador.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> qué peticiones llegan al mediador, con qué parámetros y cuántas veces (el análisis, la
/// vista previa con su modo, la escritura y el registro en el historial); qué se pinta con lo que vuelve; el rechazo de
/// un archivo por extensión o por tamaño (10 MB, decisión N4) sin llamar al mediador; que una vista previa tardía no se
/// pinta; y que solo hay una acción principal en cada fase.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> el aspecto (bUnit no evalúa CSS), la autorización (la deciden la página y los handlers de
/// Application, que aquí son un doble: que «solo Administrador» se cumple es de sus tests), ni que el lector real de la
/// plantilla produzca el plan que el doble devuelve (eso es de integración, en CI).
/// </para>
/// </summary>
public class ImportacionEstructuraTests : BunitContext
{
    /// <summary>ZonaSoltarArchivo y los módulos de descarga son JS: fuera de lo que se observa salvo donde se prepara el módulo.</summary>
    public ImportacionEstructuraTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid TenantPrueba = Guid.NewGuid();

    // ---------------------------------------------------------------- dobles

    private sealed class MediadorDoble(Func<object, CancellationToken, Task<object?>> responder) : IMediator
    {
        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            (TResponse)(await responder(request, cancellationToken))!;

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

    /// <summary>Como el <c>BrowserFile</c> real: abrir con un límite menor que el tamaño declarado lanza.</summary>
    private sealed class ArchivoDoble(string nombre, long? tamano = null) : IBrowserFile
    {
        private static readonly byte[] Contenido = [0x50, 0x4B, 0x03, 0x04];

        public string Name => nombre;
        public DateTimeOffset LastModified => new(2026, 11, 11, 0, 0, 0, TimeSpan.Zero);
        public long Size => tamano ?? Contenido.LongLength;
        public string ContentType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
        {
            if (Size > maxAllowedSize)
                throw new IOException($"Supplied file with size {Size} bytes exceeds the maximum of {maxAllowedSize} bytes.");
            cancellationToken.ThrowIfCancellationRequested();
            return new MemoryStream(Contenido);
        }
    }

    /// <summary>El «servidor» del test: lo que se le pide queda registrado y responde según el plan y el modo recibidos.</summary>
    private sealed class Servidor
    {
        public PlanImportacionMaestraDto Plan { get; set; } = PlanBase();
        public Result<PlanImportacionMaestraDto>? RespuestaAnalisis { get; set; }
        public Exception? FalloDeAnalisis { get; set; }
        public List<ValorPrevioDto> ValoresPrevios { get; } = [];
        public Func<Task>? RetenerPrevia { get; set; }
        public Func<Task>? RetenerEscritura { get; set; }
        public Func<int, Result<ResultadoImportacionMaestraDto>?>? FalloDeEscritura { get; set; }
        public List<object> Recibidas { get; } = [];
        private readonly HashSet<Guid> _confirmadas = [];
        private int _escrituras;

        public IEnumerable<T> De<T>() => Recibidas.OfType<T>();

        public async Task<object?> Responder(object peticion, CancellationToken token)
        {
            Recibidas.Add(peticion);
            switch (peticion)
            {
                case AnalizarPlantillaMaestraQuery:
                    if (FalloDeAnalisis is not null) throw FalloDeAnalisis;
                    return RespuestaAnalisis ?? Result.Exito(Plan);
                case PrevisualizarImportacionMaestraCommand p:
                    if (RetenerPrevia is { } esperaPrevia) await esperaPrevia();
                    return Result.Exito(Simular(p.Plan, p.ReemplazarExistentes));
                case EjecutarImportacionMaestraCommand e:
                    if (RetenerEscritura is { } esperaEscritura) await esperaEscritura();
                    if (FalloDeEscritura?.Invoke(++_escrituras) is { } fallo) return fallo;
                    return _confirmadas.Add(e.Plan.OperacionId)
                        ? Result.Exito(Simular(e.Plan, e.ReemplazarExistentes))
                        : Result.Exito(ResultadoImportacionMaestraDto.YaEjecutada());
                case RegistrarHistorialImportacionCommand:
                    return Result.Exito();
                default:
                    throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.");
            }
        }

        /// <summary>Simplificación del handler real: crea lo que no existe; con Reemplazar, actualiza lo que existe.</summary>
        private ResultadoImportacionMaestraDto Simular(PlanImportacionMaestraDto plan, bool reemplazar) => new(
            plan.Empresas.Count(x => !x.YaExiste), reemplazar ? plan.Empresas.Count(x => x.YaExiste) : 0,
            plan.Clientes.Count(x => !x.YaExiste), reemplazar ? plan.Clientes.Count(x => x.YaExiste) : 0,
            plan.Clientes.Count(x => !x.YaExiste),
            plan.Centros.Count(x => !x.YaExiste), reemplazar ? plan.Centros.Count(x => x.YaExiste) : 0,
            plan.Trabajadores.Count(x => !x.YaExiste), reemplazar ? plan.Trabajadores.Count(x => x.YaExiste) : 0,
            2, reemplazar ? [.. ValoresPrevios] : [], plan.Advertencias, plan.Omitidos);
    }

    // ---------------------------------------------------------------- datos (ficticios)

    private static PlanImportacionMaestraDto PlanBase() => new(
        Guid.NewGuid(),
        [new EmpresaMaestraDto(2, "Industrias Ficticias, S.L.", "B12345674", "4321", null, false, false),
         new EmpresaMaestraDto(3, "Montajes Ficticios, S.A.", "A58818501", null, null, null, true)],
        [new ClienteEmpresarialMaestraDto(2, "Distribuciones Ficticias, S.A.", "A58818501", false, "B12345674", null, false)],
        [new CentroMaestraDto(2, "Planta Norte", "A58818501", "B12345674", "PN-01", "Calle Ficticia 1, 28000 Ciudad", null, null, false, false)],
        [new TrabajadorMaestraDto(2, "Nombre", "Apellido Ficticio", "12345678Z", "B12345674", "Apellido Ficticio Nombre", "Oficial 1ª",
            new DateOnly(1985, 6, 15), null, null, null, false)],
        [], [], []);

    private static PlanImportacionMaestraDto PlanConErrores() => PlanBase() with
    {
        Omitidos =
        [
            new ItemImportacionDto("Empresas", 4, "Obras Ficticias, S.L.", "El CIF no es válido."),
            new ItemImportacionDto("Trabajadores", 7, "Apellido Ficticio (12345678Z)", "La fecha de nacimiento no se puede leer.")
        ],
        Advertencias = [new ItemImportacionDto("Centros", 3, "Planta Sur", "Sin código de Centro.")]
    };

    // ---------------------------------------------------------------- arnés

    private (IRenderedComponent<ImportacionEstructura> Cut, Servidor Servidor) Renderizar(
        Servidor? servidor = null, bool dniCompleto = false)
    {
        servidor ??= new Servidor();
        var mediador = new MediadorDoble(servidor.Responder);
        Services.AddOptions<OpcionesLimitesImportacion>();
        Services.AddSingleton<LimitadorAnalisisImportacion>();
        Services.AddSingleton<ITenantActual>(new TenantEfectivoDePrueba(TenantPrueba));
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddLogging();
        Services.AddLocalization();
        var cut = Render<ImportacionEstructura>(p => p.Add(x => x.DniCompleto, dniCompleto));
        return (cut, servidor);
    }

    private static string Normalizar(string texto) => Regex.Replace(texto, @"\s+", " ").Trim();

    private static string Texto(IElement elemento) => Normalizar(elemento.TextContent);

    private static IElement Primario(IRenderedComponent<ImportacionEstructura> cut) => cut.Find("button.boton-primario");

    private static string Metrica(IRenderedComponent<ImportacionEstructura> cut, string etiqueta) =>
        Texto(cut.FindAll(".tarjeta-metrica")
            .Single(t => Texto(t.QuerySelector(".tarjeta-metrica-etiqueta")!) == etiqueta).QuerySelector(".tarjeta-metrica-valor")!);

    private static IElement Boton(IRenderedComponent<ImportacionEstructura> cut, string texto) =>
        cut.FindAll("button").Single(b => Texto(b) == texto);

    private static string Fase(IRenderedComponent<ImportacionEstructura> cut) => cut.Find(".estructura-importacion").GetAttribute("data-fase")!;

    private static Task Pulsar(IElement boton) => boton.ClickAsync(new MouseEventArgs());

    private static Task Subir(IRenderedComponent<ImportacionEstructura> cut, string nombre = "maestra.xlsx", long? tamano = null)
    {
        var entrada = cut.FindComponent<InputFile>();
        return cut.InvokeAsync(() => entrada.Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs([new ArchivoDoble(nombre, tamano)])));
    }

    private async Task<(IRenderedComponent<ImportacionEstructura> Cut, Servidor Servidor)> HastaValidacionAsync(
        Servidor? servidor = null, bool dniCompleto = false)
    {
        var (cut, s) = Renderizar(servidor, dniCompleto);
        await Subir(cut);
        await Pulsar(Primario(cut));
        Fase(cut).Should().Be("Validacion");
        return (cut, s);
    }

    private async Task<(IRenderedComponent<ImportacionEstructura> Cut, Servidor Servidor)> HastaVistaPreviaAsync(Servidor? servidor = null)
    {
        var (cut, s) = await HastaValidacionAsync(servidor);
        // Con errores el primario es «Corregir…»: se sigue con el secundario «Continuar sin las filas con error».
        await Pulsar(cut.FindAll("button").Any(b => Texto(b) == "Continuar sin las filas con error")
            ? Boton(cut, "Continuar sin las filas con error")
            : Primario(cut));
        Fase(cut).Should().Be("VistaPrevia");
        return (cut, s);
    }

    private static Task ElegirModo(IRenderedComponent<ImportacionEstructura> cut, string valor) =>
        cut.Find($"input[name='modo-importacion'][value='{valor}']").ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

    // ---------------------------------------------------------------- 1. subida

    [Fact]
    public void Subida_OfreceLaPlantillaEnBlancoYNoDejaValidarSinArchivo()
    {
        var (cut, _) = Renderizar();

        var enlace = cut.Find("a[download]");
        enlace.GetAttribute("href").Should().Be("/importacion/plantilla-maestra.xlsx").And.Be(ImportacionEndpoints.RutaPlantillaMaestra);
        enlace.GetAttribute("download").Should().Be("PLANTILLA-MAESTRA-IMPORTADOR.xlsx");
        cut.FindAll(".hojas-plantilla-est li").Select(Texto).Should().Equal(
            "Empresas", "Subcontratas", "Clientes empresariales", "Centros", "Accesos por Centro", "Trabajadores",
            "Asignaciones", "Vehículos", "Tipos de documento");
        Texto(Primario(cut)).Should().Be("Validar archivo");
        Primario(cut).HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("[data-testid='est-archivo-listo']").Should().BeEmpty();
        cut.Markup.Should().Contain("Sin credenciales").And.Contain("Nada médico");
    }

    [Fact]
    public async Task Subida_ArchivoQueNoEsXlsx_SeRechazaSinLlamarAlMediador()
    {
        var (cut, servidor) = Renderizar();

        await Subir(cut, "datos.csv");

        Texto(cut.Find(".alerta-formulario")).Should().Be("Solo se admiten archivos .xlsx.");
        Primario(cut).HasAttribute("disabled").Should().BeTrue();
        servidor.Recibidas.Should().BeEmpty();
        Fase(cut).Should().Be("Subir");
    }

    [Fact]
    public async Task Subida_ArchivoDeMasDe10Mb_SeRechazaConLaCifraDeN4_NoConLaDeLaMaqueta()
    {
        var (cut, servidor) = Renderizar();

        await Subir(cut, tamano: ImportacionEstructura.TamanoMaximoBytes + 1);

        Texto(cut.Find(".alerta-formulario")).Should().Be("El archivo pesa más de 10 MB.");
        cut.Markup.Should().NotContain("20 MB");
        Primario(cut).HasAttribute("disabled").Should().BeTrue();
        servidor.Recibidas.Should().BeEmpty();
    }

    [Fact]
    public async Task Subida_ArchivoDeExactamente10Mb_SeAcepta()
    {
        var (cut, servidor) = Renderizar();

        await Subir(cut, tamano: ImportacionEstructura.TamanoMaximoBytes);

        cut.FindAll(".alerta-formulario").Should().BeEmpty();
        cut.Find("[data-testid='est-archivo-listo']").TextContent.Should().Contain("maestra.xlsx");
        Primario(cut).HasAttribute("disabled").Should().BeFalse();
        servidor.Recibidas.Should().BeEmpty("subir no analiza: el análisis es del botón Validar");
    }

    [Fact]
    public void Subida_ElTopeEsDe10MbYSeDeclaraEnLaZona()
    {
        ImportacionEstructura.TamanoMaximoBytes.Should().Be(10 * 1024 * 1024);
        var (cut, _) = Renderizar();

        cut.Markup.Should().Contain("hasta 10 MB");
    }

    [Fact]
    public async Task Subida_UnArchivoAceptadoYLuegoOtroRechazado_NoConservaElPrimero()
    {
        var (cut, _) = Renderizar();
        await Subir(cut, "primero.xlsx");

        await Subir(cut, "segundo.csv");

        cut.FindAll("[data-testid='est-archivo-listo']").Should().BeEmpty();
        Primario(cut).HasAttribute("disabled").Should().BeTrue();
    }

    // ---------------------------------------------------------------- 2. análisis y validación

    [Fact]
    public async Task Analisis_EnviaLosBytesUnaVezYMuestraLaValidacionLimpia()
    {
        var (cut, servidor) = await HastaValidacionAsync();

        servidor.De<AnalizarPlantillaMaestraQuery>().Should().ContainSingle()
            .Which.ContenidoArchivo.Should().Equal(0x50, 0x4B, 0x03, 0x04);
        cut.FindAll("[data-testid='est-aviso-sin-errores']").Should().HaveCount(1);
        cut.FindAll("[data-testid='est-aviso-errores']").Should().BeEmpty();
        Metrica(cut, "Filas leídas").Should().Be("5");
        Metrica(cut, "Válidas").Should().Be("5");
        Metrica(cut, "Con error").Should().Be("0");
        Texto(Primario(cut)).Should().Be("Continuar a la vista previa");
        cut.FindAll("button").Select(Texto).Should().NotContain("Continuar sin las filas con error");
    }

    [Fact]
    public async Task Analisis_ConErrores_SeAgrupanPorHojaYSeMuestraLaFilaDelExcel()
    {
        var (cut, _) = await HastaValidacionAsync(new Servidor { Plan = PlanConErrores() });

        Texto(cut.Find("[data-testid='est-aviso-errores']")).Should().Contain("2 filas tienen errores y no se importarán.");
        Metrica(cut, "Con error").Should().Be("2");
        Metrica(cut, "Con aviso").Should().Be("1");

        // La hoja seleccionada al llegar es la primera con errores y lista sus filas reales.
        cut.Find("button[data-hoja='Empresas']").GetAttribute("aria-current").Should().Be("page");
        var filas = cut.FindAll("tr[data-gravedad]");
        filas.Should().ContainSingle();
        filas[0].GetAttribute("data-gravedad").Should().Be("error");
        Texto(filas[0]).Should().Contain("4").And.Contain("Obras Ficticias, S.L.").And.Contain("El CIF no es válido.");

        // Cada hoja con incidencias lo dice en su botón; las demás no llevan número de errores.
        Texto(cut.Find("button[data-hoja='Empresas']")).Should().Contain("1");
        Texto(cut.Find("button[data-hoja='Trabajadores']")).Should().Contain("1");

        await Pulsar(cut.Find("button[data-hoja='Trabajadores']"));
        cut.Find("button[data-hoja='Trabajadores']").GetAttribute("aria-current").Should().Be("page");
        Texto(cut.Find("tr[data-gravedad='error']")).Should().Contain("7").And.Contain("La fecha de nacimiento no se puede leer.");

        await Pulsar(cut.Find("button[data-hoja='Centros']"));
        var aviso = cut.Find("tr[data-gravedad='aviso']");
        Texto(aviso).Should().Contain("3").And.Contain("Planta Sur");
    }

    [Fact]
    public async Task Analisis_ConErrores_ElPrimarioEsCorregirYSeOfreceContinuarSinLasFilasConError()
    {
        var (cut, servidor) = await HastaValidacionAsync(new Servidor { Plan = PlanConErrores() });

        Texto(Primario(cut)).Should().Be("Corregir y subir de nuevo");
        Boton(cut, "Continuar sin las filas con error").Should().NotBeNull();

        await Pulsar(Boton(cut, "Continuar sin las filas con error"));

        Fase(cut).Should().Be("VistaPrevia");
        servidor.De<PrevisualizarImportacionMaestraCommand>().Should().ContainSingle()
            .Which.ReemplazarExistentes.Should().BeFalse();
    }

    [Fact]
    public async Task Analisis_CorregirYSubirDeNuevo_TiraElPlanYVuelveAlaSubida()
    {
        var (cut, _) = await HastaValidacionAsync(new Servidor { Plan = PlanConErrores() });

        await Pulsar(Primario(cut));

        Fase(cut).Should().Be("Subir");
        cut.FindAll("[data-testid='est-archivo-listo']").Should().BeEmpty();
        Primario(cut).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Analisis_ElDniDeLosErroresSeEnmascaraSalvoParaQuienPuedeVerloCompleto()
    {
        var (enmascarado, _) = await HastaValidacionAsync(new Servidor { Plan = PlanConErrores() });
        await Pulsar(enmascarado.Find("button[data-hoja='Trabajadores']"));
        enmascarado.Markup.Should().Contain("****678Z").And.NotContain("12345678Z");
    }

    [Fact]
    public async Task Analisis_ElDniDeLosErroresSeVeCompletoConDniCompleto()
    {
        var (completo, _) = await HastaValidacionAsync(new Servidor { Plan = PlanConErrores() }, dniCompleto: true);
        await Pulsar(completo.Find("button[data-hoja='Trabajadores']"));
        completo.Markup.Should().Contain("12345678Z");
    }

    [Fact]
    public async Task Analisis_LaBusquedaFiltraLasFilasDeLaHoja()
    {
        var plan = PlanBase() with
        {
            Omitidos =
            [
                new ItemImportacionDto("Empresas", 4, "Obras Ficticias, S.L.", "El CIF no es válido."),
                new ItemImportacionDto("Empresas", 9, "Talleres Ficticios, S.L.", "Falta la razón social.")
            ]
        };
        var (cut, _) = await HastaValidacionAsync(new Servidor { Plan = plan });
        cut.FindAll("tr[data-gravedad]").Should().HaveCount(2);

        cut.Find(".busqueda-est input").Input("talleres");

        var filas = cut.FindAll("tr[data-gravedad]");
        filas.Should().ContainSingle();
        Texto(filas[0]).Should().Contain("Talleres Ficticios");
    }

    [Fact]
    public async Task Analisis_LasHojasPendientesSeContanYNoSeCallan()
    {
        var plan = PlanBase() with { HojasPendientes = [new HojaPendienteDto("Subcontratas", 3), new HojaPendienteDto("Vehículos", 1)] };
        var (cut, _) = await HastaValidacionAsync(new Servidor { Plan = plan });

        Texto(cut.Find("[data-testid='est-aviso-hojas-pendientes']")).Should().Contain("Subcontratas (3), Vehículos (1)");
        Texto(cut.Find("button[data-hoja='Subcontratas']")).Should().Contain("no se importa");

        await Pulsar(cut.Find("button[data-hoja='Subcontratas']"));
        cut.Find("button[data-hoja='Subcontratas']").GetAttribute("aria-current").Should().Be("page");
        cut.FindAll("tr[data-gravedad]").Should().BeEmpty();
        cut.FindAll("[data-testid='est-hoja-sin-incidencias']").Should().ContainSingle();
    }

    [Fact]
    public async Task Analisis_SinFilasValidas_NoSeAvanzaYSeDiceElPorque()
    {
        var plan = new PlanImportacionMaestraDto(Guid.NewGuid(), [], [], [], [], [], [],
            [new ItemImportacionDto("Empresas", 4, "Obras Ficticias, S.L.", "El CIF no es válido.")]);
        var (cut, servidor) = await HastaValidacionAsync(new Servidor { Plan = plan });

        cut.FindAll("[data-testid='est-aviso-errores']").Should().HaveCount(1);
        Texto(Primario(cut)).Should().Be("Corregir y subir de nuevo");
        cut.FindAll("button").Select(Texto).Should().NotContain("Continuar sin las filas con error");
        servidor.De<PrevisualizarImportacionMaestraCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Analisis_ArchivoSinNingunaFila_AvisaDeQueNoHayNadaQueImportar()
    {
        var plan = new PlanImportacionMaestraDto(Guid.NewGuid(), [], [], [], [], [], [], []);
        var (cut, _) = await HastaValidacionAsync(new Servidor { Plan = plan });

        cut.FindAll("[data-testid='est-aviso-sin-validas']").Should().HaveCount(1);
        Texto(Primario(cut)).Should().Be("Corregir y subir de nuevo");
    }

    [Fact]
    public async Task Analisis_ElMensajeDeApplicationSeMuestraTalCualYNoSeAvanza()
    {
        var servidor = new Servidor
        {
            RespuestaAnalisis = Result.Fallo<PlanImportacionMaestraDto>(Error.Crear("Importacion.SoloAdministrador", "Solo Administrador puede analizar la plantilla maestra."))
        };
        var (cut, _) = Renderizar(servidor);
        await Subir(cut);

        await Pulsar(Primario(cut));

        Fase(cut).Should().Be("Subir");
        Texto(cut.Find(".alerta-formulario")).Should().Be("Solo Administrador puede analizar la plantilla maestra.");
        // El archivo sigue en memoria y se puede reintentar: no es un rechazo del archivo.
        cut.FindAll("[data-testid='est-archivo-listo']").Should().ContainSingle();
        Primario(cut).HasAttribute("disabled").Should().BeFalse("el fallo no es del archivo: se puede volver a validar");
        cut.FindAll(".badge").Select(Texto).Should().NotContain("Archivo no aceptado");
    }

    [Fact]
    public async Task Analisis_UnArchivoQueNoSeSabeLeer_VuelveAlaSubidaConMensajeSinNombreDeArchivo()
    {
        var servidor = new Servidor { FalloDeAnalisis = new InvalidDataException("no es un libro") };
        var (cut, _) = Renderizar(servidor);
        await Subir(cut, "nomina-de-alguien.xlsx");

        await Pulsar(Primario(cut));

        Fase(cut).Should().Be("Subir");
        var alerta = Texto(cut.Find(".alerta-formulario"));
        alerta.Should().NotBeEmpty().And.NotContain("nomina-de-alguien").And.NotContain("no es un libro");
        cut.FindAll("[data-testid='est-archivo-listo']").Should().BeEmpty("un archivo ilegible se suelta");
    }

    [Fact]
    public async Task Subida_UnArchivoValidoTrasUnoRechazadoSeMuestraListoYSePuedeValidar()
    {
        var (cut, _) = Renderizar();
        await Subir(cut, "datos.csv");
        cut.FindAll(".badge").Select(Texto).Should().Contain("Archivo no aceptado");

        await Subir(cut, "buena.xlsx");

        cut.FindAll("[data-testid='est-archivo-listo']").Should().ContainSingle();
        cut.FindAll(".badge").Select(Texto).Should().NotContain("Archivo no aceptado");
        Primario(cut).HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Analisis_ElLimitePorHojaSeMuestraSinNombreDeArchivo()
    {
        var servidor = new Servidor { FalloDeAnalisis = new LimiteHojaImportacionException(filas: true) };
        var (cut, _) = Renderizar(servidor);
        await Subir(cut, "nomina-de-alguien.xlsx");

        await Pulsar(Primario(cut));

        Fase(cut).Should().Be("Subir");
        Texto(cut.Find(".alerta-formulario")).Should().Be("El archivo supera el máximo de filas permitido por hoja.");
        cut.FindAll("[data-testid='est-archivo-listo']").Should().BeEmpty("un archivo fuera de límite se suelta");
        cut.Markup.Should().NotContain("nomina-de-alguien");
    }

    [Fact]
    public async Task Analisis_CancelarMientrasAnaliza_VuelveALaSubidaYNoPintaLaRespuestaTardia()
    {
        var puerta = new TaskCompletionSource<Result<PlanImportacionMaestraDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var servidor = new Servidor();
        var mediador = new MediadorDoble(async (peticion, token) =>
            peticion is AnalizarPlantillaMaestraQuery ? await puerta.Task : await servidor.Responder(peticion, token));
        Services.AddOptions<OpcionesLimitesImportacion>();
        Services.AddSingleton<LimitadorAnalisisImportacion>();
        Services.AddSingleton<ITenantActual>(new TenantEfectivoDePrueba(TenantPrueba));
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddLogging();
        Services.AddLocalization();
        var cut = Render<ImportacionEstructura>();
        await Subir(cut);

        var analisis = Pulsar(Primario(cut));
        Fase(cut).Should().Be("Analizando");
        cut.FindAll("[role=progressbar]").Should().ContainSingle();
        await Pulsar(Boton(cut, "Cancelar análisis"));
        Fase(cut).Should().Be("Subir");

        puerta.SetResult(Result.Exito(PlanBase()));
        await analisis;

        Fase(cut).Should().Be("Subir", "el análisis cancelado no se pinta");
        cut.FindAll("tr[data-gravedad]").Should().BeEmpty();
    }

    // ---------------------------------------------------------------- 3. vista previa

    [Fact]
    public async Task VistaPrevia_CompletarEsElModoPorDefectoYNoEscribe()
    {
        var (cut, servidor) = await HastaVistaPreviaAsync();

        servidor.De<PrevisualizarImportacionMaestraCommand>().Should().ContainSingle()
            .Which.ReemplazarExistentes.Should().BeFalse();
        servidor.De<EjecutarImportacionMaestraCommand>().Should().BeEmpty("la vista previa no escribe");
        cut.Find("input[name='modo-importacion'][value='completar']").HasAttribute("checked").Should().BeTrue();
        cut.Find("input[name='modo-importacion'][value='reemplazar']").HasAttribute("checked").Should().BeFalse();
        cut.FindAll("[data-testid='est-aviso-reemplazar']").Should().BeEmpty();
        Metrica(cut, "Se crearán").Should().Be("5");
        Metrica(cut, "Se actualizarán").Should().Be("0");
        Metrica(cut, "Sin cambios").Should().Be("1");
        Texto(Primario(cut)).Should().Be("Confirmar e importar (cambios: 5)");
        Primario(cut).HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task VistaPrevia_LaTablaDaUnaFilaPorEntidadConSusCifras()
    {
        var (cut, _) = await HastaVistaPreviaAsync();

        // Entidad, Se crearán, Se actualizarán, Sin cambios, Se omitirán.
        Celdas(cut, "EstEntEmpresas").Should().Equal("Empresas", "1", "0", "1", "0");
        Celdas(cut, "EstEntClientes").Should().Equal("Clientes", "1", "0", "0", "0");
        Celdas(cut, "EstEntRelaciones").Should().Equal("Relaciones Empresa - Cliente", "1", "0", "0", "0");
        Celdas(cut, "EstEntCentros").Should().Equal("Centros", "1", "0", "0", "0");
        Celdas(cut, "EstEntTrabajadores").Should().Equal("Trabajadores", "1", "0", "0", "0");
    }

    private static IEnumerable<string> Celdas(IRenderedComponent<ImportacionEstructura> cut, string entidad) =>
        cut.Find($"tr[data-entidad='{entidad}']").QuerySelectorAll("td").Take(5).Select(Texto);

    [Fact]
    public async Task VistaPrevia_ReemplazarPideUnaNuevaVistaPreviaYMuestraElAvisoDeN14()
    {
        var servidor = new Servidor();
        servidor.ValoresPrevios.AddRange(
        [
            new ValorPrevioDto("Empresas", 3, "CNAE", "4321", "4322"),
            new ValorPrevioDto("Empresas", 3, "Convenio aplicable", "Construcción", "Metal"),
            new ValorPrevioDto("Trabajadores", 7, "Teléfono", "+34 600 000 010", "+34 600 000 020")
        ]);
        var (cut, _) = await HastaVistaPreviaAsync(servidor);

        await ElegirModo(cut, "reemplazar");

        var previas = servidor.De<PrevisualizarImportacionMaestraCommand>().ToList();
        previas.Should().HaveCount(2);
        previas[1].ReemplazarExistentes.Should().BeTrue();
        previas[1].Plan.OperacionId.Should().Be(previas[0].Plan.OperacionId, "es el mismo análisis, no otro");
        var aviso = Texto(cut.Find("[data-testid='est-aviso-reemplazar']"));
        aviso.Should().Contain("Cambiarían 3 campos en 2 registros");
        Metrica(cut, "Se actualizarán").Should().Be("1");
        Texto(Primario(cut)).Should().Be("Confirmar e importar (cambios: 6)");
        cut.Find("details.detalle-previos-est").TextContent.Should().Contain("Construcción").And.Contain("Metal");
        servidor.De<EjecutarImportacionMaestraCommand>().Should().BeEmpty();

        await ElegirModo(cut, "completar");
        servidor.De<PrevisualizarImportacionMaestraCommand>().Last().ReemplazarExistentes.Should().BeFalse();
        cut.FindAll("[data-testid='est-aviso-reemplazar']").Should().BeEmpty();
    }

    [Fact]
    public async Task VistaPrevia_ReemplazarSinNadaQueSobrescribir_LoDice()
    {
        var (cut, _) = await HastaVistaPreviaAsync();

        await ElegirModo(cut, "reemplazar");

        Texto(cut.Find("[data-testid='est-aviso-reemplazar']")).Should().Contain("no cambiaría ningún valor");
        cut.FindAll("details.detalle-previos-est").Should().BeEmpty();
    }

    [Fact]
    public async Task VistaPrevia_ElDniDeLosValoresPreviosSeEnmascara()
    {
        var servidor = new Servidor();
        servidor.ValoresPrevios.Add(new ValorPrevioDto("Trabajadores", 7, "Observaciones", "Documento 12345678Z", "Documento 87654321X"));
        var (cut, _) = await HastaVistaPreviaAsync(servidor);

        await ElegirModo(cut, "reemplazar");

        cut.Markup.Should().Contain("****678Z").And.NotContain("12345678Z");
    }

    [Fact]
    public async Task VistaPrevia_UnaVistaPreviaQueLlegaTardeNoSePinta()
    {
        var puerta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var servidor = new Servidor { RetenerPrevia = () => puerta.Task };
        var (cut, _) = await HastaValidacionAsync(servidor);

        var continuar = Pulsar(Primario(cut));
        cut.FindAll("[data-testid='est-error-previa']").Should().BeEmpty();
        await Pulsar(Boton(cut, "Volver"));
        Fase(cut).Should().Be("Validacion");

        servidor.RetenerPrevia = null;
        puerta.SetResult();
        await continuar;

        Fase(cut).Should().Be("Validacion");
        cut.FindAll("input[name='modo-importacion']").Should().BeEmpty("la respuesta vieja no resucita la vista previa");
    }

    [Fact]
    public async Task VistaPrevia_SiFallaElCalculo_SePuedeReintentar()
    {
        var servidor = new Servidor();
        var intentos = 0;
        servidor.RetenerPrevia = () => ++intentos == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        var (cut, _) = await HastaValidacionAsync(servidor);
        await Pulsar(Primario(cut));

        Texto(cut.Find("[data-testid='est-error-previa']")).Should().Be("No pudimos calcular la vista previa. Inténtalo de nuevo.");
        Primario(cut).HasAttribute("disabled").Should().BeTrue("sin vista previa no se confirma");

        await Pulsar(Boton(cut, "Reintentar"));

        cut.FindAll("[data-testid='est-error-previa']").Should().BeEmpty();
        Metrica(cut, "Se crearán").Should().Be("5");
    }

    [Fact]
    public async Task VistaPrevia_DescartarTiraElPlanYVolverRegresaALaValidacion()
    {
        var (cut, _) = await HastaVistaPreviaAsync();
        await Pulsar(Boton(cut, "Volver"));
        Fase(cut).Should().Be("Validacion");

        await Pulsar(Primario(cut));
        await Pulsar(Boton(cut, "Descartar esta importación"));

        Fase(cut).Should().Be("Subir");
        cut.FindAll("[data-testid='est-archivo-listo']").Should().BeEmpty();
    }

    // ---------------------------------------------------------------- 4. confirmación e informe

    [Fact]
    public async Task Confirmar_Completar_EscribeUnaVezRegistraElHistorialYMuestraElInforme()
    {
        var (cut, servidor) = await HastaVistaPreviaAsync();
        var operacion = servidor.De<PrevisualizarImportacionMaestraCommand>().Single().Plan.OperacionId;

        await Pulsar(Primario(cut));

        var ejecucion = servidor.De<EjecutarImportacionMaestraCommand>().Should().ContainSingle().Subject;
        ejecucion.ReemplazarExistentes.Should().BeFalse();
        ejecucion.Plan.OperacionId.Should().Be(operacion);
        var historial = servidor.De<RegistrarHistorialImportacionCommand>().Should().ContainSingle().Subject;
        historial.Plantilla.Should().Be("Maestra");
        historial.NombreArchivo.Should().Be("maestra.xlsx");
        historial.Exitosa.Should().BeTrue();
        historial.TotalCreados.Should().Be(5);
        Fase(cut).Should().Be("Informe");
        cut.FindAll("[data-testid='est-informe-completada']").Should().HaveCount(1);
        Metrica(cut, "Creados").Should().Be("5");
        Metrica(cut, "Actualizados").Should().Be("0");
        Metrica(cut, "Omitidos").Should().Be("0");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Which.Tono.Should().Be(TonoToast.Exito);
        Texto(Primario(cut)).Should().Be("Hacer otra importación");
    }

    [Fact]
    public async Task Confirmar_MientrasEscribe_NoHayAccionesYSeVeElProgreso()
    {
        var puerta = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var servidor = new Servidor();
        var (cut, _) = await HastaVistaPreviaAsync(servidor);
        servidor.RetenerEscritura = () => puerta.Task;

        var escritura = Pulsar(Primario(cut));

        Fase(cut).Should().Be("Escribiendo");
        cut.FindAll("button.boton-primario").Should().BeEmpty("la escritura no se pausa ni se cancela: el backend no lo soporta");
        cut.FindAll("[role=progressbar]").Should().ContainSingle();
        cut.FindAll("button").Select(Texto).Should().NotContain(t => t.Contains("Pausar") || t.Contains("Cancelar"));
        servidor.De<EjecutarImportacionMaestraCommand>().Should().ContainSingle();

        puerta.SetResult();
        await escritura;
        Fase(cut).Should().Be("Informe");
    }

    [Fact]
    public async Task Confirmar_ReemplazarConValoresPrevios_PideConfirmacionAntesDeEscribir()
    {
        var servidor = new Servidor();
        servidor.ValoresPrevios.Add(new ValorPrevioDto("Empresas", 3, "CNAE", "4321", "4322"));
        var (cut, _) = await HastaVistaPreviaAsync(servidor);
        await ElegirModo(cut, "reemplazar");

        await Pulsar(Primario(cut));

        servidor.De<EjecutarImportacionMaestraCommand>().Should().BeEmpty("el diálogo de N14 va antes de escribir");
        cut.Find(".modal-pie").TextContent.Should().Contain("Sí, reemplazar e importar");

        await Pulsar(cut.FindAll(".modal-pie button").Single(b => Texto(b) == "Sí, reemplazar e importar"));

        servidor.De<EjecutarImportacionMaestraCommand>().Should().ContainSingle().Which.ReemplazarExistentes.Should().BeTrue();
        Fase(cut).Should().Be("Informe");
        Metrica(cut, "Actualizados").Should().Be("1");
    }

    [Fact]
    public async Task Confirmar_ReemplazarSinNadaQueSobrescribir_NoPideDialogo()
    {
        var (cut, servidor) = await HastaVistaPreviaAsync();
        await ElegirModo(cut, "reemplazar");

        await Pulsar(Primario(cut));

        cut.FindAll(".modal-pie").Should().BeEmpty();
        servidor.De<EjecutarImportacionMaestraCommand>().Should().ContainSingle().Which.ReemplazarExistentes.Should().BeTrue();
    }

    [Fact]
    public async Task Confirmar_SiLaEscrituraFalla_VuelveALaVistaPreviaYReintentarEsIdempotente()
    {
        var servidor = new Servidor
        {
            FalloDeEscritura = intento => intento == 1
                ? Result.Fallo<ResultadoImportacionMaestraDto>(Error.Crear("Importacion.Fallo", "No se pudo escribir en este momento."))
                : null
        };
        var (cut, _) = await HastaVistaPreviaAsync(servidor);

        await Pulsar(Primario(cut));

        Fase(cut).Should().Be("VistaPrevia");
        Texto(cut.Find("[data-testid='est-error-importacion']")).Should().Be("No se pudo escribir en este momento.");
        var fallo = servidor.De<RegistrarHistorialImportacionCommand>().Should().ContainSingle().Subject;
        fallo.Exitosa.Should().BeFalse();
        fallo.MensajeError.Should().Be("No se pudo escribir en este momento.");
        Services.GetRequiredService<ToastService>().Mensajes.Should().BeEmpty();

        await Pulsar(Primario(cut));

        var ejecuciones = servidor.De<EjecutarImportacionMaestraCommand>().ToList();
        ejecuciones.Should().HaveCount(2);
        ejecuciones[1].Plan.OperacionId.Should().Be(ejecuciones[0].Plan.OperacionId, "reintentar confirma la misma operación");
        Fase(cut).Should().Be("Informe");
    }

    [Fact]
    public async Task Confirmar_UnaExcepcionNoFiltraSuTextoYDejaReintentar()
    {
        var servidor = new Servidor { FalloDeEscritura = intento => intento == 1 ? throw new InvalidOperationException("detalle interno secreto") : null };
        var (cut, _) = await HastaVistaPreviaAsync(servidor);

        await Pulsar(Primario(cut));

        Fase(cut).Should().Be("VistaPrevia");
        cut.Markup.Should().NotContain("detalle interno secreto");
        cut.FindAll("[data-testid='est-error-importacion']").Should().ContainSingle();
        servidor.De<RegistrarHistorialImportacionCommand>().Should().ContainSingle().Which.Exitosa.Should().BeFalse();
    }

    [Fact]
    public async Task Informe_ConfirmarDosVeces_MuestraQueYaSeEjecutoSinContarlaComoNueva()
    {
        var (cut, servidor) = await HastaVistaPreviaAsync();
        var plan = servidor.De<PrevisualizarImportacionMaestraCommand>().Single().Plan;
        // La primera confirmación de ESTE plan ya ocurrió (otra pestaña, un reintento tras un corte).
        await servidor.Responder(new EjecutarImportacionMaestraCommand(plan, false), CancellationToken.None);

        await Pulsar(Primario(cut));

        cut.FindAll("[data-testid='est-informe-ya-ejecutada']").Should().ContainSingle();
        cut.FindAll("[data-testid='est-informe-completada']").Should().BeEmpty();
    }

    [Fact]
    public async Task Informe_MuestraOmitidosYAvisosConSuFilaYElDniEnmascarado()
    {
        var plan = PlanConErrores();
        var (cut, _) = await HastaVistaPreviaAsync(new Servidor { Plan = plan });
        await Pulsar(Primario(cut));

        Fase(cut).Should().Be("Informe");
        Metrica(cut, "Omitidos").Should().Be("2");
        cut.Markup.Should().Contain("Obras Ficticias, S.L.").And.Contain("Planta Sur");
        cut.Markup.Should().NotContain("12345678Z");
        Texto(cut.Find("[data-testid='est-informe-completada']")).Should().Contain("2 filas");
    }

    [Fact]
    public async Task Informe_LosCsvSeDescarganConSuNombreYNeutralizanFormulas()
    {
        var plan = PlanBase() with
        {
            Omitidos = [new ItemImportacionDto("Empresas", 4, "=HYPERLINK(\"http://ejemplo.invalid\")", "El CIF no es válido.")]
        };
        var modulo = JSInterop.SetupModule("./js/importacion-descarga.js");
        modulo.SetupVoid("descargarTexto", _ => true).SetVoidResult();
        var (cut, _) = await HastaVistaPreviaAsync(new Servidor { Plan = plan });
        await Pulsar(Primario(cut));

        await Pulsar(Boton(cut, "Solo filas omitidas (CSV)"));

        var llamada = modulo.Invocations["descargarTexto"].Should().ContainSingle().Subject;
        llamada.Arguments[0].Should().Be("filas-omitidas-importacion.csv");
        var csv = (string)llamada.Arguments[1]!;
        csv.Should().Contain("\"'=HYPERLINK(").And.NotContain("\"=HYPERLINK(");
        csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(2, "cabecera y una fila");
    }

    [Fact]
    public async Task Descargas_LosTresCsvEnmascaranElDniSalvoPermiso()
    {
        var plan = PlanConErrores();
        var servidor = new Servidor { Plan = plan };
        servidor.ValoresPrevios.Add(new ValorPrevioDto("Trabajadores", 7, "Observaciones", "Documento 12345678Z", "Documento 87654321X"));
        var modulo = JSInterop.SetupModule("./js/importacion-descarga.js");
        modulo.SetupVoid("descargarTexto", _ => true).SetVoidResult();
        var (cut, _) = await HastaValidacionAsync(servidor, dniCompleto: false);

        await Pulsar(Boton(cut, "Descargar errores (CSV)"));
        await Pulsar(Boton(cut, "Continuar sin las filas con error"));
        await ElegirModo(cut, "reemplazar");
        await Pulsar(Boton(cut, "Valores previos (CSV)"));
        await Pulsar(Primario(cut));
        await Pulsar(Boton(cut, "Sí, reemplazar e importar"));
        await Pulsar(Boton(cut, "Informe completo (CSV)"));

        var csvs = modulo.Invocations["descargarTexto"].Select(i => (string)i.Arguments[1]!).ToList();
        csvs.Should().HaveCount(3, "errores, valores previos e informe completo");
        foreach (var csv in csvs)
            csv.Should().Contain("****678Z", "el DNI sale enmascarado").And.NotContain("12345678Z").And.NotContain("87654321X");
    }

    [Fact]
    public async Task Informe_SinOmitidos_ElBotonDeSoloOmitidasEstaDeshabilitado()
    {
        var (cut, _) = await HastaVistaPreviaAsync();
        await Pulsar(Primario(cut));

        Boton(cut, "Solo filas omitidas (CSV)").HasAttribute("disabled").Should().BeTrue();
        Boton(cut, "Informe completo (CSV)").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Informe_HacerOtraImportacionVuelveALaSubidaLimpia()
    {
        var (cut, _) = await HastaVistaPreviaAsync();
        await Pulsar(Primario(cut));

        await Pulsar(Primario(cut));

        Fase(cut).Should().Be("Subir");
        cut.FindAll("[data-testid='est-archivo-listo']").Should().BeEmpty();
        Texto(Primario(cut)).Should().Be("Validar archivo");
    }

    // ---------------------------------------------------------------- transversal

    [Fact]
    public async Task EnCadaFaseHayUnaSolaAccionPrincipal()
    {
        var servidor = new Servidor { Plan = PlanConErrores() };
        var (cut, _) = Renderizar(servidor);
        cut.FindAll("button.boton-primario").Should().ContainSingle("Subir");

        await Subir(cut);
        cut.FindAll("button.boton-primario").Should().ContainSingle("Subir con archivo");

        await Pulsar(Primario(cut));
        cut.FindAll("button.boton-primario").Should().ContainSingle("Validación");

        await Pulsar(Boton(cut, "Continuar sin las filas con error"));
        cut.FindAll("button.boton-primario").Should().ContainSingle("Vista previa");

        await Pulsar(Primario(cut));
        cut.FindAll("button.boton-primario").Should().ContainSingle("Informe");
    }

    [Fact]
    public async Task LaFilaDePasosAvanzaConLaFase()
    {
        var (cut, _) = Renderizar();
        string Actual() => Texto(cut.Find(".paso-est-actual"));
        Actual().Should().Contain("Plantilla");

        await Subir(cut);
        Actual().Should().Contain("Subir");

        await Pulsar(Primario(cut));
        Actual().Should().Contain("Validar");

        await Pulsar(Primario(cut));
        Actual().Should().Contain("Vista previa");

        await Pulsar(Primario(cut));
        Actual().Should().Contain("Informe");
    }

    [Fact]
    public async Task AvisaDeCambiosSinGuardarSoloConUnArchivoAnalizadoSinImportar()
    {
        var (cut, _) = Renderizar();
        cut.FindComponent<AvisoCambiosSinGuardar>().Instance.HayCambios().Should().BeFalse("sin archivo no hay nada que perder");

        await Subir(cut);
        await Pulsar(Primario(cut));
        cut.FindComponent<AvisoCambiosSinGuardar>().Instance.HayCambios().Should().BeTrue("un plan analizado se pierde al salir");

        await Pulsar(Primario(cut));
        await Pulsar(Primario(cut));
        cut.FindComponent<AvisoCambiosSinGuardar>().Instance.HayCambios().Should().BeFalse("ya importado");
    }
}

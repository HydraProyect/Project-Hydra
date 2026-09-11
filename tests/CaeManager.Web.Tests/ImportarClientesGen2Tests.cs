using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using CaeManager.Application.Importacion.Commands.RegistrarHistorialImportacion;
using CaeManager.Application.Importacion.Queries;
using CaeManager.Application.Importacion.Queries.ObtenerHistorialImportaciones;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Importacion;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Clientes.Pages;
using ClosedXML.Excel;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using PaginaImportacion = CaeManager.Web.Features.Importacion.Pages.Importacion;

namespace CaeManager.Web.Tests;

/// <summary>
/// Importar Clientes contra su mockup Gen 2 («Importar Clientes
/// TALVEG.dc.html»). El propio mockup declara que /clientes/importar
/// (ImportarClientes.razor) solo redirige y que lo dibujado es el asistente
/// /importacion (Importacion.razor) con la Plantilla de Clientes elegida: esto
/// prueba ese asistente con ?plantilla=clientes, y la redirección.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> qué consultas y comandos llegan al mediador,
/// con qué parámetros y cuántas veces; qué se pinta con lo que vuelve; y qué
/// pasa cuando las respuestas llegan fuera de orden, se pulsa dos veces o se
/// retira la página (<see cref="TaskCompletionSource"/>). El doble responde
/// según sus parámetros, como los handlers reales: el análisis devuelve el plan
/// del CONTENIDO del archivo que recibe (y falla con uno que no sabe leer); la
/// escritura aplica la regla de <c>EjecutarImportacionCommandHandler</c> para
/// Cliente/Centro —solo reutiliza los que ya existen, omite los demás con su
/// motivo, uno por fila— y su idempotencia por <c>OperacionId</c>; el
/// historial devuelve lo registrado, lo último primero y hasta su límite.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> el aspecto (bUnit no evalúa CSS), la autorización
/// real de la ruta (solo que el atributo sigue puesto), ni que los handlers
/// reales hagan lo que el doble imita — eso es de Application, integración y
/// del E2E <c>ImportarClientesTests</c>. Las columnas del paso 2 sí se comparan
/// con la plantilla real que genera <see cref="ClosedXmlPlantillaClientesService"/>.
/// </para>
/// </summary>
public class ImportarClientesGen2Tests : BunitContext
{
    /// <summary>ZonaSoltarArchivo y Modal importan módulos JS: quedan fuera de lo que se observa aquí.</summary>
    public ImportarClientesGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private const string MotivoClienteNoExiste =
        "Este cliente no existe todavía. Ahora requiere un CIF, que esta plantilla no recoge — créalo manualmente en Clientes.";

    private const string MotivoCentroNoExiste =
        "Este centro no existe todavía. Ahora requiere una Empresa asociada, que esta plantilla no recoge — créalo manualmente en Centros.";

    // ---------------------------------------------------------------- dobles

    private sealed class MediadorControlado(Func<object, CancellationToken, Task<object?>> responder) : IMediator
    {
        public List<(object Peticion, CancellationToken Token)> Recibidas { get; } = [];

        public IEnumerable<object> Enviados => Recibidas.Select(r => r.Peticion);

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Recibidas.Add((request, cancellationToken));
            return (TResponse)(await responder(request, cancellationToken))!;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException("La página no envía peticiones sin respuesta.");

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("La página no envía peticiones sin tipo.");

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    /// <summary>El «servidor» del test: lo que un comando cambia, la siguiente consulta lo devuelve.</summary>
    private sealed class Escenario
    {
        private DateTime _reloj = new(2026, 9, 5, 9, 0, 0, DateTimeKind.Utc);

        /// <summary>Plan que devuelve cada análisis, por consulta y contenido del archivo recibido.</summary>
        public Dictionary<(Type Consulta, string Contenido), PlanImportacionDto> Planes { get; } = [];

        /// <summary>Clientes empresariales (Empresas con EsCritico) y Centros que ya existen: los únicos que el handler reutiliza.</summary>
        public HashSet<string> ClientesExistentes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> CentrosExistentes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<HistorialImportacionDto> Historial { get; } = [];

        private HashSet<Guid> OperacionesConfirmadas { get; } = [];

        /// <summary>Si devuelve una tarea, ESA es la respuesta (una foto vieja, un fallo).</summary>
        public Func<object, Task<object?>?> Interceptar { get; set; } = _ => null;

        /// <summary>Si devuelve una tarea, la petición espera a que el test la complete y después se responde como siempre.</summary>
        public Func<object, Task?> Retener { get; set; } = _ => null;

        public PlanImportacionDto Plan<TConsulta>(
            string contenido, IEnumerable<ClienteCentroImportadoDto> clientesCentros,
            IEnumerable<ItemImportacionDto>? omitidos = null, IEnumerable<EmpresaImportadaDto>? empresas = null)
        {
            var plan = new PlanImportacionDto(
                Guid.NewGuid(), [.. clientesCentros], [.. empresas ?? []], [], [], [], [], [.. omitidos ?? []]);
            Planes[(typeof(TConsulta), contenido)] = plan;
            return plan;
        }

        public HistorialImportacionDto RegistrarEnHistorial(string plantilla, string archivo)
        {
            var entrada = new HistorialImportacionDto(Guid.NewGuid(), plantilla, archivo, _reloj = _reloj.AddMinutes(1),
                Guid.NewGuid(), true, 1, 0, 0, null);
            Historial.Add(entrada);
            return entrada;
        }

        public IReadOnlyList<HistorialImportacionDto> Foto(int limite) =>
            Historial.OrderByDescending(h => h.EjecutadaEnUtc).Take(limite).ToList();

        public async Task<object?> Responder(object peticion, CancellationToken token)
        {
            if (Interceptar(peticion) is { } respuesta)
                return await respuesta;

            if (Retener(peticion) is { } puerta)
                await puerta;

            return ResponderAhora(peticion);
        }

        private object? ResponderAhora(object peticion) =>
            peticion switch
            {
                AnalizarPlantillaClientesQuery q => Analizar(typeof(AnalizarPlantillaClientesQuery), q.ContenidoArchivo),
                AnalizarImportacionExcelQuery q => Analizar(typeof(AnalizarImportacionExcelQuery), q.ContenidoArchivo),
                ObtenerHistorialImportacionesQuery q => Foto(q.Limite),
                EjecutarImportacionCommand c => Ejecutar(c.Plan),
                RegistrarHistorialImportacionCommand c => Registrar(c),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
            };

        /// <summary>Como el handler: el plan sale de los bytes; un archivo que no sabe leer, excepción.</summary>
        private PlanImportacionDto Analizar(Type consulta, byte[] contenido) =>
            Planes.TryGetValue((consulta, Encoding.UTF8.GetString(contenido)), out var plan)
                ? plan
                : throw new InvalidDataException("No es un libro que esta plantilla sepa leer.");

        /// <summary>
        /// La regla de Cliente/Centro de EjecutarImportacionCommandHandler: nada
        /// se crea; lo que no existe se omite con su motivo, uno por fila (corta
        /// en cuanto falta el Cliente empresarial); el resultado lleva los
        /// omitidos del plan más los de la escritura. Confirmar dos veces la
        /// misma operación no escribe dos veces.
        /// </summary>
        private Result<ResultadoImportacionDto> Ejecutar(PlanImportacionDto plan)
        {
            if (!OperacionesConfirmadas.Add(plan.OperacionId))
                return Result.Exito(ResultadoImportacionDto.YaEjecutada());

            var omitidosEnEscritura = new List<ItemImportacionDto>();
            foreach (var fila in plan.ClientesCentros)
            {
                if (!ClientesExistentes.Contains(fila.Nombre))
                {
                    omitidosEnEscritura.Add(new ItemImportacionDto("Centros_Plataformas", 0, fila.Nombre, MotivoClienteNoExiste));
                    continue;
                }

                if (!CentrosExistentes.Contains(fila.Nombre))
                    omitidosEnEscritura.Add(new ItemImportacionDto("Centros_Plataformas", 0, fila.Nombre, MotivoCentroNoExiste));
            }

            return Result.Exito(new ResultadoImportacionDto(
                0, 0, plan.Empresas.Count(e => !e.YaExiste), 0, 0, 0,
                plan.Advertencias, [.. plan.Omitidos, .. omitidosEnEscritura]));
        }

        private Result Registrar(RegistrarHistorialImportacionCommand c)
        {
            Historial.Add(new HistorialImportacionDto(Guid.NewGuid(), c.Plantilla, c.NombreArchivo, _reloj = _reloj.AddMinutes(1),
                Guid.NewGuid(), c.Exitosa, c.TotalCreados, c.TotalAdvertencias, c.TotalOmitidos, c.MensajeError));
            return Result.Exito();
        }
    }

    /// <summary>Ningún usuario del historial se encuentra: la página pinta «(usuario eliminado)». Nada más se consulta.</summary>
    private sealed class AlmacenUsuariosVacio : IUserStore<ApplicationUser>
    {
        private static Exception NoDeberia() => new NotSupportedException("La página solo busca usuarios por id.");

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => Task.FromResult<ApplicationUser?>(null);
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public void Dispose() { }
    }

    private sealed class ArchivoFalso(string nombre, byte[] contenido) : IBrowserFile
    {
        public string Name => nombre;
        public DateTimeOffset LastModified => new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
        public long Size => contenido.LongLength;
        public string ContentType => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default) =>
            new MemoryStream(contenido);
    }

    // ---------------------------------------------------------------- arnés

    private (IRenderedComponent<PaginaImportacion> Cut, MediadorControlado Mediador) Renderizar(
        Escenario escenario, string url = "importacion?plantilla=clientes")
    {
        var mediador = new MediadorControlado(escenario.Responder);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddLogging();
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            new AlmacenUsuariosVacio(), null!, null!, null!, null!, null!, null!, null!, null!));
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);

        var cut = Render<PaginaImportacion>();
        return (cut, mediador);
    }

    private static string Normalizar(string texto) => Regex.Replace(texto, @"\s+", " ").Trim();

    private static string Texto(IElement elemento) => Normalizar(elemento.TextContent);

    private static IElement Boton(IRenderedComponent<PaginaImportacion> cut, string texto) =>
        cut.FindAll("button").Single(b => Texto(b) == texto);

    private static Task Pulsar(IRenderedComponent<PaginaImportacion> cut, string texto) =>
        Boton(cut, texto).ClickAsync(new MouseEventArgs());

    private static IElement PasoDelIndicador(IRenderedComponent<PaginaImportacion> cut, string nombre) =>
        cut.FindAll("button.paso-importacion").Single(b => Texto(b).EndsWith(nombre, StringComparison.Ordinal));

    private static IElement BotonDelDialogo(IRenderedComponent<PaginaImportacion> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => Texto(b) == texto);

    private static string TituloDeLaSeccion(IRenderedComponent<PaginaImportacion> cut) =>
        Texto(cut.Find("h2.titulo-seccion-importacion"));

    /// <summary>
    /// Entrega un archivo al OnChange del InputFile tal como lo haría el
    /// navegador (selector, arrastrar o pegar). No se espera aquí: si el
    /// análisis está retenido, la tarea queda pendiente hasta soltarlo.
    /// </summary>
    private static Task Subir(IRenderedComponent<PaginaImportacion> cut, string nombre, string contenido)
    {
        var entrada = cut.FindComponent<InputFile>();
        var archivo = new ArchivoFalso(nombre, Encoding.UTF8.GetBytes(contenido));
        return cut.InvokeAsync(() => entrada.Instance.OnChange.InvokeAsync(new InputFileChangeEventArgs([archivo])));
    }

    private static Task MarcarRevisado(IRenderedComponent<PaginaImportacion> cut) =>
        cut.Find("label.casilla-confirmacion-importacion input").ChangeAsync(new ChangeEventArgs { Value = true });

    private static async Task LlevarAConfirmarAsync(IRenderedComponent<PaginaImportacion> cut, string archivo, string contenido)
    {
        await Pulsar(cut, "Continuar con Plantilla de Clientes");
        await Subir(cut, archivo, contenido);
        await Pulsar(cut, "Ver plan de importación");
        await Pulsar(cut, "Continuar a confirmar");
    }

    private static T Campo<T>(PaginaImportacion instancia, string nombre) =>
        (T)(typeof(PaginaImportacion).GetField(nombre, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Importacion ya no tiene el campo {nombre}: el test ha dejado de observar lo que dice."))
        .GetValue(instancia)!;

    private static ClienteCentroImportadoDto Fila(string nombre, bool yaExisteCliente = false, bool yaExisteCentro = false) =>
        new(nombre, false, null, null, yaExisteCliente, yaExisteCentro);

    private static TaskCompletionSource Puerta() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---------------------------------------------------------------- ruta y cabecera

    [Fact]
    public void La_ruta_de_Clientes_redirige_al_asistente_con_su_plantilla_y_las_dos_siguen_exigiendo_Administrador()
    {
        Render<ImportarClientes>();

        Services.GetRequiredService<NavigationManager>().Uri
            .Should().EndWith("/importacion?plantilla=clientes", "el mockup declara que /clientes/importar solo redirige (H-1)");

        foreach (var pagina in new[] { typeof(ImportarClientes), typeof(PaginaImportacion) })
            pagina.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                .Should().ContainSingle().Which.Roles.Should().Be(Roles.Administrador, $"{pagina.Name} conserva su regla de rol");
    }

    [Fact]
    public async Task Desde_Clientes_la_cabecera_es_la_del_mockup_y_ofrece_volver_a_Clientes()
    {
        var (cut, _) = Renderizar(new Escenario());

        Texto(cut.Find("h1.titulo-pagina")).Should().Be("Importar clientes");
        Texto(cut.Find(".cabecera-pagina-kicker")).Should().Be("Configuración");
        var volver = cut.Find("a.enlace-volver-importacion");
        volver.GetAttribute("href").Should().Be("/clientes");
        Texto(volver).Should().Be("Volver a Clientes");
        Texto(cut.Find(".miga-importacion")).Should().Be("Negocio → Clientes → Importar clientes");
        cut.Find("[data-plantilla='clientes']").GetAttribute("aria-pressed").Should().Be("true");

        await cut.Find("[data-plantilla='documentos']").ClickAsync(new MouseEventArgs());

        Texto(cut.Find("h1.titulo-pagina")).Should().Be("Importar datos",
            "con otra plantilla ya no se están importando Clientes empresariales");
        Texto(cut.Find("a.enlace-volver-importacion")).Should().Be("Volver a Clientes", "se sigue habiendo llegado desde Clientes");
    }

    [Fact]
    public void Sin_venir_de_Clientes_es_la_cabecera_de_Importar_datos_sin_vuelta_a_Clientes()
    {
        var (cut, _) = Renderizar(new Escenario(), url: "importacion");

        Texto(cut.Find("h1.titulo-pagina")).Should().Be("Importar datos");
        cut.FindAll("a.enlace-volver-importacion").Should().BeEmpty();
        cut.Find("[data-plantilla='cae']").GetAttribute("aria-pressed").Should().Be("true");
    }

    // ---------------------------------------------------------------- paso 1 y 2

    [Fact]
    public void La_tarjeta_de_Clientes_dice_lo_que_hace_la_plantilla_y_su_limite_real()
    {
        var (cut, _) = Renderizar(new Escenario());

        var clientes = cut.Find("[data-plantilla='clientes']");
        Texto(clientes.QuerySelector(".descripcion-tarjeta-plantilla")!).Should()
            .Contain("No recoge CIF ni Empresa").And.NotContain("datos fiscales",
                "la plantilla no recoge el CIF: prometer sus datos fiscales sería falso");
        Texto(clientes.QuerySelector(".limite-tarjeta-plantilla")!).Should().Be(".xlsx · máx. 5 MB");
        clientes.QuerySelector("a.enlace-plantilla-blanco")!.GetAttribute("href").Should().Be("/clientes/plantilla.xlsx");

        var cae = cut.Find("[data-plantilla='cae']");
        Texto(cae.QuerySelector(".limite-tarjeta-plantilla")!).Should().Be(".xlsx · máx. 20 MB");
        cae.QuerySelector("a.enlace-plantilla-blanco").Should().BeNull("la CAE completa procesa un archivo que ya existe");

        Texto(cut.Find(".vacio-historial-importacion")).Should().Be("Todavía no se ha ejecutado ninguna importación.");
    }

    [Fact]
    public async Task Las_columnas_del_paso_2_son_las_de_la_plantilla_que_se_descarga()
    {
        var (cut, _) = Renderizar(new Escenario());
        await Pulsar(cut, "Continuar con Plantilla de Clientes");

        using var libro = new XLWorkbook(new MemoryStream(new ClosedXmlPlantillaClientesService(null!, null!).GenerarPlantilla()));
        var hoja = libro.Worksheets.Should().ContainSingle().Subject;
        hoja.Name.Should().Be("Clientes");
        Texto(cut.Find(".cabecera-columnas-plantilla")).Should().Be("Columnas que espera la hoja «Clientes»");

        var filas = cut.FindAll("tr[data-columna-plantilla]")
            .Select(f => f.QuerySelectorAll("td").Select(Texto).ToArray()).ToList();

        filas.Select(f => f[0]).Should().Equal(
            Enumerable.Range(1, 4).Select(c => hoja.Cell(1, c).GetString()),
            "los rótulos tienen que ser los de la cabecera que escribe GenerarPlantilla");
        hoja.Cell(1, 5).IsEmpty().Should().BeTrue("la plantilla no tiene más columnas que las que se enseñan");
        filas.Skip(1).Select(f => f[1]).Should().Equal(
            Enumerable.Range(2, 3).Select(c => hoja.Cell(2, c).GetString()),
            "los ejemplos de las columnas opcionales son los de la fila de ejemplo de la plantilla");
        filas.Select(f => f[2]).Should().Equal(["Sí", "No", "No", "No"], "el lector solo exige el nombre");
        Texto(cut.Find(".nota-columnas-plantilla")).Should().Contain("La lectura termina en la primera fila sin nombre");
    }

    [Fact]
    public async Task Con_otra_plantilla_el_paso_2_no_enseña_las_columnas_de_Clientes()
    {
        var (cut, _) = Renderizar(new Escenario(), url: "importacion");
        await Pulsar(cut, "Continuar con Importación CAE completa (multi-hoja)");

        cut.FindAll(".columnas-plantilla-importacion").Should().BeEmpty();
    }

    // ---------------------------------------------------------------- plan

    [Fact]
    public async Task El_plan_cuenta_lo_del_analisis_pero_avisa_de_que_ninguna_alta_de_Clientes_se_hara()
    {
        var escenario = new Escenario();
        escenario.ClientesExistentes.Add("Refrielectric S.L.");
        escenario.Plan<AnalizarPlantillaClientesQuery>("levante",
        [
            Fila("Instalaciones Vidal S.L."),
            Fila("Refrielectric S.L.", yaExisteCliente: true),
            Fila("Frío Turia S.A.", yaExisteCliente: true, yaExisteCentro: true)
        ]);
        var (cut, mediador) = Renderizar(escenario);

        await Pulsar(cut, "Continuar con Plantilla de Clientes");
        await Subir(cut, "clientes-levante.xlsx", "levante");
        mediador.Enviados.OfType<AnalizarPlantillaClientesQuery>().Should().ContainSingle()
            .Which.ContenidoArchivo.Should().Equal(Encoding.UTF8.GetBytes("levante"), "se analiza el archivo que se subió");
        await Pulsar(cut, "Ver plan de importación");

        cut.FindAll(".badges-resumen-plan .badge").Select(Texto).Should().Equal(["3 se crearán", "0 con aviso", "0 se omitirán"],
            "el recuento es el del análisis: 1 Cliente empresarial y 2 Centros con nombre nuevo");
        cut.Find(".badges-resumen-plan .badge").GetAttribute("title").Should().Be("1 Clientes empresariales y 2 Centros con nombre nuevo");
        cut.FindAll(".tabla-plan-importacion-envoltorio tbody tr").Select(f => f.QuerySelectorAll("td").Select(Texto).ToArray())
            .Should().BeEquivalentTo(new[]
            {
                new[] { "Instalaciones Vidal S.L.", "Crear cliente", "Nombre nuevo en la hoja «Clientes»." },
                new[] { "Instalaciones Vidal S.L.", "Crear centro", "Nombre nuevo en la hoja «Clientes»." },
                new[] { "Refrielectric S.L.", "Crear centro", "Nombre nuevo en la hoja «Clientes»." }
            }, o => o.WithStrictOrdering());

        Texto(cut.Find(".titulo-aviso-altas")).Should().Be("Ninguna de estas altas se hará al importar.");
        Texto(cut.Find(".detalle-aviso-altas")).Should().Be(
            "Las 2 filas de Cliente empresarial o Centro con un nombre que todavía no existe se omitirán: esta importación no recoge " +
            "el CIF que exige el alta de un Cliente empresarial ni la Empresa que exige la de un Centro. Dalos de alta a mano en Clientes y Centros.",
            "dos filas —no tres altas— porque el handler omite una vez por fila");
    }

    [Fact]
    public async Task Si_todos_los_nombres_ya_existen_no_hay_aviso_de_altas()
    {
        var escenario = new Escenario();
        escenario.Plan<AnalizarPlantillaClientesQuery>("existentes", [Fila("Refrielectric S.L.", true, true)]);
        var (cut, _) = Renderizar(escenario);

        await Pulsar(cut, "Continuar con Plantilla de Clientes");
        await Subir(cut, "existentes.xlsx", "existentes");
        await Pulsar(cut, "Ver plan de importación");

        Texto(cut.Find(".badges-resumen-plan .badge")).Should().Be("0 se crearán");
        cut.FindAll(".aviso-altas-importacion").Should().BeEmpty();
    }

    [Fact]
    public async Task En_la_CAE_completa_el_aviso_y_la_confirmacion_separan_las_altas_que_si_se_haran()
    {
        var escenario = new Escenario();
        escenario.Plan<AnalizarImportacionExcelQuery>("cae", [Fila("Obra Norte")], empresas: [new EmpresaImportadaDto("Montajes Ebro S.A.", false)]);
        var (cut, _) = Renderizar(escenario, url: "importacion");

        await Pulsar(cut, "Continuar con Importación CAE completa (multi-hoja)");
        await Subir(cut, "cae.xlsx", "cae");
        await Pulsar(cut, "Ver plan de importación");

        Texto(cut.Find(".badges-resumen-plan .badge")).Should().Be("3 se crearán");
        Texto(cut.Find(".titulo-aviso-altas")).Should().Be("2 de estas altas no se harán al importar.");
        cut.FindAll(".tabla-plan-importacion-envoltorio tbody tr").Select(f => Texto(f.QuerySelectorAll("td")[2]))
            .Should().Contain("Nombre nuevo en Centros_Plataformas.", "la CAE completa sí lee Centros_Plataformas");

        await Pulsar(cut, "Continuar a confirmar");
        Texto(cut.Find(".titulo-aviso-confirmacion")).Should().Be(
            "Se crearán como máximo 1 de las 3 altas del plan. Esta acción escribe datos reales.");
    }

    // ---------------------------------------------------------------- confirmar y escribir

    [Fact]
    public async Task Importar_ahora_pide_confirmacion_con_el_efecto_real_y_cancelar_no_escribe_nada()
    {
        var escenario = new Escenario();
        escenario.Plan<AnalizarPlantillaClientesQuery>("levante", [Fila("Instalaciones Vidal S.L.")],
            omitidos: [new ItemImportacionDto("Clientes", 14, "Montajes Ebro S.A.", "Nombre duplicado dentro del propio archivo.")]);
        var (cut, mediador) = Renderizar(escenario);
        await LlevarAConfirmarAsync(cut, "clientes-levante.xlsx", "levante");

        Texto(cut.Find(".titulo-aviso-confirmacion")).Should().Be("Ninguna de las 2 altas del plan se hará.");
        Texto(cut.Find(".detalle-aviso-confirmacion")).Should().StartWith(
            "La fila de Cliente empresarial o Centro con un nombre que todavía no existe se omitirá:");
        cut.FindAll("label.opcion-reemplazar-importacion").Should().BeEmpty("solo la Combinada actualiza lo existente");
        Boton(cut, "Importar ahora").HasAttribute("disabled").Should().BeTrue("hasta marcar la casilla de revisión");

        await MarcarRevisado(cut);
        await Pulsar(cut, "Importar ahora");

        Texto(cut.Find(".modal-contenido h2")).Should().Be("¿Importar este archivo?");
        Texto(cut.Find(".modal-cuerpo p")).Should().Be(
            "No se creará ningún elemento. La fila de Cliente empresarial o Centro con un nombre que todavía no existe se omitirá: " +
            "esta importación no recoge el CIF que exige el alta de un Cliente empresarial ni la Empresa que exige la de un Centro. " +
            "La fila que el análisis ya descartó no se toca. " +
            "Lo que se escriba no se deshace desde esta pantalla; el resultado queda en el historial de importaciones.");
        mediador.Enviados.OfType<EjecutarImportacionCommand>().Should().BeEmpty("abrir el diálogo no escribe");

        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".modal-contenido").Should().BeEmpty();
        TituloDeLaSeccion(cut).Should().Be("Confirmar importación");
        mediador.Enviados.OfType<EjecutarImportacionCommand>().Should().BeEmpty("cancelar no escribe");
    }

    [Fact]
    public async Task Confirmar_escribe_una_sola_vez_el_plan_analizado_y_el_reporte_cuadra_con_lo_anunciado()
    {
        var escenario = new Escenario();
        escenario.ClientesExistentes.Add("Refrielectric S.L.");
        var plan = escenario.Plan<AnalizarPlantillaClientesQuery>("levante",
            [Fila("Instalaciones Vidal S.L."), Fila("Refrielectric S.L.", yaExisteCliente: true)]);
        var escritura = Puerta();
        escenario.Retener = p => p is EjecutarImportacionCommand ? escritura.Task : null;
        var (cut, mediador) = Renderizar(escenario);
        await LlevarAConfirmarAsync(cut, "clientes-levante.xlsx", "levante");
        await MarcarRevisado(cut);
        // No se espera a ciegas: con la escritura retenida, un «Importar ahora»
        // que escribiera sin diálogo dejaría este clic colgado para siempre.
        var abrir = Pulsar(cut, "Importar ahora");
        abrir.IsCompleted.Should().BeTrue("«Importar ahora» solo abre el diálogo: no espera a ninguna escritura");
        await abrir;
        Texto(cut.Find(".modal-cuerpo p")).Should().Contain("Las 2 filas de Cliente empresarial o Centro con un nombre que todavía no existe se omitirán");

        var dialogo = cut.FindComponent<DialogoConfirmacion>();
        var primero = BotonDelDialogo(cut, "Sí, importar").ClickAsync(new MouseEventArgs());
        // El segundo llega por detrás del diálogo, sin su guarda: la que se prueba es la de la página.
        var segundo = cut.InvokeAsync(() => dialogo.Instance.OnConfirmar.InvokeAsync());

        cut.WaitForAssertion(() => mediador.Enviados.OfType<EjecutarImportacionCommand>().Should().ContainSingle());
        await segundo;
        mediador.Enviados.OfType<EjecutarImportacionCommand>().Should().ContainSingle("el segundo clic no puede escribir otra vez")
            .Which.Plan.OperacionId.Should().Be(plan.OperacionId, "se escribe el plan que se revisó");

        await PasoDelIndicador(cut, "Elegir plantilla").ClickAsync(new MouseEventArgs());
        TituloDeLaSeccion(cut).Should().Be("Confirmar importación", "con la escritura en vuelo el asistente no se mueve");

        escritura.SetResult();
        await primero;

        cut.WaitForAssertion(() => TituloDeLaSeccion(cut).Should().Be("Reporte"));
        cut.FindAll(".modal-contenido").Should().BeEmpty();
        string Metrica(string etiqueta) => Texto(cut.FindAll(".tarjeta-metrica")
            .Single(t => Texto(t.QuerySelector(".tarjeta-metrica-etiqueta")!) == etiqueta).QuerySelector(".tarjeta-metrica-valor")!);
        Metrica("Creados").Should().Be("0");
        Metrica("Omitidos").Should().Be("2", "las dos filas que el diálogo anunció como omitidas");
        cut.FindAll(".tabla-datos tbody tr").Select(Texto).Should().SatisfyRespectively(
            f => f.Should().Contain("Instalaciones Vidal S.L.").And.Contain("CIF"),
            f => f.Should().Contain("Refrielectric S.L.").And.Contain("Empresa asociada"));

        var alta = cut.FindAll("a").Single(a => Texto(a) == "Dar de alta un Cliente empresarial a mano");
        alta.GetAttribute("href").Should().Be("/clientes?accion=crear");

        var registro = mediador.Enviados.OfType<RegistrarHistorialImportacionCommand>().Should().ContainSingle().Subject;
        registro.Should().BeEquivalentTo(new RegistrarHistorialImportacionCommand("Clientes", "clientes-levante.xlsx", true, 0, 0, 2, null));
        Services.GetRequiredService<ToastService>().Mensajes.Last().Mensaje.Should().Be("Importación completada.");
    }

    [Fact]
    public async Task Un_archivo_nuevo_obliga_a_revisar_y_confirmar_otra_vez()
    {
        var escenario = new Escenario();
        escenario.Plan<AnalizarPlantillaClientesQuery>("A", [Fila("Alfa S.L.")]);
        escenario.Plan<AnalizarPlantillaClientesQuery>("B", [Fila("Beta S.L.")]);
        var (cut, _) = Renderizar(escenario);
        await LlevarAConfirmarAsync(cut, "a.xlsx", "A");
        await MarcarRevisado(cut);
        Boton(cut, "Importar ahora").HasAttribute("disabled").Should().BeFalse();

        await PasoDelIndicador(cut, "Analizar").ClickAsync(new MouseEventArgs());
        await Subir(cut, "b.xlsx", "B");

        PasoDelIndicador(cut, "Revisar plan").HasAttribute("disabled").Should().BeTrue("el plan nuevo no se ha revisado");
        PasoDelIndicador(cut, "Confirmar").HasAttribute("disabled").Should().BeTrue();
        await Pulsar(cut, "Ver plan de importación");
        await Pulsar(cut, "Continuar a confirmar");
        Boton(cut, "Importar ahora").HasAttribute("disabled").Should().BeTrue("la casilla marcada era para el plan del archivo anterior");
    }

    // ---------------------------------------------------------------- carreras

    [Fact]
    public async Task Un_analisis_anterior_que_termina_tarde_no_pinta_su_plan()
    {
        var escenario = new Escenario();
        escenario.Plan<AnalizarPlantillaClientesQuery>("A", [Fila("Alfa S.L.")]);
        escenario.Plan<AnalizarPlantillaClientesQuery>("B", [Fila("Beta S.L.")]);
        var puertaA = Puerta();
        var puertaB = Puerta();
        escenario.Retener = p => p is AnalizarPlantillaClientesQuery q
            ? Encoding.UTF8.GetString(q.ContenidoArchivo) == "A" ? puertaA.Task : puertaB.Task
            : null;
        var (cut, _) = Renderizar(escenario);
        await Pulsar(cut, "Continuar con Plantilla de Clientes");

        var subidaA = Subir(cut, "a.xlsx", "A");
        var subidaB = Subir(cut, "b.xlsx", "B");
        puertaB.SetResult();
        await subidaB;
        cut.WaitForAssertion(() => Boton(cut, "Ver plan de importación").HasAttribute("disabled").Should().BeFalse());

        puertaA.SetResult();
        await subidaA;

        await Pulsar(cut, "Ver plan de importación");
        Texto(cut.Find(".detalle-revision-plan")).Should().Be("b.xlsx · nada escrito todavía");
        cut.FindAll(".tabla-plan-importacion-envoltorio tbody tr td:first-child").Select(Texto).Distinct()
            .Should().Equal(["Beta S.L."], "el plan de a.xlsx llegó después, pero ya no era el vigente");
    }

    [Fact]
    public async Task Un_analisis_anterior_que_falla_tarde_no_pinta_su_error_ni_para_el_progreso_del_vigente()
    {
        var escenario = new Escenario();
        escenario.Plan<AnalizarPlantillaClientesQuery>("B", [Fila("Beta S.L.")]);
        var puertaA = Puerta();
        var puertaB = Puerta();
        escenario.Retener = p => p is AnalizarPlantillaClientesQuery q
            ? Encoding.UTF8.GetString(q.ContenidoArchivo) == "ilegible" ? puertaA.Task : puertaB.Task
            : null;
        var (cut, _) = Renderizar(escenario);
        await Pulsar(cut, "Continuar con Plantilla de Clientes");

        var subidaA = Subir(cut, "roto.xlsx", "ilegible");
        var subidaB = Subir(cut, "b.xlsx", "B");
        puertaA.SetResult();
        await subidaA;

        cut.FindAll(".alerta-formulario").Should().BeEmpty("el error es de un archivo que ya no es el vigente");
        cut.FindAll(".progreso-carga").Should().NotBeEmpty("b.xlsx sigue analizándose");

        puertaB.SetResult();
        await subidaB;
        cut.WaitForAssertion(() => Boton(cut, "Ver plan de importación").HasAttribute("disabled").Should().BeFalse());
        cut.FindAll(".alerta-formulario").Should().BeEmpty();
    }

    [Fact]
    public async Task Cambiar_de_plantilla_descarta_el_plan_y_el_analisis_en_vuelo()
    {
        var escenario = new Escenario();
        escenario.Plan<AnalizarPlantillaClientesQuery>("A", [Fila("Alfa S.L.")]);
        var puerta = Puerta();
        escenario.Retener = p => p is AnalizarPlantillaClientesQuery ? puerta.Task : null;
        var (cut, _) = Renderizar(escenario);
        await Pulsar(cut, "Continuar con Plantilla de Clientes");

        var subida = Subir(cut, "a.xlsx", "A");
        await Pulsar(cut, "← Cambiar plantilla");
        await cut.Find("[data-plantilla='cae']").ClickAsync(new MouseEventArgs());
        puerta.SetResult();
        await subida;

        foreach (var paso in new[] { "Analizar", "Revisar plan", "Confirmar", "Reporte" })
            PasoDelIndicador(cut, paso).HasAttribute("disabled").Should().BeTrue($"«{paso}» dependía del plan de la otra plantilla");
        await Pulsar(cut, "Continuar con Importación CAE completa (multi-hoja)");
        Boton(cut, "Ver plan de importación").HasAttribute("disabled").Should().BeTrue(
            "el análisis de la Plantilla de Clientes terminó después del cambio y no puede confirmarse como CAE completa");
    }

    [Fact]
    public async Task Una_carga_del_historial_que_llega_tarde_no_pisa_la_mas_reciente()
    {
        var escenario = new Escenario();
        escenario.RegistrarEnHistorial("Documentos", "documentos-agosto.xlsx");
        escenario.Plan<AnalizarPlantillaClientesQuery>("levante", [Fila("Instalaciones Vidal S.L.")]);
        var puerta = Puerta();
        var consultas = 0;
        escenario.Interceptar = p =>
        {
            if (p is not ObtenerHistorialImportacionesQuery q || ++consultas != 1) return null;
            var foto = escenario.Foto(q.Limite);
            return Tarde();

            async Task<object?> Tarde()
            {
                await puerta.Task;
                return foto;
            }
        };
        var (cut, mediador) = Renderizar(escenario);
        await LlevarAConfirmarAsync(cut, "clientes-levante.xlsx", "levante");
        await MarcarRevisado(cut);
        await Pulsar(cut, "Importar ahora");
        await BotonDelDialogo(cut, "Sí, importar").ClickAsync(new MouseEventArgs());
        mediador.Enviados.OfType<ObtenerHistorialImportacionesQuery>().Should().HaveCount(2, "la carga inicial, aún retenida, y la de después de importar");

        puerta.SetResult();
        await Pulsar(cut, "Nueva importación");

        cut.WaitForAssertion(() => cut.FindAll(".plantilla-item-historial").Select(Texto)
            .Should().Equal(["Clientes", "Documentos"], "la foto inicial, sin la importación de ahora, llegó la última pero es la vieja"));
    }

    [Fact]
    public async Task Retirar_la_pagina_cancela_el_analisis_en_vuelo_y_no_aplica_su_respuesta()
    {
        var escenario = new Escenario();
        escenario.Plan<AnalizarPlantillaClientesQuery>("A", [Fila("Alfa S.L.")]);
        var puerta = Puerta();
        escenario.Retener = p => p is AnalizarPlantillaClientesQuery ? puerta.Task : null;
        var (cut, mediador) = Renderizar(escenario);
        await Pulsar(cut, "Continuar con Plantilla de Clientes");
        var subida = Subir(cut, "a.xlsx", "A");
        cut.WaitForAssertion(() => mediador.Recibidas.Should().Contain(r => r.Peticion is AnalizarPlantillaClientesQuery));
        var token = mediador.Recibidas.Single(r => r.Peticion is AnalizarPlantillaClientesQuery).Token;
        var instancia = cut.Instance;

        await DisposeComponentsAsync();

        Campo<bool>(instancia, "_desechada").Should().BeTrue("el Dispose de la página se ejecutó");
        token.IsCancellationRequested.Should().BeTrue("el análisis en vuelo se cancela al retirar la página");

        puerta.SetResult();
        await subida;
        Campo<PlanImportacionDto?>(instancia, "_planSimple").Should().BeNull("la respuesta llegó a una página ya retirada");
    }
}

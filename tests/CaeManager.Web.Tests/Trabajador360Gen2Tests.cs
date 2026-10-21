using CaeManager.Web.Components.Layout;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.DocumentacionBase;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentacionBaseTrabajadores;
using CaeManager.Infrastructure.Identity;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Asignaciones.Commands.DarDeBajaAsignaciones;
using CaeManager.Application.Asignaciones.Commands.ReactivarAsignacion;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Gestiones.Commands.CrearGestionesParaTrabajador;
using CaeManager.Application.Gestiones.Commands.CompletarGestion;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Application.Trabajadores.Commands.EditarFichaTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Trabajador 360 (<see cref="TrabajadorDetalle"/>, ruta
/// <c>/trabajadores/{TrabajadorId:guid}</c>) contra su mockup Gen 2
/// («Paginas 360 nuevas TALVEG.maqueta.html», función P.tra). Es una PÁGINA con ruta propia, no el
/// panel de 520 px del Context Workspace: el panel sigue existiendo para el
/// contexto rápido desde Gestiones o Incidencias y no es lo que se prueba
/// aquí.
///
/// <para>
/// Prueban efectos observables: qué se ve, qué consultas salen y con qué
/// parámetros, y con qué token viajan. bUnit no evalúa CSS, así que ninguno
/// afirma un estilo — la banda de severidad se comprueba por la clase que la
/// enciende, no por su color.
/// </para>
///
/// <para>
/// Las esperas se retienen con <see cref="TaskCompletionSource"/> SIN
/// continuaciones asíncronas: al liberar dentro de <c>cut.InvokeAsync</c> la
/// continuación de la página corre en línea en el dispatcher del renderer y,
/// al volver del <c>await</c>, ya ha escrito (o descartado) lo que iba a
/// escribir. Sin eso, una comprobación negativa podría pasar solo por llegar
/// antes que la respuesta.
/// </para>
/// </summary>
public partial class Trabajador360Gen2Tests : BunitContext
{
    /// <summary>BotonCopiar importa ./js/clipboard.js.</summary>
    public Trabajador360Gen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        // CajasFicha mide las tres cajas de Ficha: en modo laxo medirAlturas devolvería null.
        // Una medida vacía las deja en su orden de llegada, que es el que afirman los tests.
        JSInterop.SetupModule("./js/cajas-ficha.js").Setup<double[]>("medirAlturas", _ => true).SetResult([]);
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    /// <summary>
    /// Responde según los parámetros de cada consulta —el Id del trabajador—,
    /// no por tipo: una consulta con el Id equivocado recibe lo que le
    /// corresponde a ese Id, y el test lo ve.
    /// </summary>
    private sealed class MediatorFalso : IMediator
    {
        public Dictionary<Guid, TrabajadorDetalleDto> Detalles { get; } = [];
        public Result ResultadoFicha { get; set; } = Result.Exito();
        public Result ResultadoCorregirDocumento { get; set; } = Result.Exito();
        public Dictionary<Guid, List<CentroDocumentacionTrabajadorDto>> Centros { get; } = [];
        public List<ContactoAgendaDto> Contactos { get; } = [];
        public Dictionary<Guid, List<GestionListaDto>> Gestiones { get; } = [];
        public List<TipoDocumentoListaDto> Tipos { get; } = [];
        public List<TrabajadorSelectorDto> Selector { get; } = [];
        public Dictionary<Guid, List<DocumentoListaDto>> Documentos { get; } = [];
        public Dictionary<Guid, DocumentacionBaseTrabajadorDto> BaseDocumental { get; } = [];

        /// <summary>La fila del Trabajador en el listado (ObtenerTrabajadoresQuery): el estado único de la cabecera.</summary>
        public Dictionary<Guid, TrabajadorListaDto> Filas { get; } = [];

        /// <summary>Si es true, ObtenerDocumentosQuery lanza: el resto de la página tiene que seguir en pie.</summary>
        public bool FallarDocumentos { get; set; }
        public bool FallarDocumentacionBase { get; set; }

        /// <summary>Detecciones de personal de la Empresa (aviso «Podría estar de baja»).</summary>
        public List<CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPorEmpresa.DeteccionTrabajadorDto> Detecciones { get; } = [];
        public CaeManager.Application.Trabajadores.Commands.ResolverDeteccionAusente.ResultadoResolucionAusente ResultadoResolverDeteccion { get; set; } =
            CaeManager.Application.Trabajadores.Commands.ResolverDeteccionAusente.ResultadoResolucionAusente.DadoDeBaja;

        /// <summary>El comando dice cuántas bajas hizo y qué errores hubo: un éxito pelado no distingue «dada de baja» de «no se dio de baja ninguna».</summary>
        public Result<ResultadoBajaLoteDto> ResultadoDarDeBajaAsignacion { get; set; } =
            Result.Exito(new ResultadoBajaLoteDto(1, []));

        public Result ResultadoReactivar { get; set; } = Result.Exito();

        /// <summary>Si tiene valor, el servidor rechaza el envío de la reclamación / la creación de la gestión con ese motivo.</summary>
        public string? FallaEnviarReclamacionCon { get; set; }

        /// <summary>Con <see cref="FallaEnviarReclamacionCon"/>, el envío a partir del cual falla (1 = todos; 2 = el primero sale y el segundo falla).</summary>
        public int FallaEnviarReclamacionDesdeElEnvio { get; set; } = 1;
        private int EnviosReclamacion;
        public string? FallaCrearGestionCon { get; set; }

        /// <summary>Si devuelve una tarea, la respuesta espera a que se complete.</summary>
        public Func<object, Task?>? Retener { get; set; }

        /// <summary>El catálogo de Delegaciones y Oficios de la Empresa; <c>null</c> = la consulta falla.</summary>
        public CaeManager.Application.Empresas.Queries.ObtenerCatalogoTrabajadoresEmpresa.CatalogoTrabajadoresEmpresaDto? CatalogoEmpresa { get; set; }

        public List<object> Enviadas { get; } = [];

        /// <summary>El token con el que llegó cada petición, en el mismo orden que <see cref="Enviadas"/>.</summary>
        public List<(object Peticion, CancellationToken Token)> Tokens { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            Tokens.Add((request, cancellationToken));
            if (Retener?.Invoke(request) is { } retenida)
                await retenida;
            return (TResponse)Responder(request)!;
        }

        private object? Responder(object request) => request switch
        {
            ObtenerTrabajadorPorIdQuery q => Detalles.GetValueOrDefault(q.Id),
            CaeManager.Application.Empresas.Queries.ObtenerCatalogoTrabajadoresEmpresa.ObtenerCatalogoTrabajadoresEmpresaQuery =>
                CatalogoEmpresa ?? throw new InvalidOperationException("Fallo simulado del catálogo de la Empresa."),
            EditarFichaTrabajadorCommand c => GuardarFicha(c),
            CaeManager.Application.Trabajadores.Commands.CorregirDocumentoIdentidadTrabajador.CorregirDocumentoIdentidadTrabajadorCommand =>
                ResultadoCorregirDocumento,
            CaeManager.Application.Trabajadores.Commands.GuardarNotaInternaTrabajador.GuardarNotaInternaTrabajadorCommand c => GuardarNota(c.Id, c.Notas),
            ObtenerDocumentacionPorCentroDeTrabajadorQuery q =>
                (IReadOnlyList<CentroDocumentacionTrabajadorDto>)(Centros.GetValueOrDefault(q.TrabajadorId) ?? []),
            ObtenerGestionesQuery q => Paginar(q),
            ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)Tipos,
            ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)Selector,
            ObtenerTrabajadoresQuery q => q.TrabajadorId is { } filaId && Filas.TryGetValue(filaId, out var fila)
                ? new ResultadoPaginado<TrabajadorListaDto>([fila], 1, q.Pagina, q.TamanoPagina)
                : new ResultadoPaginado<TrabajadorListaDto>([], 0, q.Pagina, q.TamanoPagina),
            ObtenerAgendaContactosQuery => Contactos,
            CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPorEmpresa.ObtenerDeteccionesPorEmpresaQuery =>
                Result.Exito<IReadOnlyList<CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPorEmpresa.DeteccionTrabajadorDto>>(Detecciones),
            CaeManager.Application.Trabajadores.Commands.ResolverDeteccionAusente.ResolverDeteccionAusenteCommand =>
                Result.Exito(ResultadoResolverDeteccion),
            ObtenerDocumentosQuery when FallarDocumentos => throw new InvalidOperationException("Fallo simulado de la consulta de documentos."),
            ObtenerDocumentosQuery q => PaginarDocumentos(q),
            ObtenerDocumentoPorIdQuery => null,
            CompletarGestionCommand c => Completar(c),
            DarDeBajaAsignacionesCommand => ResultadoDarDeBajaAsignacion,
            ReactivarAsignacionCommand => ResultadoReactivar,
            EnviarReclamacionCommand when FallaEnviarReclamacionCon is { } motivo && ++EnviosReclamacion >= FallaEnviarReclamacionDesdeElEnvio => Result.Fallo<EnvioReclamacionResultado>(Error.Crear("reclamacion.rechazada", motivo)),
            EnviarReclamacionCommand c => Result.Exito(new EnvioReclamacionResultado(c.DocumentoIds, ["cliente@ejemplo.com"])),
            CrearGestionesParaTrabajadorCommand when FallaCrearGestionCon is { } motivo => Result.Fallo<ResultadoCrearGestionesDto>(Error.Crear("gestion.rechazada", motivo)),
            CrearGestionesParaTrabajadorCommand => Result.Exito(new ResultadoCrearGestionesDto(1)),
            ObtenerDocumentacionBaseTrabajadoresQuery when FallarDocumentacionBase => throw new InvalidOperationException("Fallo de documentación base."),
            ObtenerDocumentacionBaseTrabajadoresQuery q => q.TrabajadorIds.ToDictionary(id => id,
                id => BaseDocumental.GetValueOrDefault(id) ?? new DocumentacionBaseTrabajadorDto(Enum.GetValues<TipoDocumentoBase>()
                    .Select(tipo => new IndicadorDocumentacionBase(tipo, EstadoIndicadorBase.SinConfirmar, Guid.NewGuid(), null)).ToList())),
            CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro.ObtenerLoteReclamacionPorFiltroQuery => LotesReclamacion,
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
        };

        /// <summary>Lo que devuelve el cajón de reclamación («Gestionar» con un Trabajador de Empresa).</summary>
        public IReadOnlyList<CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro.LoteReclamacionAgrupadoDto> LotesReclamacion { get; set; } = [];

        private Result GuardarFicha(EditarFichaTrabajadorCommand c)
        {
            if (ResultadoFicha.EsFallido) return ResultadoFicha;
            Detalles[c.Id] = Detalles[c.Id] with
            {
                NumeroAfiliacionSeguridadSocial = c.NumeroAfiliacionSeguridadSocial,
                Puesto = c.Puesto,
                FechaNacimiento = c.FechaNacimiento,
                Nacionalidad = c.Nacionalidad,
                TipoVinculo = c.TipoVinculo,
                FechaAltaEmpresa = c.FechaAltaEmpresa,
                FechaBajaEmpresa = c.FechaBajaEmpresa,
                FiguraEnRnt = c.FiguraEnRnt,
                TipoPermisoTrabajo = c.TipoPermisoTrabajo,
                CaducidadPermisoTrabajo = c.CaducidadPermisoTrabajo,
                MotivoPermisoTrabajo = c.MotivoPermisoTrabajo
            };
            return ResultadoFicha;
        }

        /// <summary>Lo que responde el servidor al guardar la nota interna; con éxito, la relectura trae la nota y otra Version.</summary>
        public Result ResultadoNota { get; set; } = Result.Exito();

        private Result GuardarNota(Guid id, string? notas)
        {
            if (ResultadoNota.EsFallido) return ResultadoNota;
            Detalles[id] = Detalles[id] with { Notas = notas, Version = Guid.NewGuid() };
            return ResultadoNota;
        }

        private Result Completar(CompletarGestionCommand comando)
        {
            foreach (var gestiones in Gestiones.Values)
                gestiones.RemoveAll(g => g.Id == comando.Id);
            return Result.Exito();
        }

        private ResultadoPaginado<GestionListaDto> Paginar(ObtenerGestionesQuery q)
        {
            var todas = q.TrabajadorId is { } id ? Gestiones.GetValueOrDefault(id) ?? [] : [];
            return new ResultadoPaginado<GestionListaDto>(todas, todas.Count, q.Pagina, q.TamanoPagina);
        }

        private ResultadoPaginado<DocumentoListaDto> PaginarDocumentos(ObtenerDocumentosQuery q)
        {
            var todos = q.TrabajadorId is { } id ? Documentos.GetValueOrDefault(id) ?? [] : [];
            return new ResultadoPaginado<DocumentoListaDto>(todos.Skip((q.Pagina - 1) * q.TamanoPagina).Take(q.TamanoPagina).ToList(), todos.Count, q.Pagina, q.TamanoPagina);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private static TrabajadorDetalleDto Detalle(
        Guid id, string nombre, string apellidos, string? telefono = null, string? email = null, string? alias = null) => new(
        id, EmpresaId: Guid.NewGuid(), SubcontrataId: null, EmpleadorNombre: "Refrielectric S.L.",
        nombre, apellidos, Dni: "12345678Z", FechaNacimiento: null, email, Observaciones: null,
        alias, telefono, Puesto: "Oficial de 1.ª — montaje", Version: Guid.NewGuid(),
        // Los tests de esta clase miran como el equipo de gestión, que recibe el permiso de trabajo; el caso del
        // Usuario de Cliente lo fija aparte con PermisoTrabajoVisible = false.
        PermisoTrabajoVisible: true);

    private static DocumentoRequeridoDto Documento(string tipo, EstadoDocumento estado, bool existe = true) =>
        new(existe ? Guid.NewGuid() : null, Guid.NewGuid(), tipo, estado,
            estado switch
            {
                EstadoDocumento.Vigente => new DateOnly(2027, 5, 3),
                // Con vencimiento dentro de la ventana de reclamación (VentanaReclamacion): Vencido, Urgente, Próximo.
                EstadoDocumento.Vencido or EstadoDocumento.Urgente or EstadoDocumento.Proximo => DiaDeNegocio.Hoy().AddDays(-10),
                _ => null
            });

    private static CentroDocumentacionTrabajadorDto Centro(
        string nombre, string clienteEmpresarial, EstadoDocumento peorEstado, params DocumentoRequeridoDto[] documentos) =>
        new(Guid.NewGuid(), Guid.NewGuid(), nombre, Guid.NewGuid(), clienteEmpresarial,
            new DateOnly(2026, 7, 2), peorEstado, documentos,
            // Con «Sin caducidad» el listado no lo trae pero el par cuenta: un caso aparte construye el Dto con su fracción.
            CumplimientoDocumental.Evaluar(documentos.Select(d => d.Estado)),
            CumplimientoDocumental.Evaluar(documentos.Select(d => d.Estado)));

    private static GestionListaDto Gestion(Guid trabajadorId, string tipoDocumento, string centro) =>
        new(Guid.NewGuid(), trabajadorId, "Javier Salas", Guid.NewGuid(), centro,
            Guid.NewGuid(), tipoDocumento, EstadoGestion.Pendiente, new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc));

    /// <summary>
    /// La página monta DrawerGestionDocumento, que los inyecta. Ningún camino
    /// de estos tests los toca: si alguno salta, la página cambió de camino.
    /// </summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Trabajador 360 no abre archivos por sí sola en estos caminos.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Trabajador 360 no convierte nada en estos caminos.");
    }

    private MediatorFalso Registrar(MediatorFalso mediador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        // Después de registrar, no antes: SetRendererInfo resuelve servicios y
        // congela el proveedor de bUnit. La página solo encadena la carga de
        // gestiones en la fase interactiva (ver el comentario de CargarAsync),
        // así que sin esto RendererInfo.IsInteractive sería false y ninguna
        // gestión llegaría a pedirse.
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return mediador;
    }

    private static void AbrirCentros(IRenderedComponent<TrabajadorDetalle> cut) =>
        cut.FindAll("[role=tab]").Single(t => t.FirstChild!.TextContent.Trim() == "Centros").Click();

    private IRenderedComponent<TrabajadorDetalle> Renderizar(Guid id) =>
        Render<TrabajadorDetalle>(p => p.Add(x => x.TrabajadorId, id));

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Ficha_muestra_todos_los_campos_y_conserva_su_posicion_y_URL(bool puesto, bool nacimiento)
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.Detalles[id] = mediador.Detalles[id] with
        {
            Puesto = puesto ? "Oficial de montaje" : null,
            FechaNacimiento = nacimiento ? new DateOnly(1988, 3, 14) : null,
            Observaciones = "Observación que no debe pintarse"
        };
        var cut = RenderizarEn(id, "?ctx=Trabajador&estado=Vencido");
        var consultas = mediador.Enviadas.Count;
        await AbrirPestanaAsync(cut, "Ficha");

        cut.FindAll("[role=tab]").Select(t => t.FirstChild!.TextContent.Trim()).Should().Equal("Operación", "Centros", "Documentación", "Plataformas", "Ficha", "Historial");
        var cajas = cut.Find(".cajas-ficha");
        cajas.QuerySelectorAll("[data-caja]").Should().HaveCount(4, "tres cajas de datos y la agenda del empleador (maqueta Trabajador 360)");
        cajas.TextContent.Should().Contain("Datos para altas en plataforma").And.NotContain("Observación que no debe pintarse").And.Contain("Vínculo con la empresa");
        cajas.QuerySelectorAll("dt").Should().HaveCount(11);
        if (puesto) cajas.TextContent.Should().Contain("Oficial de montaje");
        if (nacimiento)
        {
            cajas.TextContent.Should().Contain("14/03/1988");
            cut.FindComponents<BotonCopiar>().Where(c => c.Instance.Valor == "14/03/1988").Should().ContainSingle();
        }
        Navegacion.Uri.Should().Contain("pestana=ficha").And.Contain("ctx=Trabajador").And.Contain("estado=Vencido");
        mediador.Enviadas.Where(e => e is not ObtenerAgendaContactosQuery).Should().HaveCount(consultas,
            "abrir Ficha solo pide la agenda del empleador, que vive en esta pestaña");
    }

    [Theory]
    [InlineData("es", "Copiar", "el DNI", "el teléfono", "el correo electrónico", "la fecha de nacimiento",
        "el número de afiliación a la Seguridad Social", "la fecha de alta en la empresa",
        "la fecha de baja en la empresa", "la caducidad del permiso de trabajo")]
    [InlineData("ca-ES", "Copia", "el DNI", "el telèfon", "el correu electrònic", "la data de naixement",
        "el número d’afiliació a la Seguretat Social", "la data d’alta a l’empresa",
        "la data de baixa a l’empresa", "la caducitat del permís de treball")]
    public async Task Cada_boton_de_copiar_de_la_ficha_dice_que_dato_copia(
        string cultura, string verbo, string dni, string telefono, string correo, string nacimiento,
        string afiliacion, string alta, string baja, string caducidad)
    {
        var anterior = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new(cultura);
            var id = Guid.NewGuid();
            var mediador = ConTrabajador(id);
            mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno", telefono: "600111222", email: "javier@ejemplo.com") with
            {
                FechaNacimiento = new DateOnly(1988, 3, 14),
                NumeroAfiliacionSeguridadSocial = "281234567840",
                FechaAltaEmpresa = new DateOnly(2024, 1, 10),
                FechaBajaEmpresa = new DateOnly(2026, 6, 30),
                TipoPermisoTrabajo = "NIE",
                CaducidadPermisoTrabajo = new DateOnly(2027, 5, 3)
            };
            var cut = RenderizarEn(id, "");

            // Cada nombre va atado a su dato, no solo existe. Los botones con rótulo «Copiar» se localizan por el valor
            // que los acompaña en su fila; los que llevan el valor como rótulo lo conservan en el nombre accesible
            // (WCAG 2.5.3, «Label in Name»), de modo que el propio nombre fija la pareja.
            string DeRotuloCopiar(string valor) => cut.FindAll("button.boton-copiar")
                .Where(b => b.TextContent.Trim() == "Copiar" && b.ParentElement!.TextContent.Contains(valor))
                .Should().ContainSingle().Subject.GetAttribute("aria-label")!;
            // El DNI lleva su valor como rótulo, como las fechas: «DNI 12345678Z», sin dos puntos ni negrita (maqueta 17-10).
            cut.Find(".trabajador360-meta").TextContent.Should().Contain("DNI").And.Contain("12345678Z").And.NotContain("DNI:");
            cut.FindAll("button.boton-copiar").Select(b => b.GetAttribute("aria-label")!).Should().Contain($"{verbo} {dni}: 12345678Z");
            DeRotuloCopiar("600111222").Should().Be($"{verbo} {telefono}");
            DeRotuloCopiar("javier@ejemplo.com").Should().Be($"{verbo} {correo}");
            var enOperacion = Nombres(cut);

            // La pestaña Ficha es la cuarta (Operación, Centros, Documentación, Ficha, Historial) en cualquier cultura.
            await cut.FindAll("[role=tab]")[4].ClickAsync(new MouseEventArgs());
            var enFicha = Nombres(cut);

            enFicha.Should().Contain(new[]
            {
                $"{verbo} {nacimiento}: 14/03/1988",
                $"{verbo} {alta}: 10/01/2024",
                $"{verbo} {baja}: 30/06/2026",
                $"{verbo} {caducidad}: 03/05/2027"
            }, "las cuatro fechas copiables llevan su valor en el nombre accesible");
            enFicha.Should().ContainSingle(n => n.StartsWith($"{verbo} {afiliacion}: ", StringComparison.Ordinal) && n.Contains("28 12345678 40"));
            enOperacion.Should().OnlyHaveUniqueItems("dos botones con el mismo nombre accesible no se distinguen para un lector de pantalla");
            enFicha.Should().OnlyHaveUniqueItems("dos botones con el mismo nombre accesible no se distinguen para un lector de pantalla");
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = anterior;
        }

        static List<string> Nombres(IRenderedComponent<TrabajadorDetalle> cut) =>
            cut.FindAll("button.boton-copiar").Select(b => b.GetAttribute("aria-label")!).ToList();
    }

    [Fact]
    public void Sin_datos_de_Ficha_su_enlace_muestra_las_cajas_para_poder_rellenarlas()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.Detalles[id] = mediador.Detalles[id] with { Puesto = " ", FechaNacimiento = null, Observaciones = "No es una nota interna" };
        var cut = RenderizarEn(id, "?pestana=ficha");
        cut.FindAll("[role=tab]").Select(t => t.FirstChild!.TextContent.Trim()).Should().Contain("Ficha");
        PestanaSeleccionada(cut).Should().Be("Ficha");
        cut.FindAll("[data-caja] dd").Should().HaveCount(11).And.OnlyContain(d => d.TextContent.Trim() == "—");
    }

    [Fact]
    public async Task El_enlace_de_Ficha_se_adopta_y_se_revalida_al_cambiar_de_Trabajador()
    {
        var id = Guid.NewGuid();
        var otro = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.Detalles[otro] = Detalle(otro, "Otro", "Trabajador") with { Puesto = null };
        var cut = RenderizarEn(id, "?pestana=ficha");
        PestanaSeleccionada(cut).Should().Be("Ficha");
        await cut.InvokeAsync(() => cut.Render(p => p.Add(x => x.TrabajadorId, otro)));
        PestanaSeleccionada(cut).Should().Be("Ficha");
        cut.FindAll("[data-caja]").Should().HaveCount(4);
        await cut.InvokeAsync(() => cut.Render(p => p.Add(x => x.TrabajadorId, id)));
        PestanaSeleccionada(cut).Should().Be("Ficha");
    }

    [Fact]
    public async Task La_franja_de_Centros_cuenta_antes_de_filtrar_ordena_y_adopta_el_estado_de_la_URL_sin_releer()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.Centros[id] =
        [
            Centro("Vigente", "Titular", EstadoDocumento.Vigente, Documento("D", EstadoDocumento.Vigente)),
            Centro("Urgente", "Titular", EstadoDocumento.Urgente, Documento("D", EstadoDocumento.Urgente)),
            Centro("Vencido", "Titular", EstadoDocumento.Vencido, Documento("D", EstadoDocumento.Vencido))
        ];
        var cut = RenderizarEn(id, "");
        await AbrirPestanaAsync(cut, "Centros");
        var consultas = mediador.Enviadas.Count;
        cut.FindAll(".trabajador360-centros .fila-relacion-nombre").Select(e => e.TextContent.Trim()).Should().Equal("Vencido", "Urgente", "Vigente");
        cut.Find(".franja-estado [data-estado=Ficha360Vencido] .franja-estado-recuento").TextContent.Should().Be("1");
        await cut.Find(".franja-estado [data-estado=Ficha360Vencido]").ClickAsync(new MouseEventArgs());
        cut.FindAll(".trabajador360-centros .fila-relacion-nombre").Select(e => e.TextContent.Trim()).Should().Equal("Vencido");
        cut.Find(".franja-estado [data-estado=Ficha360PorVencer] .franja-estado-recuento").TextContent.Should().Be("1");
        cut.Find(".trabajador360-centros .lista-relaciones-encabezado").TextContent.Should().Contain("Mostrando 1 de 3 centros · del peor estado al mejor");
        Navegacion.Uri.Should().Contain("pestana=centros").And.Contain("estado=Vencido");
        Navegacion.NavigateTo($"trabajadores/{id}?pestana=centros&estado=PorVencer");
        cut.Render();
        cut.FindAll(".trabajador360-centros .fila-relacion-nombre").Select(e => e.TextContent.Trim()).Should().Equal("Urgente");
        mediador.Enviadas.Should().HaveCount(consultas);
    }

    [Fact]
    public async Task El_filtro_de_Centros_sin_resultados_explica_el_estado_vacio()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.Centros[id] = [Centro("Centro vigente", "Empresa del Centro", EstadoDocumento.Vigente,
            Documento("Documento vigente", EstadoDocumento.Vigente))];
        var cut = RenderizarEn(id, "");
        await AbrirPestanaAsync(cut, "Centros");
        await cut.Find(".franja-estado [data-estado=Ficha360Pendiente]").ClickAsync(new MouseEventArgs());
        cut.FindAll(".trabajador360-centros .fila-relacion-nombre").Should().BeEmpty();
        cut.Find(".trabajador360-centros [role=status]").TextContent.Should().Be("Ningún Centro coincide con los filtros.");
        cut.Find(".trabajador360-centros .lista-relaciones-encabezado").TextContent.Should().Contain("Mostrando 0 de 1 centro");
    }

    [Theory]
    [InlineData(EstadoIndicadorBase.Vencido, "Gestionar")]
    [InlineData(EstadoIndicadorBase.ProximoAVencer, "Gestionar")]
    [InlineData(EstadoIndicadorBase.SinConfirmar, "Gestionar")]
    public async Task La_accion_de_Documentacion_base_inicia_el_editor_con_el_ID_del_indicador(EstadoIndicadorBase estado, string accion)
    {
        var id = Guid.NewGuid();
        var documentoId = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.BaseDocumental[id] = new([new(TipoDocumentoBase.AptitudMedica, estado, documentoId, null)]);
        if (estado == EstadoIndicadorBase.ProximoAVencer)
            mediador.Documentos[id] = [DocumentoDelTrabajador("Reconocimiento médico", EstadoDocumento.Urgente, new(2025, 1, 1), new(2026, 1, 1)) with { Id = documentoId }];
        var cut = Renderizar(id);
        var boton = cut.Find(".panel-doc-base-fila button");
        boton.TextContent.Trim().Should().Be(accion);
        await boton.ClickAsync(new MouseEventArgs());
        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().ContainSingle().Which.Id.Should().Be(documentoId);
    }

    [Theory]
    [InlineData(EstadoIndicadorBase.Vigente, true, 0)]
    [InlineData(EstadoIndicadorBase.Vencido, true, 1)]
    [InlineData(EstadoIndicadorBase.Vencido, false, 0)]
    public void Documentacion_base_ofrece_Gestionar_solo_si_pide_trabajo_y_hay_Documento(EstadoIndicadorBase estado, bool existe, int botones)
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.BaseDocumental[id] = new([new(TipoDocumentoBase.AptitudMedica, estado, existe ? Guid.NewGuid() : null, null)]);
        var cut = Renderizar(id);
        cut.FindAll(".panel-doc-base-fila").Should().ContainSingle();
        cut.FindAll(".panel-doc-base-fila button").Should().HaveCount(botones, "el vigente no lleva botón (maqueta Trabajador 360)");
    }

    [Theory]
    [InlineData(EstadoDocumento.Vencido, "Gestionar")]
    [InlineData(EstadoDocumento.SinConfirmar, "Gestionar")]
    public async Task Documentacion_muestra_resumen_fechas_plataforma_y_accion_del_documento_correcto(EstadoDocumento estado, string accion)
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.Centros[id] = [];
        var documento = DocumentoDelTrabajador("Reconocimiento médico", estado, new(2025, 3, 1), new(2026, 3, 1)) with
        {
            Acreditaciones = [new(Guid.NewGuid(), "Nalanda", EstadoAcreditacion.Aceptada)]
        };
        mediador.Documentos[id] = [documento];
        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Documentación");
        var fila = cut.Find(".trabajador360-documentos .fila-relacion");
        fila.QuerySelector(".fila-relacion-detalle")!.TextContent.Should().Contain("Ningún Centro lo exige hoy");
        fila.QuerySelector(".fila-relacion-cifra")!.TextContent.Should().Contain("01/03/2026");
        fila.QuerySelector(".fila-relacion-cifra > span")!.ClassList.Should().Contain("trabajador360-solo-lectores", "«Caducidad: » solo para lectores: no cabe en 104 px");
        fila.QuerySelector(".fila-relacion-acciones")!.TextContent.Trim().Should().Be(accion);
        await fila.QuerySelector(".fila-relacion-acciones button")!.ClickAsync(new MouseEventArgs());
        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().ContainSingle().Which.Id.Should().Be(documento.Id);
    }

    [Fact]
    public async Task El_resumen_de_Documentacion_distingue_los_documentos_cargados_del_total_registrado()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.Centros[id] = [];
        mediador.Documentos[id] = Enumerable.Range(1, 201).Select(i =>
            DocumentoDelTrabajador($"Documento {i}", EstadoDocumento.Vigente, new(2025, 1, 1), new(2027, 1, 1))).ToList();
        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Documentación");
        cut.FindAll(".trabajador360-documentos .fila-relacion").Should().HaveCount(200);
        cut.Find(".trabajador360-documentos-recorte").TextContent.Should().Contain("Mostrando 200 de 201 documentos");
    }

    [Fact]
    public async Task Consulta_conserva_Documentacion_base_y_fechas_sin_las_nuevas_acciones_de_escritura()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.Documentos[id] = [DocumentoDelTrabajador("Documento", EstadoDocumento.Vencido, new(2025, 1, 1), new(2026, 1, 1))];
        var cut = Renderizar(id);
        cut.FindAll(".panel-doc-base-fila").Should().HaveCount(4);
        cut.FindAll(".panel-doc-base-fila button").Should().BeEmpty();
        await AbrirPestanaAsync(cut, "Documentación");
        cut.FindAll(".trabajador360-documentos .fila-relacion-acciones button").Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().BeEmpty();
    }

    /// <summary>El sangrado del .razor mete saltos de línea dentro del texto; la frase es lo que se lee, no su maquetación.</summary>
    private static string SinEspaciosDeMas(string texto) =>
        string.Join(' ', texto.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static IElement Boton(IRenderedComponent<TrabajadorDetalle> cut, string texto) =>
        cut.FindAll("button").Where(b => b.TextContent.Trim() == texto)
            .Should().ContainSingle($"tiene que haber exactamente un botón «{texto}»").Subject;

    /// <summary>Escena por defecto: tres centros, uno con incidencias, otro completo.</summary>
    private static (CentroDocumentacionTrabajadorDto Norte, CentroDocumentacionTrabajadorDto Berriz) Escena() => (
        Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Vencido,
            Documento("Formación PRL — 20 h", EstadoDocumento.Vencido),
            Documento("Formación específica de centro", EstadoDocumento.Faltante, existe: false),
            Documento("Certificado de aptitud", EstadoDocumento.Vigente)),
        Centro("Nave Berriz", "Talleres Berriz Coop.", EstadoDocumento.Vigente,
            Documento("Formación PRL — 20 h", EstadoDocumento.Vigente),
            Documento("Reconocimiento médico", EstadoDocumento.Vigente)));

    [Fact]
    public void La_cabecera_dice_el_cumplimiento_agregado_quien_es_y_el_desglose_del_centro_mas_urgente()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];

        var cut = Renderizar(id);

        var cabecera = cut.Find(".cabecera-identidad");
        cabecera.QuerySelector("h1")!.TextContent.Trim().Should().StartWith("Javier Salas Moreno");
        cabecera.QuerySelector(".cabecera-identidad-kicker").Should().BeNull("la maqueta 17-10 no rotula «Trabajador» sobre el nombre");
        cabecera.QuerySelectorAll(".trabajador360-datos > div").Should().HaveCount(2, "DNI · empleador arriba y las pastillas de estado en su propia fila");
        cabecera.QuerySelector(".trabajador360-meta")!.TextContent.Should().Contain("12345678Z").And.Contain("Refrielectric S.L.");
        cabecera.QuerySelector(".trabajador360-indicadores")!.TextContent.Should().Contain("2 incidencias — Centro Norte");
        cabecera.TextContent.Should().Contain("12345678Z").And.Contain("Refrielectric S.L.");
        // 3 de 5 documentos exigidos entre todos los centros están al día. El anillo va a la
        // izquierda de la identidad, como en las otras páginas 360, y no entre las acciones.
        cabecera.QuerySelector(".cabecera-identidad-anillo .anillo-cumplimiento-texto")!.TextContent.Trim().Should().StartWith("60");
        cabecera.QuerySelector(".cabecera-identidad-acciones .anillo-cumplimiento-texto").Should().BeNull();
        // El desglose literal del centro peor parado, no solo un número.
        cabecera.QuerySelector(".cabecera-identidad-datos")!.TextContent.Should().Contain("2 incidencias — Centro Norte")
            .And.Contain("Formación PRL — 20 h — Vencido")
            .And.Contain("Formación específica de centro — Pendiente");
    }

    [Theory]
    [InlineData(false, "/empresas/")]
    [InlineData(true, "/subcontratas/")]
    public void La_cabecera_es_la_tarjeta_de_identidad_y_el_empleador_lleva_su_icono_360_a_su_pagina(bool esSubcontrata, string ruta)
    {
        var id = Guid.NewGuid();
        var empleadorId = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var detalle = Detalle(id, "Javier", "Salas Moreno");
        mediador.Detalles[id] = esSubcontrata
            ? detalle with { EmpresaId = null, SubcontrataId = empleadorId }
            : detalle with { EmpresaId = empleadorId };
        mediador.Centros[id] = [];

        var cut = Renderizar(id);

        cut.FindAll(".cabecera-pagina").Should().BeEmpty("la cabecera suelta de pantalla se sustituye por la tarjeta");
        var cabecera = cut.Find("[data-pieza=\"cabecera-identidad\"]");
        cabecera.ClassList.Should().Contain("velo-cumplimiento", "el degradado del color del anillo llega por la clase compartida");
        var datos = cabecera.QuerySelector(".cabecera-identidad-datos")!;
        // El nombre abre la consulta rápida; el icono 360, la página.
        datos.QuerySelector("button.enlace-nombre-fila")!.TextContent.Trim().Should().Be("Refrielectric S.L.");
        var icono = datos.QuerySelector("a.boton-360-pagina")!;
        icono.GetAttribute("href").Should().Be(ruta + empleadorId);
        icono.GetAttribute("aria-label").Should().Contain("Refrielectric S.L.");
        cabecera.QuerySelector(".cabecera-identidad-acciones .menu-acciones-disparador")!
            .GetAttribute("aria-label").Should().Be("Más acciones");
    }

    [Fact]
    public void Sin_empleador_con_pagina_la_cabecera_no_pinta_el_icono_360()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno") with { EmpresaId = null, SubcontrataId = null };
        mediador.Centros[id] = [];

        var cut = Renderizar(id);

        cut.Find(".cabecera-identidad-datos").QuerySelectorAll("a.boton-360-pagina").Should().BeEmpty();
    }

    [Fact]
    public void Son_seis_pestanas_y_solo_Operacion_lleva_el_recuento_de_documentos_con_incidencia()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];

        var cut = Renderizar(id);

        var pestanas = cut.FindAll("[role=tab]");
        pestanas.Select(t => t.QuerySelector(".pestanas-contador") is null
            ? t.TextContent.Trim()
            : t.FirstChild!.TextContent.Trim()).Should().Equal(["Operación", "Centros", "Documentación", "Plataformas", "Ficha", "Historial"],
            "«Contactos» pasó al lateral y «Documentación» es la del trabajador a día de hoy (decisiones del propietario 2026-09-24); «Vehículos» nunca existió en esta página");

        // Maqueta «Trabajador 360 TALVEG» (17-10): Operación (en alerta) y Centros (en neutro) llevan recuento; el resto, no.
        var contadores = cut.FindAll(".pestanas-contador");
        contadores.Should().HaveCount(3, "Operación (en alerta), Centros y Documentación (neutros) cuentan algo");
        SinEspaciosDeMas(contadores[0].TextContent).Should().Be("2 documentos con incidencia",
            "el número sin unidad no dice qué cuenta: la glosa existe para el nombre accesible del botón");
        contadores[0].ClassList.Should().Contain("pestanas-contador-alerta");
        SinEspaciosDeMas(contadores[1].TextContent).Should().Be("2 centros asignados");
        contadores[1].ClassList.Should().NotContain("pestanas-contador-alerta", "los Centros asignados no son una incidencia");
        cut.Find(".pestanas-lista").ClassList.Should().Contain("pestanas-lista-pagina360",
            "las pestañas de la columna principal llevan las medidas de las páginas 360 del export de Design");
    }

    /// <summary>
    /// Maqueta 17-10: la línea secundaria de cada Centro dice «{Cliente} · Asignado desde el {fecha} · {resumen}» y, en esa misma
    /// línea, «Dar de baja de este centro» (antes iba en una columna de acciones que lo cortaba). Quien solo consulta lee la línea
    /// entera sin el enlace; el icono 360 sigue siendo la única acción de la columna.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void El_Centro_dice_desde_cuando_esta_asignado_y_la_baja_va_en_su_misma_linea_solo_con_escritura(bool consulta)
    {
        if (consulta) this.ConRolDeEscritura(Roles.Consulta);
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];

        var cut = Renderizar(id);
        AbrirCentros(cut);

        var fila = cut.FindAll(".trabajador360-centros > div > ul > .fila-relacion")[0];
        var detalle = fila.QuerySelector(".fila-relacion-detalle")!;
        SinEspaciosDeMas(detalle.TextContent).Should().Contain("Refrielectric S.A. · Asignado desde el 02/07/2026 · 1 de 3 exigidos correctos");
        fila.QuerySelector(".fila-relacion-cifra")!.TextContent.Trim().Should().BeEmpty("la fecha de alta ya va en la línea secundaria");
        detalle.QuerySelectorAll(".trabajador360-centro-baja").Should().HaveCount(consulta ? 0 : 1);
        fila.QuerySelectorAll(".fila-relacion-acciones button").Should().BeEmpty("la baja ya no vive en la columna de acciones");
        fila.QuerySelectorAll(".fila-relacion-acciones .boton-360-pagina").Should().ContainSingle();
    }

    /// <summary>
    /// «En tolerancia» (aprobado 2026-10-03): Trabajador 360 es una vista con contexto de Centro, así que rotula «Vencido · en
    /// tolerancia hasta dd/MM» al vencido que en ese Centro aún vale para acceder. Sigue siendo una incidencia (ya venció) y no
    /// cuenta como al día en el porcentaje (eso es el incremento 2 del porcentaje).
    /// </summary>
    [Fact]
    public void Un_vencido_en_tolerancia_se_rotula_con_su_fecha_sigue_siendo_incidencia_y_no_sube_el_porcentaje()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        var hasta = DiaDeNegocio.Hoy().AddDays(7);
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.EnTolerancia,
                new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Formación PRL — 20 h", EstadoDocumento.EnTolerancia,
                    DiaDeNegocio.Hoy().AddDays(-3), EnToleranciaHasta: hasta),
                Documento("Reconocimiento médico", EstadoDocumento.Vigente))
        ];

        var cut = Renderizar(id);

        var cabecera = cut.Find(".cabecera-identidad");
        cabecera.TextContent.Should().Contain("1 incidencia — Centro Norte")
            .And.Contain($"Formación PRL — 20 h — Vencido · en tolerancia hasta {hasta:dd/MM}");
        cabecera.QuerySelector(".cabecera-identidad-anillo .anillo-cumplimiento-texto")!.TextContent.Trim().Should().StartWith("50",
            "el vencido en tolerancia aún no cuenta como al día: 1 de 2");
    }

    /// <summary>
    /// D-22 (recorrido de 2026-10-01): «Sin confirmar» no es incidencia —cuenta como al día, con
    /// aviso—, igual que en el panel Documentación base (#1015). Faltante, Vencido, Urgente y Próximo
    /// siguen contando.
    /// </summary>
    [Fact]
    public void Sin_confirmar_cuenta_como_al_dia_con_aviso_y_no_como_incidencia_roja()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.SinConfirmar,
                Documento("Reconocimiento médico", EstadoDocumento.SinConfirmar),
                Documento("Formación PRL — 20 h", EstadoDocumento.Vigente),
                Documento("Formación específica de centro", EstadoDocumento.Vencido))
        ];
        mediador.Filas[id] = FilaDelListado(id, EstadoDocumento.Vencido, null,
            IncidenciaDe("Formación específica de centro", EstadoDocumento.Vencido),
            IncidenciaDe("Reconocimiento médico", EstadoDocumento.SinConfirmar));

        var cut = Renderizar(id);

        var cabecera = cut.Find(".cabecera-identidad");
        // El porcentaje sigue la definición única de CumplimientoDocumental: «Sin confirmar» no cuenta como al día en un
        // porcentaje (1 de 3), aunque en las incidencias sí; la única incidencia es el vencido.
        cabecera.QuerySelector(".cabecera-identidad-anillo .anillo-cumplimiento-texto")!.TextContent.Trim().Should().StartWith("33");
        cabecera.TextContent.Should().Contain("1 incidencia — Centro Norte")
            .And.Contain("Formación específica de centro — Vencido")
            .And.NotContain("Reconocimiento médico — Sin confirmar");
        cabecera.TextContent.Should().Contain("Vigencia sin confirmar: 1", "al día no es «sin aviso»");
        SinEspaciosDeMas(cut.Find(".pestanas-contador").TextContent).Should().Be("1 documento con incidencia");
    }

    [Fact]
    public void Un_centro_con_solo_documentos_sin_confirmar_no_tiene_incidencias_y_lo_avisa()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.SinConfirmar,
                Documento("Reconocimiento médico", EstadoDocumento.SinConfirmar))
        ];

        var cut = Renderizar(id);
        AbrirCentros(cut);

        cut.FindAll(".pestanas-contador-alerta").Should().BeEmpty("sin incidencias no hay píldora de alerta");
        cut.Find(".cabecera-identidad").TextContent.Should().NotContain("incidencia —",
            "el badge rojo de incidencias del centro no aparece");
        var tarjeta = cut.Find(".trabajador360-centros > div > ul > .fila-relacion");
        tarjeta.GetAttribute("data-tono").Should().NotBe("peligro");
        tarjeta.TextContent.Should().Contain("Sin incidencias · vigencia sin confirmar");
        cut.Find(".anillo-cumplimiento-texto").TextContent.Trim().Should().StartWith("0",
            "«Sin confirmar» no es al día en un porcentaje (decisión del propietario, 2026-10-03): solo lo es en incidencias y paneles");
    }

    /// <summary>
    /// Decisión del propietario, 2026-10-03: Próximo y Urgente siguen siendo válidos hoy y cuentan como al día en el
    /// porcentaje, aunque sigan siendo incidencias. El % es la suma de las fracciones de CumplimientoDocumental de cada Centro.
    /// </summary>
    [Fact]
    public void Proximo_y_urgente_cuentan_como_al_dia_en_el_porcentaje_aunque_sigan_siendo_incidencia()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Urgente,
                Documento("Reconocimiento médico", EstadoDocumento.Proximo),
                Documento("Formación PRL — 20 h", EstadoDocumento.Urgente),
                Documento("Formación específica de centro", EstadoDocumento.Vencido))
        ];

        var cut = Renderizar(id);

        cut.Find(".cabecera-identidad .cabecera-identidad-anillo .anillo-cumplimiento-texto").TextContent.Trim().Should().StartWith("67",
            "dos de tres exigidos están al día (Próximo y Urgente); solo el vencido no");
        SinEspaciosDeMas(cut.Find(".pestanas-contador").TextContent).Should().Be("3 documentos con incidencia",
            "Próximo y Urgente al día en el porcentaje no deja de ser incidencia: son dos preguntas distintas");
    }

    /// <summary>
    /// «No caduca» confirmado es al día Y requerido. El listado del Centro no lo trae (no es un requisito pendiente), así
    /// que el porcentaje sale de la fracción que calcula la consulta, no de contar filas.
    /// </summary>
    [Fact]
    public void Un_documento_que_no_caduca_cuenta_como_al_dia_y_como_requerido_aunque_el_listado_no_lo_traiga()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        // Dos exigidos: uno «No caduca» (omitido del listado) y uno vencido. La fracción de la consulta es 1 de 2.
        mediador.Centros[id] =
        [
            new CentroDocumentacionTrabajadorDto(
                Guid.NewGuid(), Guid.NewGuid(), "Centro Norte", Guid.NewGuid(), "Refrielectric S.A.", new DateOnly(2026, 7, 2),
                EstadoDocumento.Vencido, [Documento("Formación específica de centro", EstadoDocumento.Vencido)],
                new FraccionCumplimiento(1, 2), new FraccionCumplimiento(1, 2))
        ];

        var cut = Renderizar(id);
        AbrirCentros(cut);

        cut.Find(".cabecera-identidad .cabecera-identidad-anillo .anillo-cumplimiento-texto").TextContent.Trim().Should().StartWith("50");
        cut.Find(".fila-relacion-detalle").TextContent.Should().Contain("1 de 2 exigidos correctos");
    }

    /// <summary>
    /// D-06/D-22: el botón primario no puede quedar deshabilitado y sin salida en un trabajador
    /// recién creado; abre el drawer de Nuevo documento con el trabajador elegido y el tipo por elegir allí.
    /// </summary>
    [Fact]
    public async Task Gestionar_en_un_indicador_de_Documentacion_base_que_falta_abre_Nuevo_documento_con_el_trabajador_elegido()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.BaseDocumental[id] = new([new(TipoDocumentoBase.AptitudMedica, EstadoIndicadorBase.Falta, null, null)]);
        mediador.Selector.Add(new TrabajadorSelectorDto(id, "Salas Moreno, Javier", null, null));
        var cut = Renderizar(id);

        var boton = cut.Find(".panel-doc-base-fila button");
        boton.TextContent.Trim().Should().Be("Gestionar");
        await boton.ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("Nuevo documento");
        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().BeEmpty("no hay documento que abrir");
    }

    [Fact]
    public async Task Subir_documento_en_un_trabajador_sin_documentos_esta_habilitado_y_abre_Nuevo_documento_con_el_trabajador_elegido()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [];
        mediador.Selector.Add(new TrabajadorSelectorDto(id, "Salas Moreno, Javier", null, null));

        var cut = Renderizar(id);

        var boton = cut.Find(".cabecera-identidad .cabecera-identidad-acciones > button");
        boton.TextContent.Trim().Should().Be("Subir documento");
        boton.HasAttribute("disabled").Should().BeFalse("sin documento al que apuntar abre el alta, no queda muerto");

        await boton.ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("Nuevo documento");
        mediador.Enviadas.OfType<ObtenerTrabajadoresParaSelectorQuery>().Should().ContainSingle();
    }

    /// <summary>
    /// «Subir documento para {Trabajador}» del palette: <c>?accion=subir</c> abre la misma subida que el botón de la
    /// cabecera, con el Trabajador fijado, y se quita de la URL para no reabrirse al recargar.
    /// </summary>
    [Fact]
    public void El_enlace_accion_subir_abre_la_subida_con_el_trabajador_fijado()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [];
        mediador.Selector.Add(new TrabajadorSelectorDto(id, "Salas Moreno, Javier", null, null));

        var cut = RenderizarEn(id, "?pestana=documentacion&accion=subir");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Nuevo documento"));
        Navegacion.Uri.Should().NotContain("accion=", "la acción se consume una vez");
        Navegacion.Uri.Should().Contain("pestana=documentacion");
    }

    /// <summary>Sin escritura el enlace se queda en la ficha: la misma comprobación que oculta el botón «Subir documento».</summary>
    [Fact]
    public void Sin_escritura_el_enlace_accion_subir_no_abre_nada()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [];
        mediador.Selector.Add(new TrabajadorSelectorDto(id, "Salas Moreno, Javier", null, null));

        var cut = RenderizarEn(id, "?pestana=documentacion&accion=subir");

        cut.WaitForAssertion(() => Navegacion.Uri.Should().NotContain("accion="));
        cut.Markup.Should().NotContain("Nuevo documento");
        mediador.Enviadas.OfType<ObtenerTrabajadoresParaSelectorQuery>().Should().BeEmpty(
            "el drawer de alta no llega a abrirse");
    }

    [Fact]
    public async Task Las_pestanas_separan_Operacion_y_Centros_y_la_base_va_primero()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.Gestiones[id] = [Gestion(id, "Reconocimiento médico", "Centro Norte")];
        ComponentFactories.AddStub<CaeManager.Web.Components.Workspace.PestanaHistorial>();
        var cut = Renderizar(id);

        cut.FindAll("[role=tab]").Select(t => t.FirstChild!.TextContent.Trim())
            .Should().Equal("Operación", "Centros", "Documentación", "Plataformas", "Ficha", "Historial");
        PestanaSeleccionada(cut).Should().Be("Operación");
        cut.Find("[role=tabpanel]").FirstElementChild!.ClassList.Should().Contain("panel-doc-base");
        cut.FindAll(".panel-doc-base-fila").Select(f => f.QuerySelector(".panel-doc-base-nombre > span:first-child")!.TextContent.Trim())
            .Should().Equal("Aptitud médica", "Formación Art. 19", "Información Art. 18", "Entrega de EPI");
        cut.Find(".panel-doc-base-aviso").TextContent.Should().NotBeNullOrWhiteSpace();
        cut.Find(".panel-doc-base-nota").TextContent.Should().NotBeNullOrWhiteSpace();
        cut.FindAll(".trabajador360-centros > div > ul > .fila-relacion").Should().BeEmpty();
        cut.Find("[aria-label='Gestiones pendientes']").TextContent.Should().Contain("Completar");

        foreach (var nombre in new[] { "Centros", "Documentación", "Historial" })
        {
            await AbrirPestanaAsync(cut, nombre);
            cut.FindAll(".panel-doc-base").Should().BeEmpty();
            cut.FindAll(".separador-ambito").Should().NotContain(e => e.TextContent.Trim() == "Asignaciones");
            if (nombre == "Centros")
            {
                cut.FindAll(".trabajador360-centros > div > ul > .fila-relacion").Should().NotBeEmpty();
                cut.FindAll("[role=tab]").Single(t => t.FirstChild!.TextContent.Trim() == "Centros")
                    .QuerySelector(".pestanas-contador-alerta").Should().BeNull("el recuento de Centros es neutro: la incidencia se cuenta solo en Operación");
                cut.FindAll("[aria-label='Gestiones pendientes']").Should().BeEmpty();
            }
            else
                cut.FindAll(".trabajador360-centros > div > ul > .fila-relacion").Should().BeEmpty();
        }
    }

    [Fact]
    public void Un_fallo_de_documentacion_base_no_oculta_el_resto_de_Operacion()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.FallarDocumentacionBase = true;
        mediador.Gestiones[id] = [Gestion(id, "Reconocimiento médico", "Centro Norte")];
        var cut = Renderizar(id);

        cut.FindAll(".panel-doc-base").Should().BeEmpty();
        cut.Find("[aria-label='Gestiones pendientes']").TextContent.Should().Contain("Completar");
        AbrirCentros(cut);
        cut.FindAll(".trabajador360-centros > div > ul > .fila-relacion").Should().NotBeEmpty();
    }

    // --- Pestaña activa en la URL (criterio 3 de la homologación) -------------------------------

    private IRenderedComponent<TrabajadorDetalle> RenderizarEn(Guid id, string consulta)
    {
        // El Historial es una pieza compartida con su propia consulta: aquí solo importa qué pestaña queda abierta.
        ComponentFactories.AddStub<CaeManager.Web.Components.Workspace.PestanaHistorial>();
        Navegacion.NavigateTo($"trabajadores/{id}{consulta}");
        return Renderizar(id);
    }

    private static string PestanaSeleccionada(IRenderedComponent<TrabajadorDetalle> cut)
    {
        var activa = cut.FindAll("[role=tab][aria-selected=true]").Should().ContainSingle().Subject;
        return activa.QuerySelector(".pestanas-contador") is null ? activa.TextContent.Trim() : activa.FirstChild!.TextContent.Trim();
    }

    [Theory]
    [InlineData("", "Operación")]
    [InlineData("?pestana=centros", "Centros")]
    [InlineData("?pestana=documentacion", "Documentación")]
    [InlineData("?pestana=historial", "Historial")]
    [InlineData("?pestana=operacion", "Operación")]
    [InlineData("?pestana=no-existe", "Operación")]
    [InlineData("?pestana=Documentacion", "Operación")]
    public void La_pestana_de_la_url_abre_esa_pestana_y_un_valor_desconocido_abre_Operacion(string consulta, string esperada)
    {
        var id = Guid.NewGuid();
        ConTrabajador(id);

        var cut = RenderizarEn(id, consulta);

        PestanaSeleccionada(cut).Should().Be(esperada);
    }

    [Fact]
    public async Task Cambiar_de_pestana_la_deja_en_la_url_sin_recargar_la_ficha_ni_pisar_ctx()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        var cut = RenderizarEn(id, "?ctx=Trabajador");
        AbrirCentros(cut);
        await cut.Find(".trabajador360-centros .fila-relacion-desplegar").ClickAsync(new MouseEventArgs());
        cut.Find(".trabajador360-centros .fila-relacion-desplegar").GetAttribute("aria-expanded").Should().Be("true");

        await cut.FindAll("[role=tab]").Single(t => t.TextContent.Contains("Documentación")).ClickAsync(new MouseEventArgs());

        Navegacion.Uri.Should().Contain("pestana=documentacion").And.Contain("ctx=Trabajador", "la pestaña no pisa el panel abierto");
        PestanaSeleccionada(cut).Should().Be("Documentación");

        await cut.FindAll("[role=tab]").Single(t => t.TextContent.Contains("Operación")).ClickAsync(new MouseEventArgs());

        Navegacion.Uri.Should().NotContain("pestana=", "Operación es la pestaña de entrada: no ensucia la URL");
        Navegacion.Uri.Should().Contain("ctx=Trabajador");
        mediador.Enviadas.OfType<ObtenerTrabajadorPorIdQuery>().Should().ContainSingle(
            "la pestaña viaja en la URL y vuelve como parámetro: eso no es otra pregunta por el trabajador");
        AbrirCentros(cut);
        cut.Find(".trabajador360-centros .fila-relacion-desplegar").GetAttribute("aria-expanded").Should().Be("true",
            "una recarga cerraría el acordeón; cambiar de pestaña no recarga");
    }

    [Fact]
    public void La_pestana_que_cambia_en_la_url_desde_fuera_se_adopta_sin_recargar()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        var cut = RenderizarEn(id, "?pestana=historial");

        Navegacion.NavigateTo($"trabajadores/{id}?pestana=documentacion");

        cut.WaitForAssertion(() => PestanaSeleccionada(cut).Should().Be("Documentación"));
        mediador.Enviadas.OfType<ObtenerTrabajadorPorIdQuery>().Should().ContainSingle();
    }

    [Fact]
    public void Pasar_a_otro_trabajador_sin_pestana_en_la_url_vuelve_a_Operacion_y_carga_al_nuevo()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var mediador = ConTrabajador(a);
        mediador.Detalles[b] = Detalle(b, "Eider", "Lasa Arrieta");
        mediador.Centros[b] = [];
        var cut = RenderizarEn(a, "?pestana=historial");

        Navegacion.NavigateTo($"trabajadores/{b}");
        cut.Render(p => p.Add(x => x.TrabajadorId, b));

        cut.WaitForAssertion(() => cut.Find(".cabecera-identidad h1").TextContent.Trim().Should().StartWith("Eider Lasa Arrieta"));
        PestanaSeleccionada(cut).Should().Be("Operación");
        mediador.Enviadas.OfType<ObtenerTrabajadorPorIdQuery>().Select(q => q.Id).Should().Equal([a, b]);
    }

    [Fact]
    public async Task La_pestana_Documentacion_vacia_ofrece_Subir_documento()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [];

        var cut = Renderizar(id);
        await cut.FindAll("[role=tab]").Single(t => t.TextContent.Contains("Documentación")).ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("todavía no tiene documentos");
        cut.Find(".texto-vacio-seccion + button, .texto-vacio-seccion ~ button").TextContent.Trim().Should().Be("Subir documento");
    }

    [Fact]
    public void Sin_ninguna_incidencia_la_pestana_de_Operacion_no_lleva_pildora()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (_, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [berriz];

        var cut = Renderizar(id);

        cut.FindAll(".pestanas-contador-alerta").Should().BeEmpty("un cero no se pinta como si fuera un dato que atender");
        cut.FindAll("[role=tab]").Should().HaveCount(6);
    }

    [Fact]
    public void Cada_Centro_es_una_FilaRelacion_que_dice_su_Cliente_empresarial_su_fraccion_y_lleva_al_Centro_360()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];

        var cut = Renderizar(id);
        AbrirCentros(cut);

        cut.Markup.Should().Contain("La exigencia documental es por centro",
            "es lo primero que explica por qué el mismo trabajador sale completo en una fila y bloqueado en la de al lado");

        var tarjetas = cut.FindAll(".trabajador360-centros > div > ul > .fila-relacion");
        tarjetas.Should().HaveCount(2);

        tarjetas[0].QuerySelector(".fila-relacion-nombre")!.TextContent.Trim().Should().Be("Centro Norte");
        tarjetas[0].QuerySelector(".fila-relacion-detalle")!.TextContent.Should()
            .Contain("Refrielectric S.A.").And.Contain("1 de 3 exigidos correctos");
        tarjetas[0].QuerySelector(".badge")!.TextContent.Trim().Should().Be("2 incidencias");
        tarjetas[0].GetAttribute("data-tono").Should().Be("peligro");
        tarjetas[0].QuerySelector(".boton-360-pagina")!.GetAttribute("href")
            .Should().Be($"/centros/{norte.CentroId}");
        tarjetas[0].QuerySelector(".boton-360-pagina")!.GetAttribute("aria-label")
            .Should().Be("Abrir la ficha 360 de Centro Norte");

        tarjetas[1].QuerySelector(".badge")!.TextContent.Trim().Should().Be("Completo");
        tarjetas[1].GetAttribute("data-tono").Should().NotBe("peligro");
        tarjetas[1].QuerySelector(".fila-relacion-detalle")!.TextContent.Should()
            .Contain("2 de 2 exigidos correctos");
    }

    [Fact]
    public async Task Desplegar_un_centro_ensena_sus_documentos_exigidos_con_el_estado_y_la_accion_que_toca()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];

        var cut = Renderizar(id);
        AbrirCentros(cut);
        cut.FindAll(".fila-relacion-desplegado").Should().BeEmpty("los centros nacen plegados");

        var disparador = cut.FindAll(".trabajador360-centros .fila-relacion-desplegar")[0];
        disparador.GetAttribute("aria-expanded").Should().Be("false");
        await disparador.ClickAsync(new MouseEventArgs());

        cut.FindAll(".trabajador360-centros .fila-relacion-desplegar")[0].GetAttribute("aria-expanded").Should().Be("true");
        var detalle = cut.Find(".fila-relacion-desplegado");
        detalle.TextContent.Should().Contain("Formación PRL — 20 h").And.Contain("Certificado de aptitud");
        // Subir o renovar: la fila ofrece siempre la misma acción, «Gestionar», y abre el mismo flujo; lo vigente también la
        // lleva, en tono secundario (regla de Chris, 2026-10-18).
        var botones = detalle.QuerySelectorAll(".fila-relacion-acciones button");
        botones.Select(b => b.TextContent.Trim()).Should().Equal(["Gestionar", "Gestionar", "Gestionar"]);
        botones.Select(b => b.ClassList.Contains("gestionar-discreto")).Should().Equal([false, false, true]);
        detalle.TextContent.Should().NotContain("Nave Berriz", "solo se despliega el centro sobre el que se pulsó");
    }

    /// <summary>La fila del Trabajador tal como la entrega ObtenerTrabajadoresQuery: estado ya combinado, faltantes y desglose.</summary>
    private static TrabajadorListaDto FilaDelListado(
        Guid id, EstadoDocumento estado, IReadOnlyList<string>? faltan = null, params IncidenciaDocumentalDto[] incidencias) =>
        new(id, "Javier", "Salas Moreno", "12345678Z", "Refrielectric S.L.", estado)
        {
            DocumentosFaltantes = faltan ?? [],
            Incidencias = incidencias
        };

    private static IncidenciaDocumentalDto IncidenciaDe(string tipo, EstadoDocumento estado) =>
        new(Guid.NewGuid(), Guid.NewGuid(), tipo, estado, estado == EstadoDocumento.SinConfirmar ? null : new DateOnly(2026, 10, 1));

    [Fact]
    public void La_cabecera_nombra_los_tipos_exigidos_sin_documento_de_la_fila_del_listado()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Faltante,
                Documento("Aptitud médica", EstadoDocumento.Faltante, existe: false),
                Documento("Contrato", EstadoDocumento.Vigente)),
            Centro("Nave Berriz", "Talleres Berriz Coop.", EstadoDocumento.Faltante,
                Documento("Formación Art. 19", EstadoDocumento.Faltante, existe: false),
                Documento("Aptitud médica", EstadoDocumento.Faltante, existe: false))
        ];
        // La consulta del listado ya los da sin repetir (un Tipo exigido en dos Centros cuenta una vez).
        mediador.Filas[id] = FilaDelListado(id, EstadoDocumento.Faltante, ["Aptitud médica", "Formación Art. 19"]);

        var cut = Renderizar(id);

        var cabecera = cut.Find(".cabecera-identidad").TextContent;
        cabecera.Should().Contain("Falta: Aptitud médica, Formación Art. 19");
        cabecera.Should().NotContain("Vigente");
        cut.Find(".trabajador360-indicadores .badge").TextContent.Trim().Should().Be("Pendiente");
    }

    /// <summary>
    /// Revisión 1:1 (2026-10-21), Rubén Montero Medina: los Centros solo veían «Falta Información Art. 18» y la cabecera decía
    /// «Pendiente», mientras el listado lo daba como Vencido por su Formación Art. 19. La cabecera pinta el estado de la fila del
    /// listado (Vencido &gt; Pendiente &gt; Por vencer &gt; Sin incidencias), no el peor de los Centros, y cuenta los «sin confirmar»
    /// de ese mismo desglose.
    /// </summary>
    [Fact]
    public void La_cabecera_dice_el_mismo_estado_que_el_listado_aunque_ningun_Centro_vea_el_vencido()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Rubén", "Montero Medina");
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Faltante,
                Documento("Información Art. 18", EstadoDocumento.Faltante, existe: false),
                Documento("Reconocimiento médico", EstadoDocumento.SinConfirmar),
                Documento("Contrato", EstadoDocumento.Vigente)),
            Centro("Nave Berriz", "Talleres Berriz Coop.", EstadoDocumento.SinConfirmar,
                Documento("Reconocimiento médico", EstadoDocumento.SinConfirmar))
        ];
        mediador.Filas[id] = FilaDelListado(id, EstadoDocumento.Vencido, ["Información Art. 18"],
            IncidenciaDe("Formación Art. 19", EstadoDocumento.Vencido),
            IncidenciaDe("Reconocimiento médico", EstadoDocumento.SinConfirmar));

        var cut = Renderizar(id);

        var indicadores = cut.Find(".trabajador360-indicadores");
        indicadores.QuerySelector(".badge")!.TextContent.Trim().Should().Be("Vencido", "es el estado del listado: el vencido manda sobre lo que falta");
        indicadores.TextContent.Should().Contain("Falta: Información Art. 18");
        // Un documento sin confirmar exigido en dos Centros es UN documento sin confirmar.
        indicadores.TextContent.Should().Contain("Vigencia sin confirmar: 1").And.NotContain("Vigencia sin confirmar: 2");
    }

    [Fact]
    public async Task Cada_documento_exigido_es_una_FilaRelacion_ordenada_y_tenida_segun_su_estado()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Faltante,
            Documento("Vigente", EstadoDocumento.Vigente),
            Documento("Sin confirmar", EstadoDocumento.SinConfirmar),
            Documento("Pendiente", EstadoDocumento.Faltante, existe: false))];
        var cut = Renderizar(id);
        AbrirCentros(cut);
        await cut.Find(".trabajador360-centros .fila-relacion-desplegar").ClickAsync(new MouseEventArgs());
        var lista = cut.Find(".fila-relacion-desplegado ul");
        lista.GetAttribute("aria-label").Should().Contain("Centro Norte");
        var filas = lista.QuerySelectorAll("li.fila-relacion");
        filas.Should().HaveCount(3);
        filas.Select(f => f.QuerySelector(".fila-relacion-nombre")!.TextContent).Should()
            .Equal("Pendiente", "Sin confirmar", "Vigente");
        filas.Select(f => f.GetAttribute("data-tono")).Should().Equal("peligro", null, null);
        filas.Select(f => f.QuerySelector(".fila-relacion-estado")!.TextContent.Trim()).Should()
            .Equal("Pendiente", "Sin confirmar", "Vigente");
        cut.FindComponents<FilaRelacion>().Should().HaveCount(4, "un Centro y tres documentos exigidos");
    }

    [Theory]
    [InlineData(EstadoDocumento.Vencido, "peligro")]
    [InlineData(EstadoDocumento.Faltante, "peligro")]
    [InlineData(EstadoDocumento.EnTolerancia, "advertencia")]
    [InlineData(EstadoDocumento.Urgente, "advertencia")]
    [InlineData(EstadoDocumento.Proximo, null)]
    [InlineData(EstadoDocumento.SinConfirmar, null)]
    [InlineData(EstadoDocumento.Vigente, null)]
    [InlineData(EstadoDocumento.SinCaducidad, null)]
    public async Task El_documento_y_su_Centro_usan_el_tinte_canonico_del_peor_estado(EstadoDocumento peorEstado, string? tono)
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [Centro("Centro Norte", "Refrielectric S.A.", peorEstado,
            Documento("Menos grave", EstadoDocumento.SinCaducidad),
            Documento("Zeta", peorEstado, existe: peorEstado != EstadoDocumento.Faltante),
            Documento("Alfa", peorEstado, existe: peorEstado != EstadoDocumento.Faltante))];
        var cut = Renderizar(id);
        AbrirCentros(cut);

        var centro = cut.Find(".trabajador360-centros > div > ul > .fila-relacion");
        centro.GetAttribute("data-tono").Should().Be(tono);
        await centro.QuerySelector(".fila-relacion-desplegar")!.ClickAsync(new MouseEventArgs());
        var documentos = cut.Find(".fila-relacion-desplegado ul").QuerySelectorAll("li.fila-relacion");
        var nombres = documentos.Select(f => f.QuerySelector(".fila-relacion-nombre")!.TextContent).ToList();
        nombres.IndexOf("Alfa").Should().BeLessThan(nombres.IndexOf("Zeta"), "a igual estado se desempata por nombre");
        foreach (var documento in documentos.Where(f => f.QuerySelector(".fila-relacion-nombre")!.TextContent is "Alfa" or "Zeta"))
            documento.GetAttribute("data-tono").Should().Be(tono);
        documentos.Single(f => f.QuerySelector(".fila-relacion-nombre")!.TextContent == "Menos grave")
            .GetAttribute("data-tono").Should().BeNull();
    }

    [Fact]
    public async Task La_baja_del_segundo_Centro_pide_confirmacion_pliega_y_Deshacer_reabre_su_asignacion()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        var segundo = mediador.Centros[id][1];
        var cut = Renderizar(id);
        AbrirCentros(cut);
        await cut.FindAll(".trabajador360-centros .fila-relacion-desplegar")[1].ClickAsync(new MouseEventArgs());
        cut.FindAll(".trabajador360-centros .fila-relacion-desplegar")[1].GetAttribute("aria-expanded").Should().Be("true");
        await cut.FindAll(BotonBajaAsignacion)[1].ClickAsync(new MouseEventArgs());
        mediador.Enviadas.OfType<DarDeBajaAsignacionesCommand>().Should().BeEmpty();
        cut.Find("[role=dialog]").TextContent.Should().Contain(segundo.CentroNombre);
        await BotonDelDialogo(cut, "Dar de baja").ClickAsync(new MouseEventArgs());
        mediador.Enviadas.OfType<DarDeBajaAsignacionesCommand>().Should().ContainSingle()
            .Which.Ids.Should().Equal(segundo.AsignacionId);
        cut.FindAll(".trabajador360-centros .fila-relacion-desplegar")[1].GetAttribute("aria-expanded").Should().Be("false");
        var aviso = Avisos.Mensajes.Single();
        aviso.TextoAccion.Should().Be("Deshacer");
        aviso.OnAccion.Should().NotBeNull();
        await cut.InvokeAsync(aviso.OnAccion!);
        mediador.Enviadas.OfType<ReactivarAsignacionCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(segundo.AsignacionId);
    }
    private static DocumentoListaDto DocumentoDelTrabajador(
        string tipo, EstadoDocumento estado, DateOnly emision, DateOnly? vencimiento, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), AmbitoAplicacion.Trabajador, "Javier Salas Moreno", tipo,
            emision, vencimiento, estado, ArchivoUrl: null, Acreditaciones: []);

    private static TipoDocumentoListaDto Tipo(Guid id, string nombre, RequisitoDocumental requerido) =>
        new(id, nombre, VigenciaMeses: 12, AplicaVencimientoAutomatico: true, Orden: 0,
            AmbitoAplicacion.Trabajador, requerido, Naturaleza: default, Descripcion: null,
            CriteriosValidacion: null, SeSolicitaA: null, Observaciones: null,
            LecturaIaActiva: false, DeteccionTrabajadoresActiva: false, VerificacionIaActiva: false,
            PerfilDocumentoOficial.Ninguno, Aliases: []);

    private static async Task AbrirPestanaAsync(IRenderedComponent<TrabajadorDetalle> cut, string nombre)
    {
        var pestana = cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim().StartsWith(nombre, StringComparison.Ordinal));
        await pestana.ClickAsync(new MouseEventArgs());
    }

    private static IReadOnlyList<string[]> Filas(IRenderedComponent<TrabajadorDetalle> cut, string tabla) =>
        cut.Find($"ul[aria-label='{tabla}']").QuerySelectorAll("li.fila-relacion")
            .Select(f =>
            {
                // Nombre · dónde se exige (línea de detalle) · estado · vigencia (maqueta Trabajador 360: ya no hay columna de emisión).
                return new[]
                {
                    SinEspaciosDeMas(f.QuerySelector(".fila-relacion-nombre")!.TextContent),
                    SinEspaciosDeMas(f.QuerySelector(".fila-relacion-detalle")!.TextContent),
                    SinEspaciosDeMas(f.QuerySelector(".fila-relacion-estado")!.TextContent),
                    SinEspaciosDeMas(f.QuerySelector(".trabajador360-vigencia")!.TextContent),
                };
            }).ToList();

    [Fact]
    public void Operacion_abre_con_la_franja_de_lo_que_esta_por_vencer_y_solo_con_eso()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];
        mediador.Documentos[id] =
        [
            DocumentoDelTrabajador("Reconocimiento médico", EstadoDocumento.Urgente, new(2025, 10, 1), new(2026, 10, 1)),
            DocumentoDelTrabajador("Entrega de EPI", EstadoDocumento.Proximo, new(2025, 10, 20), new(2026, 10, 20)),
            DocumentoDelTrabajador("Formación PRL — 20 h", EstadoDocumento.Vigente, new(2025, 3, 1), new(2027, 3, 1)),
            DocumentoDelTrabajador("Formación PRL — 60 h", EstadoDocumento.Vencido, new(2024, 3, 1), new(2026, 3, 1)),
            DocumentoDelTrabajador("DNI", EstadoDocumento.SinCaducidad, new(2020, 1, 1), null),
        ];

        var cut = Renderizar(id);

        var franja = cut.Find(".trabajador360-por-vencer ul");
        franja.GetAttribute("aria-label").Should().Be("Por vencer");
        franja.QuerySelectorAll("li[data-pieza=fila]").Should().HaveCount(2);
        franja.QuerySelectorAll("li.fila-relacion").Select(f => f.GetAttribute("data-tono"))
            .Should().Equal(new string?[] { "advertencia", null }, "solo el tramo Urgente tiñe");
        franja.QuerySelectorAll("li.fila-relacion")
            .Select(f => SinEspaciosDeMas(f.TextContent))
            .Should().Equal(
                ["Reconocimiento médico Vence: 01/10/2026 Por vencer Gestionar", "Entrega de EPI Vence: 20/10/2026 Por vencer Gestionar"],
                "la franja usa el umbral ámbar del sistema (Q3): Próximo y Urgente (los dos se rotulan «Por vencer»), lo más cercano primero; lo vencido ya es incidencia de Centro y lo vigente no apremia");
    }

    /// <summary>
    /// Regla 8 de LEEME-AUTORIDAD (2026-10-18): en Operación (Documentación base y franja «Por vencer») la fila que pide
    /// trabajo ofrece solo «Gestionar» a quien escribe; Consulta y Cliente no ven acción de escritura; lo vigente no la lleva (maqueta Trabajador 360, 2026-10-21).
    /// </summary>
    [Theory]
    [InlineData(Roles.Administrador, true)]
    [InlineData(Roles.DireccionCae, true)]
    [InlineData(Roles.CoordinadorCae, true)]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    [InlineData(Roles.Cliente, false)]
    public void Operacion_ofrece_solo_Gestionar_a_quien_escribe_y_solo_en_lo_que_pide_trabajo(string rol, bool escribe)
    {
        this.ConRolDeEscritura(rol);
        var id = Guid.NewGuid();
        var vencidoId = Guid.NewGuid();
        var urgenteId = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.BaseDocumental[id] = new([
            new(TipoDocumentoBase.AptitudMedica, EstadoIndicadorBase.Vencido, vencidoId, null),
            new(TipoDocumentoBase.FormacionArt19, EstadoIndicadorBase.SinConfirmar, Guid.NewGuid(), null),
            new(TipoDocumentoBase.InformacionArt18, EstadoIndicadorBase.Vigente, Guid.NewGuid(), null)]);
        mediador.Documentos[id] =
        [
            DocumentoDelTrabajador("Reconocimiento médico", EstadoDocumento.Urgente, new(2025, 10, 1), new(2026, 10, 1)) with { Id = urgenteId },
            DocumentoDelTrabajador("Entrega de EPI", EstadoDocumento.Proximo, new(2025, 10, 20), new(2026, 10, 20)),
            DocumentoDelTrabajador("Formación PRL — 20 h", EstadoDocumento.Vigente, new(2025, 3, 1), new(2027, 3, 1)),
        ];

        var cut = Renderizar(id);

        var filasBase = cut.FindAll(".panel-doc-base-fila");
        filasBase.Should().HaveCount(3);
        var botonesBase = filasBase.Select(f => f.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ToList()).ToList();
        var botonesFranja = cut.FindAll(".trabajador360-por-vencer li.fila-relacion")
            .Select(f => f.QuerySelectorAll(".fila-relacion-acciones button").Select(b => b.TextContent.Trim()).ToList()).ToList();
        if (escribe)
        {
            botonesBase.Should().Equal(new List<string>[] { ["Gestionar"], ["Gestionar"], [] },
                (a, b) => a.SequenceEqual(b), "Vencido y Sin confirmar piden trabajo; lo vigente no lleva botón (maqueta Trabajador 360)");
            botonesFranja.Should().HaveCount(2).And.OnlyContain(b => b.SequenceEqual(new[] { "Gestionar" }),
                "Por vencer, urgente o no, lleva «Gestionar»");
        }
        else
        {
            botonesBase.Should().OnlyContain(b => b.Count == 0);
            botonesFranja.Should().HaveCount(2).And.OnlyContain(b => b.Count == 0);
        }
    }

    [Fact]
    public void Operacion_nombra_el_documento_con_la_vigencia_sin_confirmar_que_la_cabecera_cuenta()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Documentos[id] = [DocumentoDelTrabajador("Documento de identidad", EstadoDocumento.SinConfirmar, new(2026, 5, 31), null)];

        var cut = Renderizar(id);

        var franja = cut.Find("[aria-label='Vigencia sin confirmar']");
        franja.TextContent.Should().Contain("Documento de identidad").And.Contain("Gestionar");
    }

    private MediatorFalso ConDeteccionAusente(Guid id, Guid? trabajadorDeLaDeteccion = null)
    {
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [];
        mediador.Detecciones.Add(new(Guid.NewGuid(), CaeManager.Domain.Trabajadores.TipoDeteccion.Ausente, "Javier", "Salas Moreno",
            "12345678Z", trabajadorDeLaDeteccion ?? id, DateTime.UtcNow, 2));
        return mediador;
    }

    [Fact]
    public void Una_deteccion_Ausente_del_trabajador_pinta_el_aviso_de_posible_baja_a_quien_escribe()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var id = Guid.NewGuid();
        ConDeteccionAusente(id);

        var cut = Renderizar(id);

        cut.Find(".trabajador360-baja").TextContent.Should().Contain("Podría estar de baja").And.Contain("Confirmar baja").And.Contain("Ignorar");
    }

    [Theory]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Cliente)]
    public void El_aviso_de_posible_baja_no_se_pinta_sin_permiso_de_escritura(string rol)
    {
        this.ConRolDeEscritura(rol);
        var id = Guid.NewGuid();
        ConDeteccionAusente(id);

        Renderizar(id).FindAll(".trabajador360-baja").Should().BeEmpty();
    }

    [Fact]
    public void La_deteccion_de_otro_trabajador_no_pinta_el_aviso()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var id = Guid.NewGuid();
        ConDeteccionAusente(id, trabajadorDeLaDeteccion: Guid.NewGuid());

        Renderizar(id).FindAll(".trabajador360-baja").Should().BeEmpty();
    }

    [Fact]
    public async Task Ignorar_el_aviso_resuelve_la_deteccion_sin_dar_de_baja_y_lo_oculta()
    {
        this.ConRolDeEscritura(Roles.GestorCae);
        var id = Guid.NewGuid();
        var mediador = ConDeteccionAusente(id);
        var cut = Renderizar(id);

        await cut.Find(".trabajador360-baja-acciones button:last-of-type").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CaeManager.Application.Trabajadores.Commands.ResolverDeteccionAusente.ResolverDeteccionAusenteCommand>()
            .Should().ContainSingle().Which.Desactivar.Should().BeFalse();
        cut.FindAll(".trabajador360-baja").Should().BeEmpty();
    }

    [Fact]
    public void Sin_nada_por_vencer_no_se_pinta_la_franja()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, _) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte];
        mediador.Documentos[id] = [DocumentoDelTrabajador("DNI", EstadoDocumento.SinCaducidad, new(2020, 1, 1), null)];

        var cut = Renderizar(id);

        cut.FindAll(".trabajador360-por-vencer ul").Should().BeEmpty("una lista vacía con su rótulo diría que hay algo que mirar");
    }

    [Fact]
    public async Task La_documentacion_del_trabajador_dice_de_cada_documento_su_fecha_y_cuando_caduca()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Documentos[id] =
        [
            DocumentoDelTrabajador("Formación PRL — 20 h", EstadoDocumento.Vigente, new(2025, 3, 1), new(2027, 3, 1)),
            DocumentoDelTrabajador("DNI", EstadoDocumento.SinCaducidad, new(2020, 1, 1), null),
            DocumentoDelTrabajador("Reconocimiento médico", EstadoDocumento.Vencido, new(2024, 9, 1), new(2025, 9, 1)),
        ];

        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Documentación");

        Filas(cut, "Documentos del trabajador").Should().BeEquivalentTo(
            new[]
            {
                new[] { "Reconocimiento médico", "Ningún Centro lo exige hoy", "Vencido", "01/09/2025" },
                new[] { "Formación PRL — 20 h", "Ningún Centro lo exige hoy", "Vigente", "01/03/2027" },
                new[] { "DNI", "Ningún Centro lo exige hoy", "Sin caducidad", "Sin caducidad" },
            },
            o => o.WithStrictOrdering(),
            "lo más grave arriba; lo que no caduca lo dice (Q4) en vez de dejar la celda en blanco");

        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(
                new { TrabajadorId = (Guid?)id, Ambito = (AmbitoAplicacion?)AmbitoAplicacion.Trabajador, OrdenarPor = "Estado", Descendente = false },
                "la misma consulta, alcance y RLS que la lista de Documentos, acotada a este trabajador; con lo más grave primero, " +
                "si el tope de la página corta la lista, lo que se pierde es lo vigente y no lo que alimenta «Por vencer»");
    }

    /// <summary>
    /// Mismo criterio en el tercer nivel de la pestaña Operación (Centro desplegado): la celda de vigencia de un
    /// documento exigido indica la fecha que falta; «Sin confirmar» conserva el aviso y «Pendiente» no lo lleva.
    /// </summary>
    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public async Task La_vigencia_de_un_exigido_en_el_centro_desplegado_indica_la_fecha_que_falta_sin_guiones(string rol, bool gestionar)
    {
        this.ConRolDeEscritura(rol);
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.SinConfirmar,
                Documento("Reconocimiento médico", EstadoDocumento.SinConfirmar),
                Documento("Autorización de acceso", EstadoDocumento.Faltante, existe: false))
        ];

        var cut = Renderizar(id);
        AbrirCentros(cut);
        await cut.FindAll(".trabajador360-centros .fila-relacion-desplegar")[0].ClickAsync(new MouseEventArgs());

        var documentos = cut.FindAll(".fila-relacion-desplegado .fila-relacion-compacta");
        documentos.Should().HaveCount(2);
        foreach (var documento in documentos)
        {
            documento.Children.Should().HaveCount(4);
            documento.QuerySelectorAll(".fila-relacion-acciones button").Should().HaveCount(gestionar ? 1 : 0);
        }
        var filas = cut.Find(".fila-relacion-desplegado").QuerySelectorAll(".fila-relacion")
            .ToDictionary(f => SinEspaciosDeMas(f.QuerySelector(".fila-relacion-nombre")!.TextContent),
                f => f.QuerySelector(".celda-documento-vigencia")!);

        SinEspaciosDeMas(filas["Reconocimiento médico"].TextContent).Should().Be("Falta la fecha de vencimiento");
        filas["Reconocimiento médico"].ClassList.Should().Contain("celda-documento-vigencia-sin-confirmar");
        SinEspaciosDeMas(filas["Autorización de acceso"].TextContent).Should().Be("Falta la fecha de vencimiento");
        filas["Autorización de acceso"].ClassList.Should().NotContain("celda-documento-vigencia-sin-confirmar");
    }

    /// <summary>
    /// «Sin confirmar» no tiene fecha de vencimiento y esa celda no puede quedar en «—»: «—» oculta un estado
    /// conocido (decisión del propietario, 2026-10-03). Dice «Sin confirmar» con el mismo aviso ámbar que Centro 360;
    /// un estado sin fecha que no es ni «Sin caducidad» ni «Sin confirmar» (un hueco) sigue en «—», sin aviso.
    /// </summary>
    [Fact]
    public async Task La_celda_Vence_de_un_documento_sin_confirmar_dice_Sin_confirmar_con_aviso_y_un_hueco_sigue_en_raya()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Documentos[id] =
        [
            DocumentoDelTrabajador("Reconocimiento médico", EstadoDocumento.SinConfirmar, new(2025, 5, 1), null),
            DocumentoDelTrabajador("DNI", EstadoDocumento.SinCaducidad, new(2020, 1, 1), null),
            DocumentoDelTrabajador("Formación de centro", EstadoDocumento.Faltante, new(2025, 6, 1), null),
        ];

        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Documentación");

        CeldaVence(cut, "Documentos del trabajador", "Reconocimiento médico").TextContent.Trim().Should().Be("Sin confirmar");
        CeldaVence(cut, "Documentos del trabajador", "Reconocimiento médico").ClassList
            .Should().Contain("celda-documento-vigencia-sin-confirmar", "lleva el mismo aviso ámbar que Centro 360");
        CeldaVence(cut, "Documentos del trabajador", "DNI").TextContent.Trim().Should().Be("Sin caducidad");
        CeldaVence(cut, "Documentos del trabajador", "DNI").ClassList.Should().NotContain("celda-documento-vigencia-sin-confirmar");
        CeldaVence(cut, "Documentos del trabajador", "Formación de centro").TextContent.Trim()
            .Should().Be("—", "un estado sin fecha que no es «Sin caducidad» ni «Sin confirmar» no tiene vigencia que rotular");
        CeldaVence(cut, "Documentos del trabajador", "Formación de centro").ClassList
            .Should().NotContain("celda-documento-vigencia-sin-confirmar");
    }

    [Fact]
    public async Task La_celda_Renovar_antes_de_un_exigido_sin_confirmar_dice_Sin_confirmar_con_aviso_y_un_hueco_sigue_en_raya()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");

        var tipoGeneral = Guid.NewGuid();
        mediador.Tipos.Add(Tipo(tipoGeneral, "Formación PRL — 20 h", RequisitoDocumental.Si));
        var tipoSinConfirmar = Guid.NewGuid();
        var tipoHueco = Guid.NewGuid();
        mediador.Tipos.Add(Tipo(tipoSinConfirmar, "Reconocimiento médico de centro", RequisitoDocumental.No));
        mediador.Tipos.Add(Tipo(tipoHueco, "Autorización de acceso", RequisitoDocumental.No));

        var documentoSinConfirmar = Guid.NewGuid();
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.SinConfirmar,
                new DocumentoRequeridoDto(documentoSinConfirmar, tipoSinConfirmar, "Reconocimiento médico de centro", EstadoDocumento.SinConfirmar, null),
                new DocumentoRequeridoDto(null, tipoHueco, "Autorización de acceso", EstadoDocumento.Faltante, null))
        ];
        mediador.Documentos[id] =
        [
            DocumentoDelTrabajador("Reconocimiento médico de centro", EstadoDocumento.SinConfirmar, new(2025, 5, 1), null, documentoSinConfirmar),
        ];

        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Documentación");

        var tabla = "Exigidos en todos sus Centros";
        CeldaVence(cut, tabla, "Reconocimiento médico de centro").TextContent.Trim().Should().Be("Sin confirmar");
        CeldaVence(cut, tabla, "Reconocimiento médico de centro").ClassList.Should().Contain("celda-documento-vigencia-sin-confirmar");
        CeldaVence(cut, tabla, "Autorización de acceso").TextContent.Trim().Should().Be("—");
        CeldaVence(cut, tabla, "Autorización de acceso").ClassList.Should().NotContain("celda-documento-vigencia-sin-confirmar");
    }

    /// <summary>Última celda (la fecha de vencimiento o de renovación) de la fila de un documento, en la tabla indicada.</summary>
    private static AngleSharp.Dom.IElement CeldaVence(IRenderedComponent<TrabajadorDetalle> cut, string tabla, string documento) =>
        cut.Find($"ul[aria-label='{tabla}']").QuerySelectorAll("li.fila-relacion")
            .Single(f => SinEspaciosDeMas(f.QuerySelector(".fila-relacion-nombre")!.TextContent) == documento)
            .QuerySelector(".trabajador360-vigencia")!;

    [Fact]
    public async Task Los_exigidos_solo_en_algunos_Centros_dicen_en_que_Centro_de_que_Cliente_empresarial_y_cuando_renovar()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");

        var tipoGeneral = Guid.NewGuid();
        var tipoDeCentro = Guid.NewGuid();
        var documentoDeCentro = Guid.NewGuid();
        mediador.Tipos.Add(Tipo(tipoGeneral, "Formación PRL — 20 h", RequisitoDocumental.Si));
        mediador.Tipos.Add(Tipo(tipoDeCentro, "Formación específica de centro", RequisitoDocumental.No));

        var norte = Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Proximo,
            new DocumentoRequeridoDto(Guid.NewGuid(), tipoGeneral, "Formación PRL — 20 h", EstadoDocumento.Vigente, new(2027, 3, 1)),
            new DocumentoRequeridoDto(documentoDeCentro, tipoDeCentro, "Formación específica de centro", EstadoDocumento.Proximo, new(2026, 10, 15)));
        mediador.Centros[id] = [norte];
        mediador.Documentos[id] =
        [
            DocumentoDelTrabajador("Formación específica de centro", EstadoDocumento.Proximo, new(2025, 10, 15), new(2026, 10, 15), documentoDeCentro),
        ];

        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Documentación");

        // Con un único Centro, todo lo que ese Centro exige se exige «en todos sus Centros» (maqueta Trabajador 360).
        Filas(cut, "Exigidos en todos sus Centros").Should().BeEquivalentTo(
            new[]
            {
                new[] { "Formación específica de centro", "En su único Centro", "Por vencer", "15/10/2026" },
                new[] { "Formación PRL — 20 h", "En su único Centro", "Vigente", "01/03/2027" },
            },
            o => o.WithStrictOrdering(),
            "del peor estado al mejor; la vigencia es la caducidad del documento (Q1)");
        cut.FindAll("ul[aria-label='Exigidos solo en algunos Centros']").Should().BeEmpty("un único Centro no tiene «algunos»");
    }

    [Fact]
    public async Task Los_exigidos_solo_en_algunos_Centros_dicen_en_cuales()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        var (norte, berriz) = Escena();
        var tipoComun = Guid.NewGuid();
        var tipoDeNorte = Guid.NewGuid();
        var comunNorte = new DocumentoRequeridoDto(Guid.NewGuid(), tipoComun, "Formación PRL — 20 h", EstadoDocumento.Vigente, new(2027, 3, 1));
        var comunBerriz = new DocumentoRequeridoDto(Guid.NewGuid(), tipoComun, "Formación PRL — 20 h", EstadoDocumento.Vigente, new(2027, 3, 1));
        var soloNorte = new DocumentoRequeridoDto(Guid.NewGuid(), tipoDeNorte, "Formación específica de centro", EstadoDocumento.Proximo, new(2026, 10, 15));
        mediador.Centros[id] = [norte with { Documentos = [comunNorte, soloNorte] }, berriz with { Documentos = [comunBerriz] }];

        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Documentación");

        Filas(cut, "Exigidos en todos sus Centros").Should().ContainSingle().Which.Take(3).Should().Equal(
            "Formación PRL — 20 h", "En sus 2 Centros", "Vigente");
        Filas(cut, "Exigidos solo en algunos Centros").Should().ContainSingle().Which.Take(3).Should().Equal(
            "Formación específica de centro", "Solo en " + norte.CentroNombre, "Por vencer");
    }

    [Fact]
    public async Task Si_la_documentacion_falla_el_resto_de_la_pagina_sigue_y_se_puede_reintentar()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso { FallarDocumentos = true });
        var (norte, _) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte];

        var cut = Renderizar(id);
        AbrirCentros(cut);
        cut.FindAll(".trabajador360-centros > div > ul > .fila-relacion").Should().ContainSingle("Centros no depende de la consulta de documentos");
        cut.FindAll(".trabajador360-por-vencer ul").Should().BeEmpty();

        await AbrirPestanaAsync(cut, "Documentación");
        cut.FindAll("[role=tabpanel] ul[aria-label]").Should().BeEmpty(
            "un fallo no se pinta como «sin documentos» ni como lista");

        mediador.FallarDocumentos = false;
        mediador.Documentos[id] = [DocumentoDelTrabajador("DNI", EstadoDocumento.SinCaducidad, new(2020, 1, 1), null)];
        await cut.InvokeAsync(() => Boton(cut, "Reintentar").Click());

        cut.WaitForAssertion(() => Filas(cut, "Otros documentos del trabajador").Should().ContainSingle());
    }

    [Fact]
    public void Se_conservan_las_asignaciones_con_su_baja_y_las_gestiones_pendientes()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];
        mediador.Gestiones[id] = [Gestion(id, "Reconocimiento médico", "Centro Norte")];

        var cut = Renderizar(id);
        AbrirCentros(cut);

        var asignaciones = cut.Find(".trabajador360-centros");
        asignaciones.TextContent.Should().Contain("Refrielectric S.A.",
            "la contraparte de una Relación Empresarial se rotula «Cliente» (decisión del 2026-10-09)")
            .And.NotContain("Cliente empresarial").And.Contain("Centro Norte").And.Contain("02/07/2026");
        asignaciones.QuerySelectorAll(".trabajador360-centro-baja").Select(b => b.TextContent.Trim())
            .Should().Equal(["Dar de baja de este centro", "Dar de baja de este centro"]);

        cut.FindAll("[role=tab]").Single(t => t.TextContent.Contains("Operación")).Click();
        var gestiones = cut.Find("[aria-label='Gestiones pendientes']");
        gestiones.TextContent.Should().Contain("Reconocimiento médico").And.Contain("Completar");
        mediador.Enviadas.OfType<ObtenerGestionesQuery>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { TrabajadorId = id, Estado = EstadoGestion.Pendiente });
    }

    [Fact]
    public async Task Consulta_ve_documentos_asignaciones_y_gestiones_sin_que_se_le_ofrezca_subir_dar_de_baja_ni_completar()
    {
        // Subir/Renovar/Ver abre DrawerGestionDocumento (solo crea o renueva); dar de baja y
        // completar son DarDeBajaAsignacionesCommand y CompletarGestionCommand. Los tres acaban
        // en ICommand que AutorizacionEscrituraBehavior deniega a Consulta.
        this.ConRolDeEscritura(Roles.Consulta);
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];
        mediador.Gestiones[id] = [Gestion(id, "Reconocimiento médico", "Centro Norte")];

        var cut = Renderizar(id);
        AbrirCentros(cut);
        await cut.FindAll(".trabajador360-centros .fila-relacion-desplegar")[0].ClickAsync(new MouseEventArgs());

        cut.Find(".fila-relacion-desplegado").TextContent.Should().Contain("Formación PRL — 20 h",
            "los documentos exigidos y su estado son lectura: se ven");
        cut.Find(".trabajador360-centros").TextContent.Should().Contain("Centro Norte");
        cut.FindAll(".trabajador360-centro-baja").Should().BeEmpty();
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Contains("Operación")).Click();
        cut.Find("[aria-label='Gestiones pendientes']").TextContent.Should().Contain("Reconocimiento médico");
        cut.FindAll(".columna-accion button").Should().BeEmpty("Completar requiere escritura");
        AbrirCentros(cut);
        cut.FindAll(".fila-relacion-desplegado .fila-relacion-acciones button").Should().BeEmpty(
            "Subir, Renovar, Ver, Dar de baja y Completar son las únicas acciones de fila y todas escriben");
        cut.FindAll(".fila-relacion-desplegado .fila-relacion-nombre-boton").Should().BeEmpty(
            "el nombre del documento abre el mismo drawer de gestión: a Consulta se le pinta como texto");
        cut.FindAll(".menu-acciones-disparador").Should().BeEmpty(
            "Editar, Gestionar y Crear gestión son escrituras: sin ellas el «⋯» abriría un menú vacío");
    }

    /// <summary>
    /// P41b (decisión del propietario, 2026-09-19): «Dar de baja» del trabajador
    /// solo vive en la lista. El menú de la ficha ya no la ofrece; las bajas de
    /// las asignaciones vigentes, que son otra cosa, se quedan en su tabla.
    /// </summary>
    [Fact]
    public async Task La_ficha_no_ofrece_dar_de_baja_al_trabajador()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];

        var cut = Renderizar(id);

        cut.FindAll(".menu-acciones-disparador").Should().ContainSingle(
            "la página tiene un único menú de acciones, el de la cabecera (lo da por hecho DeepLinksTests)");
        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=menuitem]").Select(i => i.TextContent.Trim())
            .Should().Equal(["Editar", "Gestionar", "Crear gestión", "Corregir documento de identidad"]);
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public void El_lateral_ensena_el_telefono_y_el_correo_del_trabajador_y_no_inventa_los_que_no_existen()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (_, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno",
            telefono: "644 902 118", email: "javier.salas@refrielectric.example");
        mediador.Centros[id] = [berriz];

        var cut = Renderizar(id);

        // Sin pulsar ninguna pestaña: lo que era «Contactos» vive en el lateral.
        var rejilla = cut.Find(".cuerpo-con-lateral-lateral .trabajador360-contactos");
        rejilla.TextContent.Should().Contain("644 902 118").And.Contain("javier.salas@refrielectric.example");
        rejilla.QuerySelectorAll("dt").Select(d => d.TextContent.Trim())
            .Should().Equal(["Teléfono", "Correo electrónico"],
                "la dirección no se construye y sin contacto de emergencia no se pintan sus filas");
        rejilla.QuerySelectorAll(".boton-copiar").Should().HaveCount(2, "los dos son datos para pegar en otro sitio");
    }

    [Fact]
    public void Sin_telefono_ni_correo_la_tarjeta_de_datos_se_pinta_con_Sin_informar()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (_, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [berriz];

        var cut = Renderizar(id);

        var lateral = cut.Find(".cuerpo-con-lateral-lateral");
        lateral.TextContent.Should().Contain("Datos del trabajador", "la maqueta la pinta siempre, no la esconde");
        lateral.QuerySelectorAll(".trabajador360-contacto-vacio").Select(v => v.TextContent.Trim())
            .Should().Equal("Sin informar", "Sin informar");
        lateral.QuerySelectorAll(".boton-copiar").Should().BeEmpty("no hay nada que copiar");
    }

    [Fact]
    public async Task Documentacion_agrupa_lo_exigido_en_todos_los_Centros_y_solo_en_algunos()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte, berriz];
        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Documentación");

        var titulos = cut.FindAll(".separador-ambito").Select(t => t.TextContent.Trim()).ToList();
        titulos.Should().NotContain("Documentos del trabajador", "la maqueta agrupa por exigencia, no una lista plana");
        titulos.Should().Contain(t => t.StartsWith("Exigidos en todos sus Centros", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("Exigidos solo en algunos Centros", StringComparison.OrdinalIgnoreCase));
        cut.FindAll(".trabajador360-vigencia").Should().NotBeEmpty();
        cut.FindAll(".fila-relacion-cifra").Should().OnlyContain(
            c => !c.TextContent.Contains("Caducidad") || c.QuerySelector(".trabajador360-solo-lectores") != null,
            "el rótulo «Caducidad: » no cabe en la columna de 104 px: va solo para lectores de pantalla");
    }

    [Fact]
    public async Task Completar_en_Operacion_envia_la_gestion_pulsada_y_recarga_solo_las_gestiones()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        var gestion = Gestion(id, "Aptitud médica", "Centro Norte");
        mediador.Gestiones[id] = [gestion];
        var cut = Renderizar(id);
        var consultasDocumentos = mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Count();
        var consultasGestiones = mediador.Enviadas.OfType<ObtenerGestionesQuery>().Count();

        await cut.Find("[aria-label='Gestiones pendientes'] button").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CompletarGestionCommand>().Should().ContainSingle()
            .Which.Should().Be(new CompletarGestionCommand(gestion.Id, true));
        mediador.Enviadas.OfType<ObtenerGestionesQuery>().Should().HaveCount(consultasGestiones + 1);
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().HaveCount(consultasDocumentos);
        cut.FindAll("[aria-label='Gestiones pendientes']").Should().BeEmpty();
    }

    [Theory]
    [InlineData(EstadoDocumento.Vencido, "peligro")]
    [InlineData(EstadoDocumento.Faltante, "peligro")]
    [InlineData(EstadoDocumento.EnTolerancia, "advertencia")]
    [InlineData(EstadoDocumento.Urgente, "advertencia")]
    [InlineData(EstadoDocumento.Proximo, null)]
    [InlineData(EstadoDocumento.SinConfirmar, null)]
    [InlineData(EstadoDocumento.Vigente, null)]
    [InlineData(EstadoDocumento.SinCaducidad, null)]
    public async Task Documentacion_usa_filas_por_documento_con_tinte_canonico_y_desempate_por_nombre(EstadoDocumento estado, string? tono)
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Documentos[id] =
        [
            DocumentoDelTrabajador("Zeta", estado, new(2025, 1, 1), new(2026, 1, 1)),
            DocumentoDelTrabajador("Alfa", estado, new(2025, 2, 1), new(2026, 2, 1))
        ];
        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Documentación");

        var filas = cut.Find("ul[aria-label='Documentos del trabajador']").QuerySelectorAll("li[data-pieza=fila]");
        filas.Should().HaveCount(2);
        filas.Select(f => f.QuerySelector(".fila-relacion-nombre")!.TextContent).Should().Equal("Alfa", "Zeta");
        filas.Select(f => f.GetAttribute("data-tono")).Should().Equal(tono, tono);
    }

    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public async Task La_agenda_vacia_de_la_ficha_es_compacta_y_solo_escritura_abre_el_contacto(string rol, bool puedeEscribir)
    {
        this.ConRolDeEscritura(rol);
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        var cut = Renderizar(id);
        await AbrirPestanaAsync(cut, "Ficha");

        var agenda = cut.Find(".trabajador360-agenda");
        agenda.QuerySelector(".agenda-vacio-compacto").Should().NotBeNull();
        agenda.QuerySelector(".agenda-vacio-compacto")!.TextContent.Should().Contain("Sin contactos para reclamar.");
        agenda.QuerySelectorAll(".estado-vacio").Should().BeEmpty();
        var botones = agenda.QuerySelectorAll("button").Where(b => b.TextContent.Trim() == "Añadir contacto").ToList();
        botones.Should().HaveCount(puedeEscribir ? 1 : 0);
        if (puedeEscribir)
        {
            await botones.Single().ClickAsync(new MouseEventArgs());
            cut.FindComponent<ModalContactoAgenda>().Instance.Visible.Should().BeTrue();
        }
    }

    [Fact]
    public void La_agenda_con_contactos_conserva_el_marcado_al_activar_la_variante_compacta()
    {
        var mediador = Registrar(new MediatorFalso());
        mediador.Contactos.Add(new ContactoAgendaDto(Guid.NewGuid(), "Lucía Ferrer", "lucia@example.test",
            "986555014", "Responsable de PRL", null, true, false, false, [], [], []));
        var cut = Render<PestanaAgendaContactos>(p => p.Add(x => x.Tipo, TipoPropietarioAgenda.Empresa).Add(x => x.PropietarioId, Guid.NewGuid()));
        static string SinIdentificadoresDeEventos(string marcado) =>
            System.Text.RegularExpressions.Regex.Replace(marcado, @"\s+blazor:[^=]+=""[^""]*""", string.Empty);
        var listado = SinIdentificadoresDeEventos(cut.Find(".agenda-lista").OuterHtml);

        cut.Render(p => p.Add(x => x.VacioCompacto, true));

        SinIdentificadoresDeEventos(cut.Find(".agenda-lista").OuterHtml).Should().Be(listado);
        cut.FindAll(".agenda-vacio-compacto").Should().BeEmpty();
    }

    [Fact]
    public void La_agenda_de_otras_fichas_conserva_el_vacio_completo_por_defecto()
    {
        Registrar(new MediatorFalso());
        var cut = Render<PestanaAgendaContactos>(p => p.Add(x => x.Tipo, TipoPropietarioAgenda.Empresa).Add(x => x.PropietarioId, Guid.NewGuid()));

        cut.FindAll(".agenda-vacio-compacto").Should().BeEmpty();
        cut.Markup.Should().Contain("Sin contactos en la agenda");
        cut.FindAll(".estado-vacio").Should().ContainSingle();
    }

    [Fact]
    public async Task La_pestana_Ficha_lleva_la_agenda_del_empleador_que_pide_por_su_Id_y_su_tipo()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        var (_, berriz) = Escena();
        var detalle = Detalle(id, "Javier", "Salas Moreno");
        mediador.Detalles[id] = detalle;
        mediador.Centros[id] = [berriz];

        var cut = Renderizar(id);

        await AbrirPestanaAsync(cut, "Ficha");

        cut.Find(".cajas-ficha").TextContent.Should().Contain("Agenda del empleador");
        cut.Find(".cuerpo-con-lateral-lateral").TextContent.Should().NotContain("Agenda del empleador");
        mediador.Enviadas.OfType<ObtenerAgendaContactosQuery>().Should().ContainSingle()
            .Which.Should().Be(new ObtenerAgendaContactosQuery(TipoPropietarioAgenda.Empresa, detalle.EmpresaId!.Value),
                "la agenda es la del empleador del trabajador");
    }

    [Fact]
    public async Task Abrir_otro_trabajador_con_la_carga_del_primero_en_vuelo_no_pinta_el_primero()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var respuestaDeA = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerTrabajadorPorIdQuery q && q.Id == a ? respuestaDeA.Task : null
        });
        var (norte, berriz) = Escena();
        mediador.Detalles[a] = Detalle(a, "Javier", "Salas Moreno");
        mediador.Detalles[b] = Detalle(b, "Eider", "Lasa Arrieta");
        mediador.Centros[a] = [norte];
        mediador.Centros[b] = [berriz];

        var cut = Renderizar(a);
        cut.FindAll(".cabecera-identidad").Should().BeEmpty("la cabecera de A sigue en vuelo");

        cut.Render(p => p.Add(x => x.TrabajadorId, b));
        cut.Find(".cabecera-identidad h1").TextContent.Trim().Should().StartWith("Eider Lasa Arrieta");

        await cut.InvokeAsync(() => respuestaDeA.SetResult());

        cut.Find(".cabecera-identidad h1").TextContent.Trim().Should().StartWith("Eider Lasa Arrieta",
            "la respuesta de A llegó tarde y ya no es la vigente");
        cut.Markup.Should().NotContain("Centro Norte", "los centros de A no se piden: su cadena se corta al volver");
        mediador.Enviadas.OfType<ObtenerDocumentacionPorCentroDeTrabajadorQuery>()
            .Select(q => q.TrabajadorId).Should().Equal([b]);
    }

    [Fact]
    public async Task Las_gestiones_pedidas_para_el_primero_no_se_pintan_en_el_segundo()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var gestionesDeA = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerGestionesQuery q && q.TrabajadorId == a ? gestionesDeA.Task : null
        });
        var (norte, berriz) = Escena();
        mediador.Detalles[a] = Detalle(a, "Javier", "Salas Moreno");
        mediador.Detalles[b] = Detalle(b, "Eider", "Lasa Arrieta");
        mediador.Centros[a] = [norte];
        mediador.Centros[b] = [berriz];
        mediador.Gestiones[a] = [Gestion(a, "Certificado de aptitud de A", "Centro Norte")];

        var cut = Renderizar(a);
        AbrirCentros(cut);
        cut.Render(p => p.Add(x => x.TrabajadorId, b));
        await cut.InvokeAsync(() => gestionesDeA.SetResult());

        cut.Find(".cabecera-identidad h1").TextContent.Trim().Should().StartWith("Eider Lasa Arrieta");

        // Un repintado que NO recarga nada (desplegar un centro es estado
        // local y síncrono). Sin él, este test solo observaría que la
        // respuesta superada no dispara StateHasChanged, no que además se
        // descarte: dos guardas distintas, y la del finally tapaba a la del
        // dato. Medido — con la guarda del dato debilitada, el test seguía en
        // verde hasta añadir este repintado.
        await cut.Find(".trabajador360-centros .fila-relacion-desplegar").ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=tab]").Single(t => t.TextContent.Contains("Operación")).Click();

        cut.Markup.Should().NotContain("Certificado de aptitud de A",
            "son las gestiones de otro trabajador");
        cut.FindAll("[aria-label='Gestiones pendientes']").Should().BeEmpty();
    }

    [Fact]
    public void Al_pasar_a_otro_trabajador_la_franja_Por_vencer_no_ensena_los_documentos_del_anterior()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var documentosDeB = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerDocumentosQuery q && q.TrabajadorId == b ? documentosDeB.Task : null
        });
        var (norte, berriz) = Escena();
        mediador.Detalles[a] = Detalle(a, "Javier", "Salas Moreno");
        mediador.Detalles[b] = Detalle(b, "Eider", "Lasa Arrieta");
        mediador.Centros[a] = [norte];
        mediador.Centros[b] = [berriz];
        mediador.Documentos[a] = [DocumentoDelTrabajador("Reconocimiento médico de A", EstadoDocumento.Urgente, new(2025, 10, 1), new(2026, 10, 1))];

        var cut = Renderizar(a);
        cut.Find(".trabajador360-por-vencer ul").TextContent.Should().Contain("Reconocimiento médico de A");

        // Los documentos de B siguen en vuelo; el detalle y los centros de B ya han llegado.
        cut.Render(p => p.Add(x => x.TrabajadorId, b));

        cut.Find(".cabecera-identidad h1").TextContent.Trim().Should().StartWith("Eider Lasa Arrieta");
        cut.Markup.Should().NotContain("Reconocimiento médico de A", "son los documentos de otro trabajador");
    }

    [Fact]
    public async Task Las_consultas_llevan_el_token_de_la_pagina_y_retirarla_las_cancela()
    {
        var id = Guid.NewGuid();
        var enVuelo = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = p => p is ObtenerDocumentacionPorCentroDeTrabajadorQuery ? enVuelo.Task : null
        });
        var (norte, _) = Escena();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.Centros[id] = [norte];

        Renderizar(id);

        var consultas = mediador.Tokens
            .Where(t => t.Peticion.GetType().Name.EndsWith("Query", StringComparison.Ordinal)).ToList();
        consultas.Should().NotBeEmpty("sin consultas emitidas este test no observaría nada");
        consultas.Should().OnlyContain(t => t.Token.CanBeCanceled && !t.Token.IsCancellationRequested,
            "cada consulta tiene que llevar el token del ciclo de la página, no CancellationToken.None");

        await DisposeComponentsAsync();

        consultas.Should().OnlyContain(t => t.Token.IsCancellationRequested,
            "retirada la página no queda ninguna consulta suya trabajando para nadie");

        enVuelo.SetResult();
    }

    [Fact]
    public void Si_el_trabajador_no_esta_en_la_empresa_activa_pinta_FichaNoEncontrada_sin_Reintentar()
    {
        var id = Guid.NewGuid();
        ComponentFactories.AddStub<FichaNoEncontrada>();
        Registrar(new MediatorFalso());

        var cut = Renderizar(id);
        cut.RutaListaDeFichaNoEncontrada().Should().Be("/trabajadores");
        cut.Markup.Should().NotContain("Reintentar").And.NotContain("No pudimos cargar este trabajador");
        cut.FindAll(".cabecera-identidad").Should().BeEmpty();
    }

    // --------------- Hallazgos de la revisión de Codex sobre esta pantalla

    private ToastService Avisos => Services.GetRequiredService<ToastService>();

    private const string BotonBajaAsignacion = ".trabajador360-centro-baja";

    private static IElement BotonDelDialogo(IRenderedComponent<TrabajadorDetalle> cut, string texto) =>
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == texto);

    private static async Task ConfirmarBajaAsignacionAsync(IRenderedComponent<TrabajadorDetalle> cut)
    {
        await cut.Find(BotonBajaAsignacion).ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Dar de baja").ClickAsync(new MouseEventArgs());
    }

    // --------------- FS-13 (auditoría UX de flujos sin salida, 2026-09-24)

    /// <summary>
    /// La baja salía al primer clic y sin vuelta atrás. Ahora se pregunta
    /// antes, nombrando a quién y de qué centro, y cancelar no envía nada.
    /// </summary>
    [Fact]
    public async Task Dar_de_baja_una_asignacion_pide_confirmacion_y_cancelar_no_envia_nada()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);

        var cut = Renderizar(id);
        AbrirCentros(cut);
        await cut.Find(BotonBajaAsignacion).ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<DarDeBajaAsignacionesCommand>().Should().BeEmpty("sin confirmar no se da de baja");
        cut.Find("[role=dialog]").TextContent.Should().Contain("Javier Salas Moreno").And.Contain("Centro Norte")
            .And.Contain("Podrás deshacerlo desde el aviso");

        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<DarDeBajaAsignacionesCommand>().Should().BeEmpty();
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public async Task Confirmar_da_de_baja_esa_asignacion_y_cierra_el_dialogo()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        var norte = mediador.Centros[id][0];

        var cut = Renderizar(id);
        AbrirCentros(cut);
        await ConfirmarBajaAsignacionAsync(cut);

        mediador.Enviadas.OfType<DarDeBajaAsignacionesCommand>().Should().ContainSingle()
            .Which.Ids.Should().Equal([norte.AsignacionId]);
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    /// <summary>
    /// «Deshacer» reabre la MISMA asignación (no crea otra) y recarga la ficha.
    /// </summary>
    [Fact]
    public async Task Deshacer_del_aviso_reabre_la_asignacion_dada_de_baja()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        var norte = mediador.Centros[id][0];

        var cut = Renderizar(id);
        AbrirCentros(cut);
        await ConfirmarBajaAsignacionAsync(cut);

        var aviso = Avisos.Mensajes.Should().ContainSingle().Subject;
        aviso.TextoAccion.Should().Be("Deshacer");
        var cargasAntes = mediador.Enviadas.OfType<ObtenerDocumentacionPorCentroDeTrabajadorQuery>().Count();

        await cut.InvokeAsync(aviso.OnAccion!);

        mediador.Enviadas.OfType<ReactivarAsignacionCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(norte.AsignacionId);
        Avisos.Mensajes.Select(m => (m.Mensaje, m.Tono)).Should().Contain(
            ("Baja deshecha: la asignación vuelve a estar activa.", TonoToast.Exito));
        mediador.Enviadas.OfType<ObtenerDocumentacionPorCentroDeTrabajadorQuery>().Count().Should().BeGreaterThan(cargasAntes,
            "la asignación reabierta tiene que volver a verse en la tabla");
    }

    /// <summary>Si el comando rechaza la reapertura, se dice por qué y no se anuncia como hecha.</summary>
    [Fact]
    public async Task Deshacer_rechazado_muestra_el_motivo_y_no_se_anuncia_como_hecho()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.ResultadoReactivar = Result.Fallo(Error.Crear(
            "Asignacion.SolapaConOtra", "Este trabajador tiene otra asignación a este centro posterior a esta; no se puede reabrir."));

        var cut = Renderizar(id);
        AbrirCentros(cut);
        await ConfirmarBajaAsignacionAsync(cut);
        await cut.InvokeAsync(Avisos.Mensajes.Single().OnAccion!);

        Avisos.Mensajes.Select(m => (m.Mensaje, m.Tono)).Should().Contain(
            ("Este trabajador tiene otra asignación a este centro posterior a esta; no se puede reabrir.", TonoToast.Error));
        Avisos.Mensajes.Should().NotContain(m => m.Mensaje.StartsWith("Baja deshecha"));
    }

    private static async Task AbrirModalCrearGestionAsync(IRenderedComponent<TrabajadorDetalle> cut)
    {
        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitem]").Single(i => i.TextContent.Trim() == "Crear gestión")
            .ClickAsync(new MouseEventArgs());
    }

    /// <summary>
    /// Trabajador de Subcontrata: Â«GestionarÂ» sigue abriendo la confirmaciÃ³n por Cliente empresarial. Con un Trabajador de
    /// Empresa abre el cajÃ³n de reclamaciÃ³n a su Empresa empleadora (decisiÃ³n de Chris, 2026-10-18).
    /// </summary>
    private MediatorFalso ConTrabajadorDeSubcontrata(Guid id, string nombre = "Javier", string apellidos = "Salas Moreno")
    {
        var mediador = ConTrabajador(id, nombre, apellidos);
        mediador.Detalles[id] = DeSubcontrata(mediador.Detalles[id]);
        return mediador;
    }

    private static TrabajadorDetalleDto DeSubcontrata(TrabajadorDetalleDto detalle) =>
        detalle with { EmpresaId = null, SubcontrataId = Guid.NewGuid() };

    private MediatorFalso ConTrabajador(Guid id, string nombre = "Javier", string apellidos = "Salas Moreno")
    {
        var mediador = Registrar(new MediatorFalso());
        var (norte, berriz) = Escena();
        mediador.Detalles[id] = Detalle(id, nombre, apellidos);
        mediador.Centros[id] = [norte, berriz];
        return mediador;
    }

    /// <summary>
    /// Las modales se pintan fuera del bloque de la página y su estado no se
    /// tocaba al cambiar de ruta: abrir una modal para un trabajador y abrir la
    /// ficha de otro la dejaba abierta sobre el segundo. Lo preparado para un
    /// trabajador no puede ejecutarse sobre otro.
    /// </summary>
    [Fact]
    public async Task Una_modal_abierta_para_un_trabajador_no_sobrevive_al_abrir_la_ficha_de_otro()
    {
        var primero = Guid.NewGuid();
        var segundo = Guid.NewGuid();
        var mediador = ConTrabajador(primero);
        mediador.Detalles[segundo] = Detalle(segundo, "Eider", "Lasa Arrieta");
        mediador.Centros[segundo] = mediador.Centros[primero];

        var cut = Renderizar(primero);
        await AbrirModalCrearGestionAsync(cut);
        cut.FindAll(".modal-contenido").Should().ContainSingle("la modal está abierta para el primero");

        cut.Render(p => p.Add(x => x.TrabajadorId, segundo));
        cut.Find(".cabecera-identidad h1").TextContent.Trim().Should().StartWith("Eider Lasa Arrieta");

        cut.FindAll(".modal-contenido").Should().BeEmpty(
            "la modal preguntaba por el primero y en pantalla ya está el segundo");
    }

    /// <summary>
    /// El comando devuelve éxito aunque no haya dado de baja ninguna
    /// asignación (ya cerrada, o fuera de alcance). Decir «Asignación dada de
    /// baja» entonces afirma un efecto que no ocurrió.
    /// </summary>
    [Fact]
    public async Task Una_baja_de_asignacion_que_no_dio_ninguna_no_se_anuncia_como_hecha()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.ResultadoDarDeBajaAsignacion = Result.Exito(
            new ResultadoBajaLoteDto(0, ["La asignación ya estaba cerrada."]));

        var cut = Renderizar(id);
        AbrirCentros(cut);
        await ConfirmarBajaAsignacionAsync(cut);

        Avisos.Mensajes.Select(m => (m.Mensaje, m.Tono)).Should().Equal(
            [("La asignación ya estaba cerrada.", TonoToast.Error)]);
    }

    /// <summary>Control positivo del anterior: con una baja real, el aviso sí dice que se hizo.</summary>
    [Fact]
    public async Task Una_baja_de_asignacion_que_si_ocurrio_se_anuncia_como_hecha()
    {
        var id = Guid.NewGuid();
        ConTrabajador(id);

        var cut = Renderizar(id);
        AbrirCentros(cut);
        await ConfirmarBajaAsignacionAsync(cut);

        Avisos.Mensajes.Select(m => (m.Mensaje, m.Tono)).Should().Equal(
            [("Asignación dada de baja.", TonoToast.Exito)]);
    }

    /// <summary>
    /// El botón de la fila nunca se deshabilita, así que un segundo evento
    /// llegaba mientras el primero esperaba al servidor y mandaba otra baja.
    /// El clic NO se espera antes de soltar la puerta: su manejador está
    /// detenido en la respuesta retenida y esperarlo colgaría el caso.
    /// </summary>
    [Fact]
    public async Task Dos_eventos_seguidos_sobre_la_misma_asignacion_no_mandan_dos_bajas()
    {
        var id = Guid.NewGuid();
        var puerta = new TaskCompletionSource();
        var mediador = ConTrabajador(id);

        var cut = Renderizar(id);
        AbrirCentros(cut);
        await cut.Find(BotonBajaAsignacion).ClickAsync(new MouseEventArgs());
        // Se retiene después de abrir el diálogo: si la fila mandara la baja sin
        // confirmar, el caso tiene que fallar al buscar el diálogo, no colgarse.
        mediador.Retener = p => p is DarDeBajaAsignacionesCommand ? puerta.Task : null;
        var confirmar = BotonDelDialogo(cut, "Dar de baja");
        var primera = confirmar.ClickAsync(new MouseEventArgs());
        var segunda = confirmar.ClickAsync(new MouseEventArgs());

        mediador.Retener = null;
        await cut.InvokeAsync(puerta.SetResult);
        await primera;
        await segunda;

        mediador.Enviadas.OfType<DarDeBajaAsignacionesCommand>().Should().ContainSingle(
            "la guarda de reentrada impide que el segundo evento mande otra baja");
    }

    // --- P1-E2b: aviso de cambios sin guardar -------------------------------------------------

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    [Fact]
    public async Task Aviso_crear_gestion_con_un_tipo_elegido_pregunta_al_salir()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        var tipo = Guid.NewGuid();
        mediador.Tipos.Add(Tipo(tipo, "Formación PRL — 20 h", RequisitoDocumental.Si));
        var cut = Renderizar(id);
        await AbrirModalCrearGestionAsync(cut);

        await cut.Find(".modal-contenido select").ChangeAsync(new ChangeEventArgs { Value = tipo.ToString() });

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_crear_gestion_recien_abierta_no_pregunta_al_salir()
    {
        var id = Guid.NewGuid();
        ConTrabajador(id);
        var cut = Renderizar(id);
        await AbrirModalCrearGestionAsync(cut);
        cut.FindAll(".modal-contenido").Should().ContainSingle("el test necesita la modal abierta");

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "sin tipo elegido no hay nada que perder");
    }

    /// <summary>
    /// Alinea la ficha con el envío (EnviarReclamacion rechaza lo que no tiene FechaVencimiento): un
    /// «Sin confirmar» sin fecha no se ofrece para reclamar y, si es lo único pendiente, se explica por qué.
    /// </summary>
    [Fact]
    public async Task Reclamar_faltantes_no_ofrece_un_sin_confirmar_sin_fecha_y_explica_por_que()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajadorDeSubcontrata(id);
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.SinConfirmar,
                Documento("Reconocimiento médico", EstadoDocumento.SinConfirmar))
        ];
        var cut = Renderizar(id);

        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitem]").Single(i => i.TextContent.Trim() == "Gestionar")
            .ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().BeEmpty("el envío rechazaría un documento sin fecha");
        Avisos.Mensajes.Select(m => m.Mensaje).Should().ContainSingle()
            .Which.Should().Contain("«Sin confirmar» no tienen fecha de vencimiento");
    }

    [Fact]
    public async Task Reclamar_faltantes_con_un_sin_confirmar_sin_fecha_y_un_vencido_solo_reclama_el_vencido()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajadorDeSubcontrata(id);
        var vencido = Documento("Formación PRL — 20 h", EstadoDocumento.Vencido);
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Vencido,
                Documento("Reconocimiento médico", EstadoDocumento.SinConfirmar), vencido)
        ];
        var cut = Renderizar(id);

        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitem]").Single(i => i.TextContent.Trim() == "Gestionar")
            .ClickAsync(new MouseEventArgs());

        // Un clic en «Gestionar» solo abre la confirmación (es un correo a un tercero): nada sale hasta «Reclamar».
        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().BeEmpty("un clic no envía la reclamación");
        cut.Find(".modal-contenido .workspace-nota-contextual").TextContent.Should().Contain("Refrielectric S.A.").And.Contain("al confirmar");

        await BotonDelPie(cut, "Reclamar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().ContainSingle()
            .Which.DocumentoIds.Should().Equal([vencido.DocumentoId!.Value]);
    }

    [Fact]
    public async Task Reclamar_faltantes_no_ofrece_un_documento_con_vencimiento_mas_alla_de_la_ventana()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajadorDeSubcontrata(id);
        var lejano = new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Formación PRL — 20 h", EstadoDocumento.Proximo,
            VentanaReclamacion.Limite(DiaDeNegocio.Hoy()).AddDays(1));
        var dentro = new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Reconocimiento médico", EstadoDocumento.Proximo,
            VentanaReclamacion.Limite(DiaDeNegocio.Hoy()));
        mediador.Centros[id] = [Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Proximo, lejano, dentro)];
        var cut = Renderizar(id);

        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitem]").Single(i => i.TextContent.Trim() == "Gestionar")
            .ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().BeEmpty("un clic no envía la reclamación");

        await BotonDelPie(cut, "Reclamar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().ContainSingle()
            .Which.DocumentoIds.Should().Equal([dentro.DocumentoId!.Value],
                "el límite es inclusivo y un día más allá el envío lo rechazaría");
    }

    [Fact]
    public async Task Gestionar_con_un_solo_Cliente_empresarial_cancelar_no_envia_nada()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajadorDeSubcontrata(id);
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Vencido, Documento("Formación PRL — 20 h", EstadoDocumento.Vencido))
        ];
        var cut = Renderizar(id);

        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitem]").Single(i => i.TextContent.Trim() == "Gestionar")
            .ClickAsync(new MouseEventArgs());
        await BotonDelPie(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().BeEmpty("cancelar la confirmación no envía el correo al Cliente empresarial");
    }

    [Fact]
    public async Task Gestionar_con_un_Trabajador_de_Empresa_abre_el_cajon_que_reclama_a_su_Delegacion_y_no_envia_nada()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        var empresaId = mediador.Detalles[id]!.EmpresaId!.Value;
        var delegacionEmpresaId = Guid.NewGuid();
        mediador.LotesReclamacion =
        [
            new(empresaId, "Refrielectric S.L.", AmbitoAplicacion.Trabajador, null,
                [new(Guid.NewGuid(), id, "Javier Salas Moreno", Guid.NewGuid(), "Formación PRL — 20 h", DateOnly.FromDateTime(DateTime.Today).AddDays(10), EstadoDocumento.Proximo)],
                null,
                [new(delegacionEmpresaId, "Marta Sevilla", "reclamaciones.sevilla@refrielectric.example", ["Contacto de reclamación de la Delegación Sevilla"])],
                EmpresaEmpleadora: new(delegacionEmpresaId, "Sevilla")),
        ];
        var cut = Renderizar(id);

        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitem]").Single(i => i.TextContent.Trim() == "Gestionar")
            .ClickAsync(new MouseEventArgs());
        cut.FindAll(".reclamacion-destinatarios input[type=checkbox]").Should().BeEmpty("no se abre la confirmación por Cliente empresarial");
        await cut.FindAll(".selector-lote-acciones button").Single(b => b.TextContent.Trim() == "Continuar")
            .ClickAsync(new MouseEventArgs());

        var consulta = mediador.Enviadas
            .OfType<CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro.ObtenerLoteReclamacionPorFiltroQuery>()
            .Should().ContainSingle().Subject;
        consulta.PorEmpresaEmpleadora.Should().BeTrue("lo de un Trabajador de Empresa se reclama a su Empresa empleadora");
        consulta.Filtro.Ambito.Should().Be(AmbitoAplicacion.Trabajador);
        consulta.Filtro.EntidadId.Should().Be(id);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Delegación Sevilla").And.Contain("reclamaciones.sevilla@refrielectric.example"));
        mediador.Enviadas.Should().NotContain(p => p.GetType().Name.StartsWith("EnviarReclamacion"),
            "abrir el cajón no envía nada: el correo sale solo con el clic explícito del Gestor CAE");
    }

    private MediatorFalso ConDosClientesEmpresarialesQueReclamar(Guid id)
    {
        var mediador = ConTrabajadorDeSubcontrata(id);
        mediador.Centros[id] =
        [
            Centro("Centro Norte", "Refrielectric S.A.", EstadoDocumento.Vencido, Documento("Formación PRL — 20 h", EstadoDocumento.Vencido)),
            Centro("Nave Berriz", "Talleres Berriz Coop.", EstadoDocumento.Vencido, Documento("Reconocimiento médico", EstadoDocumento.Vencido)),
        ];
        return mediador;
    }

    private static async Task AbrirModalReclamarAsync(IRenderedComponent<TrabajadorDetalle> cut)
    {
        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menuitem]").Single(i => i.TextContent.Trim() == "Gestionar")
            .ClickAsync(new MouseEventArgs());
        cut.FindAll(".reclamacion-destinatarios input[type=checkbox]").Should().HaveCount(2,
            "el test necesita la modal con dos Clientes");
    }

    [Fact]
    public async Task Aviso_reclamar_con_todos_los_Clientes_empresariales_marcados_de_partida_no_pregunta()
    {
        var id = Guid.NewGuid();
        ConDosClientesEmpresarialesQueReclamar(id);
        var cut = Renderizar(id);
        await AbrirModalReclamarAsync(cut);

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "las casillas marcadas de partida no son un cambio");
    }

    [Fact]
    public async Task Aviso_reclamar_con_una_casilla_desmarcada_pregunta_y_volver_a_marcarla_no()
    {
        var id = Guid.NewGuid();
        ConDosClientesEmpresarialesQueReclamar(id);
        var cut = Renderizar(id);
        await AbrirModalReclamarAsync(cut);

        await cut.FindAll(".reclamacion-destinatarios input[type=checkbox]")[0].ChangeAsync(new ChangeEventArgs { Value = false });
        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
        await cut.PulsarEnElAvisoAsync("Seguir editando");

        await cut.FindAll(".reclamacion-destinatarios input[type=checkbox]")[0].ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "volver a marcarla deja la modal como se abrió");
    }

    // ---- S12 lote 3b: los dos modales de la ficha son ModalFormulario

    private static IElement BotonDelPie(IRenderedComponent<TrabajadorDetalle> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == texto);

    private static IReadOnlyList<string> AlertasDelModal(IRenderedComponent<TrabajadorDetalle> cut) =>
        cut.FindAll("[role=dialog] [role=alert]").Select(a => a.TextContent.Trim()).ToList();

    [Fact]
    public async Task Crear_gestion_sin_tipo_deshabilita_Crear_con_su_motivo_y_con_tipo_lo_habilita()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        var tipo = Guid.NewGuid();
        mediador.Tipos.Add(Tipo(tipo, "Formación PRL — 20 h", RequisitoDocumental.Si));
        var cut = Renderizar(id);
        await AbrirModalCrearGestionAsync(cut);

        var crear = BotonDelPie(cut, "Crear");
        crear.HasAttribute("disabled").Should().BeTrue("sin tipo elegido no hay nada que crear");
        crear.GetAttribute("title").Should().Be("Elige un tipo de documento", "un primario deshabilitado dice por qué");

        await cut.Find("[role=dialog] select").ChangeAsync(new ChangeEventArgs { Value = tipo.ToString() });

        BotonDelPie(cut, "Crear").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public async Task Crear_gestion_rechazada_por_el_servidor_sale_en_el_aviso_fijo_no_en_un_toast_y_elegir_otro_tipo_lo_quita()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        mediador.FallaCrearGestionCon = "Ya hay una gestión abierta de este tipo.";
        var tipo = Guid.NewGuid();
        mediador.Tipos.Add(Tipo(tipo, "Formación PRL — 20 h", RequisitoDocumental.Si));
        var cut = Renderizar(id);
        await AbrirModalCrearGestionAsync(cut);
        await cut.Find("[role=dialog] select").ChangeAsync(new ChangeEventArgs { Value = tipo.ToString() });
        AlertasDelModal(cut).Should().BeEmpty("control positivo: antes del intento no hay aviso");

        await BotonDelPie(cut, "Crear").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearGestionesParaTrabajadorCommand>().Should().ContainSingle("control positivo: se intentó");
        AlertasDelModal(cut).Should().Equal("Ya hay una gestión abierta de este tipo.");
        cut.FindAll("[role=dialog] .modal-cuerpo [role=alert]").Should().BeEmpty("el aviso va fuera del cuerpo desplazable (D-20)");
        Avisos.Mensajes.Should().BeEmpty("el motivo ya no es un toast que desaparece");

        await cut.Find("[role=dialog] select").ChangeAsync(new ChangeEventArgs { Value = string.Empty });
        AlertasDelModal(cut).Should().BeEmpty("el motivo era del intento anterior");
    }

    [Fact]
    public async Task Cancelar_en_crear_gestion_con_un_tipo_elegido_pregunta_y_sin_tipo_cierra()
    {
        var id = Guid.NewGuid();
        var mediador = ConTrabajador(id);
        var tipo = Guid.NewGuid();
        mediador.Tipos.Add(Tipo(tipo, "Formación PRL — 20 h", RequisitoDocumental.Si));
        var cut = Renderizar(id);
        await AbrirModalCrearGestionAsync(cut);
        await BotonDelPie(cut, "Cancelar").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().BeEmpty("sin tipo elegido «Cancelar» cierra sin preguntar");

        await AbrirModalCrearGestionAsync(cut);
        await cut.Find("[role=dialog] select").ChangeAsync(new ChangeEventArgs { Value = tipo.ToString() });
        await BotonDelPie(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().Contain("Descartar cambios", "con un tipo elegido «Cancelar» pregunta como la X");
    }

    [Fact]
    public async Task Reclamar_sin_ningun_Cliente_empresarial_marcado_deshabilita_Reclamar_con_su_motivo()
    {
        var id = Guid.NewGuid();
        ConDosClientesEmpresarialesQueReclamar(id);
        var cut = Renderizar(id);
        await AbrirModalReclamarAsync(cut);
        BotonDelPie(cut, "Reclamar").HasAttribute("disabled").Should().BeFalse("control positivo: con las dos marcadas se puede reclamar");

        for (var i = 0; i < 2; i++)
            await cut.FindAll(".reclamacion-destinatarios input[type=checkbox]")[i].ChangeAsync(new ChangeEventArgs { Value = false });

        var reclamar = BotonDelPie(cut, "Reclamar");
        reclamar.HasAttribute("disabled").Should().BeTrue();
        reclamar.GetAttribute("title").Should().Be("Marca al menos un Cliente");
    }

    /// <summary>El modal cerraba antes de enviar: la selección se perdía y, si el envío fallaba, solo quedaban toasts.</summary>
    [Fact]
    public async Task Reclamar_mantiene_el_modal_abierto_mientras_envia_y_lo_cierra_al_terminar()
    {
        var id = Guid.NewGuid();
        var mediador = ConDosClientesEmpresarialesQueReclamar(id);
        var envio = new TaskCompletionSource();
        mediador.Retener = p => p is EnviarReclamacionCommand ? envio.Task : null;
        var cut = Renderizar(id);
        await AbrirModalReclamarAsync(cut);

        var pulsado = BotonDelPie(cut, "Reclamar").ClickAsync(new MouseEventArgs());
        try
        {
            mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().ContainSingle("control positivo: el primer envío está en curso");
            cut.FindAll("[role=dialog]").Should().NotBeEmpty("el modal sigue abierto mientras se envía");
            cut.FindAll(".reclamacion-destinatarios input[type=checkbox]").Should().HaveCount(2, "y conserva la selección");
            cut.Find(".reclamacion-destinatarios").HasAttribute("disabled").Should().BeTrue("las casillas no se tocan mientras se envía");
            BotonDelPie(cut, "Cancelar").HasAttribute("disabled").Should().BeTrue();
        }
        finally
        {
            await cut.InvokeAsync(() => envio.TrySetResult());
            await pulsado.WaitAsync(TimeSpan.FromSeconds(10));
        }

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().HaveCount(2, "un envío por Cliente");
        cut.FindAll("[role=dialog]").Should().BeEmpty("al terminar bien, se cierra");
    }

    /// <summary>El kit bloquea Escape y el clic fuera mientras guarda, pero la X es del Modal: la pantalla ignora el cierre mientras envía.</summary>
    [Fact]
    public async Task La_X_no_cierra_el_modal_de_reclamar_ni_el_de_crear_gestion_mientras_se_envia()
    {
        var id = Guid.NewGuid();
        var mediador = ConDosClientesEmpresarialesQueReclamar(id);
        var envio = new TaskCompletionSource();
        mediador.Retener = p => p is EnviarReclamacionCommand ? envio.Task : null;
        var cut = Renderizar(id);
        await AbrirModalReclamarAsync(cut);

        var pulsado = BotonDelPie(cut, "Reclamar").ClickAsync(new MouseEventArgs());
        try
        {
            mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().ContainSingle("control positivo: el envío está en curso");
            await cut.Find("[role=dialog] .modal-cerrar").ClickAsync(new MouseEventArgs());
            cut.FindAll("[role=dialog]").Should().NotBeEmpty("cerrar con la X a mitad de un envío perdería su resultado");
        }
        finally
        {
            await cut.InvokeAsync(() => envio.TrySetResult());
            await pulsado.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task Reclamar_con_exito_parcial_cierra_y_avisa_en_toasts_del_envio_que_fallo()
    {
        var id = Guid.NewGuid();
        var mediador = ConDosClientesEmpresarialesQueReclamar(id);
        mediador.FallaEnviarReclamacionCon = "Sin contacto con email.";
        mediador.FallaEnviarReclamacionDesdeElEnvio = 2;
        var cut = Renderizar(id);
        await AbrirModalReclamarAsync(cut);

        await BotonDelPie(cut, "Reclamar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().HaveCount(2, "control positivo: se intentó con los dos");
        cut.FindAll("[role=dialog]").Should().BeEmpty("alguno salió: se cierra");
        Avisos.Mensajes.Should().Contain(m => m.Tono == TonoToast.Error && m.Mensaje.Contains("Sin contacto con email."), "el fallo parcial no se pierde");
        Avisos.Mensajes.Should().Contain(m => m.Tono == TonoToast.Exito, "y el envío que salió también se anuncia");
    }

    /// <summary>Con el modal abierto durante el envío, la ficha puede cambiar de trabajador antes de que termine.</summary>
    [Fact]
    public async Task Un_envio_de_reclamacion_que_termina_tras_cambiar_de_trabajador_no_escribe_en_la_ficha_nueva_y_sus_fallos_salen_en_toasts()
    {
        var primero = Guid.NewGuid();
        var segundo = Guid.NewGuid();
        var mediador = ConDosClientesEmpresarialesQueReclamar(primero);
        mediador.Detalles[segundo] = DeSubcontrata(Detalle(segundo, "Eider", "Lasa Arrieta"));
        mediador.Centros[segundo] = mediador.Centros[primero];
        mediador.FallaEnviarReclamacionCon = "Sin contacto con email.";
        var envio = new TaskCompletionSource();
        mediador.Retener = p => p is EnviarReclamacionCommand ? envio.Task : null;
        var cut = Renderizar(primero);
        await AbrirModalReclamarAsync(cut);

        var pulsado = BotonDelPie(cut, "Reclamar").ClickAsync(new MouseEventArgs());
        try
        {
            mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().ContainSingle("control positivo: el envío del primero está en curso");
            cut.Render(p => p.Add(x => x.TrabajadorId, segundo));
            cut.Find(".cabecera-identidad h1").TextContent.Trim().Should().StartWith("Eider Lasa Arrieta");
        }
        finally
        {
            await cut.InvokeAsync(() => envio.TrySetResult());
            await pulsado.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // El segundo trabajador abre su propio modal: no hereda el aviso del envío del primero.
        await AbrirModalReclamarAsync(cut);
        AlertasDelModal(cut).Should().BeEmpty("el rechazo era del trabajador anterior");
        Avisos.Mensajes.Should().Contain(m => m.Tono == TonoToast.Error && m.Mensaje.Contains("Sin contacto con email."), "el fallo del primero no se pierde: sale en toast");
    }

    [Fact]
    public async Task Reclamar_con_todos_los_envios_rechazados_deja_el_modal_con_la_seleccion_y_el_motivo_en_el_aviso_fijo()
    {
        var id = Guid.NewGuid();
        var mediador = ConDosClientesEmpresarialesQueReclamar(id);
        mediador.FallaEnviarReclamacionCon = "El Cliente no tiene contacto con email.";
        var cut = Renderizar(id);
        await AbrirModalReclamarAsync(cut);

        await BotonDelPie(cut, "Reclamar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EnviarReclamacionCommand>().Should().HaveCount(2, "control positivo: se intentó con los dos");
        cut.FindAll("[role=dialog]").Should().NotBeEmpty("si no se envió ninguno, el modal sigue para reintentar");
        AlertasDelModal(cut).Should().ContainSingle().Which.Should().Contain("El Cliente no tiene contacto con email.");
        cut.FindAll(".reclamacion-destinatarios input[type=checkbox]").Should().OnlyContain(c => c.HasAttribute("checked"), "la selección se conserva");
        Avisos.Mensajes.Should().BeEmpty("los motivos van en el aviso fijo, no en toasts");

        await cut.FindAll(".reclamacion-destinatarios input[type=checkbox]")[0].ChangeAsync(new ChangeEventArgs { Value = false });
        AlertasDelModal(cut).Should().BeEmpty("el motivo era del intento anterior");
    }
}

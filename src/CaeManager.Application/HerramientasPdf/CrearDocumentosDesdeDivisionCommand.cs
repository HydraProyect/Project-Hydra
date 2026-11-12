using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>Un tramo revisado por una persona: páginas, titular, tipo y fechas son los que quedaron a la vista.</summary>
public record TramoACrear(
    int PaginaInicial, int PaginaFinal, ClaseTitular Clase, Guid TitularId, Guid TipoDocumentoId,
    DateOnly FechaEmision, DateOnly? FechaVencimientoManual, bool NoCaduca);

public enum EstadoTramoCreado
{
    Creado = 0,

    /// <summary>El tramo supera <see cref="LimitesArchivoSubido.TamanoMaximoBytes"/>: se avisa y no se crea.</summary>
    DemasiadoGrande = 1,

    /// <summary><see cref="CrearDocumentoCommand"/> lo rechazó; <see cref="ResultadoTramoCreado.Detalle"/> dice por qué.</summary>
    Rechazado = 2,

    /// <summary>
    /// El alta falló sin respuesta: el Documento pudo quedar guardado antes del fallo, así que se conserva su archivo
    /// y se pide comprobarlo en vez de repetirlo.
    /// </summary>
    SinConfirmar = 3
}

/// <param name="EntradaRetenida">
/// El lote se cortó aquí porque el proceso aparte no consta terminado: quien reservó memoria para el original no la
/// devuelve (<see cref="EntradaPdfRetenidaException.Retener"/>).
/// </param>
/// <param name="CodigoError">
/// Qué pasó con un tramo que no se creó, como código y nunca con el contenido ni los nombres: el de la extracción o del alta,
/// uno de <see cref="CrearDocumentosDesdeDivisionCommandHandler"/>, o <see cref="CrearDocumentosDesdeDivisionCommandHandler.PrefijoNoIntentado"/>
/// más la causa del corte si el tramo ya no se intentó. Es lo que el registro guarda de cada tramo cuando nadie ve el resultado.
/// </param>
public record ResultadoTramoCreado(
    int PaginaInicial, int PaginaFinal, EstadoTramoCreado Estado, Guid? DocumentoId, string? Detalle, bool EntradaRetenida = false,
    string? CodigoError = null);

/// <summary>
/// Crea los Documentos de un PDF dividido, tramo a tramo. No decide nada que ya decida el alta normal: cada tramo pasa
/// por <see cref="CrearDocumentoCommand"/> (autorización, alcance de cartera, verificación del archivo, encolado del
/// análisis y sus guardas por tipo). Lo que añade es lo que el alta normal no puede ver: que el Tenant sea el del
/// inicio del asistente y que cada titular pertenezca a esa Empresa gestionada o a una Subcontrata suya.
///
/// No es todo o nada: un tramo rechazado no deshace los anteriores, y el resultado dice qué pasó con cada uno. Un
/// titular ajeno, en cambio, no es un tramo rechazado sino una petición manipulada, y no se crea nada.
/// </summary>
/// <param name="TenantEsperadoId">El Tenant con el que empezó el asistente; si el selector cambió, se rechaza.</param>
/// <param name="Indice">
/// Solo ese tramo de <paramref name="Tramos"/>, tras validarlos todos igual que el lote entero: es la forma en que lo envía
/// <see cref="CreacionDivisionPdfPorTramos"/>, un Command por tramo, para no retener la <see cref="PuertaAccesoDatos"/> del
/// circuito durante todo el lote. El resultado es ese tramo y, si el lote se corta en él, los que faltan detrás. Sin él, el
/// lote entero en un solo Command (lo usa <c>DividirAdjuntoPdfCommand</c>, que ya es un Command).
/// </param>
public record CrearDocumentosDesdeDivisionCommand(
    byte[] ContenidoPdf, string NombreOriginal, Guid TenantEsperadoId, Guid EmpresaGestionadaId,
    IReadOnlyList<TramoACrear> Tramos, int? Indice = null) : ICommand<IReadOnlyList<ResultadoTramoCreado>>;

public class CrearDocumentosDesdeDivisionCommandValidator : AbstractValidator<CrearDocumentosDesdeDivisionCommand>
{
    public CrearDocumentosDesdeDivisionCommandValidator()
    {
        RuleFor(c => c.ContenidoPdf).NotEmpty();
        RuleFor(c => c.ContenidoPdf.Length).LessThanOrEqualTo((int)LimitesDivisionPdf.TamanoMaximoOriginalBytes);
        RuleFor(c => c.TenantEsperadoId).NotEmpty();
        RuleFor(c => c.EmpresaGestionadaId).NotEmpty();
        RuleFor(c => c.Tramos).NotEmpty().WithMessage("No hay documentos que crear.");
        RuleFor(c => c.Indice)
            .Must((c, indice) => indice is null || (c.Tramos is not null && indice >= 0 && indice < c.Tramos.Count))
            .WithMessage("El documento que se pide crear no está en la lista.");
    }
}

public class CrearDocumentosDesdeDivisionCommandHandler(
    IDisponibilidadHerramientasPdf disponibilidad,
    ITenantActual tenantActual,
    IMediator mediator,
    IDivisorPdf divisor,
    IFileStorageService almacen,
    IAlcanceDatosService alcanceDatos,
    ILogger<CrearDocumentosDesdeDivisionCommandHandler> logger)
    : IRequestHandler<CrearDocumentosDesdeDivisionCommand, Result<IReadOnlyList<ResultadoTramoCreado>>>
{
    public const string ErrorTenantCruzado = "DivisionPdf.TenantCruzado";
    public const string ErrorTitularAjeno = "DivisionPdf.TitularAjeno";
    public const string ErrorTramosInvalidos = "DivisionPdf.TramosInvalidos";

    public async Task<Result<IReadOnlyList<ResultadoTramoCreado>>> Handle(
        CrearDocumentosDesdeDivisionCommand request, CancellationToken cancellationToken)
    {
        if (!await disponibilidad.EstaDisponibleAsync(cancellationToken))
            return Fallo(PrepararDivisionPdfQueryHandler.ErrorNoDisponible, "Herramientas PDF no está disponible.");

        if (tenantActual.TenantId is not { } tenant || tenant != request.TenantEsperadoId)
            return Fallo(ErrorTenantCruzado, "La Empresa ha cambiado. Vuelve a empezar para no crear documentos en otra.");

        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaGestionadaId, cancellationToken))
            return Fallo(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable, "La Empresa no está en tu cartera.");

        var catalogo = await mediator.Send(new ObtenerCatalogoDivisionPdfQuery(request.EmpresaGestionadaId), cancellationToken);
        if (!catalogo.EsExitoso)
            return Result.Fallo<IReadOnlyList<ResultadoTramoCreado>>(catalogo.Error);

        if (request.Tramos.Any(t => !catalogo.Valor.Contiene(t.Clase, t.TitularId)))
            return Fallo(ErrorTitularAjeno, "Un documento se asigna a alguien que no pertenece a esta Empresa ni a sus Subcontratas.");

        // Antes de crear nada: el alta lanza si no valida, y a mitad del lote dejaría creados los tramos anteriores.
        if (MotivoDeTramoInvalido(request, catalogo.Valor) is { } motivo)
            return Fallo(ErrorTramosInvalidos, motivo);

        // En un proceso aparte con su límite de memoria y de tiempo. Sin turno libre o con un proceso aparte que no consta
        // terminado, lanza antes de crear nada: la pantalla avisa y, en el segundo caso, retiene la reserva del original.
        var abierto = await divisor.AbrirAsync(request.ContenidoPdf, cancellationToken);
        if (!abierto.EsExitoso)
            return Result.Fallo<IReadOnlyList<ResultadoTramoCreado>>(abierto.Error);

        using var pdf = abierto.Valor;

        if (!TramosCaben(request.Tramos, pdf.NumeroPaginas))
            return Fallo(ErrorTramosInvalidos, "Las páginas de los documentos no coinciden con el PDF.");

        // Cada corte del lote deja este tramo y los que faltan sin preparar y devuelve lo ya creado: con Documentos creados
        // (o que pudieron quedar creados), lanzar perdería su lista y el usuario los duplicaría al repetir. Sin ninguno, la
        // falta de turno y la cancelación sí lanzan, y el cambio de Tenant falla como al empezar: la pantalla avisa y
        // conserva el original para reintentar.
        // Con Indice, solo ese tramo: lo ya creado en los Commands anteriores no está en esta lista, así que los cortes que
        // dependen de ello (sin turno, cancelación, cambio de Tenant) lanzan o fallan y los decide CreacionDivisionPdfPorTramos.
        var primero = request.Indice ?? 0;
        var aCrear = request.Indice is { } indice ? request.Tramos.Skip(indice).Take(1) : request.Tramos;
        var resultados = new List<ResultadoTramoCreado>(request.Tramos.Count - primero);
        foreach (var tramo in aCrear)
        {
            string detalleRestantes;
            string causa;
            var retenida = false;
            try
            {
                resultados.Add(await CrearTramoSinPerderElLoteAsync(request, pdf, tramo, cancellationToken));
                continue;
            }
            catch (CorteDelLoteException ex)
            {
                // El proceso aparte agotó su memoria o su tiempo, o murió, con este tramo, o no se pudo usar. Seguir lanzaría
                // un proceso más por cada tramo que falta, con el mismo PDF, y ocuparía el turno que comparten Unir y Dividir.
                resultados.Add(ex.Resultado);
                detalleRestantes = ex.DetalleRestantes;
                causa = ex.Resultado.CodigoError ?? ErrorTramoNoPreparado;
            }
            catch (TenantCambiadoException) when (HayDocumentosCreados(resultados))
            {
                logger.LogWarning("Dividir PDF cortó el lote: el Tenant activo ya no es el del inicio del asistente.");
                detalleRestantes = DetalleTenantCambiado;
                causa = ErrorTenantCruzado;
            }
            catch (TenantCambiadoException)
            {
                return Fallo(ErrorTenantCruzado, DetalleTenantCambiado);
            }
            catch (OperationCanceledException) when (HayDocumentosCreados(resultados))
            {
                logger.LogWarning("Dividir PDF cortó el lote al cancelarse la operación.");
                detalleRestantes = DetalleCancelado;
                causa = ErrorLoteCancelado;
            }
            catch (Exception ex) when (HayDocumentosCreados(resultados) && ex is PresupuestoMemoriaAgotadoException or EntradaPdfRetenidaException)
            {
                // Sin turno para el proceso aparte, o con uno que no consta terminado (y entonces no habrá más turnos en la
                // instancia). Si el proceso no consta terminado, el resultado lo dice para que la pantalla retenga la
                // reserva del original.
                logger.LogWarning("Dividir PDF cortó el lote sin turno en el proceso aparte ({Excepcion}).", ex.GetType().Name);
                retenida = ex is EntradaPdfRetenidaException;
                detalleRestantes = DetalleSinTurno;
                causa = ErrorSinTurno;
            }

            resultados.AddRange(Restantes(request.Tramos.Skip(primero + resultados.Count), detalleRestantes, causa, retenida));
            break;
        }

        return Result.Exito<IReadOnlyList<ResultadoTramoCreado>>(resultados);
    }

    /// <summary>Los tramos que el corte deja sin intentar: rechazados, con lo que se dice de ellos y la causa del corte.</summary>
    internal static IEnumerable<ResultadoTramoCreado> Restantes(
        IEnumerable<TramoACrear> pendientes, string detalle, string causa, bool retenida) =>
        pendientes.Select(p => Resultado(p, EstadoTramoCreado.Rechazado, null, detalle, PrefijoNoIntentado + causa) with { EntradaRetenida = retenida })
            .ToList();

    /// <summary>Código de un tramo que el corte del lote dejó sin intentar; le sigue el código de la causa.</summary>
    public const string PrefijoNoIntentado = "DivisionPdf.NoIntentado.";
    public const string ErrorTramoNoPreparado = "DivisionPdf.TramoNoPreparado";
    public const string ErrorAltaNoValida = "DivisionPdf.AltaNoValida";
    public const string ErrorAltaSinConfirmar = "DivisionPdf.AltaSinConfirmar";
    public const string ErrorLoteCancelado = "DivisionPdf.LoteCancelado";
    public const string ErrorSinTurno = "DivisionPdf.SinTurno";

    internal const string DetalleSinTurno = "No se ha podido preparar este documento ahora. Inténtalo más tarde.";
    internal const string DetalleTenantCambiado = "La Empresa ha cambiado. Vuelve a empezar para no crear documentos en otra.";
    internal const string DetalleCancelado = "No se ha preparado este documento: la operación se ha cancelado.";
    private const string DetalleTrasPdfDemasiadoComplejo =
        "No se ha preparado este documento: el PDF no se pudo procesar al separar un documento anterior.";
    private const string DetalleTrasFalloInterno =
        "No se ha preparado este documento: un fallo interno de TALVEG impidió separar uno anterior. Inténtalo más tarde.";

    /// <summary>
    /// El Tenant activo sigue siendo el del inicio del asistente. Se comprueba sin ninguna espera entre la comprobación y lo
    /// que protege: el almacenamiento cifra y guarda con el Tenant activo, que lee al entrar y no compara con nadie.
    /// </summary>
    private bool TenantSigueSiendoElEsperado(CrearDocumentosDesdeDivisionCommand request) =>
        tenantActual.TenantId is { } tenant && tenant == request.TenantEsperadoId;

    /// <summary>El Tenant activo cambió a mitad del lote: no se escribe ni se da de alta nada más.</summary>
    private sealed class TenantCambiadoException() : Exception("El Tenant activo cambió a mitad de Dividir PDF.");

    /// <summary>
    /// El proceso aparte no pudo con el PDF al extraer un tramo, o no se pudo usar: lleva el resultado de ese tramo y lo que se
    /// dice de los que faltan, que ya no se intentan.
    /// </summary>
    private sealed class CorteDelLoteException(ResultadoTramoCreado resultado, string detalleRestantes)
        : Exception("Dividir PDF corta el lote en el proceso aparte.")
    {
        public ResultadoTramoCreado Resultado { get; } = resultado;
        public string DetalleRestantes { get; } = detalleRestantes;
    }

    private static bool HayDocumentosCreados(List<ResultadoTramoCreado> resultados) =>
        resultados.Exists(r => r.Estado is EstadoTramoCreado.Creado or EstadoTramoCreado.SinConfirmar);

    private static string? MotivoDeTramoInvalido(CrearDocumentosDesdeDivisionCommand request, CatalogoDivisionPdf catalogo)
    {
        if (request.Tramos.Any(t => catalogo.Tipos.All(tipo => tipo.Id != t.TipoDocumentoId)))
            return "Un documento tiene un tipo que no está disponible.";

        var validador = new CrearDocumentoCommandValidator();

        return request.Tramos
            .Select(t => validador.Validate(ComandoDeAlta(request, t, string.Empty)))
            .FirstOrDefault(v => !v.IsValid)?.Errors[0].ErrorMessage;
    }

    /// <summary>
    /// Cada alta confirma por separado: un fallo a mitad no puede borrar la lista de lo ya creado, o el usuario
    /// repetiría el lote y duplicaría esos Documentos.
    /// </summary>
    private async Task<ResultadoTramoCreado> CrearTramoSinPerderElLoteAsync(
        CrearDocumentosDesdeDivisionCommand request, IPdfParaDividir pdf, TramoACrear tramo, CancellationToken cancellationToken)
    {
        try
        {
            return await CrearTramoAsync(request, pdf, tramo, cancellationToken);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or PresupuestoMemoriaAgotadoException or EntradaPdfRetenidaException
                                       or TenantCambiadoException or CorteDelLoteException))
        {
            logger.LogWarning("Dividir PDF no pudo preparar un tramo ({Excepcion}).", ex.GetType().Name);
            return Resultado(tramo, EstadoTramoCreado.Rechazado, null, "No se ha podido preparar este documento.", ErrorTramoNoPreparado);
        }
    }

    private static Result<IReadOnlyList<ResultadoTramoCreado>> Fallo(string codigo, string mensaje) =>
        Result.Fallo<IReadOnlyList<ResultadoTramoCreado>>(Error.Crear(codigo, mensaje));

    private static bool TramosCaben(IReadOnlyList<TramoACrear> tramos, int numeroPaginas)
    {
        var ordenados = tramos.OrderBy(t => t.PaginaInicial).ToList();

        return ordenados.All(t => t.PaginaInicial >= 1 && t.PaginaFinal >= t.PaginaInicial && t.PaginaFinal <= numeroPaginas)
            && ordenados.Zip(ordenados.Skip(1)).All(par => par.First.PaginaFinal < par.Second.PaginaInicial);
    }

    private async Task<ResultadoTramoCreado> CrearTramoAsync(
        CrearDocumentosDesdeDivisionCommand request, IPdfParaDividir pdf, TramoACrear tramo, CancellationToken cancellationToken)
    {
        // Con otro Tenant activo, no se ocupa un turno del proceso aparte para un tramo que no se va a guardar.
        if (!TenantSigueSiendoElEsperado(request))
            throw new TenantCambiadoException();

        // La cota es la del alta: un tramo que no cabría como Documento se corta mientras se escribe, sin ocupar más.
        Result<byte[]> extraido;
        try
        {
            extraido = await pdf.ExtraerPaginasAsync(tramo.PaginaInicial, tramo.PaginaFinal, LimitesArchivoSubido.TamanoMaximoBytes, cancellationToken);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or PresupuestoMemoriaAgotadoException
                                       or EntradaPdfRetenidaException or OutOfMemoryException))
        {
            // Lo que depende del PDF llega tipado en el Result; lo que lanza es un fallo de la instancia (arrancar el proceso
            // aparte, sus tuberías), y reintentarlo con cada tramo que falta solo ocuparía el turno compartido.
            logger.LogWarning("Dividir PDF cortó el lote: no se pudo usar el proceso aparte ({Excepcion}).", ex.GetType().Name);
            throw new CorteDelLoteException(
                Resultado(tramo, EstadoTramoCreado.Rechazado, null, "No se ha podido preparar este documento.", ErroresDivisorPdf.FalloInterno),
                DetalleTrasFalloInterno);
        }

        if (!extraido.EsExitoso)
        {
            if (extraido.Error.Codigo == ErroresDivisorPdf.DemasiadoComplejo)
            {
                logger.LogWarning("Dividir PDF cortó el lote: el proceso aparte no pudo con el PDF.");
                throw new CorteDelLoteException(
                    Resultado(tramo, EstadoTramoCreado.Rechazado, null, extraido.Error.Mensaje, extraido.Error.Codigo),
                    DetalleTrasPdfDemasiadoComplejo);
            }

            return extraido.Error.Codigo == ErroresDivisorPdf.DemasiadoGrande
                ? Resultado(tramo, EstadoTramoCreado.DemasiadoGrande, null, null, extraido.Error.Codigo)
                : Resultado(tramo, EstadoTramoCreado.Rechazado, null, extraido.Error.Mensaje, extraido.Error.Codigo);
        }

        if (extraido.Valor.Length > LimitesArchivoSubido.TamanoMaximoBytes)
            return Resultado(tramo, EstadoTramoCreado.DemasiadoGrande, null, null, ErroresDivisorPdf.DemasiadoGrande);

        // La comprobación del inicio quedó detrás de las esperas de la apertura y de la extracción. Entre esta y la lectura
        // del Tenant activo que hace el almacenamiento al entrar en GuardarAsync no hay ninguna espera.
        if (!TenantSigueSiendoElEsperado(request))
            throw new TenantCambiadoException();

        using var flujo = new MemoryStream(extraido.Valor, writable: false);
        var archivoUrl = await almacen.GuardarAsync(flujo, NombreDelTramo(request.NombreOriginal, tramo), cancellationToken);

        // Tras la espera del guardado, y antes de dar de alta: el alta también sella con el Tenant activo. Si ya no es el
        // esperado, el archivo se retira con el Tenant activo, que es el único con el que el almacenamiento lo encuentra si
        // se llegó a escribir bajo él; si se escribió bajo el esperado, no lo encuentra y queda huérfano en su propio Tenant.
        if (!TenantSigueSiendoElEsperado(request))
        {
            logger.LogWarning("Dividir PDF no da de alta un tramo ya guardado: el Tenant activo cambió durante el guardado.");
            await EliminarConElTenantActivoAsync(archivoUrl);
            throw new TenantCambiadoException();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // El alta aún no ha empezado: el archivo no tiene Documento.
            await EliminarSinOcultarAsync(request, archivoUrl);
            cancellationToken.ThrowIfCancellationRequested();
        }

        Result<Guid> creado;
        try
        {
            creado = await mediator.Send(ComandoDeAlta(request, tramo, archivoUrl), cancellationToken);
        }
        catch (ValidationException ex)
        {
            await EliminarSinOcultarAsync(request, archivoUrl);
            return Resultado(tramo, EstadoTramoCreado.Rechazado, null, ex.Errors.FirstOrDefault()?.ErrorMessage, ErrorAltaNoValida);
        }
        catch (Exception ex)
        {
            // El alta guarda antes de publicar su evento: el Documento pudo quedar creado y necesita su archivo. También si
            // se canceló a mitad: el lote se corta en el siguiente tramo, y este se da por pendiente de comprobar.
            logger.LogWarning("Dividir PDF no pudo confirmar un alta ({Excepcion}).", ex.GetType().Name);
            return Resultado(tramo, EstadoTramoCreado.SinConfirmar, null, null, ErrorAltaSinConfirmar);
        }

        if (creado.EsExitoso)
            return Resultado(tramo, EstadoTramoCreado.Creado, creado.Valor, null);

        await EliminarSinOcultarAsync(request, archivoUrl);
        return Resultado(tramo, EstadoTramoCreado.Rechazado, null, creado.Error.Mensaje, creado.Error.Codigo);
    }

    private static ResultadoTramoCreado Resultado(
        TramoACrear tramo, EstadoTramoCreado estado, Guid? documentoId, string? detalle, string? codigoError = null) =>
        new(tramo.PaginaInicial, tramo.PaginaFinal, estado, documentoId, detalle, CodigoError: codigoError);

    private static CrearDocumentoCommand ComandoDeAlta(
        CrearDocumentosDesdeDivisionCommand request, TramoACrear tramo, string archivoUrl)
    {
        // La Subcontrata es una fila de Empresas: su documentación va por EmpresaId, igual que la de una Empresa propia.
        Guid? trabajadorId = tramo.Clase == ClaseTitular.Trabajador ? tramo.TitularId : null;
        Guid? vehiculoId = tramo.Clase == ClaseTitular.Vehiculo ? tramo.TitularId : null;
        Guid? empresaId = tramo.Clase is ClaseTitular.EmpresaGestionada or ClaseTitular.Subcontrata ? tramo.TitularId : null;

        return new CrearDocumentoCommand(
            trabajadorId, null, empresaId, vehiculoId, null, tramo.TipoDocumentoId, tramo.FechaEmision,
            tramo.FechaVencimientoManual, archivoUrl, Comentario(tramo), tramo.NoCaduca);
    }

    // Sin el nombre del PDF: lo pone el usuario, puede llevar datos personales y un Documento de Empresa se lee con más alcance.
    private static string Comentario(TramoACrear tramo) =>
        $"Creado con Dividir: páginas {tramo.PaginaInicial}-{tramo.PaginaFinal} del PDF original.";

    private static string NombreDelTramo(string nombreOriginal, TramoACrear tramo) =>
        NombresTramoPdf.De(nombreOriginal, tramo.PaginaInicial, tramo.PaginaFinal);

    /// <summary>
    /// Retira el archivo de un tramo que no llegó a ser Documento. El almacenamiento resuelve el archivo con el Tenant activo:
    /// si ya no es el esperado, no lo encontraría (el archivo se escribió bajo el esperado) y el borrado callaría el fallo,
    /// así que no se intenta y se registra el huérfano, que queda en su propio Tenant.
    /// </summary>
    private async Task EliminarSinOcultarAsync(CrearDocumentosDesdeDivisionCommand request, string archivoUrl)
    {
        if (!TenantSigueSiendoElEsperado(request))
        {
            logger.LogWarning("Dividir PDF no retira el archivo de un tramo sin Documento: el Tenant activo cambió; queda huérfano en el Tenant del asistente.");
            return;
        }

        await EliminarConElTenantActivoAsync(archivoUrl);
    }

    private async Task EliminarConElTenantActivoAsync(string archivoUrl)
    {
        try
        {
            await almacen.EliminarAsync(archivoUrl, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // El fallo que importa es el del alta; un archivo huérfano se registra sin taparlo.
            logger.LogWarning(ex, "No se pudo retirar el archivo de un tramo de Dividir que no llegó a ser Documento.");
        }
    }
}

/// <summary>
/// Guarda en TALVEG los tramos de un PDF dividido desde la pantalla, con un <see cref="CrearDocumentosDesdeDivisionCommand"/>
/// por tramo (<see cref="CrearDocumentosDesdeDivisionCommand.Indice"/>). Cada Command pasa por la
/// <see cref="PuertaAccesoDatos"/> del circuito y la suelta al acabar su tramo, así que la revalidación del circuito, que
/// espera en esa misma puerta, entra entre un tramo y el siguiente; y cada Command vuelve a comprobar, antes de abrir el PDF,
/// el Tenant esperado, la Empresa en la cartera y el catálogo de titulares. Una revocación corta el lote en el tramo
/// siguiente a la revalidación, en vez de esperar a que acabe el lote entero. Lo que la puerta sigue reteniendo es un tramo:
/// abrirlo y extraerlo en el proceso aparte, guardarlo y darlo de alta, cada espera con su propio tope.
///
/// Conserva lo que hacía el lote en un solo Command: un fallo antes de crear nada falla igual (y la cancelación o la falta de
/// turno lanzan, para que la pantalla avise y conserve el original); con algún Documento creado o sin confirmar, cualquier
/// corte (Tenant cambiado, Empresa o titular que dejan de valer, cancelación, sin turno, proceso aparte agotado o inservible)
/// devuelve lo ya creado y marca los que faltan como no intentados, con la entrada retenida si el proceso aparte no consta
/// terminado.
///
/// No es un request de MediatR, a propósito: si lo fuera, retendría la puerta durante todo el lote, y por lo mismo quien lo
/// llame no puede estar ya dentro de ella (la puerta es reentrante por flujo y los Commands no la soltarían).
/// </summary>
public static class CreacionDivisionPdfPorTramos
{
    public static async Task<Result<IReadOnlyList<ResultadoTramoCreado>>> CrearAsync(
        IMediator mediator, byte[] contenidoPdf, string nombreOriginal, Guid tenantEsperadoId, Guid empresaGestionadaId,
        IReadOnlyList<TramoACrear> tramos, ILogger logger, CancellationToken cancellationToken)
    {
        var resultados = new List<ResultadoTramoCreado>(tramos.Count);
        while (resultados.Count < tramos.Count)
        {
            var indice = resultados.Count;
            string detalle;
            string causa;
            var retenida = false;
            try
            {
                var creacion = await mediator.Send(
                    new CrearDocumentosDesdeDivisionCommand(contenidoPdf, nombreOriginal, tenantEsperadoId, empresaGestionadaId, tramos, indice),
                    cancellationToken);
                if (creacion.EsExitoso)
                {
                    // El tramo y, si el lote se cortó en él, los que faltan.
                    if (creacion.Valor.Count == 0)
                        throw new InvalidOperationException("Dividir PDF: un Command de tramo no devolvió ese tramo.");
                    resultados.AddRange(creacion.Valor);
                    continue;
                }

                if (!HayDocumentosCreados(resultados))
                    return creacion;

                // Lo comprobado al empezar el tramo dejó de valer: la revalidación del circuito cambió el Tenant o la cartera
                // entre dos tramos, o cambió el catálogo.
                logger.LogWarning("Dividir PDF cortó el lote antes de un tramo ({Codigo}).", creacion.Error.Codigo);
                causa = creacion.Error.Codigo;
                detalle = causa == CrearDocumentosDesdeDivisionCommandHandler.ErrorTenantCruzado
                    ? CrearDocumentosDesdeDivisionCommandHandler.DetalleTenantCambiado
                    : creacion.Error.Mensaje;
            }
            catch (OperationCanceledException) when (HayDocumentosCreados(resultados))
            {
                logger.LogWarning("Dividir PDF cortó el lote al cancelarse la operación.");
                detalle = CrearDocumentosDesdeDivisionCommandHandler.DetalleCancelado;
                causa = CrearDocumentosDesdeDivisionCommandHandler.ErrorLoteCancelado;
            }
            catch (Exception ex) when (HayDocumentosCreados(resultados) && ex is PresupuestoMemoriaAgotadoException or EntradaPdfRetenidaException)
            {
                logger.LogWarning("Dividir PDF cortó el lote sin turno en el proceso aparte ({Excepcion}).", ex.GetType().Name);
                retenida = ex is EntradaPdfRetenidaException;
                detalle = CrearDocumentosDesdeDivisionCommandHandler.DetalleSinTurno;
                causa = CrearDocumentosDesdeDivisionCommandHandler.ErrorSinTurno;
            }

            resultados.AddRange(CrearDocumentosDesdeDivisionCommandHandler.Restantes(tramos.Skip(indice), detalle, causa, retenida));
        }

        return Result.Exito<IReadOnlyList<ResultadoTramoCreado>>(resultados);
    }

    private static bool HayDocumentosCreados(List<ResultadoTramoCreado> resultados) =>
        resultados.Exists(r => r.Estado is EstadoTramoCreado.Creado or EstadoTramoCreado.SinConfirmar);
}
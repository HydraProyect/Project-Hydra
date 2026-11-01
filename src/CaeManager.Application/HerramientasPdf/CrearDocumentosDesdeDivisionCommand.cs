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
    Rechazado = 2
}

public record ResultadoTramoCreado(
    int PaginaInicial, int PaginaFinal, EstadoTramoCreado Estado, Guid? DocumentoId, string? Detalle);

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
public record CrearDocumentosDesdeDivisionCommand(
    byte[] ContenidoPdf, string NombreOriginal, Guid TenantEsperadoId, Guid EmpresaGestionadaId,
    IReadOnlyList<TramoACrear> Tramos) : ICommand<IReadOnlyList<ResultadoTramoCreado>>;

public class CrearDocumentosDesdeDivisionCommandValidator : AbstractValidator<CrearDocumentosDesdeDivisionCommand>
{
    public CrearDocumentosDesdeDivisionCommandValidator()
    {
        RuleFor(c => c.ContenidoPdf).NotEmpty();
        RuleFor(c => c.ContenidoPdf.Length).LessThanOrEqualTo((int)LimitesDivisionPdf.TamanoMaximoOriginalBytes);
        RuleFor(c => c.TenantEsperadoId).NotEmpty();
        RuleFor(c => c.EmpresaGestionadaId).NotEmpty();
        RuleFor(c => c.Tramos).NotEmpty().WithMessage("No hay documentos que crear.");
    }
}

public class CrearDocumentosDesdeDivisionCommandHandler(
    IDisponibilidadHerramientasPdf disponibilidad,
    ITenantActual tenantActual,
    IMediator mediator,
    IDivisorPdf divisor,
    IFileStorageService almacen,
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
            return Fallo(ErrorTenantCruzado, "La Empresa gestionada ha cambiado. Vuelve a empezar para no crear documentos en otra.");

        var catalogo = await mediator.Send(new ObtenerCatalogoDivisionPdfQuery(request.EmpresaGestionadaId), cancellationToken);
        if (!catalogo.EsExitoso)
            return Result.Fallo<IReadOnlyList<ResultadoTramoCreado>>(catalogo.Error);

        if (request.Tramos.Any(t => !catalogo.Valor.Contiene(t.Clase, t.TitularId)))
            return Fallo(ErrorTitularAjeno, "Un documento se asigna a alguien que no pertenece a esta Empresa gestionada ni a sus Subcontratas.");

        var abierto = divisor.Abrir(request.ContenidoPdf);
        if (!abierto.EsExitoso)
            return Result.Fallo<IReadOnlyList<ResultadoTramoCreado>>(abierto.Error);

        using var pdf = abierto.Valor;

        if (!TramosCaben(request.Tramos, pdf.NumeroPaginas))
            return Fallo(ErrorTramosInvalidos, "Las páginas de los documentos no coinciden con el PDF.");

        var resultados = new List<ResultadoTramoCreado>(request.Tramos.Count);
        foreach (var tramo in request.Tramos)
            resultados.Add(await CrearTramoAsync(request, pdf, tramo, cancellationToken));

        return Result.Exito<IReadOnlyList<ResultadoTramoCreado>>(resultados);
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
        var extraido = pdf.ExtraerPaginas(tramo.PaginaInicial, tramo.PaginaFinal);
        if (!extraido.EsExitoso)
            return Resultado(tramo, EstadoTramoCreado.Rechazado, null, extraido.Error.Mensaje);

        if (extraido.Valor.Length > LimitesArchivoSubido.TamanoMaximoBytes)
            return Resultado(tramo, EstadoTramoCreado.DemasiadoGrande, null, null);

        using var flujo = new MemoryStream(extraido.Valor, writable: false);
        var archivoUrl = await almacen.GuardarAsync(flujo, NombreDelTramo(request.NombreOriginal, tramo), cancellationToken);

        Result<Guid> creado;
        try
        {
            creado = await mediator.Send(ComandoDeAlta(request, tramo, archivoUrl), cancellationToken);
        }
        catch
        {
            await EliminarSinOcultarAsync(archivoUrl);
            throw;
        }

        if (creado.EsExitoso)
            return Resultado(tramo, EstadoTramoCreado.Creado, creado.Valor, null);

        await EliminarSinOcultarAsync(archivoUrl);
        return Resultado(tramo, EstadoTramoCreado.Rechazado, null, creado.Error.Mensaje);
    }

    private static ResultadoTramoCreado Resultado(TramoACrear tramo, EstadoTramoCreado estado, Guid? documentoId, string? detalle) =>
        new(tramo.PaginaInicial, tramo.PaginaFinal, estado, documentoId, detalle);

    private static CrearDocumentoCommand ComandoDeAlta(
        CrearDocumentosDesdeDivisionCommand request, TramoACrear tramo, string archivoUrl)
    {
        // La Subcontrata es una fila de Empresas: su documentación va por EmpresaId, igual que la de una Empresa propia.
        Guid? trabajadorId = tramo.Clase == ClaseTitular.Trabajador ? tramo.TitularId : null;
        Guid? vehiculoId = tramo.Clase == ClaseTitular.Vehiculo ? tramo.TitularId : null;
        Guid? empresaId = tramo.Clase is ClaseTitular.EmpresaGestionada or ClaseTitular.Subcontrata ? tramo.TitularId : null;

        return new CrearDocumentoCommand(
            trabajadorId, null, empresaId, vehiculoId, null, tramo.TipoDocumentoId, tramo.FechaEmision,
            tramo.FechaVencimientoManual, archivoUrl, Comentario(request.NombreOriginal, tramo), tramo.NoCaduca);
    }

    private static string Comentario(string nombreOriginal, TramoACrear tramo)
    {
        var texto = $"Creado con Dividir: páginas {tramo.PaginaInicial}-{tramo.PaginaFinal} de {Path.GetFileName(nombreOriginal)}.";
        return texto.Length <= Documento.LongitudMaximaComentarios ? texto : texto[..Documento.LongitudMaximaComentarios];
    }

    private static string NombreDelTramo(string nombreOriginal, TramoACrear tramo) =>
        $"{Path.GetFileNameWithoutExtension(nombreOriginal)}-pag-{tramo.PaginaInicial}-{tramo.PaginaFinal}.pdf";

    private async Task EliminarSinOcultarAsync(string archivoUrl)
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

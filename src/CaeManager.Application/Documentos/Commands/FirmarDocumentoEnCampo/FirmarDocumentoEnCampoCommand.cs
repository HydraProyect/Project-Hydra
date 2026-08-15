using System.Security.Cryptography;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Eventos;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Documentos.Commands.FirmarDocumentoEnCampo;

/// <summary>
/// Estampa una firma en campo (trazo manuscrito capturado en pantalla) sobre
/// el archivo actual de un Documento — capacidad genérica sobre Documento,
/// no atada a ningún formato concreto (plan Fase A de firma en campo).
/// </summary>
public record FirmarDocumentoEnCampoCommand(Guid DocumentoId, string TrazoPngBase64, string? Ubicacion) : ICommand;

public class FirmarDocumentoEnCampoCommandValidator : AbstractValidator<FirmarDocumentoEnCampoCommand>
{
    // Anti-abuso: un trazo dibujado a mano en un canvas HTML5 no debería
    // acercarse ni de lejos a este límite en base64.
    private const int LongitudMaximaTrazoBase64 = 2_000_000;

    public FirmarDocumentoEnCampoCommandValidator()
    {
        RuleFor(c => c.DocumentoId).NotEmpty();
        RuleFor(c => c.TrazoPngBase64).NotEmpty().MaximumLength(LongitudMaximaTrazoBase64);
        RuleFor(c => c.Ubicacion).MaximumLength(FirmaEnCampoDocumento.LongitudMaximaUbicacion);
    }
}

public class FirmarDocumentoEnCampoCommandHandler(
    IDocumentoRepository repositorio,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAlcanceDatosService alcanceDatos,
    IProyectosQueryContext proyectosContext,
    IFirmaDigitalDocumentoRepository firmasDigitalesRepositorio,
    IFirmaEnCampoDocumentoRepository firmasEnCampoRepositorio,
    IFileStorageService almacenamiento,
    IEstampadoFirmaEnCampoPdfService estampador,
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    IPublisher publisher,
    IUnitOfWork unitOfWork,
    ILogger<FirmarDocumentoEnCampoCommandHandler> logger)
    : IRequestHandler<FirmarDocumentoEnCampoCommand, Result>
{
    public async Task<Result> Handle(FirmarDocumentoEnCampoCommand request, CancellationToken cancellationToken)
    {
        var documento = await repositorio.ObtenerPorIdAsync(request.DocumentoId, cancellationToken);
        if (documento is null || !await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
            return Result.Fallo(Error.Crear("Documento.NoEncontrado", "No encontramos este documento."));

        if (documento.ArchivoUrl is null)
            return Result.Fallo(Error.Crear("Documento.SinArchivo", "Este documento no tiene ningún archivo que firmar."));

        var tipoDocumento = await tiposDocumentoContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == documento.TipoDocumentoId, cancellationToken);
        if (tipoDocumento is null)
            return Result.Fallo(Error.Crear("Documento.TipoDocumentoNoEncontrado", "No encontramos el tipo de documento asociado."));

        // Guarda crítica: PdfSharp no soporta incremental update — estampar
        // reescribe el PDF entero, así que firmar en campo un documento
        // oficial destruiría una firma criptográfica real (TGSS/AEAT).
        if (tipoDocumento.PerfilDocumentoOficial != PerfilDocumentoOficial.Ninguno)
            return Result.Fallo(Error.Crear(
                "Documento.FirmaEnCampoBloqueadaPorPerfilOficial",
                "Este documento es un formato oficial de la Administración: firmarlo en campo destruiría su firma digital. No se puede firmar en campo."));

        // Defensa en profundidad: aunque el tipo no tenga perfil oficial hoy,
        // el archivo actual puede llevar una firma digital válida de antes.
        var firmasDigitales = await firmasDigitalesRepositorio.ObtenerPorDocumentoAsync(documento.Id, cancellationToken);
        if (firmasDigitales.Any(f => f.Estado == EstadoFirmaPdf.Valida))
            return Result.Fallo(Error.Crear(
                "Documento.FirmaEnCampoBloqueadaPorFirmaExistente",
                "Este archivo ya lleva una firma digital válida: firmarlo en campo la destruiría. No se puede firmar en campo."));

        byte[] trazoPng;
        try
        {
            trazoPng = Convert.FromBase64String(request.TrazoPngBase64);
        }
        catch (FormatException)
        {
            return Result.Fallo(Error.Crear("FirmaEnCampo.TrazoInvalido", "El trazo de la firma no es una imagen válida."));
        }

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is not { } firmanteUsuarioId)
            return Result.Fallo(Error.Crear("FirmaEnCampo.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        var nombres = await directorioUsuarios.ObtenerNombresVisiblesAsync([firmanteUsuarioId], cancellationToken);
        var firmanteNombre = nombres.GetValueOrDefault(firmanteUsuarioId, string.Empty);
        var firmanteRol = await currentUserService.ObtenerRolActualAsync() ?? string.Empty;

        await using var flujoOriginal = await almacenamiento.AbrirAsync(documento.ArchivoUrl, cancellationToken);
        using var memoriaOriginal = new MemoryStream();
        await flujoOriginal.CopyToAsync(memoriaOriginal, cancellationToken);
        var pdfOriginal = memoriaOriginal.ToArray();

        var firmadoEnUtc = DateTime.UtcNow;
        var contenidoFirmado = estampador.Estampar(
            pdfOriginal, trazoPng, firmanteNombre, firmanteRol, firmadoEnUtc, request.Ubicacion);
        var hash = Convert.ToHexStringLower(SHA256.HashData(contenidoFirmado));

        var urlAnterior = documento.ArchivoUrl;
        using var flujoFirmado = new MemoryStream(contenidoFirmado);
        var nuevaUrl = await almacenamiento.GuardarAsync(flujoFirmado, "documento-firmado.pdf", cancellationToken);
        documento.AdjuntarArchivo(nuevaUrl);

        firmasEnCampoRepositorio.Agregar(new FirmaEnCampoDocumento(
            documento.Id, firmanteUsuarioId, firmanteNombre, firmanteRol, firmadoEnUtc, request.Ubicacion, hash));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // El ArchivoUrl cambió: un expediente de Visita puede depender de este
        // documento. Mismo patrón que RenovarDocumentoCommandHandler.
        await publisher.Publish(new DocumentacionCambiadaEvent(documento.Id), cancellationToken);

        try
        {
            await almacenamiento.EliminarAsync(urlAnterior, cancellationToken);
        }
        catch (Exception ex)
        {
            // No se aborta la operación por no poder borrar el archivo
            // anterior: la firma ya está confirmada. Queda constancia porque
            // un archivo huérfano es justo lo que esto existe para evitar.
            logger.LogError(ex,
                "No se pudo borrar el archivo anterior {Archivo} del documento {DocumentoId} tras firmar en campo.",
                urlAnterior, documento.Id);
        }

        return Result.Exito();
    }
}

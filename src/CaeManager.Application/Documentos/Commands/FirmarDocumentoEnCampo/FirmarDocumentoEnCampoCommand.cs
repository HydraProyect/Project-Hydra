using System.Security.Cryptography;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.GuardarFirmaGuardadaUsuario;
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
/// Estampa una firma en campo (trazo manuscrito capturado en pantalla, o la
/// firma guardada del usuario) sobre el archivo actual de un Documento, con
/// un sello de empresa opcional — capacidad genérica sobre Documento, no
/// atada a ningún formato concreto (plan Fase A de firma en campo).
/// </summary>
public record FirmarDocumentoEnCampoCommand(
    Guid DocumentoId,
    string? TrazoPngBase64,
    string? Ubicacion,
    bool UsarFirmaGuardada = false,
    Guid? SelloEmpresaId = null,
    bool GuardarComoFirmaHabitual = false) : ICommand;

public class FirmarDocumentoEnCampoCommandValidator : AbstractValidator<FirmarDocumentoEnCampoCommand>
{
    // Anti-abuso: un trazo dibujado a mano en un canvas HTML5 no debería
    // acercarse ni de lejos a este límite en base64.
    private const int LongitudMaximaTrazoBase64 = 2_000_000;

    public FirmarDocumentoEnCampoCommandValidator()
    {
        RuleFor(c => c.DocumentoId).NotEmpty();

        RuleFor(c => c.TrazoPngBase64).MaximumLength(LongitudMaximaTrazoBase64);
        RuleFor(c => c)
            .Must(c => c.UsarFirmaGuardada || !string.IsNullOrEmpty(c.TrazoPngBase64))
            .WithMessage("Hace falta un trazo dibujado o usar la firma guardada.")
            .WithName(nameof(FirmarDocumentoEnCampoCommand.TrazoPngBase64));

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
    IFirmaGuardadaUsuarioRepository firmaGuardadaRepositorio,
    ISelloEmpresaRepository selloEmpresaRepositorio,
    IFileStorageService almacenamiento,
    IEstampadoFirmaEnCampoPdfService estampador,
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    IMediator mediator,
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

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is not { } firmanteUsuarioId)
            return Result.Fallo(Error.Crear("FirmaEnCampo.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        byte[] firmaPng;
        var trazoDibujadoEnEstaFirma = !request.UsarFirmaGuardada;

        if (request.UsarFirmaGuardada)
        {
            var firmaGuardada = await firmaGuardadaRepositorio.ObtenerPorUsuarioAsync(firmanteUsuarioId, cancellationToken);
            if (firmaGuardada is null)
                return Result.Fallo(Error.Crear(
                    "FirmaEnCampo.SinFirmaGuardada", "No tienes ninguna firma guardada. Dibújala o súbela primero en 'Mi firma'."));

            await using var flujoFirmaGuardada = await almacenamiento.AbrirAsync(firmaGuardada.ImagenUrl, cancellationToken);
            using var memoriaFirmaGuardada = new MemoryStream();
            await flujoFirmaGuardada.CopyToAsync(memoriaFirmaGuardada, cancellationToken);
            firmaPng = memoriaFirmaGuardada.ToArray();
        }
        else
        {
            try
            {
                firmaPng = Convert.FromBase64String(request.TrazoPngBase64!);
            }
            catch (FormatException)
            {
                return Result.Fallo(Error.Crear("FirmaEnCampo.TrazoInvalido", "El trazo de la firma no es una imagen válida."));
            }
        }

        // Autorización del sello (auditoría de seguridad del módulo,
        // 2026-08-30): SelloEmpresaId llega del cliente sin comprobar antes
        // que esa Empresa esté en la cartera del usuario — sin este gate, un
        // operador podía estampar sobre un documento visible el sello de
        // cualquier otra Empresa del tenant. Distinto del caso "ya no hay
        // sello guardado" (se borró entre que se cargó el selector y se
        // firmó): ese sigue firmando sin sello, porque no es un problema de
        // autorización — pero un SelloEmpresaId fuera de cartera SÍ debe
        // fallar explícitamente, nunca continuar en silencio sin él.
        byte[]? selloPng = null;
        if (request.SelloEmpresaId is { } empresaIdDelSello)
        {
            if (!await alcanceDatos.EmpresaVisibleAsync(empresaIdDelSello, cancellationToken))
                return Result.Fallo(Error.Crear("FirmaEnCampo.SelloNoAutorizado", "No tienes acceso a este sello de empresa."));

            var selloEmpresa = await selloEmpresaRepositorio.ObtenerPorEmpresaAsync(empresaIdDelSello, cancellationToken);
            if (selloEmpresa is not null)
            {
                await using var flujoSello = await almacenamiento.AbrirAsync(selloEmpresa.ImagenUrl, cancellationToken);
                using var memoriaSello = new MemoryStream();
                await flujoSello.CopyToAsync(memoriaSello, cancellationToken);
                selloPng = memoriaSello.ToArray();
            }
        }

        var nombres = await directorioUsuarios.ObtenerNombresVisiblesAsync([firmanteUsuarioId], cancellationToken);
        var firmanteNombre = nombres.GetValueOrDefault(firmanteUsuarioId, string.Empty);
        var firmanteRol = await currentUserService.ObtenerRolActualAsync() ?? string.Empty;

        await using var flujoOriginal = await almacenamiento.AbrirAsync(documento.ArchivoUrl, cancellationToken);
        using var memoriaOriginal = new MemoryStream();
        await flujoOriginal.CopyToAsync(memoriaOriginal, cancellationToken);
        var pdfOriginal = memoriaOriginal.ToArray();

        var firmadoEnUtc = DateTime.UtcNow;
        var contenidoFirmado = estampador.Estampar(
            pdfOriginal, firmaPng, selloPng, firmanteNombre, firmanteRol, firmadoEnUtc, request.Ubicacion);
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

        // Solo tiene sentido guardar como firma habitual un trazo nuevo — si
        // ya se usó la firma guardada, no hay nada que actualizar.
        if (request.GuardarComoFirmaHabitual && trazoDibujadoEnEstaFirma)
        {
            var resultadoGuardar = await mediator.Send(
                new GuardarFirmaGuardadaUsuarioCommand(firmaPng, EsImagenDibujada: true), cancellationToken);
            if (resultadoGuardar.EsFallido)
            {
                logger.LogWarning(
                    "No se pudo guardar la firma habitual del usuario {UsuarioId} tras firmar el documento {DocumentoId}: {Motivo}",
                    firmanteUsuarioId, documento.Id, resultadoGuardar.Error.Mensaje);
            }
        }

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

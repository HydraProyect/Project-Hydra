using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Documentos.Commands.GuardarFirmaGuardadaUsuario;

/// <summary>
/// Guarda o reemplaza la firma habitual del usuario actual (plan Fase A de
/// firma en campo) — un usuario, una firma guardada. Si viene dibujada
/// (trazo de canvas, ya PNG con fondo transparente) no se recorta fondo; si
/// viene subida (foto de una firma en papel) sí, vía IConversorImagenSelloService.
/// </summary>
public record GuardarFirmaGuardadaUsuarioCommand(byte[] ImagenOriginal, bool EsImagenDibujada) : ICommand;

public class GuardarFirmaGuardadaUsuarioCommandValidator : AbstractValidator<GuardarFirmaGuardadaUsuarioCommand>
{
    private const int TamanoMaximoBytes = 5 * 1024 * 1024;

    public GuardarFirmaGuardadaUsuarioCommandValidator()
    {
        RuleFor(c => c.ImagenOriginal).NotEmpty();
        RuleFor(c => c.ImagenOriginal.Length).LessThanOrEqualTo(TamanoMaximoBytes)
            .WithMessage("La imagen no puede superar los 5 MB.");
    }
}

public class GuardarFirmaGuardadaUsuarioCommandHandler(
    IFirmaGuardadaUsuarioRepository repositorio,
    IConversorImagenSelloService conversor,
    IFileStorageService almacenamiento,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork,
    ILogger<GuardarFirmaGuardadaUsuarioCommandHandler> logger)
    : IRequestHandler<GuardarFirmaGuardadaUsuarioCommand, Result>
{
    public async Task<Result> Handle(GuardarFirmaGuardadaUsuarioCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is not { } usuarioIdValido)
            return Result.Fallo(Error.Crear("FirmaGuardadaUsuario.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        var pngNormalizado = conversor.NormalizarAPng(request.ImagenOriginal, recortarFondoClaro: !request.EsImagenDibujada);

        var ahora = DateTime.UtcNow;
        using var flujo = new MemoryStream(pngNormalizado);
        var nuevaUrl = await almacenamiento.GuardarAsync(flujo, "firma.png", cancellationToken);

        var existente = await repositorio.ObtenerPorUsuarioAsync(usuarioIdValido, cancellationToken);
        string? urlAnterior = null;

        if (existente is null)
        {
            repositorio.Agregar(new FirmaGuardadaUsuario(usuarioIdValido, nuevaUrl, ahora));
        }
        else
        {
            urlAnterior = existente.ImagenUrl;
            existente.Reemplazar(nuevaUrl, ahora);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (urlAnterior is not null)
        {
            try
            {
                await almacenamiento.EliminarAsync(urlAnterior, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "No se pudo borrar la imagen anterior {Archivo} de la firma guardada del usuario {UsuarioId}.",
                    urlAnterior, usuarioIdValido);
            }
        }

        return Result.Exito();
    }
}

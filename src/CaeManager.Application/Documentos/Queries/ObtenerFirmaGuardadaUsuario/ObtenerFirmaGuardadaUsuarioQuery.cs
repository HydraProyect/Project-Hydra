using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerFirmaGuardadaUsuario;

/// <summary>Firma guardada del usuario actual, o null si todavía no ha configurado ninguna.</summary>
public record ObtenerFirmaGuardadaUsuarioQuery : IRequest<FirmaGuardadaUsuarioDto?>;

public record FirmaGuardadaUsuarioDto(string ImagenUrl, DateTime ActualizadaEnUtc);

public class ObtenerFirmaGuardadaUsuarioQueryHandler(
    IDocumentosQueryContext documentosContext, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerFirmaGuardadaUsuarioQuery, FirmaGuardadaUsuarioDto?>
{
    public async Task<FirmaGuardadaUsuarioDto?> Handle(
        ObtenerFirmaGuardadaUsuarioQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is not { } usuarioIdValido) return null;

        return await documentosContext.FirmasGuardadasUsuario
            .Where(f => f.UsuarioId == usuarioIdValido)
            .Select(f => new FirmaGuardadaUsuarioDto(f.ImagenUrl, f.ActualizadaEnUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }
}

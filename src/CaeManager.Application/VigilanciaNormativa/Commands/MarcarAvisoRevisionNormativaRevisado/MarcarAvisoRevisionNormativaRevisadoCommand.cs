using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.VigilanciaNormativa;
using MediatR;

namespace CaeManager.Application.VigilanciaNormativa.Commands.MarcarAvisoRevisionNormativaRevisado;

/// <summary>
/// Cierra el ciclo que <see cref="AvisoRevisionNormativa.MarcarRevisado"/> ya
/// modelaba en el dominio sin que nada lo invocara (H-3). Exclusiva del Actor
/// de Plataforma TALVEG — <c>PuedeGlobalmenteAsync</c>, el mismo predicado que
/// <c>EsAdministradorPlataformaQuery</c> — porque revisar es decidir si una
/// publicación del BOE afecta al catálogo de formatos
/// (CATALOGO_FORMATOS_PRL.md), que mantiene TALVEG, no un tenant. Un tenant
/// beneficiario lee el aviso (DEC-8) pero no puede resolverlo: la
/// distinción de audiencia vive aquí, no en la lectura.
/// </summary>
public record MarcarAvisoRevisionNormativaRevisadoCommand(Guid Id, string? Notas) : ICommand;

public class MarcarAvisoRevisionNormativaRevisadoCommandHandler(
    IAvisoRevisionNormativaRepository repositorio,
    IAutorizacionAdminPlataforma autorizacion,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<MarcarAvisoRevisionNormativaRevisadoCommand, Result>
{
    public async Task<Result> Handle(MarcarAvisoRevisionNormativaRevisadoCommand request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("AvisoRevisionNormativa.SinUsuario", "No pudimos identificarte. Vuelve a iniciar sesión."));

        // Global y sin tenant: no hay "tenant objetivo" que comprobar, igual
        // que ObtenerEstadoComercialTenantsQuery. Solo PuedeGlobalmenteAsync,
        // nunca PuedeSobreTenantAsync — una concesión acotada a un tenant no
        // debe poder resolver un catálogo que no es de ningún tenant.
        if (!await autorizacion.PuedeGlobalmenteAsync(usuarioId.Value, cancellationToken))
            return Result.Fallo(Error.Crear(
                "AvisoRevisionNormativa.NoAutorizado", "No tienes autorización para revisar avisos normativos."));

        var aviso = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (aviso is null)
            return Result.Fallo(Error.Crear("AvisoRevisionNormativa.NoEncontrado", "No encontramos este aviso."));

        aviso.MarcarRevisado(usuarioId.Value, request.Notas, DateTime.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

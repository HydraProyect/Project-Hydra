using CaeManager.Application.Common;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using MediatR;

namespace CaeManager.Application.Operaciones.ApoyoCartera.Commands;

/// <summary>
/// El destinatario de una propuesta de apoyo la rechaza, y solo él: ni quien la propuso (que la
/// retira con <see cref="RetirarPropuestaApoyoCarteraCommand"/>) ni un tercero. No emite ni
/// cierra nada.
/// </summary>
public record RechazarPropuestaApoyoCarteraCommand(Guid PropuestaId) : ICommand;

public class RechazarPropuestaApoyoCarteraCommandHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    ICatalogoIncorporacionCartera catalogo,
    IPropuestaApoyoCarteraRepository repositorio)
    : IRequestHandler<RechazarPropuestaApoyoCarteraCommand, Result>
{
    public async Task<Result> Handle(RechazarPropuestaApoyoCarteraCommand request, CancellationToken cancellationToken)
    {
        var contexto = await ContextoOperadorCae.ResolverAsync(currentUserService, directorioUsuarios, cancellationToken);
        if (contexto.EsFallido || contexto.Valor.Rol is null)
            return Result.Fallo(ErroresPropuestaApoyo.SinPermiso);
        var ctx = contexto.Valor;

        using (ctx.EnOrigen())
        {
            var propuesta = await repositorio.ObtenerPorIdAsync(request.PropuestaId, ctx.OperadorTenantId, cancellationToken);
            if (propuesta is null || propuesta.DestinatarioUsuarioId != ctx.UsuarioId)
                return Result.Fallo(ErroresPropuestaApoyo.NoEncontrada);
            if (propuesta.Estado != EstadoPropuestaApoyoCartera.Pendiente)
                return Result.Fallo(ErroresPropuestaApoyo.YaResuelta);

            propuesta.Rechazar(ctx.UsuarioId, DateTime.UtcNow);

            // Quien la propuso la retiró a la vez: la versión deja pasar un solo cambio.
            return await catalogo.GuardarDetectandoCarreraAsync(cancellationToken)
                ? Result.Exito()
                : Result.Fallo(ErroresPropuestaApoyo.YaResuelta);
        }
    }
}

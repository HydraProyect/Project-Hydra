using CaeManager.Application.Common;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using MediatR;

namespace CaeManager.Application.Operaciones.ApoyoCartera.Commands;

/// <summary>
/// Quien propuso un apoyo retira la propuesta mientras sigue pendiente, y solo él. Es lo único
/// que puede hacer con ella: aceptar y rechazar son del destinatario. No toca ninguna cartera:
/// retirar un apoyo ya aceptado es otra operación.
/// </summary>
public record RetirarPropuestaApoyoCarteraCommand(Guid PropuestaId) : ICommand;

public class RetirarPropuestaApoyoCarteraCommandHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    ICatalogoIncorporacionCartera catalogo,
    IPropuestaApoyoCarteraRepository repositorio)
    : IRequestHandler<RetirarPropuestaApoyoCarteraCommand, Result>
{
    public async Task<Result> Handle(RetirarPropuestaApoyoCarteraCommand request, CancellationToken cancellationToken)
    {
        var contexto = await ContextoOperadorCae.ResolverAsync(currentUserService, directorioUsuarios, cancellationToken);
        if (contexto.EsFallido || contexto.Valor.Rol is null)
            return Result.Fallo(ErroresPropuestaApoyo.SinPermiso);
        var ctx = contexto.Valor;

        using (ctx.EnOrigen())
        {
            var propuesta = await repositorio.ObtenerPorIdAsync(request.PropuestaId, ctx.OperadorTenantId, cancellationToken);
            if (propuesta is null || propuesta.ProponenteUsuarioId != ctx.UsuarioId)
                return Result.Fallo(ErroresPropuestaApoyo.NoEncontrada);
            if (propuesta.Estado != EstadoPropuestaApoyoCartera.Pendiente)
                return Result.Fallo(ErroresPropuestaApoyo.YaResuelta);

            propuesta.Retirar(ctx.UsuarioId, DateTime.UtcNow);

            // El destinatario respondió a la vez: la versión deja pasar un solo cambio.
            return await catalogo.GuardarDetectandoCarreraAsync(cancellationToken)
                ? Result.Exito()
                : Result.Fallo(ErroresPropuestaApoyo.YaResuelta);
        }
    }
}

using CaeManager.Application.Common;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using MediatR;

namespace CaeManager.Application.Operaciones.ApoyoCartera.Commands;

/// <summary>
/// El Gestor CAE principal —o el Coordinador CAE principal— de una Asignación de Operación
/// propone a otro Gestor CAE de su mismo Operador CAE una Asignación de Cartera de apoyo sobre
/// ese Tenant propietario (ADR-011 § 2.7, enmienda 2026-10-08). <b>No concede nada</b>: la
/// propuesta queda pendiente hasta que el destinatario la acepte.
///
/// <para>
/// El comando lleva la operación y la persona, y nada más: el rol de la cartera (Gestor CAE),
/// su ámbito (el Tenant entero) y la marca de principal (nunca) no son parámetros de quien
/// propone. La Operación nunca concede roles de Propiedad.
/// </para>
///
/// <para>
/// <b>Autoridad</b>, leída en Identity sobre el Tenant de origen y nunca en el claim: cuenta
/// activa con rol Gestor CAE o Coordinador CAE del Operador CAE, que lleve hoy la marca de
/// principal en una cartera vigente de esa operación. <b>Destinatario</b>: otra cuenta activa
/// con rol Gestor CAE <b>del mismo Operador CAE</b>, sin ese Tenant en su cartera y sin otra
/// propuesta pendiente. <b>Operación</b>: externa, no raíz, del Tenant entero, vigente y con la
/// delegación al Operador CAE viva.
/// </para>
/// </summary>
public record ProponerApoyoCarteraCommand(Guid AsignacionOperacionId, Guid DestinatarioUsuarioId) : ICommand<Guid>;

public class ProponerApoyoCarteraCommandHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    ICatalogoIncorporacionCartera catalogo,
    IPropuestaApoyoCarteraRepository repositorio)
    : IRequestHandler<ProponerApoyoCarteraCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ProponerApoyoCarteraCommand request, CancellationToken cancellationToken)
    {
        var contexto = await ContextoOperadorCae.ResolverAsync(currentUserService, directorioUsuarios, cancellationToken);
        if (contexto.EsFallido || !contexto.Valor.ParticipaEnIncorporacionCartera)
            return Result.Fallo<Guid>(ErroresPropuestaApoyo.SinPermiso);
        var ctx = contexto.Valor;

        if (request.DestinatarioUsuarioId == ctx.UsuarioId)
            return Result.Fallo<Guid>(ErroresPropuestaApoyo.DestinatarioNoValido);

        using (ctx.EnOrigen())
        {
            // Solo las carteras del propio Operador CAE: una operación de otro no devuelve
            // nada y quien pregunta «no es el principal», sin revelar si la operación existe.
            var carteras = await catalogo.ObtenerCarterasVivasAsync(ctx.OperadorTenantId, null, cancellationToken);
            var principal = carteras.FirstOrDefault(
                c => c.AsignacionOperacionId == request.AsignacionOperacionId && c.EsPrincipal);
            if (principal is null || principal.UsuarioId != ctx.UsuarioId)
                return Result.Fallo<Guid>(ErroresPropuestaApoyo.NoEresElPrincipal);

            // El destinatario es de ESTE Operador CAE: la cuenta se busca en el Tenant de
            // origen de quien propone, no en el suyo. Un Gestor CAE de otro Operador CAE no
            // recibe propuestas, aunque exista y esté activo.
            if (!await directorioUsuarios.EsCuentaActivaConRolAsync(
                    request.DestinatarioUsuarioId, ctx.OperadorTenantId, ContextoOperadorCae.RolGestorCae, cancellationToken))
                return Result.Fallo<Guid>(ErroresPropuestaApoyo.DestinatarioNoValido);

            // La operación tiene que seguir pudiéndose poner en una cartera (externa, del
            // Tenant entero, vigente, con delegación viva)...
            var asignables = await catalogo.ObtenerAsignablesAsync(ctx.OperadorTenantId, cancellationToken);
            if (asignables.All(a => a.AsignacionOperacionId != request.AsignacionOperacionId))
                return Result.Fallo<Guid>(ErroresPropuestaApoyo.OperacionNoDisponible);

            // ...y el destinatario no puede tener ya ese Tenant: proponerle entrar donde ya
            // está solo ensancharía en silencio el alcance que otro decidió.
            var candidatos = await catalogo.ObtenerCandidatosAsync(
                ctx.OperadorTenantId, request.DestinatarioUsuarioId, cancellationToken);
            if (candidatos.All(c => c.AsignacionOperacionId != request.AsignacionOperacionId))
                return Result.Fallo<Guid>(ErroresPropuestaApoyo.DestinatarioYaEnCartera);

            if (await repositorio.ExistePendienteAsync(
                    request.AsignacionOperacionId, request.DestinatarioUsuarioId, cancellationToken))
                return Result.Fallo<Guid>(ErroresPropuestaApoyo.YaPendiente);

            var operacion = await catalogo.ObtenerOperacionVigenteAsync(request.AsignacionOperacionId, cancellationToken);
            if (operacion is null || operacion.OperadorTenantId != ctx.OperadorTenantId)
                return Result.Fallo<Guid>(ErroresPropuestaApoyo.OperacionNoDisponible);

            var propuesta = PropuestaApoyoCartera.Crear(
                operacion, ctx.UsuarioId, request.DestinatarioUsuarioId, vigenciaHastaPropuesta: null, DateTime.UtcNow);
            repositorio.Agregar(propuesta);

            // Dos propuestas a la vez al mismo destinatario: el índice único de pendientes deja pasar una.
            if (!await catalogo.GuardarDetectandoCarreraAsync(cancellationToken))
                return Result.Fallo<Guid>(ErroresPropuestaApoyo.YaPendiente);

            return Result.Exito(propuesta.Id);
        }
    }
}

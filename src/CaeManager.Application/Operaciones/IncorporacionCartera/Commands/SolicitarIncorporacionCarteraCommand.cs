using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using MediatR;

namespace CaeManager.Application.Operaciones.IncorporacionCartera.Commands;

/// <summary>
/// Un Gestor CAE pide incorporarse a la cartera de un Tenant propietario
/// entero que su Operador CAE ya opera (contrato de Mi trabajo Gen2
/// multi-Tenant § 13). No concede nada: la solicitud queda Pendiente hasta que
/// un Coordinador CAE del mismo Operador CAE la resuelva.
/// </summary>
public record SolicitarIncorporacionCarteraCommand(Guid TenantPropietarioId, string Mensaje) : ICommand<Guid>;

public class SolicitarIncorporacionCarteraCommandHandler(
    ICurrentUserService currentUserService,
    ICatalogoIncorporacionCartera catalogo,
    ISolicitudIncorporacionCarteraRepository repositorio)
    : IRequestHandler<SolicitarIncorporacionCarteraCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(SolicitarIncorporacionCarteraCommand request, CancellationToken cancellationToken)
    {
        var contexto = await ContextoOperadorCae.ResolverAsync(currentUserService);
        if (contexto.EsFallido) return Result.Fallo<Guid>(contexto.Error);
        var ctx = contexto.Valor;

        // Solo un Gestor CAE pide para sí. Un Coordinador CAE no se solicita
        // cartera a sí mismo: la resolvería otro Coordinador CAE por él, y su
        // rol ya le da otra vía de reparto.
        if (!ctx.EsGestorCae)
            return Result.Fallo<Guid>(ErroresSolicitudCartera.SinPermiso);

        string mensaje;
        try
        {
            mensaje = SolicitudIncorporacionCartera.NormalizarMensaje(request.Mensaje);
        }
        catch (ArgumentException)
        {
            return Result.Fallo<Guid>(ErroresSolicitudCartera.MensajeInvalido);
        }

        using (ctx.EnOrigen())
        {
            // El Tenant pedido tiene que salir de la lista de candidatos de ESTE
            // usuario, no del parámetro a secas: un Guid de un Tenant que su
            // Operador CAE no opera, o que ya tiene en cartera, no es candidato.
            var candidatos = await catalogo.ObtenerCandidatosAsync(ctx.OperadorTenantId, ctx.UsuarioId, cancellationToken);
            var candidato = candidatos.FirstOrDefault(c => c.PropietarioTenantId == request.TenantPropietarioId);
            if (candidato is null)
                return Result.Fallo<Guid>(ErroresSolicitudCartera.TenantNoCandidato);

            if (await repositorio.ExistePendienteAsync(candidato.AsignacionOperacionId, ctx.UsuarioId, cancellationToken))
                return Result.Fallo<Guid>(ErroresSolicitudCartera.YaPendiente);

            var operacion = await catalogo.ObtenerOperacionVigenteAsync(candidato.AsignacionOperacionId, cancellationToken);
            if (operacion is null)
                return Result.Fallo<Guid>(ErroresSolicitudCartera.TenantNoCandidato);

            var solicitud = SolicitudIncorporacionCartera.Crear(operacion, ctx.UsuarioId, mensaje, DateTime.UtcNow);
            repositorio.Agregar(solicitud);

            // Dos envíos a la vez: el índice único de pendientes deja pasar uno.
            if (!await catalogo.GuardarDetectandoCarreraAsync(cancellationToken))
                return Result.Fallo<Guid>(ErroresSolicitudCartera.YaPendiente);

            return Result.Exito(solicitud.Id);
        }
    }
}

using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard;

public record ReabrirMesCuadroDireccionCommand(Guid TenantBeneficiarioId, int Anyo, int Mes, string Motivo) : ICommand;

public class ReabrirMesCuadroDireccionCommandHandler(
    ICurrentUserService usuario, ICierresCuadroDireccion contexto, IUnitOfWork unidad,
    IAlcanceDatosService alcance, IMediator mediator, TimeProvider reloj) : IRequestHandler<ReabrirMesCuadroDireccionCommand, Result>
{
    public async Task<Result> Handle(ReabrirMesCuadroDireccionCommand request, CancellationToken cancellationToken)
    {
        if (await usuario.ObtenerRolEfectivoAsync() != "DireccionCae")
            return Result.Fallo(Error.Crear("CuadroDireccion.SinPermiso", "Solo Dirección CAE puede reabrir un mes."));
        if (string.IsNullOrWhiteSpace(request.Motivo) || request.Motivo.Length > 500)
            return Result.Fallo(Error.Crear("CuadroDireccion.MotivoObligatorio", "Indica el motivo de la reapertura, de hasta 500 caracteres."));
        var autorizados = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);
        if (!autorizados.Any(t => t.TenantId == request.TenantBeneficiarioId))
            return Result.Fallo(Error.Crear("CuadroDireccion.NoDisponible", "No encontramos este cierre mensual."));
        using var ambito = AmbitoTenantExplicito.Establecer(request.TenantBeneficiarioId);
        if (!await alcance.AlcanzaElTenantEnteroAsync(cancellationToken))
            return Result.Fallo(Error.Crear("CuadroDireccion.SinPermiso", "La reapertura requiere acceso a toda la organización."));
        var cierre = await contexto.CierresMensuales.SingleOrDefaultAsync(c => c.Anyo == request.Anyo && c.Mes == request.Mes, cancellationToken);
        if (cierre is null || cierre.ReabiertoEnUtc is not null)
            return Result.Fallo(Error.Crear("CuadroDireccion.NoDisponible", "No encontramos un mes cerrado disponible para reabrir."));
        cierre.Reabrir(request.Motivo, reloj.GetUtcNow().UtcDateTime);
        await unidad.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}

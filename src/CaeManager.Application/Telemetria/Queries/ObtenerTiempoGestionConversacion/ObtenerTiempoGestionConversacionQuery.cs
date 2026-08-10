using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Telemetria.Queries.ObtenerTiempoGestionConversacion;

/// <summary>
/// Lo que el medidor necesita al abrir un hilo: si la medición está activa, cuánto
/// tiempo lleva ya acumulado <b>esta persona</b> en esta gestión y con qué umbral de
/// inactividad trabajar.
///
/// El total es solo del usuario actual a propósito: la transparencia que exige el
/// art. 87 LOPDGDD es que cada quien vea su propia medición, no la de sus compañeros.
/// </summary>
public record ObtenerTiempoGestionConversacionQuery(Guid ConversacionId) : IRequest<TiempoGestionConversacionDto>;

public record TiempoGestionConversacionDto(bool MedicionActiva, int SegundosAcumulados, int SegundosInactividadPausa);

public class ObtenerTiempoGestionConversacionQueryHandler(
    ITelemetriaQueryContext telemetriaContext,
    IConfiguracionQueryContext configuracionContext,
    ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerTiempoGestionConversacionQuery, TiempoGestionConversacionDto>
{
    public async Task<TiempoGestionConversacionDto> Handle(ObtenerTiempoGestionConversacionQuery request, CancellationToken cancellationToken)
    {
        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        if (!parametros.MedicionTiempoActiva)
            return new TiempoGestionConversacionDto(false, 0, parametros.SegundosInactividadPausa);

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return new TiempoGestionConversacionDto(false, 0, parametros.SegundosInactividadPausa);

        var acumulado = await telemetriaContext.RegistrosTiempoGestion
            .Where(r => r.ConversacionId == request.ConversacionId && r.UsuarioId == usuarioId.Value)
            .SumAsync(r => (int?)r.SegundosActivos, cancellationToken) ?? 0;

        return new TiempoGestionConversacionDto(true, acumulado, parametros.SegundosInactividadPausa);
    }
}

using CaeManager.Application.Common;
using CaeManager.Domain.AsistenteIa;
using CaeManager.Domain.Common;

namespace CaeManager.Application.AsistenteIa.Tareas;

/// <summary>
/// El esqueleto común de los Commands que cambian una tarea existente: resolver
/// a la persona, cargar <b>su</b> tarea (nunca la de otra, aunque sea del mismo
/// Tenant), aplicar el cambio del dominio y guardar. Las reglas de transición
/// son del dominio; aquí solo se traducen sus rechazos a <see cref="Result"/>.
/// </summary>
public sealed class ModificacionTareaAsistente(
    ITareaAsistenteRepository repositorio,
    IActorAuditoria actorAuditoria,
    ICurrentUserService currentUserService,
    ITenantActual tenantActual,
    IUnitOfWork unitOfWork)
{
    public async Task<Result> AplicarAsync(
        Guid tareaId,
        Guid? versionEsperada,
        Action<TareaAsistente, PersonaTareaAsistente> cambio,
        CancellationToken cancellationToken)
    {
        var persona = await ResolucionPersonaTareaAsistente.ResolverAsync(actorAuditoria, currentUserService, tenantActual);
        if (persona.EsFallido)
            return Result.Fallo(persona.Error);

        var tarea = await repositorio.ObtenerDePersonaAsync(tareaId, persona.Valor.ActorRealUsuarioId, cancellationToken);
        if (tarea is null)
            return Result.Fallo(ResolucionPersonaTareaAsistente.NoEncontrada());

        if (versionEsperada is { } version
            && ConcurrenciaOptimista.Verificar(tarea, version, "esta tarea del asistente") is { } conflicto)
            return Result.Fallo(conflicto);

        try
        {
            cambio(tarea, persona.Valor);
        }
        catch (InvalidOperationException ex)
        {
            return Result.Fallo(ResolucionPersonaTareaAsistente.TransicionNoValida(ex.Message));
        }
        catch (ArgumentException ex)
        {
            return Result.Fallo(Error.Crear("TareaAsistente.DatosNoValidos", ex.Message));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}

namespace CaeManager.Application.Operaciones.Traspasos;

public interface ITraspasosCarteraWriter
{
    /// <summary>Traspasa una asignación sin mover datos del Tenant propietario. Una asignación del destinatario ya existente no se modifica.</summary>
    Task<bool> TraspasarAsync(Guid operacionId, Guid propietarioTenantId, Guid operadorTenantId,
        Guid origenUsuarioId, Guid destinoUsuarioId, Guid actorUsuarioId, bool mantenerApoyo,
        Guid? coberturaAusenciaId, CancellationToken ct);

    /// <summary>Cierra solo las asignaciones creadas para esta cobertura, nunca las preexistentes.</summary>
    Task RetirarCoberturaAsync(Guid ausenciaId, Guid operacionId, Guid propietarioTenantId,
        Guid operadorTenantId, Guid actorUsuarioId, CancellationToken ct);

}

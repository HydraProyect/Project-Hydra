namespace CaeManager.Domain.Inbound.ControlAcceso;

/// <summary>Los Centros de portería de cada usuario con el rol «Control de acceso». Lee y escribe solo en el Tenant activo.</summary>
public interface IUsuarioControlAccesoCentroRepository
{
    Task<IReadOnlyList<UsuarioControlAccesoCentro>> ObtenerDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    void Agregar(UsuarioControlAccesoCentro asignacion);

    void Retirar(UsuarioControlAccesoCentro asignacion);
}

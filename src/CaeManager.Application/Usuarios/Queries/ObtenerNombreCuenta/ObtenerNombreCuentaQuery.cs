using MediatR;

namespace CaeManager.Application.Usuarios.Queries.ObtenerNombreCuenta;

/// <summary>Lo único que la presentación necesita de otra cuenta para pintarla.</summary>
public sealed record NombreCuentaDto(string NombreCompleto, string? Email);

/// <summary>
/// Proyección de la cuenta sin sus columnas de credenciales (ASVS 4.0.3 V4.1.3). Pasa por
/// la RLS de <c>AspNetUsers</c>: una cuenta que el contexto actual no ve da <c>null</c>,
/// igual que una que no existe.
/// </summary>
public interface ILectorNombreCuenta
{
    Task<NombreCuentaDto?> ObtenerAsync(Guid usuarioId, CancellationToken cancellationToken);
}

public sealed record ObtenerNombreCuentaQuery(Guid UsuarioId) : IRequest<NombreCuentaDto?>;

public sealed class ObtenerNombreCuentaQueryHandler(ILectorNombreCuenta lector)
    : IRequestHandler<ObtenerNombreCuentaQuery, NombreCuentaDto?>
{
    public Task<NombreCuentaDto?> Handle(ObtenerNombreCuentaQuery request, CancellationToken cancellationToken) =>
        lector.ObtenerAsync(request.UsuarioId, cancellationToken);
}

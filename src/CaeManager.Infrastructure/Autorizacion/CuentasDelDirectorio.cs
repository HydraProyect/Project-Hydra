namespace CaeManager.Infrastructure.Autorizacion;

/// <summary>
/// Lo que un selector necesita de una cuenta visible en un rol. Es una lectura:
/// no sustituye a la cuenta y nada se guarda a partir de ella.
/// </summary>
public sealed record CuentaVisibleEnRol(Guid Id, string NombreCompleto, string? Email, string? Avatar);

/// <summary>Lo que la lista de pendientes de <c>/roles</c> pinta de una cuenta propia sin rol.</summary>
public sealed record CuentaPropiaSinRol(Guid Id, string? Email, string NombreCompleto, DateTime FechaCreacion);

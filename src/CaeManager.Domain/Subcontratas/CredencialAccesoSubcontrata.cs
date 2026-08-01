using CaeManager.Domain.Common;

namespace CaeManager.Domain.Subcontratas;

/// <summary>
/// Credenciales de una Subcontrata para acceder a una plataforma externa de
/// gestión documental. Relación 1:1 con Subcontrata — mismo patrón que
/// CredencialAccesoEmpresa.
///
/// Dato sensible: Usuario/Contrasena se persisten cifrados en reposo
/// mediante un ValueConverter de EF Core en Infrastructure — este tipo de
/// dominio no conoce el cifrado, solo modela el valor en texto plano en
/// memoria durante el ciclo de vida de la petición.
/// </summary>
public class CredencialAccesoSubcontrata : CredencialAccesoPortal
{
    public Guid SubcontrataId { get; private set; }

    private CredencialAccesoSubcontrata()
    {
    }

    public CredencialAccesoSubcontrata(Guid subcontrataId, string? urlAcceso, string? campoEmpresa, string? usuario, string? contrasena, string? notas = null)
    {
        if (subcontrataId == Guid.Empty)
            throw new ArgumentException("Las credenciales deben pertenecer a una subcontrata.", nameof(subcontrataId));

        SubcontrataId = subcontrataId;
        Actualizar(urlAcceso, campoEmpresa, usuario, contrasena, notas);
    }
}

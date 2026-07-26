using CaeManager.Domain.Common;

namespace CaeManager.Domain.Subcontratas;

/// <summary>
/// Asociación entre una Subcontrata y el Cliente que la contrató
/// directamente — mismo patrón que EmpresaCliente, un nivel más abajo. Sin
/// ciclo de vida propio: desvincular es una baja física, no un soft delete.
/// </summary>
public class SubcontrataCliente : EntidadConTenant
{
    public Guid SubcontrataId { get; private set; }
    public Guid ClienteId { get; private set; }

    private SubcontrataCliente()
    {
    }

    public SubcontrataCliente(Guid subcontrataId, Guid clienteId)
    {
        if (subcontrataId == Guid.Empty)
            throw new ArgumentException("La asociación debe tener una subcontrata.", nameof(subcontrataId));
        if (clienteId == Guid.Empty)
            throw new ArgumentException("La asociación debe tener un cliente.", nameof(clienteId));

        SubcontrataId = subcontrataId;
        ClienteId = clienteId;
    }
}

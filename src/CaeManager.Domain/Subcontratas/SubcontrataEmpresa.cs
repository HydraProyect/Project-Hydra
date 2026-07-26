using CaeManager.Domain.Common;

namespace CaeManager.Domain.Subcontratas;

/// <summary>
/// Asociación entre una Subcontrata y la Empresa a la que presta servicio —
/// una Subcontrata puede prestar servicio a varias Empresas, y una Empresa
/// puede tener varias Subcontratas. Mismo patrón que EmpresaCliente. Sin
/// ciclo de vida propio: desvincular es una baja física, no un soft delete.
/// </summary>
public class SubcontrataEmpresa : EntidadConTenant
{
    public Guid SubcontrataId { get; private set; }
    public Guid EmpresaId { get; private set; }

    private SubcontrataEmpresa()
    {
    }

    public SubcontrataEmpresa(Guid subcontrataId, Guid empresaId)
    {
        if (subcontrataId == Guid.Empty)
            throw new ArgumentException("La asociación debe tener una subcontrata.", nameof(subcontrataId));
        if (empresaId == Guid.Empty)
            throw new ArgumentException("La asociación debe tener una empresa.", nameof(empresaId));

        SubcontrataId = subcontrataId;
        EmpresaId = empresaId;
    }
}

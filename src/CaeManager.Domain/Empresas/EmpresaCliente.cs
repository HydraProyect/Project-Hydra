using CaeManager.Domain.Common;

namespace CaeManager.Domain.Empresas;

/// <summary>
/// Asociación entre una Empresa y un Cliente que trabaja con ella — una
/// Empresa puede prestar servicio a varios Clientes, y un Cliente puede
/// recibir servicio de varias Empresas. Sin ciclo de vida propio (a
/// diferencia de Asignacion): desvincular es una baja física, no un soft
/// delete ni una fecha de baja.
/// </summary>
public class EmpresaCliente : EntidadConTenant
{
    public Guid EmpresaId { get; private set; }
    public Guid ClienteId { get; private set; }

    private EmpresaCliente()
    {
    }

    public EmpresaCliente(Guid empresaId, Guid clienteId)
    {
        if (empresaId == Guid.Empty)
            throw new ArgumentException("La asociación debe tener una empresa.", nameof(empresaId));
        if (clienteId == Guid.Empty)
            throw new ArgumentException("La asociación debe tener un cliente.", nameof(clienteId));

        EmpresaId = empresaId;
        ClienteId = clienteId;
    }
}

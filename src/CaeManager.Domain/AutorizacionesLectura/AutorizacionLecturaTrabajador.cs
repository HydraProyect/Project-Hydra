using CaeManager.Domain.Common;

namespace CaeManager.Domain.AutorizacionesLectura;

/// <summary>
/// Un Trabajador que la Empresa contratista asigna al contrato de una <see cref="AutorizacionLecturaDocumental"/>:
/// sus Documentos son los que el Tenant destinatario podrá leer. Las dos FK son compuestas con <c>TenantId</c>, así
/// que la autorización y el Trabajador son siempre del mismo Tenant. Sin UPDATE: asignar es insertar y retirar es
/// borrar, y las dos cosas solo mientras la autorización no esté revocada (lo repite un disparador en la base).
/// Se crea solo con <see cref="AutorizacionLecturaDocumental.AsignarTrabajador"/>.
/// </summary>
public class AutorizacionLecturaTrabajador : EntidadConTenant
{
    public Guid AutorizacionId { get; private set; }

    public Guid TrabajadorId { get; private set; }

    private AutorizacionLecturaTrabajador()
    {
        // Requerido por EF Core.
    }

    internal AutorizacionLecturaTrabajador(Guid autorizacionId, Guid trabajadorId)
    {
        AutorizacionId = autorizacionId;
        TrabajadorId = trabajadorId;
    }
}

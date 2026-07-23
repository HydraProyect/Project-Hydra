using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Restringe un Tipo de Documento a un Centro concreto — un Tipo sin
/// ninguna fila aquí aplica a todos los Centros (comportamiento global,
/// el de todo el catálogo semilla). Sin ciclo de vida propio (igual que
/// EmpresaCliente): desvincular es una baja física.
/// </summary>
public class TipoDocumentoCentro : EntidadConTenant
{
    public Guid TipoDocumentoId { get; private set; }
    public Guid CentroId { get; private set; }

    private TipoDocumentoCentro()
    {
    }

    public TipoDocumentoCentro(Guid tipoDocumentoId, Guid centroId)
    {
        if (tipoDocumentoId == Guid.Empty)
            throw new ArgumentException("La asociación debe tener un tipo de documento.", nameof(tipoDocumentoId));
        if (centroId == Guid.Empty)
            throw new ArgumentException("La asociación debe tener un centro.", nameof(centroId));

        TipoDocumentoId = tipoDocumentoId;
        CentroId = centroId;
    }
}

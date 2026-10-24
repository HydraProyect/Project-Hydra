using System.Linq.Expressions;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos;

public static class ReglaSubidaAcreditacion
{
    // Renovar deja una acreditación pendiente; un rechazo vigente no se puede reenviar.
    public static readonly Expression<Func<AcreditacionDocumentoPlataforma, bool>> SinRechazoVigente =
        acreditacion => acreditacion.Estado != EstadoAcreditacion.Rechazada;

    public static readonly Func<AcreditacionDocumentoPlataforma, bool> PermiteSubir = SinRechazoVigente.Compile();
}

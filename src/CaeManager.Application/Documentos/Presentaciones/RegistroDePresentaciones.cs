using CaeManager.Application.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Presentaciones;

/// <summary>
/// El único sitio que escribe una <see cref="PresentacionDocumentoEnCentro"/>: la fecha es el día de negocio de hoy
/// (<see cref="DiaDeNegocio.Hoy"/>) y el registro es <b>idempotente</b> por Documento + Centro + fecha + origen (marcar subida dos
/// veces el mismo día no escribe dos filas). No guarda: quien lo llama confirma el cambio con su propio
/// <c>SaveChangesAsync</c>, así que la presentación y el hecho que la motiva entran juntos o ninguno.
///
/// <para>
/// <b>No autoriza</b>: quien lo llama comprueba antes el alcance del Documento y del Centro. Una presentación es un hecho del
/// historial, nunca una regla: la que decide cuándo vence un Documento en un Centro es <c>ReglaBloqueoDeAcceso</c>.
/// </para>
/// </summary>
public interface IRegistroDePresentaciones
{
    /// <summary>Registra la presentación de hoy; devuelve <c>false</c> si ya constaba (mismo día y origen) y no escribió nada.</summary>
    Task<bool> RegistrarAsync(
        Guid documentoId, Guid centroId, OrigenPresentacionDocumentoEnCentro origen, CancellationToken cancellationToken);

    /// <summary>
    /// Registra la presentación de hoy al Centro del acceso de gestión documental dado (<c>CanalGestionDocumental.CentroId</c>: cada
    /// acceso es de un único Centro). Sin ese acceso, no hay a qué Centro atribuirla y no escribe nada.
    /// </summary>
    Task<bool> RegistrarEnElCentroDelAccesoAsync(
        Guid documentoId, Guid canalGestionDocumentalId, OrigenPresentacionDocumentoEnCentro origen, CancellationToken cancellationToken);
}

public class RegistroDePresentaciones(
    IPresentacionDocumentoEnCentroRepository repositorio, ICentrosQueryContext centrosContext) : IRegistroDePresentaciones
{
    public async Task<bool> RegistrarAsync(
        Guid documentoId, Guid centroId, OrigenPresentacionDocumentoEnCentro origen, CancellationToken cancellationToken)
    {
        var hoy = DiaDeNegocio.Hoy();
        if (await repositorio.ExisteAsync(documentoId, centroId, hoy, origen, cancellationToken))
            return false;

        repositorio.Agregar(new PresentacionDocumentoEnCentro(documentoId, centroId, hoy, origen, DateTime.UtcNow));
        return true;
    }

    public async Task<bool> RegistrarEnElCentroDelAccesoAsync(
        Guid documentoId, Guid canalGestionDocumentalId, OrigenPresentacionDocumentoEnCentro origen, CancellationToken cancellationToken)
    {
        var centroId = await centrosContext.CanalesGestionDocumental
            .Where(c => c.Id == canalGestionDocumentalId)
            .Select(c => (Guid?)c.CentroId)
            .FirstOrDefaultAsync(cancellationToken);

        return centroId is { } centro && await RegistrarAsync(documentoId, centro, origen, cancellationToken);
    }
}

using CaeManager.Domain.Retencion;

namespace CaeManager.Application.Retencion;

public interface IRetencionQueryContext
{
    IQueryable<SolicitudPurga> SolicitudesPurga { get; }
    IQueryable<DocumentoPropuestoPurga> DocumentosPropuestosPurga { get; }
}

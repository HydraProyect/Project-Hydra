using CaeManager.Domain.Common;

namespace CaeManager.Domain.Inbound.ContratosAcceso;

/// <summary>
/// Un cambio del fin contratado de un contrato de acceso (plan backend Inbound § 2.2, B-05): fin anterior y nuevo, qué
/// acto lo cambió, quién y cuándo. Vive en el Tenant TCE, el Tenant propietario del contrato, con la RLS estándar.
///
/// <para>
/// <b>Solo inserción.</b> Lo crea <see cref="ContratoAcceso"/> al renovar o al editar el fin, nunca otro código; nada lo
/// modifica. Solo lo borra la cascada de su FK (<c>ON DELETE CASCADE</c>) cuando se borra físicamente su contrato, cosa
/// que hoy solo hace la retirada de un Tenant de demostración con la identidad de bootstrap. La base lo repite: el runtime
/// tiene <c>SELECT</c> e <c>INSERT</c>, sin <c>UPDATE</c> ni <c>DELETE</c>, y tampoco puede borrar contratos.
/// </para>
/// </summary>
public class HistorialVigenciaContrato : EntidadConTenant
{
    public Guid ContratoAccesoId { get; private set; }
    public DateOnly? FechaFinAnterior { get; private set; }
    public DateOnly? FechaFinNueva { get; private set; }
    public OrigenCambioVigencia Origen { get; private set; }
    public Guid RegistradoPorUsuarioId { get; private set; }
    public DateTime RegistradoEnUtc { get; private set; }

    private HistorialVigenciaContrato()
    {
    }

    internal HistorialVigenciaContrato(
        Guid contratoAccesoId, DateOnly? fechaFinAnterior, DateOnly? fechaFinNueva, OrigenCambioVigencia origen,
        Guid registradoPorUsuarioId, DateTime registradoEnUtc)
    {
        if (contratoAccesoId == Guid.Empty)
            throw new ArgumentException("El cambio de vigencia debe pertenecer a un contrato de acceso.", nameof(contratoAccesoId));
        if (registradoPorUsuarioId == Guid.Empty)
            throw new ArgumentException("El cambio de vigencia debe registrar quién lo hizo.", nameof(registradoPorUsuarioId));
        if (fechaFinAnterior == fechaFinNueva)
            throw new ArgumentException("Un cambio de vigencia cambia el fin contratado.", nameof(fechaFinNueva));

        ContratoAccesoId = contratoAccesoId;
        FechaFinAnterior = fechaFinAnterior;
        FechaFinNueva = fechaFinNueva;
        Origen = origen;
        RegistradoPorUsuarioId = registradoPorUsuarioId;
        RegistradoEnUtc = registradoEnUtc;
    }
}

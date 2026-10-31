using CaeManager.Application.Common;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Retencion;

/// <summary>
/// Una pasada de la conservación de 5 años de los registros de auditoría
/// (<c>RegistrosAuditoria</c>, <c>RegistrosAccesoDocumentoSensible</c>,
/// <c>RegistrosActividadSoporte</c> y <c>AuditoriasExtraccionIa</c>), decidida el 2026-10-31.
///
/// <para>
/// La purga cruza Tenants y el rol de aplicación no puede hacerlo: no fija ningún Tenant ni
/// debilita RLS. Lo hace <c>app_purgar_registros_auditoria(boolean)</c>, cuya identidad explícita
/// es su propietario, con el corte escrito dentro. El runtime elige solo entre contar y borrar:
/// no puede adelantar el plazo ni borrar una fila concreta.
/// </para>
///
/// <para>
/// <b>Apagada por defecto.</b> Sin <see cref="RetencionDatosOptions.PurgaRegistrosActiva"/> solo
/// cuenta, y el recuento queda en el log para revisarlo antes de encenderla.
/// </para>
/// </summary>
public class PurgaRegistrosCaducados(
    CaeManagerDbContext db, IOptions<RetencionDatosOptions> opciones, ILogger<PurgaRegistrosCaducados> logger)
{
    /// <summary>Devuelve las filas borradas o, con la purga apagada, las que se borrarían.</summary>
    public async Task<int> EjecutarAsync(CancellationToken cancellationToken = default)
    {
        var simular = opciones.Value.PurgaRegistrosActiva && false;

        var filas = await db.Database
            .SqlQuery<int>($"SELECT public.app_purgar_registros_auditoria({simular}) AS \"Value\"")
            .SingleAsync(cancellationToken);

        if (filas > 0)
        {
            logger.LogInformation(
                simular
                    ? "Purga de auditoría apagada: {Filas} registros con más de 5 años se borrarían."
                    : "Purgados {Filas} registros de auditoría con más de 5 años.",
                filas);
        }

        return filas;
    }
}

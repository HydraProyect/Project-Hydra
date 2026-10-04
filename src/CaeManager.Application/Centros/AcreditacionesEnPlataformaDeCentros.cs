using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros;

/// <summary>Qué dice la plataforma del Cliente empresarial de un Documento en un Centro.</summary>
public enum VeredictoDePlataforma
{
    /// <summary>La vigencia anotada en la plataforma ya venció, aunque en TALVEG el documento siga vigente.</summary>
    VencidaEnPlataforma = 0,

    /// <summary>La plataforma rechazó la acreditación y el tipo es aplicable a ese Centro.</summary>
    Rechazada = 1
}

/// <summary>
/// Una acreditación de un Documento contra la plataforma de UN Centro cuyo veredicto impide acceder a ese Centro.
/// Con <paramref name="TrabajadorId"/> el sujeto es ese Trabajador; con <paramref name="EmpresaId"/> son todos los Trabajadores de
/// esa Empresa asignados al Centro; sin ninguno de los dos (un documento de otro ámbito) no hay a quién bloquear.
/// </summary>
/// <param name="VencimientoEnPlataforma">Cuándo venció allí; <c>null</c> en un rechazo, que no es un vencimiento de fecha.</param>
public sealed record AcreditacionEnPlataformaDeCentro(
    Guid CentroId, Guid DocumentoId, Guid TipoDocumentoId, string TipoDocumentoNombre,
    Guid? TrabajadorId, Guid? EmpresaId, VeredictoDePlataforma Veredicto, DateOnly? VencimientoEnPlataforma);

/// <summary>
/// <b>Punto único</b> de la lectura del veredicto de la plataforma del Cliente empresarial (D-7 del piloto Outbound): qué
/// acreditaciones de documentos operativos tienen la vigencia vencida allí o están rechazadas y son aplicables a su Centro. Lo
/// leen <see cref="EvaluacionDeAccesoPorCentroService"/> (que bloquea al Trabajador o a la Empresa afectados) y
/// <see cref="CalculoEstadoCentroService"/> (la causa «vencido en la plataforma» del Centro). Ninguno decide por su cuenta qué es
/// un veredicto de la plataforma; no se filtra por asignación ni por alcance, que es de quien lo usa.
///
/// <para>
/// <b>Vencida</b>: la condición es la fecha, no el estado de la acreditación. Si alguien anotó que allí vence el día tal y ese
/// día pasó, ya no vale esté la acreditación como esté. Una vigencia «sin confirmar» no cuenta: ponerla en rojo bloquearía a
/// todo el mundo el día que se despliega, porque las acreditaciones que ya existían nacen sin confirmar.
/// </para>
///
/// <para>
/// <b>Rechazada</b>: solo <c>Rechazada</c> (pendiente de subir y subida son trabajo y seguimiento, no un «no» de la plataforma), y
/// solo aplicable a ese Centro: el canal de ese Centro y un tipo que le aplique (fila explícita del Centro o, sin ella,
/// requerido por defecto, como en el resto del cálculo de cumplimiento). Rechazar reinicia la vigencia en plataforma, así que un
/// mismo Documento no cuenta como las dos a la vez; renovarlo reinicia la acreditación a Pendiente y retira el veredicto.
/// </para>
/// </summary>
internal static class AcreditacionesEnPlataformaDeCentros
{
    /// <summary>
    /// El filtro por Centros se compone sobre los canales, no se escribe como «null o Contains» dentro de la consulta: EF no
    /// traduce de forma fiable una comparación de la lista con null mezclada con Contains.
    /// </summary>
    private static IQueryable<CanalGestionDocumental> CanalesDe(ICentrosQueryContext centrosContext, IReadOnlyList<Guid>? centroIds)
    {
        var canales = centrosContext.CanalesGestionDocumental.AsQueryable();
        return centroIds is null ? canales : canales.Where(canal => centroIds.Contains(canal.CentroId));
    }

    /// <param name="centroIds">Limita la lectura a estos Centros; <c>null</c> = todos los del Tenant (RLS aísla el resto).</param>
    public static async Task<IReadOnlyList<AcreditacionEnPlataformaDeCentro>> LeerVencidasAsync(
        IDocumentosQueryContext documentosContext, ICentrosQueryContext centrosContext,
        ITiposDocumentoQueryContext tiposDocumentoContext, IReadOnlyList<Guid>? centroIds, DateOnly hoy,
        CancellationToken cancellationToken)
    {
        var canales = CanalesDe(centrosContext, centroIds);

        var vencidas = await (
            from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
            where acreditacion.EstadoVigencia == EstadoVigenciaEnPlataforma.VenceEnFecha
            where acreditacion.FechaVencimientoEnPlataforma != null
                  && acreditacion.FechaVencimientoEnPlataforma < hoy
            join canal in canales
                on acreditacion.CanalGestionDocumentalId equals canal.Id
            join documento in documentosContext.Documentos.Operativos()
                on acreditacion.DocumentoId equals documento.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento
                on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                canal.CentroId,
                DocumentoId = documento.Id,
                documento.TipoDocumentoId,
                documento.TrabajadorId,
                documento.EmpresaId,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                FechaVencimientoEnPlataforma = acreditacion.FechaVencimientoEnPlataforma!.Value
            })
            .ToListAsync(cancellationToken);

        return vencidas
            .Select(v => new AcreditacionEnPlataformaDeCentro(
                v.CentroId, v.DocumentoId, v.TipoDocumentoId, v.TipoDocumentoNombre, v.TrabajadorId, v.EmpresaId,
                VeredictoDePlataforma.VencidaEnPlataforma, v.FechaVencimientoEnPlataforma))
            .ToList();
    }

    /// <param name="centroIds">Limita la lectura a estos Centros; <c>null</c> = todos los del Tenant (RLS aísla el resto).</param>
    public static async Task<IReadOnlyList<AcreditacionEnPlataformaDeCentro>> LeerRechazadasAplicablesAsync(
        IDocumentosQueryContext documentosContext, ICentrosQueryContext centrosContext,
        ITiposDocumentoQueryContext tiposDocumentoContext, IReadOnlyList<Guid>? centroIds,
        CancellationToken cancellationToken)
    {
        var canales = CanalesDe(centrosContext, centroIds);

        var rechazadas = await (
            from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
            where acreditacion.Estado == EstadoAcreditacion.Rechazada
            join canal in canales
                on acreditacion.CanalGestionDocumentalId equals canal.Id
            join documento in documentosContext.Documentos.Operativos()
                on acreditacion.DocumentoId equals documento.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento
                on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                canal.CentroId,
                DocumentoId = documento.Id,
                documento.TipoDocumentoId,
                documento.TrabajadorId,
                documento.EmpresaId,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                CuentaParaCumplimiento = tipoDocumento.Requerido == RequisitoDocumental.Si
            })
            .ToListAsync(cancellationToken);

        if (rechazadas.Count == 0)
            return [];

        var tipoIds = rechazadas.Select(r => r.TipoDocumentoId).Distinct().ToList();
        var centrosDeRechazos = rechazadas.Select(r => r.CentroId).Distinct().ToList();
        var filasPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIds.Contains(tc.TipoDocumentoId) && centrosDeRechazos.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        // Misma regla de aplicabilidad que el resto del cálculo: una fila explícita del Centro manda; sin ella, solo cuenta
        // un tipo requerido por defecto. Un rechazo de un tipo opcional no bloquea, pero sigue visible como trabajo en la Bandeja.
        return rechazadas
            .Where(r => ResolucionTipoDocumentoCentro.Aplica(filasPorPar, r.TipoDocumentoId, r.CentroId, r.CuentaParaCumplimiento))
            .Select(r => new AcreditacionEnPlataformaDeCentro(
                r.CentroId, r.DocumentoId, r.TipoDocumentoId, r.TipoDocumentoNombre, r.TrabajadorId, r.EmpresaId,
                VeredictoDePlataforma.Rechazada, VencimientoEnPlataforma: null))
            .ToList();
    }
}

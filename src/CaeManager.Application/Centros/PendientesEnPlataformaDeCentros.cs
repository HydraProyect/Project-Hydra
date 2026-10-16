using CaeManager.Application.Asignaciones;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros;

/// <summary>
/// Un documento pendiente en la plataforma CAE de un Centro, ya filtrado por su contexto: una fila por Centro, sujeto y
/// tipo. El sujeto es <paramref name="TrabajadorId"/> (documento de Trabajador) o <paramref name="EmpresaId"/> (documento
/// de Empresa), nunca los dos.
/// </summary>
/// <param name="EstadoAcreditacion">El más atrasado de los accesos de plataforma del Centro: sin subir antes que subido.</param>
/// <param name="BloqueaAcceso">
/// El Centro marca este tipo como bloqueante (<see cref="TipoDocumentoCentro.BloqueaAcceso"/>, con el tipo incluido). Solo
/// entonces el Pendiente bloquea a personas y baja el porcentaje del Centro; sin la marca solo pone el Centro en «Pendiente».
/// </param>
public sealed record PendienteEnPlataformaDeCentro(
    Guid CentroId, Guid DocumentoId, Guid TipoDocumentoId, string TipoDocumentoNombre,
    Guid? TrabajadorId, string? TrabajadorNombre, Guid? EmpresaId,
    EstadoAcreditacion EstadoAcreditacion, DateOnly? FechaVencimiento, bool BloqueaAcceso);

/// <summary>
/// Carga única de «qué está pendiente en la plataforma de cada Centro» (<see cref="ReglaPendienteEnPlataforma"/>, decisión
/// del propietario del 2026-10-10). La usan el estado del Centro (<see cref="CalculoEstadoCentroService"/>, causa
/// <see cref="EstadoCentro.Pendiente"/>) y el acceso por Centro (<see cref="EvaluacionDeAccesoPorCentroService"/>, que
/// bloquea a las personas), para que las dos respuestas no puedan discrepar.
///
/// <para>
/// Contexto, el mismo que el del rechazo en plataforma: solo accesos de tipo Plataforma del Centro (uno sin plataforma
/// nunca tiene un Pendiente), documentos operativos, un tipo que le aplique al Centro
/// (<see cref="ResolucionTipoDocumentoCentro.Aplica"/>) y un sujeto con actividad allí — el Trabajador con Asignación
/// activa en ese Centro, o la Empresa con algún Trabajador suyo con Asignación activa en él (decisión del 2026-10-10: los
/// Trabajadores de la Empresa que cuentan son los asignados). La acreditación sobrevive a la baja y a que el tipo deje de
/// exigirse, por eso se vuelve a comprobar aquí.
/// </para>
///
/// <para>
/// Solo cuenta la acreditación del <b>documento efectivo</b> del sujeto y el tipo (<see cref="DocumentoEfectivo"/>, el mismo
/// que mide el porcentaje y el requisito): la de una copia duplicada que no manda no es trabajo pendiente. Y la vigencia es
/// la del <b>requisito en ese Centro</b> (<see cref="ReglaBloqueoDeAcceso.Evaluar"/> con la periodicidad especial y la
/// tolerancia del Centro, <see cref="VigenciaEnCentro.Condiciones"/>), no la fecha de vencimiento del documento: lo que
/// ya no vale allí es causa más grave y no es un Pendiente.
/// </para>
///
/// <para>
/// Lee solo con los contextos de consulta del Tenant en curso, bajo RLS: no cruza Tenants. El alcance de cartera lo
/// aplica quien elige los <c>centroIds</c>.
/// </para>
/// </summary>
internal static class PendientesEnPlataformaDeCentros
{
    public static async Task<IReadOnlyList<PendienteEnPlataformaDeCentro>> CargarAsync(
        ICentrosQueryContext centrosContext,
        IDocumentosQueryContext documentosContext,
        ITiposDocumentoQueryContext tiposDocumentoContext,
        ITrabajadoresQueryContext trabajadoresContext,
        IQueryable<CaeManager.Domain.Asignaciones.Asignacion> asignaciones,
        IReadOnlyCollection<Guid> centroIds,
        DateOnly hoy,
        CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0)
            return [];

        var estadosPendientes = ReglaPendienteEnPlataforma.EstadosPendientes.ToArray();
        var filas = await (
            from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
            where estadosPendientes.Contains(acreditacion.Estado)
            join canal in centrosContext.CanalesGestionDocumental
                on acreditacion.CanalGestionDocumentalId equals canal.Id
            where canal.Tipo == TipoCanalGestion.Plataforma && centroIds.Contains(canal.CentroId)
            join documento in documentosContext.Documentos.Operativos()
                on acreditacion.DocumentoId equals documento.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento
                on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                canal.CentroId,
                documento.Id,
                documento.TipoDocumentoId,
                documento.TrabajadorId,
                documento.EmpresaId,
                documento.EstadoVigencia,
                documento.FechaVencimiento,
                documento.FechaEmision,
                documento.CreadoEnUtc,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                CuentaParaCumplimiento = tipoDocumento.Requerido == RequisitoDocumental.Si,
                tipoDocumento.TiposContratacionDeSerie,
                EstadoAcreditacion = acreditacion.Estado
            })
            .ToListAsync(cancellationToken);

        if (filas.Count == 0)
            return [];

        // La Empresa del Trabajador, sea cual sea la columna que la guarda (AsignacionParaBloqueo).
        var asignacionesActivas = (await (
            from asignacion in asignaciones
            where asignacion.FechaBaja == null && centroIds.Contains(asignacion.CentroId)
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            select new
            {
                asignacion.CentroId,
                asignacion.TrabajadorId,
                trabajador.EmpresaId,
                trabajador.SubcontrataId,
                trabajador.TipoContratacion,
                Nombre = trabajador.Nombre + " " + trabajador.Apellidos
            })
            .ToListAsync(cancellationToken))
            .Select(a => new { a.CentroId, a.TrabajadorId, EmpresaDelTrabajadorId = a.EmpresaId ?? a.SubcontrataId, a.TipoContratacion, a.Nombre })
            .ToList();
        var trabajadoresPorCentro = asignacionesActivas
            .GroupBy(a => a.CentroId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.TrabajadorId).ToHashSet());
        var empresasPorCentro = asignacionesActivas
            .Where(a => a.EmpresaDelTrabajadorId is not null)
            .GroupBy(a => a.CentroId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.EmpresaDelTrabajadorId!.Value).ToHashSet());
        var nombres = asignacionesActivas
            .GroupBy(a => a.TrabajadorId)
            .ToDictionary(g => g.Key, g => g.First().Nombre);
        var tipoContratacionPorTrabajador = asignacionesActivas
            .GroupBy(a => a.TrabajadorId)
            .ToDictionary(g => g.Key, g => g.First().TipoContratacion);

        var tipoIds = filas.Select(f => f.TipoDocumentoId).Distinct().ToList();
        var filasPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIds.Contains(tc.TipoDocumentoId) && centroIds.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        // El documento efectivo de cada (sujeto, tipo): el mismo que elige el porcentaje y el requisito (DocumentoEfectivo).
        // Se traen todas las copias operativas de los sujetos y tipos con alguna acreditación pendiente, no solo las
        // acreditadas, para que una copia duplicada que no manda no cuente como pendiente.
        var trabajadorIds = filas.Where(f => f.TrabajadorId != null).Select(f => f.TrabajadorId!.Value).Distinct().ToList();
        var empresaIds = filas.Where(f => f.TrabajadorId == null && f.EmpresaId != null).Select(f => f.EmpresaId!.Value).Distinct().ToList();
        var copias = await documentosContext.Documentos.Operativos()
            .Where(d => tipoIds.Contains(d.TipoDocumentoId)
                && ((d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId!.Value))
                    || (d.EmpresaId != null && empresaIds.Contains(d.EmpresaId!.Value))))
            .Select(d => new { d.Id, d.TrabajadorId, d.EmpresaId, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision, d.CreadoEnUtc })
            .ToListAsync(cancellationToken);
        var efectivos = DocumentoEfectivo.UnoPorClave(
            copias, d => (d.TrabajadorId, d.EmpresaId, d.TipoDocumentoId), d => d.EstadoVigencia, d => d.FechaVencimiento,
            d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id, hoy);

        // La vigencia que cuenta es la del requisito en ESE Centro: periodicidad especial y tolerancia (VigenciaEnCentro).
        var centroIdsConFilas = filas.Select(f => f.CentroId).Distinct().ToList();
        var clientePorCentro = (await centrosContext.Centros
            .Where(c => centroIdsConFilas.Contains(c.Id))
            .Select(c => new { c.Id, Titular = c.ClienteId })
            .ToListAsync(cancellationToken))
            .ToDictionary(c => c.Id, c => c.Titular);
        var toleranciasDeClientes = await VigenciaEnCentro.CargarToleranciasDeClientesAsync(
            tiposDocumentoContext, clientePorCentro.Values.Distinct().ToList(), tipoIds, cancellationToken);

        var resultado = new List<PendienteEnPlataformaDeCentro>();
        var yaContados = new HashSet<(Guid CentroId, Guid? TrabajadorId, Guid? EmpresaId, Guid TipoDocumentoId)>();

        // Sin subir antes que subido: con dos accesos en distinto punto, cuenta lo que más falta. Por Id después, para
        // que el orden de salida no dependa del orden en que llegan las filas.
        foreach (var fila in filas.OrderBy(f => f.EstadoAcreditacion).ThenBy(f => f.Id))
        {
            // Solo el documento efectivo del sujeto y el tipo: la acreditación de una copia que no manda no cuenta.
            if (!efectivos.TryGetValue((fila.TrabajadorId, fila.EmpresaId, fila.TipoDocumentoId), out var efectivo) || efectivo.Id != fila.Id)
                continue;

            // ¿Vale el documento en este Centro? La regla del requisito (periodicidad y tolerancia del Centro), no su fecha propia.
            filasPorPar.TryGetValue((fila.TipoDocumentoId, fila.CentroId), out var filaDelCentro);
            var condiciones = VigenciaEnCentro.Condiciones(
                filaDelCentro,
                clientePorCentro.TryGetValue(fila.CentroId, out var titular)
                    && toleranciasDeClientes.TryGetValue((titular, fila.TipoDocumentoId), out var delCliente) ? delCliente : null);
            var requisitoEnElCentro = ReglaBloqueoDeAcceso.Evaluar(
                [new DocumentoParaAcceso(VigenciaDocumento.Rehidratar(fila.EstadoVigencia, fila.FechaVencimiento), fila.FechaEmision)],
                condiciones, hoy);
            if (!ReglaPendienteEnPlataforma.CuentaEnElCentro(fila.EstadoAcreditacion, requisitoEnElCentro))
                continue;

            Guid? empresaSujeto = null;
            if (fila.TrabajadorId is { } trabajadorId)
            {
                if (!(trabajadoresPorCentro.TryGetValue(fila.CentroId, out var asignados) && asignados.Contains(trabajadorId)))
                    continue;
            }
            else if (fila.EmpresaId is { } empresaId)
            {
                if (!(empresasPorCentro.TryGetValue(fila.CentroId, out var empresas) && empresas.Contains(empresaId)))
                    continue;
                empresaSujeto = empresaId;
            }
            else
            {
                // Cliente, Vehículo o Proyecto: no tienen acreditaciones de plataforma (AltaAcreditacionesPlataformaService).
                continue;
            }

            // Un tipo de serie por tipo de contratación se evalúa con el del Trabajador; el de una Empresa persiste Todos.
            var aplica = fila.TrabajadorId is { } trabajadorDeLaFila
                         && tipoContratacionPorTrabajador.TryGetValue(trabajadorDeLaFila, out var tipoContratacion)
                ? ResolucionTipoDocumentoCentro.Aplica(
                    filasPorPar, fila.TipoDocumentoId, fila.CentroId, fila.CuentaParaCumplimiento,
                    fila.TiposContratacionDeSerie, tipoContratacion)
                : ResolucionTipoDocumentoCentro.Aplica(filasPorPar, fila.TipoDocumentoId, fila.CentroId, fila.CuentaParaCumplimiento);
            if (!aplica)
                continue;

            if (!yaContados.Add((fila.CentroId, fila.TrabajadorId, empresaSujeto, fila.TipoDocumentoId)))
                continue;

            resultado.Add(new PendienteEnPlataformaDeCentro(
                fila.CentroId, fila.Id, fila.TipoDocumentoId, fila.TipoDocumentoNombre,
                fila.TrabajadorId, fila.TrabajadorId is { } id && nombres.TryGetValue(id, out var nombre) ? nombre : null,
                empresaSujeto, fila.EstadoAcreditacion, fila.FechaVencimiento,
                BloqueaAcceso: filaDelCentro is { Incluido: true, BloqueaAcceso: true }));
        }

        return resultado;
    }
}

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
public sealed record PendienteEnPlataformaDeCentro(
    Guid CentroId, Guid DocumentoId, Guid TipoDocumentoId, string TipoDocumentoNombre,
    Guid? TrabajadorId, string? TrabajadorNombre, Guid? EmpresaId,
    EstadoAcreditacion EstadoAcreditacion, DateOnly? FechaVencimiento);

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
        IAsignacionesQueryContext asignacionesContext,
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
                TipoDocumentoNombre = tipoDocumento.Nombre,
                CuentaParaCumplimiento = tipoDocumento.Requerido == RequisitoDocumental.Si,
                EstadoAcreditacion = acreditacion.Estado
            })
            .ToListAsync(cancellationToken);

        if (filas.Count == 0)
            return [];

        var asignacionesActivas = (await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null && centroIds.Contains(asignacion.CentroId)
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            select new
            {
                asignacion.CentroId,
                asignacion.TrabajadorId,
                trabajador.EmpresaId,
                trabajador.SubcontrataId,
                Nombre = trabajador.Nombre + " " + trabajador.Apellidos
            })
            .ToListAsync(cancellationToken))
            // La Empresa del Trabajador sea cual sea la columna que la guarda (AsignacionParaBloqueo).
            .Select(a => new { a.CentroId, a.TrabajadorId, EmpresaDelTrabajadorId = a.EmpresaId ?? a.SubcontrataId, a.Nombre })
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

        var tipoIds = filas.Select(f => f.TipoDocumentoId).Distinct().ToList();
        var filasPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIds.Contains(tc.TipoDocumentoId) && centroIds.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        var resultado = new List<PendienteEnPlataformaDeCentro>();
        var yaContados = new HashSet<(Guid CentroId, Guid? TrabajadorId, Guid? EmpresaId, Guid TipoDocumentoId)>();

        // Sin subir antes que subido: con dos accesos en distinto punto, cuenta lo que más falta. Por Id después, para
        // que el documento elegido entre copias duplicadas no dependa del orden en que llegan las filas.
        foreach (var fila in filas.OrderBy(f => f.EstadoAcreditacion).ThenBy(f => f.Id))
        {
            // Solo se pregunta si el documento vale hoy; los umbrales de aviso no cambian si está vencido.
            var estadoDocumento = CalculadoraEstadoDocumento.Calcular(fila.EstadoVigencia, fila.FechaVencimiento, hoy, umbralAmbarDias: 0, umbralRojoDias: 0);
            if (!ReglaPendienteEnPlataforma.CuentaEnElCentro(fila.EstadoAcreditacion, estadoDocumento))
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

            if (!ResolucionTipoDocumentoCentro.Aplica(filasPorPar, fila.TipoDocumentoId, fila.CentroId, fila.CuentaParaCumplimiento))
                continue;

            if (!yaContados.Add((fila.CentroId, fila.TrabajadorId, empresaSujeto, fila.TipoDocumentoId)))
                continue;

            resultado.Add(new PendienteEnPlataformaDeCentro(
                fila.CentroId, fila.Id, fila.TipoDocumentoId, fila.TipoDocumentoNombre,
                fila.TrabajadorId, fila.TrabajadorId is { } id && nombres.TryGetValue(id, out var nombre) ? nombre : null,
                empresaSujeto, fila.EstadoAcreditacion, fila.FechaVencimiento));
        }

        return resultado;
    }
}

using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros;

/// <summary>
/// Causa concreta que empuja el EstadoCentro por debajo de Vigente — un
/// Documento (de la Empresa o de un Trabajador) que no está Vigente, o un
/// hueco de <c>TipoDocumentoCentro.BloqueaAcceso</c> sin Documento Vigente.
/// Solo se generan causas para lo que efectivamente aporta al peor caso —
/// nada Vigente aparece aquí, igual que ObtenerAlertasQuery no lista
/// Documentos al día.
/// </summary>
/// <summary>
/// A quién pertenece la incidencia. Hasta ahora el ámbito solo se podía
/// deducir del sufijo de <see cref="CausaEstadoCentro.Descripcion"/>
/// ("… — Empresa" frente a "… — Nombre del trabajador"), que no es un dato:
/// es una cadena de presentación. El blueprint del Centro 360 § 3.2 exige que
/// el desglose de un recuento declare cuántas incidencias son de cada ámbito
/// (DDL-031, DDL-047), y eso no se puede sostener sobre un sufijo.
/// </summary>
public enum AmbitoCausa
{
    Empresa = 0,
    Trabajador = 1
}

/// <param name="DocumentoId">
/// <c>null</c> salvo en causas de Empresa, donde siempre hay un Documento real
/// detrás (§ AgregarCausasDeEmpresaAsync: sin detección de falta total ahí,
/// solo vigencia) — junto con <paramref name="TipoDocumentoId"/> y
/// <paramref name="FechaVencimiento"/> es lo que necesita la tabla de
/// documentos del bloque Empresa del Centro 360 (Documento · Estado ·
/// Vigencia · Acción, mismas columnas que la tabla de Trabajador) para
/// enlazar "Gestionar" sin una consulta aparte — reutiliza las mismas causas
/// que ya decidieron el EstadoCentro.
/// </param>
public record CausaEstadoCentro(
    string Descripcion, EstadoDocumento? Estado, bool Bloqueante, AmbitoCausa Ambito,
    Guid? DocumentoId, Guid? TipoDocumentoId, DateOnly? FechaVencimiento);

public record ResultadoEstadoCentro(EstadoCentro Estado, IReadOnlyList<CausaEstadoCentro> Causas);

/// <summary>
/// % de cumplimiento documental de un Centro (Centro 360, PLAN-EJECUCION-UX.md
/// § 0.5/0.8) — <c>Requeridos</c> es el número de pares Trabajador×TipoDocumento
/// aplicables a ese Centro (ver <see cref="Documentos.ResolucionTipoDocumentoCentro"/>),
/// <c>AlDia</c> cuántos de esos pares tienen hoy un Documento Vigente o SinCaducidad.
/// <see cref="Porcentaje"/> es <c>null</c> cuando el centro no tiene ningún par
/// aplicable — un 0% o 100% ahí sería engañoso, "sin requisitos" es la lectura
/// correcta.
/// </summary>
public record FraccionCumplimiento(int AlDia, int Requeridos)
{
    public int? Porcentaje => Requeridos == 0 ? null : (int)Math.Round(AlDia * 100.0 / Requeridos);
}

/// <summary>
/// Cálculo compartido entre ObtenerCentrosQuery (badge de la tabla) y
/// ObtenerEstadoCentroQuery (desglose del Workspace) — agrega de una sola
/// vez los Documentos de Empresa, los Documentos y huecos obligatorios de
/// cada Trabajador con Asignación activa, y los RequisitosDocumentales
/// bloqueantes de uno o varios Centros, para no lanzar N consultas al pintar
/// una página de la tabla. La lógica de "documento faltante" replica la de
/// ObtenerAlertasQuery.ObtenerFaltantesAsync (Trabajador únicamente — los
/// Documentos de Empresa aquí solo aportan su vigencia, sin detección de
/// falta total, mismo alcance que esa Query). Además, dos causas bloqueantes que
/// vienen de la plataforma del Cliente empresarial y no del archivo documental: la
/// vigencia vencida en la plataforma y la acreditación rechazada por ella.
/// </summary>
public interface ICalculoEstadoCentroService
{
    Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken);

    /// <summary>
    /// Método aparte de <see cref="CalcularAsync"/> a propósito (PLAN-EJECUCION-UX.md
    /// § 0.5): mismas fuentes de datos (asignaciones activas, tipos
    /// obligatorios, allow-list de <c>TipoDocumentoCentro</c>) pero una
    /// pregunta distinta ("qué fracción" en vez de "cuál es el peor caso") —
    /// separarlo evita arriesgar la lógica de <c>CalcularAsync</c>, ya en
    /// producción y compartida por dos pantallas, al añadirle un cálculo
    /// nuevo dentro del mismo método.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken);
}

public class CalculoEstadoCentroService(
    ICentrosQueryContext centrosContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    IConfiguracionQueryContext configuracionContext)
    : ICalculoEstadoCentroService
{
    public async Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0)
            return new Dictionary<Guid, ResultadoEstadoCentro>();

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var causasPorCentro = centroIds.Distinct().ToDictionary(id => id, _ => new List<CausaEstadoCentro>());

        await AgregarCausasDeEmpresaAsync(centroIds, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias, causasPorCentro, cancellationToken);
        await AgregarCausasDeTrabajadorAsync(centroIds, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias, causasPorCentro, cancellationToken);
        await AgregarCausasDeVigenciaEnPlataformaAsync(centroIds, hoy, causasPorCentro, cancellationToken);
        await AgregarCausasDeRechazoEnPlataformaAsync(centroIds, causasPorCentro, cancellationToken);

        return causasPorCentro.ToDictionary(
            par => par.Key,
            par => new ResultadoEstadoCentro(
                CalculadoraEstadoCentro.Calcular(
                    par.Value.Where(c => c.Estado is not null).Select(c => c.Estado!.Value).ToList(),
                    par.Value.Any(c => c.Bloqueante)),
                par.Value));
    }

    /// <summary>
    /// Un documento cuya vigencia <b>en la plataforma</b> ya venció pone el
    /// Centro en rojo, aunque en TALVEG siga vigente por su fecha de emisión.
    /// Decisión del propietario (2026-09-21): si el portal no lo acepta, el
    /// Trabajador no entra, y el semáforo existe para decir si se puede
    /// trabajar — no para describir el archivo documental.
    ///
    /// <para>
    /// Solo cuentan las vigencias <b>vencidas</b>, nunca las que están sin
    /// confirmar. Meter «no lo sé» en rojo pondría en rojo todos los Centros a
    /// la vez el día que se despliegue esto, porque las acreditaciones que ya
    /// existen nacen sin confirmar. Eso no es lo que se decidió y no sería
    /// información: sería ruido con el que nadie puede trabajar. Que falte por
    /// confirmar se resuelve en su propia pantalla, no aquí.
    /// </para>
    ///
    /// <para>
    /// No se filtra por <c>EstadoAcreditacion</c> a propósito. La condición es
    /// la fecha: si alguien anotó que aquello vence el día tal y ese día pasó,
    /// allí ya no vale, esté la acreditación como esté. Condicionarlo además al
    /// estado ataría esta regla a una correlación (solo las aceptadas tienen
    /// fecha) que hoy se cumple y que un cambio futuro podría romper en
    /// silencio.
    /// </para>
    /// </summary>
    private async Task AgregarCausasDeVigenciaEnPlataformaAsync(
        IReadOnlyList<Guid> centroIds, DateOnly hoy,
        Dictionary<Guid, List<CausaEstadoCentro>> causasPorCentro, CancellationToken cancellationToken)
    {
        // Un documento de Trabajador solo cuenta si ese Trabajador sigue
        // asignado al Centro: la acreditación sobrevive a la baja, y sin este
        // filtro una vigencia vencida bloquearía un Centro por alguien que ya
        // no trabaja ahí. Es el mismo criterio que usa el resto del cálculo de
        // documentos de Trabajador; los de Empresa no dependen de asignaciones.
        var asignacionesActivas = await asignacionesContext.Asignaciones
            .Where(a => a.FechaBaja == null && centroIds.Contains(a.CentroId))
            .Select(a => new { a.CentroId, a.TrabajadorId })
            .ToListAsync(cancellationToken);

        var trabajadoresPorCentro = asignacionesActivas
            .GroupBy(a => a.CentroId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.TrabajadorId).ToHashSet());

        var vencidasEnPlataforma = await (
            from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
            where acreditacion.EstadoVigencia == EstadoVigenciaEnPlataforma.VenceEnFecha
            where acreditacion.FechaVencimientoEnPlataforma != null
                  && acreditacion.FechaVencimientoEnPlataforma < hoy
            join canal in centrosContext.CanalesGestionDocumental
                on acreditacion.CanalGestionDocumentalId equals canal.Id
            where centroIds.Contains(canal.CentroId)
            join documento in documentosContext.Documentos
                on acreditacion.DocumentoId equals documento.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento
                on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                canal.CentroId,
                documento.Id,
                documento.TipoDocumentoId,
                documento.TrabajadorId,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                FechaVencimientoEnPlataforma = acreditacion.FechaVencimientoEnPlataforma!.Value
            })
            .ToListAsync(cancellationToken);

        foreach (var fila in vencidasEnPlataforma)
        {
            if (!causasPorCentro.TryGetValue(fila.CentroId, out var causas)) continue;

            if (fila.TrabajadorId is { } trabajadorId
                && !(trabajadoresPorCentro.TryGetValue(fila.CentroId, out var asignados)
                     && asignados.Contains(trabajadorId)))
                continue;

            causas.Add(new CausaEstadoCentro(
                $"{fila.TipoDocumentoNombre} — vencido en la plataforma",
                EstadoDocumento.Vencido,
                Bloqueante: true,
                fila.TrabajadorId is null ? AmbitoCausa.Empresa : AmbitoCausa.Trabajador,
                fila.Id, fila.TipoDocumentoId, fila.FechaVencimientoEnPlataforma));
        }
    }

    /// <summary>
    /// Una acreditación <b>aplicable</b> que la plataforma rechazó pone el Centro
    /// en rojo, aunque el documento siga vigente en TALVEG (D-7 del piloto
    /// Outbound). Validez documental en TALVEG, estado de acreditación externa y
    /// cumplimiento contextual son tres cosas distintas: el semáforo dice si se
    /// puede trabajar, y con la acreditación rechazada en la plataforma del
    /// Cliente empresarial no se puede, esté el archivo como esté aquí.
    ///
    /// <para>
    /// «Aplicable» es exactamente el contexto de este Centro: el canal de
    /// plataforma de ESTE Centro (una rechazada de otro Centro no cuenta), el
    /// Trabajador aún asignado a él (la acreditación sobrevive a la baja) y un
    /// tipo que le aplique (fila explícita del Centro o, sin ella, requerido por defecto,
    /// como en el resto del cálculo). Es el mismo motor: no
    /// hay un segundo cálculo ni un estado global «Apto».
    /// </para>
    ///
    /// <para>
    /// Solo <c>Rechazada</c>. Pendiente de subir y Subida (esperando respuesta)
    /// son trabajo y seguimiento, no un «no» de la plataforma, y no bloquean.
    /// Rechazar reinicia la vigencia en plataforma, así que esta causa y la de
    /// vigencia vencida nunca cuentan la misma acreditación dos veces; renovar el
    /// documento reinicia la acreditación a Pendiente y retira el bloqueo.
    /// </para>
    /// </summary>
    private async Task AgregarCausasDeRechazoEnPlataformaAsync(
        IReadOnlyList<Guid> centroIds,
        Dictionary<Guid, List<CausaEstadoCentro>> causasPorCentro, CancellationToken cancellationToken)
    {
        var rechazadas = await (
            from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
            where acreditacion.Estado == EstadoAcreditacion.Rechazada
            join canal in centrosContext.CanalesGestionDocumental
                on acreditacion.CanalGestionDocumentalId equals canal.Id
            where centroIds.Contains(canal.CentroId)
            join documento in documentosContext.Documentos
                on acreditacion.DocumentoId equals documento.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento
                on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                canal.CentroId,
                documento.Id,
                documento.TipoDocumentoId,
                documento.TrabajadorId,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                CuentaParaCumplimiento = tipoDocumento.Requerido == RequisitoDocumental.Si
            })
            .ToListAsync(cancellationToken);

        if (rechazadas.Count == 0) return;

        var asignacionesActivas = await asignacionesContext.Asignaciones
            .Where(a => a.FechaBaja == null && centroIds.Contains(a.CentroId))
            .Select(a => new { a.CentroId, a.TrabajadorId })
            .ToListAsync(cancellationToken);
        var trabajadoresPorCentro = asignacionesActivas
            .GroupBy(a => a.CentroId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.TrabajadorId).ToHashSet());

        var tipoIds = rechazadas.Select(r => r.TipoDocumentoId).Distinct().ToList();
        var filasPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIds.Contains(tc.TipoDocumentoId) && centroIds.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        var trabajadorIds = rechazadas.Where(r => r.TrabajadorId is not null).Select(r => r.TrabajadorId!.Value).Distinct().ToList();
        var nombres = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Nombre + " " + t.Apellidos, cancellationToken);

        foreach (var fila in rechazadas)
        {
            if (!causasPorCentro.TryGetValue(fila.CentroId, out var causas)) continue;

            if (fila.TrabajadorId is { } trabajadorId
                && !(trabajadoresPorCentro.TryGetValue(fila.CentroId, out var asignados)
                     && asignados.Contains(trabajadorId)))
                continue;

            // Misma regla de aplicabilidad que el resto del cálculo: una fila explícita del Centro
            // manda; sin ella, solo cuenta un tipo requerido por defecto. Un rechazo de un tipo
            // opcional no bloquea, pero sigue visible como trabajo en la Bandeja.
            if (!ResolucionTipoDocumentoCentro.Aplica(filasPorPar, fila.TipoDocumentoId, fila.CentroId, fila.CuentaParaCumplimiento))
                continue;

            var propietario = fila.TrabajadorId is { } id && nombres.TryGetValue(id, out var nombre)
                ? $" — {nombre}"
                : " — Empresa";
            // Sin vigencia documental que describir (no es un vencimiento de fecha),
            // pero el Badge de la UI (AcordeonAsignacionesCentro) indexa por
            // EstadoDocumento y no admite null: mismo criterio que su causa hermana
            // "vencido en la plataforma" (arriba, misma familia — vigencia decidida
            // por la plataforma del Cliente empresarial, no por archivo documental).
            causas.Add(new CausaEstadoCentro(
                $"{fila.TipoDocumentoNombre}{propietario} — rechazado por la plataforma",
                Estado: EstadoDocumento.Vencido,
                Bloqueante: true,
                fila.TrabajadorId is null ? AmbitoCausa.Empresa : AmbitoCausa.Trabajador,
                fila.Id, fila.TipoDocumentoId, FechaVencimiento: null));
        }
    }

    private async Task AgregarCausasDeEmpresaAsync(
        IReadOnlyList<Guid> centroIds, DateOnly hoy, int umbralAmbarDias, int umbralRojoDias,
        Dictionary<Guid, List<CausaEstadoCentro>> causasPorCentro, CancellationToken cancellationToken)
    {
        var centros = await centrosContext.Centros
            .Where(c => centroIds.Contains(c.Id))
            .Select(c => new { c.Id, c.EmpresaId })
            .ToListAsync(cancellationToken);

        var centroIdsPorEmpresa = centros
            .GroupBy(c => c.EmpresaId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList());

        var empresaIds = centroIdsPorEmpresa.Keys.ToList();

        var documentosEmpresa = await (
            from documento in documentosContext.Documentos
            where documento.EmpresaId != null && empresaIds.Contains(documento.EmpresaId!.Value)
            where documento.FechaVencimiento != null
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                documento.Id,
                EmpresaId = documento.EmpresaId!.Value,
                documento.TipoDocumentoId,
                documento.FechaVencimiento,
                tipoDocumento.Nombre
            })
            .ToListAsync(cancellationToken);

        foreach (var documento in documentosEmpresa)
        {
            var estado = CalculadoraEstadoDocumento.Calcular(documento.FechaVencimiento, hoy, umbralAmbarDias, umbralRojoDias);
            if (estado is EstadoDocumento.SinCaducidad or EstadoDocumento.Vigente) continue;
            if (!centroIdsPorEmpresa.TryGetValue(documento.EmpresaId, out var centrosDeEmpresa)) continue;

            var causa = new CausaEstadoCentro(
                $"{documento.Nombre} — Empresa", estado, Bloqueante: false, AmbitoCausa.Empresa,
                documento.Id, documento.TipoDocumentoId, documento.FechaVencimiento);
            foreach (var centroId in centrosDeEmpresa)
                causasPorCentro[centroId].Add(causa);
        }
    }

    private async Task AgregarCausasDeTrabajadorAsync(
        IReadOnlyList<Guid> centroIds, DateOnly hoy, int umbralAmbarDias, int umbralRojoDias,
        Dictionary<Guid, List<CausaEstadoCentro>> causasPorCentro, CancellationToken cancellationToken)
    {
        var asignacionesActivas = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null && centroIds.Contains(asignacion.CentroId)
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            select new
            {
                asignacion.CentroId,
                TrabajadorId = trabajador.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos
            })
            .ToListAsync(cancellationToken);

        if (asignacionesActivas.Count == 0) return;

        var trabajadorIds = asignacionesActivas.Select(a => a.TrabajadorId).Distinct().ToList();

        // Vigencia de los Documentos de Trabajador que ya existen — igual que
        // el bloque "alertasVigencia" de ObtenerAlertasQuery, sin filtrar por
        // EsObligatorio: un Documento vencido cuenta para el Centro exista o
        // no exista una fila de obligatoriedad para su TipoDocumento.
        var documentosTrabajador = await (
            from documento in documentosContext.Documentos
            where documento.TrabajadorId != null && trabajadorIds.Contains(documento.TrabajadorId!.Value)
            where documento.FechaVencimiento != null
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new { TrabajadorId = documento.TrabajadorId!.Value, documento.FechaVencimiento, tipoDocumento.Nombre })
            .ToListAsync(cancellationToken);

        foreach (var asignacion in asignacionesActivas)
        {
            foreach (var documento in documentosTrabajador.Where(d => d.TrabajadorId == asignacion.TrabajadorId))
            {
                var estado = CalculadoraEstadoDocumento.Calcular(documento.FechaVencimiento, hoy, umbralAmbarDias, umbralRojoDias);
                if (estado is EstadoDocumento.SinCaducidad or EstadoDocumento.Vigente) continue;

                causasPorCentro[asignacion.CentroId].Add(
                    new CausaEstadoCentro(
                        $"{documento.Nombre} — {asignacion.TrabajadorNombre}", estado, Bloqueante: false, AmbitoCausa.Trabajador,
                        DocumentoId: null, TipoDocumentoId: null, FechaVencimiento: null));
            }
        }

        // Huecos requeridos — misma lógica que ObtenerAlertasQuery.ObtenerFaltantesAsync,
        // reacotada a estos Centros. Candidatos = todo el catálogo de Trabajador, no solo
        // EsObligatorio=true: un Centro puede exigir explícitamente un tipo no obligatorio
        // globalmente (PLAN-EJECUCION-UX.md § 0.4, TipoDocumentoCentro.Incluido).
        var tiposCandidatos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new { t.Id, t.Nombre, CuentaParaCumplimiento = t.Requerido == RequisitoDocumental.Si })
            .ToListAsync(cancellationToken);

        if (tiposCandidatos.Count == 0) return;

        var tipoIdsCandidatos = tiposCandidatos.Select(t => t.Id).ToHashSet();

        var filasPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIdsCandidatos.Contains(tc.TipoDocumentoId) && centroIds.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        var parejasConDocumento = (await documentosContext.Documentos
            .Where(d => d.TrabajadorId != null
                && trabajadorIds.Contains(d.TrabajadorId!.Value)
                && tipoIdsCandidatos.Contains(d.TipoDocumentoId))
            .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId })
            .ToListAsync(cancellationToken))
            .Select(d => (d.TrabajadorId, d.TipoDocumentoId))
            .ToHashSet();

        foreach (var asignacion in asignacionesActivas)
        {
            foreach (var tipo in tiposCandidatos)
            {
                if (!ResolucionTipoDocumentoCentro.Aplica(filasPorPar, tipo.Id, asignacion.CentroId, tipo.CuentaParaCumplimiento))
                    continue;

                if (parejasConDocumento.Contains((asignacion.TrabajadorId, tipo.Id)))
                    continue;

                // BloqueaAcceso de la fila explícita (si hay) fuerza EstadoCentro.Bloqueado
                // aquí mismo — sustituye a RequisitoDocumental.BloqueaAcceso/Cumplido
                // (retirado): antes era un check manual a nivel de Centro, ahora es
                // automático por trabajador, igual que el resto de este servicio.
                var bloquea = filasPorPar.TryGetValue((tipo.Id, asignacion.CentroId), out var fila) && fila.BloqueaAcceso;

                causasPorCentro[asignacion.CentroId].Add(new CausaEstadoCentro(
                    $"{tipo.Nombre} — {asignacion.TrabajadorNombre}", EstadoDocumento.Faltante, Bloqueante: bloquea, AmbitoCausa.Trabajador,
                    DocumentoId: null, TipoDocumentoId: null, FechaVencimiento: null));
            }
        }
    }

    /// <summary>
    /// Alcance igual al de "huecos obligatorios" de <see cref="AgregarCausasDeTrabajadorAsync"/>
    /// (Trabajador únicamente, sin Documentos de Empresa — así lo pide
    /// PLAN-EJECUCION-UX.md § 0.5: "por trabajador dentro de un centro"),
    /// pero contando el universo completo de pares aplicables en vez de solo
    /// los que fallan.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken)
    {
        var acumulado = centroIds.Distinct().ToDictionary(id => id, _ => (AlDia: 0, Requeridos: 0));
        if (centroIds.Count == 0)
            return acumulado.ToDictionary(p => p.Key, p => new FraccionCumplimiento(p.Value.AlDia, p.Value.Requeridos));

        var asignacionesActivas = await asignacionesContext.Asignaciones
            .Where(a => a.FechaBaja == null && centroIds.Contains(a.CentroId))
            .Select(a => new { a.CentroId, a.TrabajadorId })
            .ToListAsync(cancellationToken);

        if (asignacionesActivas.Count == 0)
            return acumulado.ToDictionary(p => p.Key, p => new FraccionCumplimiento(p.Value.AlDia, p.Value.Requeridos));

        var tiposCandidatos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new { t.Id, CuentaParaCumplimiento = t.Requerido == RequisitoDocumental.Si })
            .ToListAsync(cancellationToken);

        if (tiposCandidatos.Count == 0)
            return acumulado.ToDictionary(p => p.Key, p => new FraccionCumplimiento(p.Value.AlDia, p.Value.Requeridos));

        var tipoIdsCandidatos = tiposCandidatos.Select(t => t.Id).ToHashSet();

        var filasPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIdsCandidatos.Contains(tc.TipoDocumentoId) && centroIds.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        var trabajadorIds = asignacionesActivas.Select(a => a.TrabajadorId).Distinct().ToList();
        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var estadosPorPareja = (await documentosContext.Documentos
            .Where(d => d.TrabajadorId != null
                && trabajadorIds.Contains(d.TrabajadorId!.Value)
                && tipoIdsCandidatos.Contains(d.TipoDocumentoId))
            .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId, d.FechaVencimiento })
            .ToListAsync(cancellationToken))
            .ToDictionary(
                d => (d.TrabajadorId, d.TipoDocumentoId),
                d => CalculadoraEstadoDocumento.Calcular(d.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias));

        foreach (var asignacion in asignacionesActivas)
        {
            foreach (var tipo in tiposCandidatos)
            {
                if (!ResolucionTipoDocumentoCentro.Aplica(filasPorPar, tipo.Id, asignacion.CentroId, tipo.CuentaParaCumplimiento))
                    continue;

                var actual = acumulado[asignacion.CentroId];
                var alDia = actual.AlDia;
                if (estadosPorPareja.TryGetValue((asignacion.TrabajadorId, tipo.Id), out var estado)
                    && estado is EstadoDocumento.Vigente or EstadoDocumento.SinCaducidad)
                    alDia++;

                acumulado[asignacion.CentroId] = (alDia, actual.Requeridos + 1);
            }
        }

        return acumulado.ToDictionary(p => p.Key, p => new FraccionCumplimiento(p.Value.AlDia, p.Value.Requeridos));
    }
}

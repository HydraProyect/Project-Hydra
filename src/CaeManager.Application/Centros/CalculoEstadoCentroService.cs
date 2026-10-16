using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
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
/// hueco total (ningún Documento) de un Tipo exigido por el Centro.
/// Un documento de Trabajador o de Empresa, ausente o vencido, bloquea a PERSONAS
/// (<see cref="ReglaBloqueoDeAcceso"/> por Centro, vía <see cref="IEvaluacionDeAccesoPorCentroService"/>: Mi trabajo y el detalle
/// por Trabajador del Centro 360) y NUNCA pone el Centro en Bloqueado (decisión del propietario, 2026-10-03): «Bloqueado» es un
/// estado del Trabajador. <see cref="CausaEstadoCentro.Bloqueante"/> queda solo para las causas que vienen de la plataforma
/// del Cliente empresarial (vigencia vencida y acreditación rechazada, D-7).
/// <see cref="CausaEstadoCentro.PendienteEnPlataforma"/> marca un documento que vale pero está sin subir o sin validar en la
/// plataforma CAE de ese Centro (<see cref="ReglaPendienteEnPlataforma"/>, decisión del 2026-10-10): pone el Centro en
/// <see cref="EstadoCentro.Pendiente"/> sea cual sea el tipo, y bloquea a personas en ese Centro (nunca el Centro entero) solo
/// si el Centro marca el tipo con <c>BloqueaAcceso</c> (<see cref="IEvaluacionDeAccesoPorCentroService"/>).
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
/// El Documento real que hay detrás de la causa; <c>null</c> solo cuando la
/// causa es la falta total de un documento de Trabajador (no hay nada que
/// renovar: se crea). En causas de Empresa siempre viene
/// (§ AgregarCausasDeEmpresaAsync: sin detección de falta total ahí,
/// solo vigencia) — junto con <paramref name="TipoDocumentoId"/> y
/// <paramref name="FechaVencimiento"/> es lo que necesita la tabla de
/// documentos del bloque Empresa del Centro 360 (Documento · Estado ·
/// Vigencia · Acción, mismas columnas que la tabla de Trabajador) para
/// enlazar "Gestionar" sin una consulta aparte — reutiliza las mismas causas
/// que ya decidieron el EstadoCentro.
/// </param>
/// <param name="TrabajadorId">
/// De quién es la causa cuando <paramref name="Ambito"/> es
/// <see cref="AmbitoCausa.Trabajador"/>. Con él y <paramref name="TipoDocumentoId"/>
/// el listado de Centros abre la corrección de una incidencia —renovar el
/// Documento o subir el que falta— sin salir de la pantalla. Es un dato para
/// localizar el formulario, no autoridad: el comando que guarda vuelve a
/// comprobar el alcance.
/// </param>
/// <param name="PendienteEnPlataforma">
/// La causa es un documento pendiente en la plataforma CAE de este Centro (sin subir o subido sin validar). Llega con
/// <paramref name="Estado"/> en <c>null</c>: no describe la vigencia del documento, que vale, sino el trabajo que falta
/// en la plataforma; y sin <paramref name="Bloqueante"/>, porque no pone el Centro en Bloqueado.
/// </param>
/// <param name="BloqueaAcceso">
/// Solo con <paramref name="PendienteEnPlataforma"/>: el Centro marca el tipo del documento «bloquea el acceso»
/// (<c>TipoDocumentoCentro.BloqueaAcceso</c>), así que este pendiente bloquea a personas en el Centro. Sin la marca (una ISO
/// opcional) el Centro está Pendiente pero nadie queda bloqueado, y el pendiente no cuenta en el porcentaje.
/// </param>
public record CausaEstadoCentro(
    string Descripcion, EstadoDocumento? Estado, bool Bloqueante, AmbitoCausa Ambito,
    Guid? DocumentoId, Guid? TipoDocumentoId, DateOnly? FechaVencimiento, Guid? TrabajadorId = null,
    bool PendienteEnPlataforma = false, bool BloqueaAcceso = false);

public record ResultadoEstadoCentro(EstadoCentro Estado, IReadOnlyList<CausaEstadoCentro> Causas);

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
/// vigencia vencida en la plataforma y la acreditación rechazada por ella; y una
/// no bloqueante para el Centro, el documento pendiente en su plataforma (sin
/// subir o subido sin validar), que lo pone en <see cref="EstadoCentro.Pendiente"/>.
/// Desde el 2026-10-15 suma también los requisitos de cada evento del Centro (Proyecto de tipo Evento, criterio en
/// VigenciaEvento.AportaRequisitosEn: desde la asignación, mientras el evento es futuro o vigente, decisión 2026-10-16)
/// para todos los Trabajadores asignados a él, voluntarios o no y de la Empresa o de una Subcontrata, tengan o no
/// Asignación al Centro; y no cuenta a los voluntarios inactivos (sin evento vigente ni futuro).
/// </summary>
public interface ICalculoEstadoCentroService
{
    Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken);

    /// <summary>
    /// Cumplimiento documental de cada Centro (Centro 360, Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md
    /// § 0.5): <c>Requeridos</c> son los pares Trabajador×TipoDocumento incluidos explícitamente en el Centro para sus Trabajadores con
    /// Asignación activa más los pares Empresa×TipoDocumento (ámbito Empresa) incluidos explícitamente para las Empresas de esos Trabajadores
    /// (decisión del propietario, 2026-10-10), más los pares Trabajador×TipoDocumento que pide cada evento futuro o vigente del Centro a quien
    /// está asignado a él aunque el Centro no incluya ese tipo (decisiones 2026-10-15 y 2026-10-16), y <c>AlDia</c> los que <see cref="CumplimientoDocumental"/> cuenta como al día. Es el contexto
    /// <see cref="ContextoCumplimiento.Centro"/>; un Centro sin ningún par exigido (o sin gestión CAE) queda en 0/0,
    /// cuyo porcentaje es <c>null</c>.
    ///
    /// Método aparte de <see cref="CalcularAsync"/> a propósito: mismas fuentes de datos pero una pregunta distinta
    /// («qué fracción» en vez de «cuál es el peor caso»); separarlo evita arriesgar la lógica de <c>CalcularAsync</c>,
    /// compartida por varias pantallas.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken);

    /// <summary>
    /// Los pares exigidos de esos Centros, uno por Centro × Trabajador × TipoDocumento, con el estado de su documento
    /// preferido. Empresa y Cliente empresarial se obtienen agrupando estos pares, sin recalcular el universo.
    /// El porcentaje del Centro en <see cref="CalcularCumplimientoAsync"/> aplica además el filtro de inclusiones
    /// explícitas; esta lectura conserva la herencia global para sus demás consumidores.
    /// </summary>
    Task<IReadOnlyList<ParDocumentalExigido>> ObtenerParesExigidosAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken);
}

public class CalculoEstadoCentroService(
    ICentrosQueryContext centrosContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    IConfiguracionQueryContext configuracionContext,
    IEmpresasQueryContext empresasContext,
    IProyectosQueryContext proyectosContext,
    IRequisitosEventoQueryContext requisitosEventoContext)
    : ICalculoEstadoCentroService
{
    /// <summary>
    /// Las Asignaciones activas que cuentan en el Centro: todas menos las de los voluntarios inactivos, sin evento
    /// vigente ni futuro; esos no se borran, pero su documentación no cuenta para el estado ni el cumplimiento.
    /// </summary>
    private IQueryable<CaeManager.Domain.Asignaciones.Asignacion> AsignacionesQueCuentan()
    {
        var inactivos = ActividadVoluntarios.VoluntariosInactivosIds(trabajadoresContext.Trabajadores, proyectosContext, DiaDeNegocio.Hoy());
        return asignacionesContext.Asignaciones.Where(a => inactivos.Contains(a.TrabajadorId) == false);
    }

    public async Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0)
            return new Dictionary<Guid, ResultadoEstadoCentro>();

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();

        // P1-X2: un Centro sin gestión CAE no exige documentación, así que no
        // se buscan causas en él (ni de Empresa, ni de Trabajador, ni de las
        // acreditaciones en plataforma de canales que conserve de antes): su
        // estado es SinGestionCae, nunca un Vigente que nadie ha comprobado.
        var sinGestionCae = await CentrosSinGestionCae.FiltrarAsync(centrosContext, centroIds, cancellationToken);
        var conGestionCae = centroIds.Where(id => !sinGestionCae.Contains(id)).Distinct().ToList();

        var causasPorCentro = conGestionCae.ToDictionary(id => id, _ => new List<CausaEstadoCentro>());

        if (conGestionCae.Count > 0)
        {
            await AgregarCausasDeEmpresaAsync(conGestionCae, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias, causasPorCentro, cancellationToken);
            await AgregarCausasDeTrabajadorAsync(conGestionCae, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias, causasPorCentro, cancellationToken);
            await AgregarCausasDeRequisitosDeEventoAsync(conGestionCae, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias, causasPorCentro, cancellationToken);
            await AgregarCausasDeVigenciaEnPlataformaAsync(conGestionCae, hoy, causasPorCentro, cancellationToken);
            await AgregarCausasDeRechazoEnPlataformaAsync(conGestionCae, causasPorCentro, cancellationToken);
            await AgregarCausasPendientesEnPlataformaAsync(conGestionCae, hoy, causasPorCentro, cancellationToken);
        }

        var resultado = causasPorCentro.ToDictionary(
            par => par.Key,
            par => new ResultadoEstadoCentro(
                CalculadoraEstadoCentro.Calcular(
                    par.Value.Where(c => c.Estado is not null).Select(c => c.Estado!.Value).ToList(),
                    par.Value.Any(c => c.Bloqueante),
                    par.Value.Any(c => c.PendienteEnPlataforma)),
                par.Value));

        foreach (var centroId in sinGestionCae)
            resultado[centroId] = new ResultadoEstadoCentro(EstadoCentro.SinGestionCae, []);

        return resultado;
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
        var asignacionesActivas = await AsignacionesQueCuentan()
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
                fila.Id, fila.TipoDocumentoId, fila.FechaVencimientoEnPlataforma, fila.TrabajadorId));
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
    /// no son un «no» de la plataforma y no ponen el Centro en Bloqueado: desde
    /// el 2026-10-10 son su propia causa, <see cref="EstadoCentro.Pendiente"/>
    /// (<see cref="AgregarCausasPendientesEnPlataformaAsync"/>), que bloquea a
    /// personas en ese Centro solo si el Centro marca el tipo con
    /// <c>BloqueaAcceso</c>.
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
                TipoDocumentoNombre = tipoDocumento.Nombre,
                CuentaParaCumplimiento = tipoDocumento.Requerido == RequisitoDocumental.Si
            })
            .ToListAsync(cancellationToken);

        if (rechazadas.Count == 0) return;

        var asignacionesActivas = await AsignacionesQueCuentan()
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
            // Sin vigencia documental que describir: no es un vencimiento de
            // fecha, es un rechazo activo de la plataforma del Cliente
            // empresarial (a diferencia de su causa hermana "vencido en la
            // plataforma", que sí tiene una FechaVencimientoEnPlataforma real
            // y por eso reutiliza EstadoDocumento.Vencido con propiedad). Forzar
            // aquí un EstadoDocumento sería una clasificación documental falsa
            // — un rechazo no es un vencimiento — así que Estado se deja sin
            // valor a propósito. ObtenerCentrosQuery.Desglosar la bucketiza por
            // Bloqueante, y AcordeonAsignacionesCentro ya sabe renderizar esta
            // causa sin badge de vigencia documental.
            causas.Add(new CausaEstadoCentro(
                $"{fila.TipoDocumentoNombre}{propietario} — rechazado por la plataforma",
                Estado: null,
                Bloqueante: true,
                fila.TrabajadorId is null ? AmbitoCausa.Empresa : AmbitoCausa.Trabajador,
                fila.Id, fila.TipoDocumentoId, FechaVencimiento: null, fila.TrabajadorId));
        }
    }

    /// <summary>
    /// Un documento que todavía vale en TALVEG y que en la plataforma CAE de ESTE Centro está sin subir o subido sin
    /// validar (<see cref="ReglaPendienteEnPlataforma"/>, decisión del propietario, 2026-10-10) pone el Centro en
    /// <see cref="EstadoCentro.Pendiente"/>. No es <see cref="CausaEstadoCentro.Bloqueante"/>: el Centro no queda Bloqueado;
    /// quien queda bloqueado es la persona, en ese Centro, y solo si el Centro marca el tipo con <c>BloqueaAcceso</c>
    /// (<see cref="IEvaluacionDeAccesoPorCentroService"/>). Esta causa sale para cualquier tipo que le aplique al Centro: una
    /// ISO opcional pendiente pone el Centro en «Pendiente» aunque no bloquee a nadie.
    ///
    /// <para>
    /// Mismo contexto que el rechazo: solo accesos de tipo Plataforma de este Centro (un Centro sin plataforma nunca tiene
    /// un Pendiente), un tipo que le aplique (<see cref="ResolucionTipoDocumentoCentro.Aplica"/>) y un propietario con
    /// actividad allí — el Trabajador con Asignación activa, o la Empresa con algún Trabajador suyo con Asignación activa:
    /// la acreditación sobrevive a la baja y a que el tipo deje de exigirse (<see cref="PendientesEnPlataformaDeCentros"/>, la
    /// misma carga que usa el acceso por Centro). Una causa por Centro, sujeto y tipo, aunque el Centro tenga varios accesos
    /// de plataforma o el sujeto copias duplicadas del documento.
    /// </para>
    /// </summary>
    private async Task AgregarCausasPendientesEnPlataformaAsync(
        IReadOnlyList<Guid> centroIds, DateOnly hoy,
        Dictionary<Guid, List<CausaEstadoCentro>> causasPorCentro, CancellationToken cancellationToken)
    {
        var pendientes = await PendientesEnPlataformaDeCentros.CargarAsync(
            centrosContext, documentosContext, tiposDocumentoContext, trabajadoresContext, AsignacionesQueCuentan(),
            centroIds, hoy, cancellationToken);

        // El nombre de la Empresa dueña del documento (una Subcontrata o la Empresa propia del Trabajador), no un «Empresa» genérico.
        var empresaIds = pendientes.Where(p => p.EmpresaId is not null).Select(p => p.EmpresaId!.Value).Distinct().ToList();
        var nombresDeEmpresa = empresaIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await empresasContext.Empresas
                .Where(e => empresaIds.Contains(e.Id))
                .Select(e => new { e.Id, e.RazonSocial })
                .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        foreach (var pendiente in pendientes)
        {
            if (!causasPorCentro.TryGetValue(pendiente.CentroId, out var causas)) continue;

            var propietario = pendiente.TrabajadorNombre is { } nombre
                ? $" — {nombre}"
                : pendiente.EmpresaId is { } empresaId && nombresDeEmpresa.TryGetValue(empresaId, out var razonSocial)
                    ? $" — {razonSocial}"
                    : " — Empresa";
            var queFalta = pendiente.EstadoAcreditacion == EstadoAcreditacion.Subida
                ? "subido, sin validar en la plataforma"
                : "sin subir a la plataforma";
            causas.Add(new CausaEstadoCentro(
                $"{pendiente.TipoDocumentoNombre}{propietario} — {queFalta}",
                Estado: null,
                Bloqueante: false,
                pendiente.TrabajadorId is null ? AmbitoCausa.Empresa : AmbitoCausa.Trabajador,
                pendiente.DocumentoId, pendiente.TipoDocumentoId, pendiente.FechaVencimiento, pendiente.TrabajadorId,
                PendienteEnPlataforma: true, BloqueaAcceso: pendiente.BloqueaAcceso));
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
            from documento in documentosContext.Documentos.Operativos()
            where documento.EmpresaId != null && empresaIds.Contains(documento.EmpresaId!.Value)
            // Solo documentos con fecha (VenceEnFecha, por CK_Documentos_EstadoVigenciaCoherente).
            // Ni «no caduca» ni «sin confirmar» son causa de color: lo sin confirmar resta
            // del cumplimiento (CalcularCumplimientoAsync) pero no pone el Centro en ámbar o
            // rojo, por la misma razón que la vigencia en plataforma sin confirmar
            // (AgregarCausasDeVigenciaEnPlataformaAsync): los documentos que ya existían sin
            // fecha nacen sin confirmar, y teñirlos todos a la vez sería ruido, no información.
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
            var estado = CalculadoraEstadoDocumento.Calcular(
                VigenciaDocumento.VenceEl(documento.FechaVencimiento!.Value), hoy, umbralAmbarDias, umbralRojoDias);
            if (estado is EstadoDocumento.Vigente) continue;
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
            from asignacion in AsignacionesQueCuentan()
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
        //
        // Con vencimiento más allá del umbral ámbar el estado es SIEMPRE
        // Vigente (CalculadoraEstadoDocumento) y el bucle de abajo lo salta:
        // la cota superior lo deja en PostgreSQL en vez de traerlo y
        // descartarlo aquí (mismo límite que ObtenerAlertasQuery; P1-D1).
        var fechaLimiteCausa = hoy.AddDays(umbralAmbarDias);
        var documentosTrabajador = (await (
            from documento in documentosContext.Documentos.Operativos()
            where documento.TrabajadorId != null && trabajadorIds.Contains(documento.TrabajadorId!.Value)
            // Solo con fecha: mismo criterio que en AgregarCausasDeEmpresaAsync.
            where documento.FechaVencimiento != null && documento.FechaVencimiento <= fechaLimiteCausa
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new { documento.Id, documento.TipoDocumentoId, TrabajadorId = documento.TrabajadorId!.Value, documento.FechaVencimiento, tipoDocumento.Nombre })
            .ToListAsync(cancellationToken))
            .ToLookup(d => d.TrabajadorId);

        foreach (var asignacion in asignacionesActivas)
        {
            foreach (var documento in documentosTrabajador[asignacion.TrabajadorId])
            {
                var estado = CalculadoraEstadoDocumento.Calcular(
                    VigenciaDocumento.VenceEl(documento.FechaVencimiento!.Value), hoy, umbralAmbarDias, umbralRojoDias);
                if (estado is EstadoDocumento.Vigente) continue;

                causasPorCentro[asignacion.CentroId].Add(
                    new CausaEstadoCentro(
                        $"{documento.Nombre} — {asignacion.TrabajadorNombre}", estado, Bloqueante: false, AmbitoCausa.Trabajador,
                        documento.Id, documento.TipoDocumentoId, documento.FechaVencimiento, asignacion.TrabajadorId));
            }
        }

        // Huecos requeridos — misma lógica que ObtenerAlertasQuery.ObtenerFaltantesAsync,
        // reacotada a estos Centros. Candidatos = todo el catálogo de Trabajador, no solo
        // EsObligatorio=true: un Centro puede exigir explícitamente un tipo no obligatorio
        // globalmente (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.4, TipoDocumentoCentro.Incluido).
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

        var parejasConDocumento = (await documentosContext.Documentos.Operativos()
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

                // Aunque la fila marque BloqueaAcceso, la falta total de un documento es un «Falta documentación» del Centro,
                // no un «Bloqueado»: quien queda bloqueado es el Trabajador (ReglaBloqueoDeAcceso, por Centro).
                causasPorCentro[asignacion.CentroId].Add(new CausaEstadoCentro(
                    $"{tipo.Nombre} — {asignacion.TrabajadorNombre}", EstadoDocumento.Faltante, Bloqueante: false, AmbitoCausa.Trabajador,
                    DocumentoId: null, tipo.Id, FechaVencimiento: null, asignacion.TrabajadorId));
            }
        }
    }

    /// <summary>
    /// Alcance de los pares de Trabajador igual al de "huecos obligatorios" de <see cref="AgregarCausasDeTrabajadorAsync"/>
    /// (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.5: "por trabajador dentro de un centro"), pero
    /// contando el universo completo de pares aplicables en vez de solo los que fallan. Desde el 2026-10-10 suma además los
    /// pares de Empresa (<see cref="ObtenerParesDeEmpresaAsync"/>), que solo este porcentaje recibe.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken)
    {
        // Dos universos en el denominador del Centro (decisión del propietario, 2026-10-10): los pares Trabajador × Tipo y los
        // pares Empresa × Tipo de ámbito Empresa. Las dos mitades comparten una sola carga de lo pendiente en la plataforma.
        var conGestionCae = await CentrosConGestionCaeAsync(centroIds, cancellationToken);
        var hoy = DiaDeNegocio.Hoy();
        var pendientes = await PendientesQueBajanElPorcentajeAsync(conGestionCae, hoy, cancellationToken);
        var paresDeTrabajador = await ObtenerParesDeTrabajadorAsync(centroIds, conGestionCae, pendientes, hoy, cancellationToken);
        var paresDeEmpresa = await ObtenerParesDeEmpresaAsync(conGestionCae, pendientes, hoy, cancellationToken);
        // El porcentaje cuenta únicamente inclusiones explícitas del Centro, en las dos mitades. La herencia global
        // sigue vigente para las otras lecturas de pares exigidos y no cambia su contrato.
        var incluidos = await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => centroIds.Contains(tc.CentroId) && tc.Incluido)
            .Select(tc => new { tc.CentroId, tc.TipoDocumentoId })
            .ToListAsync(cancellationToken);
        var clavesIncluidas = incluidos.Select(tc => (tc.CentroId, tc.TipoDocumentoId)).ToHashSet();
        // Los requisitos de un evento futuro o vigente cuentan aunque el Centro no incluya ese tipo: los exige el evento.
        var paresDeEvento = await ObtenerParesDeEventoAsync(conGestionCae, pendientes, hoy, cancellationToken);
        var porCentro = CumplimientoDocumental.PorCentro(
            UnirConParesDeEvento(paresDeTrabajador.Where(p => clavesIncluidas.Contains((p.CentroId, p.TipoDocumentoId))), paresDeEvento),
            paresDeEmpresa.Where(p => clavesIncluidas.Contains((p.CentroId, p.TipoDocumentoId))).ToList());

        // Un Centro sin ningún par (sin gestión CAE, sin Trabajadores o sin tipos exigidos) sigue en el resultado, en
        // 0/0: su porcentaje es null («sin requisitos»), nunca un 100 %.
        return centroIds.Distinct().ToDictionary(id => id, id => porCentro.GetValueOrDefault(id, FraccionCumplimiento.SinRequisitos));
    }

    /// <summary>P1-X2: un Centro sin gestión CAE no exige nada — no aporta ningún par.</summary>
    private async Task<List<Guid>> CentrosConGestionCaeAsync(IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0)
            return [];

        var sinGestionCae = await CentrosSinGestionCae.FiltrarAsync(centrosContext, centroIds, cancellationToken);
        return centroIds.Where(id => !sinGestionCae.Contains(id)).Distinct().ToList();
    }

    /// <summary>
    /// Pendiente en la plataforma (decisión 2026-10-10): la MISMA carga que la causa del estado del Centro y que el
    /// bloqueo de personas (<see cref="PendientesEnPlataformaDeCentros"/>: documento efectivo, vigencia del requisito en el
    /// Centro), para que el porcentaje no discrepe de ellos. Solo baja el porcentaje el Pendiente de un tipo que el Centro
    /// marca «bloquea el acceso»: lo obligatorio configurado (decisión 3 del 2026-10-10). Un Centro sin plataforma no devuelve ninguno.
    /// </summary>
    private async Task<IReadOnlyList<PendienteEnPlataformaDeCentro>> PendientesQueBajanElPorcentajeAsync(
        List<Guid> conGestionCae, DateOnly hoy, CancellationToken cancellationToken) =>
        conGestionCae.Count == 0
            ? []
            : (await PendientesEnPlataformaDeCentros.CargarAsync(
                    centrosContext, documentosContext, tiposDocumentoContext, trabajadoresContext, AsignacionesQueCuentan(),
                    conGestionCae, hoy, cancellationToken))
                .Where(p => p.BloqueaAcceso)
                .ToList();

    public async Task<IReadOnlyList<ParDocumentalExigido>> ObtenerParesExigidosAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0)
            return [];

        var conGestionCae = await CentrosConGestionCaeAsync(centroIds, cancellationToken);
        var hoy = DiaDeNegocio.Hoy();
        // Sin la carga de lo pendiente en la plataforma: la marca PendienteEnPlataforma de un par solo la lee el porcentaje
        // del Centro (CalcularCumplimientoAsync, que la carga por su cuenta), y ningún consumidor de este método lo calcula.
        return UnirConParesDeEvento(
            await ObtenerParesDeTrabajadorAsync(centroIds, conGestionCae, [], hoy, cancellationToken),
            await ObtenerParesDeEventoAsync(conGestionCae, [], hoy, cancellationToken));
    }

    /// <summary>
    /// Los pares del Centro más los de los eventos futuros o vigentes que el Centro no tenga ya: si un mismo Trabajador × Tipo sale por
    /// las dos vías, cuenta una vez.
    /// </summary>
    private static List<ParDocumentalExigido> UnirConParesDeEvento(
        IEnumerable<ParDocumentalExigido> paresDelCentro, IReadOnlyList<ParDocumentalExigido> paresDeEvento)
    {
        var resultado = paresDelCentro.ToList();
        var claves = resultado.Select(p => (p.CentroId, p.TrabajadorId, p.TipoDocumentoId)).ToHashSet();
        resultado.AddRange(paresDeEvento.Where(p => claves.Add((p.CentroId, p.TrabajadorId, p.TipoDocumentoId))));
        return resultado;
    }

    /// <summary>
    /// Requisitos de los eventos futuros o vigentes de estos Centros para todos los Trabajadores asignados a ellos
    /// (decisiones 2026-10-15 y 2026-10-16, criterio en <see cref="CaeManager.Domain.Proyectos.VigenciaEvento.AportaRequisitosEn"/>):
    /// cuentan en el Centro del evento aunque el Trabajador no tenga Asignación en él, porque participar en un evento no la
    /// crea. Un voluntario inactivo nunca aparece aquí: participa en un evento sin terminar, luego está activo.
    /// </summary>
    private async Task<List<RequisitoDeEvento>> RequisitosDeEventosAsync(
        List<Guid> conGestionCae, DateOnly hoy, CancellationToken cancellationToken) =>
        conGestionCae.Count == 0
            ? []
            : await RequisitosDeEventos
                .DeEventosQueLosAportan(
                    trabajadoresContext.Trabajadores, proyectosContext, requisitosEventoContext.ProyectosRequisitos, tiposDocumentoContext.TiposDocumento, hoy)
                .Where(r => conGestionCae.Contains(r.CentroId))
                .Distinct()
                .ToListAsync(cancellationToken);

    /// <summary>
    /// Causas de los requisitos de eventos futuros o vigentes, con el mismo criterio que las de Trabajador con Asignación
    /// (<see cref="AgregarCausasDeTrabajadorAsync"/>): Faltante si no hay ningún documento operativo de ese tipo, o la
    /// vigencia de cada documento que vence dentro del umbral. No repite una causa que el Centro ya tenga.
    /// </summary>
    private async Task AgregarCausasDeRequisitosDeEventoAsync(
        List<Guid> conGestionCae, DateOnly hoy, int umbralAmbarDias, int umbralRojoDias,
        Dictionary<Guid, List<CausaEstadoCentro>> causasPorCentro, CancellationToken cancellationToken)
    {
        var requisitos = await RequisitosDeEventosAsync(conGestionCae, hoy, cancellationToken);
        if (requisitos.Count == 0) return;

        var trabajadorIds = requisitos.Select(r => r.TrabajadorId).Distinct().ToList();
        var tipoIds = requisitos.Select(r => r.TipoDocumentoId).Distinct().ToList();
        var nombresTipo = await tiposDocumentoContext.TiposDocumento
            .Where(t => tipoIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);
        var documentos = (await documentosContext.Documentos.Operativos()
                .Where(d => d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId!.Value) && tipoIds.Contains(d.TipoDocumentoId))
                .Select(d => new { d.Id, TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId, d.FechaVencimiento })
                .ToListAsync(cancellationToken))
            .ToLookup(d => (d.TrabajadorId, d.TipoDocumentoId));

        foreach (var requisito in requisitos)
        {
            if (!nombresTipo.TryGetValue(requisito.TipoDocumentoId, out var nombreTipo)) continue;
            var causas = causasPorCentro[requisito.CentroId];
            var descripcion = $"{nombreTipo} — {requisito.TrabajadorNombre}";
            var delPar = documentos[(requisito.TrabajadorId, requisito.TipoDocumentoId)].ToList();

            if (delPar.Count == 0)
            {
                if (!causas.Any(c => c.TrabajadorId == requisito.TrabajadorId && c.TipoDocumentoId == requisito.TipoDocumentoId && c.DocumentoId is null))
                    causas.Add(new CausaEstadoCentro(
                        descripcion, EstadoDocumento.Faltante, Bloqueante: false, AmbitoCausa.Trabajador,
                        DocumentoId: null, requisito.TipoDocumentoId, FechaVencimiento: null, requisito.TrabajadorId));
                continue;
            }

            // Ya en memoria y solo los de estos pares: la clasificación es la de CalculadoraEstadoDocumento, sin prefiltro.
            foreach (var documento in delPar.Where(d => d.FechaVencimiento != null))
            {
                var estado = CalculadoraEstadoDocumento.Calcular(
                    VigenciaDocumento.VenceEl(documento.FechaVencimiento!.Value), hoy, umbralAmbarDias, umbralRojoDias);
                if (estado is EstadoDocumento.Vigente) continue;
                if (causas.Any(c => c.TrabajadorId == requisito.TrabajadorId && c.DocumentoId == documento.Id)) continue;

                causas.Add(new CausaEstadoCentro(
                    descripcion, estado, Bloqueante: false, AmbitoCausa.Trabajador,
                    documento.Id, documento.TipoDocumentoId, documento.FechaVencimiento, requisito.TrabajadorId));
            }
        }
    }

    /// <summary>
    /// Los pares Trabajador × Tipo que añaden los eventos futuros o vigentes en su Centro (<see cref="RequisitosDeEventosAsync"/>), con
    /// el estado del documento efectivo, igual que <see cref="ObtenerParesDeTrabajadorAsync"/>.
    /// </summary>
    private async Task<IReadOnlyList<ParDocumentalExigido>> ObtenerParesDeEventoAsync(
        List<Guid> conGestionCae, IReadOnlyList<PendienteEnPlataformaDeCentro> pendientes, DateOnly hoy, CancellationToken cancellationToken)
    {
        var requisitos = await RequisitosDeEventosAsync(conGestionCae, hoy, cancellationToken);
        if (requisitos.Count == 0)
            return [];

        var centroIds = requisitos.Select(r => r.CentroId).Distinct().ToList();
        var clientePorCentro = await ClienteEmpresarialPorCentroAsync(centroIds, cancellationToken);
        var trabajadorIds = requisitos.Select(r => r.TrabajadorId).Distinct().ToList();
        var tipoIds = requisitos.Select(r => r.TipoDocumentoId).Distinct().ToList();
        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var documentosExistentes = await documentosContext.Documentos.Operativos()
            .Where(d => d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId!.Value) && tipoIds.Contains(d.TipoDocumentoId))
            .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.Id, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision, d.CreadoEnUtc })
            .ToListAsync(cancellationToken);
        var efectivos = DocumentoEfectivo.UnoPorClave(
                documentosExistentes, d => (d.TrabajadorId, d.TipoDocumentoId), d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id, hoy)
            .ToDictionary(
                p => p.Key,
                p => CalculadoraEstadoDocumento.Calcular(p.Value.EstadoVigencia, p.Value.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias));
        var pendientesDeTrabajador = pendientes
            .Where(p => p.TrabajadorId is not null)
            .Select(p => (p.CentroId, TrabajadorId: p.TrabajadorId!.Value, p.TipoDocumentoId))
            .ToHashSet();

        return requisitos
            .Where(r => clientePorCentro.ContainsKey(r.CentroId))
            .Select(r =>
            {
                var tieneEfectivo = efectivos.TryGetValue((r.TrabajadorId, r.TipoDocumentoId), out var estado);
                return new ParDocumentalExigido(
                    r.CentroId, clientePorCentro[r.CentroId], r.EmpresaId, r.TrabajadorId, r.TipoDocumentoId,
                    tieneEfectivo ? estado : EstadoDocumento.Faltante,
                    tieneEfectivo && pendientesDeTrabajador.Contains((r.CentroId, r.TrabajadorId, r.TipoDocumentoId)));
            })
            .ToList();
    }

    /// <summary>
    /// Los pares Empresa × Tipo de ámbito Empresa de cada Centro con gestión CAE (decisión del propietario, 2026-10-10: el
    /// Pendiente de Empresa baja el porcentaje del Centro y los documentos de Empresa entran en su denominador): cada Empresa
    /// con algún Trabajador de Asignación activa en el Centro (la del Trabajador, o su Subcontrata si no tiene Empresa, como en
    /// <see cref="PendientesEnPlataformaDeCentros"/>) × cada tipo de ámbito Empresa que le aplica al Centro
    /// (<see cref="ResolucionTipoDocumentoCentro.Aplica"/>, la misma regla que los pares de Trabajador). El estado es el del
    /// documento efectivo de la Empresa y el tipo (<see cref="DocumentoEfectivo"/>), o Faltante.
    /// </summary>
    private async Task<IReadOnlyList<ParDocumentalDeEmpresaExigido>> ObtenerParesDeEmpresaAsync(
        List<Guid> conGestionCae, IReadOnlyList<PendienteEnPlataformaDeCentro> pendientes, DateOnly hoy, CancellationToken cancellationToken)
    {
        if (conGestionCae.Count == 0)
            return [];

        var empresasPorCentro = (await (
            from asignacion in AsignacionesQueCuentan()
            where asignacion.FechaBaja == null && conGestionCae.Contains(asignacion.CentroId)
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            select new { asignacion.CentroId, trabajador.EmpresaId, trabajador.SubcontrataId })
            .ToListAsync(cancellationToken))
            .Select(a => (a.CentroId, EmpresaId: a.EmpresaId ?? a.SubcontrataId))
            .Where(a => a.EmpresaId is not null)
            .Select(a => (a.CentroId, EmpresaId: a.EmpresaId!.Value))
            .Distinct()
            .ToList();

        if (empresasPorCentro.Count == 0)
            return [];

        var tiposCandidatos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Empresa)
            .Select(t => new { t.Id, CuentaParaCumplimiento = t.Requerido == RequisitoDocumental.Si })
            .ToListAsync(cancellationToken);

        if (tiposCandidatos.Count == 0)
            return [];

        var tipoIdsCandidatos = tiposCandidatos.Select(t => t.Id).ToHashSet();
        var filasPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIdsCandidatos.Contains(tc.TipoDocumentoId) && conGestionCae.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        var empresaIds = empresasPorCentro.Select(e => e.EmpresaId).Distinct().ToList();
        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var documentos = await documentosContext.Documentos.Operativos()
            .Where(d => d.TrabajadorId == null && d.EmpresaId != null
                && empresaIds.Contains(d.EmpresaId!.Value)
                && tipoIdsCandidatos.Contains(d.TipoDocumentoId))
            .Select(d => new { EmpresaId = d.EmpresaId!.Value, d.Id, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision, d.CreadoEnUtc })
            .ToListAsync(cancellationToken);
        var efectivos = DocumentoEfectivo.UnoPorClave(
                documentos, d => (d.EmpresaId, d.TipoDocumentoId), d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id, hoy)
            .ToDictionary(
                p => p.Key,
                p => CalculadoraEstadoDocumento.Calcular(p.Value.EstadoVigencia, p.Value.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias));

        var pendientesDeEmpresa = pendientes
            .Where(p => p.TrabajadorId is null && p.EmpresaId is not null)
            .Select(p => (p.CentroId, EmpresaId: p.EmpresaId!.Value, p.TipoDocumentoId))
            .ToHashSet();

        var pares = new List<ParDocumentalDeEmpresaExigido>();
        foreach (var (centroId, empresaId) in empresasPorCentro)
        {
            foreach (var tipo in tiposCandidatos)
            {
                if (!ResolucionTipoDocumentoCentro.Aplica(filasPorPar, tipo.Id, centroId, tipo.CuentaParaCumplimiento))
                    continue;

                var tieneEfectivo = efectivos.TryGetValue((empresaId, tipo.Id), out var estadoEfectivo);
                pares.Add(new ParDocumentalDeEmpresaExigido(
                    centroId, empresaId, tipo.Id,
                    tieneEfectivo ? estadoEfectivo : EstadoDocumento.Faltante,
                    tieneEfectivo && pendientesDeEmpresa.Contains((centroId, empresaId, tipo.Id))));
            }
        }

        return pares;
    }

    private async Task<IReadOnlyList<ParDocumentalExigido>> ObtenerParesDeTrabajadorAsync(
        IReadOnlyList<Guid> centroIds, List<Guid> conGestionCae, IReadOnlyList<PendienteEnPlataformaDeCentro> pendientes, DateOnly hoy,
        CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0)
            return [];

        var asignacionesActivas = await (
            from asignacion in AsignacionesQueCuentan()
            where asignacion.FechaBaja == null && conGestionCae.Contains(asignacion.CentroId)
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            select new { asignacion.CentroId, asignacion.TrabajadorId, trabajador.EmpresaId })
            .ToListAsync(cancellationToken);

        if (asignacionesActivas.Count == 0)
            return [];

        var clientePorCentro = await ClienteEmpresarialPorCentroAsync(
            asignacionesActivas.Select(a => a.CentroId).Distinct().ToList(), cancellationToken);

        var tiposCandidatos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new { t.Id, CuentaParaCumplimiento = t.Requerido == RequisitoDocumental.Si })
            .ToListAsync(cancellationToken);

        if (tiposCandidatos.Count == 0)
            return [];

        var tipoIdsCandidatos = tiposCandidatos.Select(t => t.Id).ToHashSet();

        var filasPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIdsCandidatos.Contains(tc.TipoDocumentoId) && centroIds.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        var trabajadorIds = asignacionesActivas.Select(a => a.TrabajadorId).Distinct().ToList();
        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);

        var documentosExistentes = await documentosContext.Documentos.Operativos()
            .Where(d => d.TrabajadorId != null
                && trabajadorIds.Contains(d.TrabajadorId!.Value)
                && tipoIdsCandidatos.Contains(d.TipoDocumentoId))
            .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.Id, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision, d.CreadoEnUtc })
            .ToListAsync(cancellationToken);

        // Un solo documento operativo por par en el caso normal (el vencido y su renovación ya no coexisten: el
        // anterior pasa al historial); con duplicados aún sin resolver manda el documento efectivo (DocumentoEfectivo),
        // el mismo que elige el paquete de acreditación de la Visita.
        var efectivos = DocumentoEfectivo.UnoPorClave(
                documentosExistentes, d => (d.TrabajadorId, d.TipoDocumentoId), d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id, hoy)
            .ToDictionary(
                p => p.Key,
                p => (p.Value.Id, Estado: CalculadoraEstadoDocumento.Calcular(p.Value.EstadoVigencia, p.Value.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)));

        var pendientesQueBajanElPorcentaje = pendientes
            .Where(p => p.TrabajadorId is not null)
            .Select(p => (p.CentroId, TrabajadorId: p.TrabajadorId!.Value, p.TipoDocumentoId))
            .ToHashSet();

        var pares = new List<ParDocumentalExigido>();
        // Como el join con Centros de antes: una Asignación a un Centro que no se lee no aporta pares.
        foreach (var asignacion in asignacionesActivas.Where(a => clientePorCentro.ContainsKey(a.CentroId)))
        {
            foreach (var tipo in tiposCandidatos)
            {
                if (!ResolucionTipoDocumentoCentro.Aplica(filasPorPar, tipo.Id, asignacion.CentroId, tipo.CuentaParaCumplimiento))
                    continue;

                // Sin documento del par, Faltante. El estado lo cuenta CumplimientoDocumental, no esta clase.
                var tieneEfectivo = efectivos.TryGetValue((asignacion.TrabajadorId, tipo.Id), out var efectivo);
                var estado = tieneEfectivo ? efectivo.Estado : EstadoDocumento.Faltante;
                var pendienteEnPlataforma = tieneEfectivo
                    && pendientesQueBajanElPorcentaje.Contains((asignacion.CentroId, asignacion.TrabajadorId, tipo.Id));

                pares.Add(new ParDocumentalExigido(
                    asignacion.CentroId, clientePorCentro[asignacion.CentroId], asignacion.EmpresaId, asignacion.TrabajadorId, tipo.Id, estado,
                    pendienteEnPlataforma));
            }
        }

        return pares;
    }

    /// <summary>El Cliente empresarial de cada Centro, que lleva cada par exigido; una sola lectura para las dos vías.</summary>
    private Task<Dictionary<Guid, Guid>> ClienteEmpresarialPorCentroAsync(List<Guid> centroIds, CancellationToken cancellationToken) =>
        centrosContext.Centros
            .Where(c => centroIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.ClienteId, cancellationToken);
}

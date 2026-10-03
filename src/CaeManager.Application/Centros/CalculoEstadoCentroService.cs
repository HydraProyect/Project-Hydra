using CaeManager.Domain.Common;
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
/// hueco total (ningún Documento) de un Tipo exigido por el Centro.
/// Un documento de Trabajador o de Empresa, ausente o vencido, bloquea a PERSONAS
/// (<see cref="ReglaBloqueoDeAcceso"/> por Centro, vía <see cref="IEvaluacionDeAccesoPorCentroService"/>: Mi trabajo y el detalle
/// por Trabajador del Centro 360) y NUNCA pone el Centro en Bloqueado (decisión del propietario, 2026-10-03): «Bloqueado» es un
/// estado del Trabajador. <see cref="CausaEstadoCentro.Bloqueante"/> queda solo para las causas que vienen de la plataforma
/// del Cliente empresarial (vigencia vencida y acreditación rechazada, D-7).
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
    /// Cumplimiento documental de cada Centro (Centro 360, Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md
    /// § 0.5): <c>Requeridos</c> son los pares Trabajador×TipoDocumento que el Centro exige a sus Trabajadores con
    /// Asignación activa y <c>AlDia</c> los que <see cref="CumplimientoDocumental"/> cuenta como al día. Es el contexto
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
    /// preferido: el universo con el que se mide CUALQUIER contexto de <see cref="ContextoCumplimiento"/>
    /// (<see cref="CumplimientoDocumental.De"/>). Un Empresa o un Cliente empresarial se obtienen agrupando estos pares,
    /// no recalculando el universo.
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
    IConfiguracionQueryContext configuracionContext)
    : ICalculoEstadoCentroService
{
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
            await AgregarCausasDeVigenciaEnPlataformaAsync(conGestionCae, hoy, causasPorCentro, cancellationToken);
            await AgregarCausasDeRechazoEnPlataformaAsync(conGestionCae, causasPorCentro, cancellationToken);
        }

        var resultado = causasPorCentro.ToDictionary(
            par => par.Key,
            par => new ResultadoEstadoCentro(
                CalculadoraEstadoCentro.Calcular(
                    par.Value.Where(c => c.Estado is not null).Select(c => c.Estado!.Value).ToList(),
                    par.Value.Any(c => c.Bloqueante)),
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
        //
        // Con vencimiento más allá del umbral ámbar el estado es SIEMPRE
        // Vigente (CalculadoraEstadoDocumento) y el bucle de abajo lo salta:
        // la cota superior lo deja en PostgreSQL en vez de traerlo y
        // descartarlo aquí (mismo límite que ObtenerAlertasQuery; P1-D1).
        var fechaLimiteCausa = hoy.AddDays(umbralAmbarDias);
        var documentosTrabajador = (await (
            from documento in documentosContext.Documentos
            where documento.TrabajadorId != null && trabajadorIds.Contains(documento.TrabajadorId!.Value)
            // Solo con fecha: mismo criterio que en AgregarCausasDeEmpresaAsync.
            where documento.FechaVencimiento != null && documento.FechaVencimiento <= fechaLimiteCausa
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new { TrabajadorId = documento.TrabajadorId!.Value, documento.FechaVencimiento, tipoDocumento.Nombre })
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
                        DocumentoId: null, TipoDocumentoId: null, FechaVencimiento: null));
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

                // Aunque la fila marque BloqueaAcceso, la falta total de un documento es un «Falta documentación» del Centro,
                // no un «Bloqueado»: quien queda bloqueado es el Trabajador (ReglaBloqueoDeAcceso, por Centro).
                causasPorCentro[asignacion.CentroId].Add(new CausaEstadoCentro(
                    $"{tipo.Nombre} — {asignacion.TrabajadorNombre}", EstadoDocumento.Faltante, Bloqueante: false, AmbitoCausa.Trabajador,
                    DocumentoId: null, TipoDocumentoId: null, FechaVencimiento: null));
            }
        }
    }

    /// <summary>
    /// Alcance igual al de "huecos obligatorios" de <see cref="AgregarCausasDeTrabajadorAsync"/>
    /// (Trabajador únicamente, sin Documentos de Empresa — así lo pide
    /// Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.5: "por trabajador dentro de un centro"),
    /// pero contando el universo completo de pares aplicables en vez de solo
    /// los que fallan.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken)
    {
        var pares = await ObtenerParesExigidosAsync(centroIds, cancellationToken);
        var porCentro = CumplimientoDocumental.PorContexto(ContextoCumplimiento.Centro, pares);

        // Un Centro sin ningún par (sin gestión CAE, sin Trabajadores o sin tipos exigidos) sigue en el resultado, en
        // 0/0: su porcentaje es null («sin requisitos»), nunca un 100 %.
        return centroIds.Distinct().ToDictionary(id => id, id => porCentro.GetValueOrDefault(id, FraccionCumplimiento.SinRequisitos));
    }

    public async Task<IReadOnlyList<ParDocumentalExigido>> ObtenerParesExigidosAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0)
            return [];

        // P1-X2: un Centro sin gestión CAE no exige nada — no aporta ningún par.
        var sinGestionCae = await CentrosSinGestionCae.FiltrarAsync(centrosContext, centroIds, cancellationToken);
        var conGestionCae = centroIds.Where(id => !sinGestionCae.Contains(id)).Distinct().ToList();

        var asignacionesActivas = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null && conGestionCae.Contains(asignacion.CentroId)
            join centro in centrosContext.Centros on asignacion.CentroId equals centro.Id
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            select new { asignacion.CentroId, ClienteEmpresarialId = centro.ClienteId, asignacion.TrabajadorId, trabajador.EmpresaId })
            .ToListAsync(cancellationToken);

        if (asignacionesActivas.Count == 0)
            return [];

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
        var hoy = DiaDeNegocio.Hoy();

        var documentosExistentes = await documentosContext.Documentos
            .Where(d => d.TrabajadorId != null
                && trabajadorIds.Contains(d.TrabajadorId!.Value)
                && tipoIdsCandidatos.Contains(d.TipoDocumentoId))
            .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision })
            .ToListAsync(cancellationToken);

        // Puede haber varios por par (el vencido y su renovación): el índice
        // (TrabajadorId, TipoDocumentoId) no es único y la subida no rechaza un
        // segundo documento del mismo tipo. Manda el que decide PreferenciaDocumentoPorTipo
        // (la copia que mejor cumple: estado y vigencia). NO es el que elige el paquete de
        // acreditación, que usa PreferenciaCopiaDelPaquete (emisión más reciente primero).
        var estadosPorPareja = PreferenciaDocumentoPorTipo.UnoPorClave(
                documentosExistentes, d => (d.TrabajadorId, d.TipoDocumentoId), d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision, hoy)
            .ToDictionary(
                p => p.Key,
                p => CalculadoraEstadoDocumento.Calcular(p.Value.EstadoVigencia, p.Value.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias));

        var pares = new List<ParDocumentalExigido>();
        foreach (var asignacion in asignacionesActivas)
        {
            foreach (var tipo in tiposCandidatos)
            {
                if (!ResolucionTipoDocumentoCentro.Aplica(filasPorPar, tipo.Id, asignacion.CentroId, tipo.CuentaParaCumplimiento))
                    continue;

                // Sin documento del par, Faltante. El estado lo cuenta CumplimientoDocumental, no esta clase.
                var estado = estadosPorPareja.TryGetValue((asignacion.TrabajadorId, tipo.Id), out var calculado)
                    ? calculado
                    : EstadoDocumento.Faltante;

                pares.Add(new ParDocumentalExigido(
                    asignacion.CentroId, asignacion.ClienteEmpresarialId, asignacion.EmpresaId, asignacion.TrabajadorId, tipo.Id, estado));
            }
        }

        return pares;
    }
}

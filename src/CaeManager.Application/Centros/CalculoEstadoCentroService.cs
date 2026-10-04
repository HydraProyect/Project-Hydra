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
/// Un documento de Trabajador o de Empresa, ausente o vencido, y el veredicto de la plataforma del Cliente empresarial
/// (vigencia vencida allí o acreditación rechazada, D-7) bloquean a PERSONAS (<see cref="ReglaBloqueoDeAcceso"/> por Centro, vía
/// <see cref="IEvaluacionDeAccesoPorCentroService"/>: Mi trabajo y el detalle por Trabajador del Centro 360) y NUNCA ponen el
/// Centro en Bloqueado (decisiones del propietario, 2026-10-03 y 2026-10-04): «Bloqueado» es un estado del Trabajador. Por eso
/// la causa ya no lleva marca de bloqueo.
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
    string Descripcion, EstadoDocumento? Estado, AmbitoCausa Ambito,
    Guid? DocumentoId, Guid? TipoDocumentoId, DateOnly? FechaVencimiento);

public record ResultadoEstadoCentro(EstadoCentro Estado, IReadOnlyList<CausaEstadoCentro> Causas);

/// <summary>
/// Cálculo compartido entre ObtenerCentrosQuery (badge de la tabla) y
/// ObtenerEstadoCentroQuery (desglose del Workspace) — agrega de una sola
/// vez los Documentos de Empresa, los Documentos y huecos obligatorios de
/// cada Trabajador con Asignación activa de uno o varios Centros, y la vigencia
/// vencida en la plataforma, para no lanzar N consultas al pintar
/// una página de la tabla. La lógica de "documento faltante" replica la de
/// ObtenerAlertasQuery.ObtenerFaltantesAsync (Trabajador únicamente — los
/// Documentos de Empresa aquí solo aportan su vigencia, sin detección de
/// falta total, mismo alcance que esa Query). La vigencia vencida en la plataforma
/// del Cliente empresarial es una causa «vencido» como cualquier otra; el veredicto de esa
/// plataforma sobre una persona (rechazo, vencimiento) bloquea al Trabajador, no al Centro.
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
        }

        var resultado = causasPorCentro.ToDictionary(
            par => par.Key,
            par => new ResultadoEstadoCentro(
                CalculadoraEstadoCentro.Calcular(
                    par.Value.Where(c => c.Estado is not null).Select(c => c.Estado!.Value).ToList()),
                par.Value));

        foreach (var centroId in sinGestionCae)
            resultado[centroId] = new ResultadoEstadoCentro(EstadoCentro.SinGestionCae, []);

        return resultado;
    }

    /// <summary>
    /// Un documento cuya vigencia <b>en la plataforma</b> ya venció es una causa «vencido» del Centro, aunque en TALVEG siga
    /// vigente por su fecha de emisión: el semáforo existe para decir si se puede trabajar, no para describir el archivo
    /// documental. NO pone el Centro en «Bloqueado» (decisión del propietario, 2026-10-04): si el portal no lo acepta, el
    /// Trabajador no entra, y eso lo dice el bloqueo por Trabajador del Centro (<see cref="IEvaluacionDeAccesoPorCentroService"/>,
    /// que lee el mismo veredicto en <see cref="AcreditacionesEnPlataformaDeCentros"/>). Lo que sigue aquí es el color «Vencido»
    /// del Centro y su incidencia, igual que un documento vencido en TALVEG.
    ///
    /// <para>
    /// Solo cuentan las vigencias <b>vencidas</b>, nunca las que están sin confirmar. Meter «no lo sé» en rojo pondría en rojo
    /// todos los Centros a la vez el día que se despliegue, porque las acreditaciones que ya existen nacen sin confirmar. No se
    /// filtra por <c>EstadoAcreditacion</c>: la condición es la fecha (ver <see cref="AcreditacionesEnPlataformaDeCentros"/>).
    /// </para>
    ///
    /// <para>
    /// El rechazo de la plataforma NO es una causa del Centro: no describe un documento vencido ni falta, es un veredicto sobre un
    /// Trabajador o una Empresa y vive en su bloqueo por Trabajador (mismo motivo que lo retiró de «Bloqueado»).
    /// </para>
    /// </summary>
    private async Task AgregarCausasDeVigenciaEnPlataformaAsync(
        IReadOnlyList<Guid> centroIds, DateOnly hoy,
        Dictionary<Guid, List<CausaEstadoCentro>> causasPorCentro, CancellationToken cancellationToken)
    {
        // Un documento de Trabajador solo cuenta si ese Trabajador sigue
        // asignado al Centro: la acreditación sobrevive a la baja, y sin este
        // filtro una vigencia vencida marcaría un Centro por alguien que ya
        // no trabaja ahí. Es el mismo criterio que usa el resto del cálculo de
        // documentos de Trabajador; los de Empresa no dependen de asignaciones.
        var asignacionesActivas = await asignacionesContext.Asignaciones
            .Where(a => a.FechaBaja == null && centroIds.Contains(a.CentroId))
            .Select(a => new { a.CentroId, a.TrabajadorId })
            .ToListAsync(cancellationToken);

        var trabajadoresPorCentro = asignacionesActivas
            .GroupBy(a => a.CentroId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.TrabajadorId).ToHashSet());

        var vencidasEnPlataforma = await AcreditacionesEnPlataformaDeCentros.LeerVencidasAsync(
            documentosContext, centrosContext, tiposDocumentoContext, centroIds, hoy, cancellationToken);

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
                fila.TrabajadorId is null ? AmbitoCausa.Empresa : AmbitoCausa.Trabajador,
                fila.DocumentoId, fila.TipoDocumentoId, fila.VencimientoEnPlataforma));
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
                $"{documento.Nombre} — Empresa", estado, AmbitoCausa.Empresa,
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
            from documento in documentosContext.Documentos.Operativos()
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
                        $"{documento.Nombre} — {asignacion.TrabajadorNombre}", estado, AmbitoCausa.Trabajador,
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
                    $"{tipo.Nombre} — {asignacion.TrabajadorNombre}", EstadoDocumento.Faltante, AmbitoCausa.Trabajador,
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

        var documentosExistentes = await documentosContext.Documentos.Operativos()
            .Where(d => d.TrabajadorId != null
                && trabajadorIds.Contains(d.TrabajadorId!.Value)
                && tipoIdsCandidatos.Contains(d.TipoDocumentoId))
            .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.Id, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision, d.CreadoEnUtc })
            .ToListAsync(cancellationToken);

        // Un solo documento operativo por par en el caso normal (el vencido y su renovación ya no coexisten: el
        // anterior pasa al historial); con duplicados aún sin resolver manda el documento efectivo (DocumentoEfectivo),
        // el mismo que elige el paquete de acreditación de la Visita.
        var estadosPorPareja = DocumentoEfectivo.UnoPorClave(
                documentosExistentes, d => (d.TrabajadorId, d.TipoDocumentoId), d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id, hoy)
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

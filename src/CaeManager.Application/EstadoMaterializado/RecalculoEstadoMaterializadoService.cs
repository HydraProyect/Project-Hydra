using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Domain.Proyectos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.EstadoMaterializado;

/// <inheritdoc cref="IRecalculoEstadoMaterializado"/>
/// <remarks>
/// Solo lee proyecciones (nunca entidades), así que el <c>DbContext</c> de vida larga de un circuito no le devuelve
/// instancias rastreadas con valores viejos (diseño § 4.3, revisión A4).
/// </remarks>
public sealed class RecalculoEstadoMaterializadoService(
    ICalculoEstadoCentroService calculoEstadoCentro,
    ICalculoEstadoDocumentalService calculoEstadoDocumental,
    IEvaluacionDeAccesoDelTenant evaluacionDeAcceso,
    ICentrosQueryContext centrosContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    IConfiguracionQueryContext configuracionContext,
    IEmpresasQueryContext empresasContext,
    IProyectosQueryContext proyectosContext,
    IRequisitosEventoQueryContext requisitosEventoContext,
    IEscritorEstadoMaterializado escritor)
    : IRecalculoEstadoMaterializado
{
    /// <summary>Lote de Centros del relleno inicial (diseño § 7.3).</summary>
    public const int LoteDeCentrosDelRelleno = 200;

    private const int LoteDeTitularesDelRelleno = 2000;

    public async Task<CalculoEstadoMaterializado> CalcularAsync(RaicesDeRecalculo raices, CancellationToken cancellationToken)
    {
        if (raices.EstaVacia)
            return Vacio(DiaDeNegocio.Hoy());

        var (centros, trabajadores, empresas) = await ClausuraAsync(raices, cancellationToken);
        return await CalcularConjuntoAsync(centros, trabajadores, empresas, cancellationToken);
    }

    public async Task<ResultadoRecalculoEstado> RecalcularAsync(RaicesDeRecalculo raices, CancellationToken cancellationToken)
    {
        var calculo = await CalcularAsync(raices, cancellationToken);
        // Si el día de negocio cambió mientras se calculaba (la medianoche de Madrid), se calcula otra vez con el día nuevo:
        // un estado escrito con el día anterior se quedaría sin recoger hasta la pasada siguiente (diseño § 2.2, M3).
        if (calculo.Dia != DiaDeNegocio.Hoy())
            calculo = await CalcularAsync(raices, cancellationToken);

        return await EscribirAsync(calculo, crearVersionSiFalta: false, cancellationToken);
    }

    public async Task<ResultadoRecalculoEstado> RellenarTenantAsync(CancellationToken cancellationToken)
    {
        var resultado = ResultadoRecalculoEstado.Ninguno;

        var centroIds = await centrosContext.Centros.OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(cancellationToken);
        foreach (var lote in centroIds.Chunk(LoteDeCentrosDelRelleno))
            resultado = resultado.Mas(await EscribirAsync(
                await CalcularConjuntoAsync(lote, [], [], cancellationToken), crearVersionSiFalta: false, cancellationToken));

        var trabajadorIds = await trabajadoresContext.Trabajadores.OrderBy(t => t.Id).Select(t => t.Id).ToListAsync(cancellationToken);
        foreach (var lote in trabajadorIds.Chunk(LoteDeTitularesDelRelleno))
            resultado = resultado.Mas(await EscribirAsync(
                await CalcularConjuntoAsync([], lote, [], cancellationToken), crearVersionSiFalta: false, cancellationToken));

        var empresaIds = await empresasContext.Empresas.OrderBy(e => e.Id).Select(e => e.Id).ToListAsync(cancellationToken);
        var lotesDeEmpresa = empresaIds.Chunk(LoteDeTitularesDelRelleno).ToList();
        // Siempre al menos una escritura final, aunque el Tenant no tenga Empresas: es la que crea la fila de versión.
        if (lotesDeEmpresa.Count == 0)
            lotesDeEmpresa.Add([]);
        for (var i = 0; i < lotesDeEmpresa.Count; i++)
            resultado = resultado.Mas(await EscribirAsync(
                await CalcularConjuntoAsync([], [], lotesDeEmpresa[i], cancellationToken),
                crearVersionSiFalta: i == lotesDeEmpresa.Count - 1, cancellationToken));

        return resultado;
    }

    private async Task<ResultadoRecalculoEstado> EscribirAsync(
        CalculoEstadoMaterializado calculo, bool crearVersionSiFalta, CancellationToken cancellationToken)
    {
        await escritor.EscribirAsync(calculo, crearVersionSiFalta, cancellationToken);
        return new ResultadoRecalculoEstado(
            calculo.Entidades.Count(e => e.Ambito == AmbitoEstadoEntidad.Centro),
            calculo.Entidades.Count(e => e.Ambito == AmbitoEstadoEntidad.Trabajador),
            calculo.Entidades.Count(e => e.Ambito == AmbitoEstadoEntidad.Empresa),
            calculo.Pares.Count,
            calculo.Plataforma.Count);
    }

    private static CalculoEstadoMaterializado Vacio(DateOnly dia) => new(dia, [], [], [], [], [], []);

    /// <summary>
    /// Todo lo que depende de las raíces, leído con el contexto del Tenant de la conexión (un identificador de otro Tenant no
    /// devuelve nada). Por encima de lo estrictamente afectado, nunca por debajo: una fila de más solo cuesta tiempo.
    /// <list type="bullet">
    /// <item>Centros: los raíz; los de la Empresa raíz (<c>Centro.EmpresaId</c>, cuyos documentos son causa del Centro); y
    /// aquellos donde un Trabajador raíz o de una Empresa raíz tiene Asignación activa o participa en un evento sin
    /// terminar.</item>
    /// <item>Trabajadores: los raíz, y los de los Centros raíz (sus requisitos «bloquea el acceso» deciden su rojo).</item>
    /// <item>Empresas: las raíz, y las de los Trabajadores raíz o de los Centros raíz (dónde opera una Empresa decide qué
    /// requisitos la ponen en rojo).</item>
    /// </list>
    /// </summary>
    private async Task<(List<Guid> Centros, List<Guid> Trabajadores, List<Guid> Empresas)> ClausuraAsync(
        RaicesDeRecalculo raices, CancellationToken cancellationToken)
    {
        var centroRaiz = raices.CentroIds.Distinct().ToList();
        var trabajadorRaiz = raices.TrabajadorIds.Distinct().ToList();
        var empresaRaiz = raices.EmpresaIds.Distinct().ToList();
        var hoy = DiaDeNegocio.Hoy();

        var centros = centroRaiz.ToHashSet();
        var trabajadores = trabajadorRaiz.ToHashSet();
        var empresas = empresaRaiz.ToHashSet();

        if (empresaRaiz.Count > 0)
            centros.UnionWith(await centrosContext.Centros
                .Where(c => empresaRaiz.Contains(c.EmpresaId))
                .Select(c => c.Id)
                .ToListAsync(cancellationToken));

        var asignaciones = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where centroRaiz.Contains(asignacion.CentroId)
                  || trabajadorRaiz.Contains(trabajador.Id)
                  || (trabajador.EmpresaId != null && empresaRaiz.Contains(trabajador.EmpresaId.Value))
                  || (trabajador.SubcontrataId != null && empresaRaiz.Contains(trabajador.SubcontrataId.Value))
            select new { asignacion.CentroId, asignacion.TrabajadorId, EmpresaId = trabajador.EmpresaId ?? trabajador.SubcontrataId })
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var a in asignaciones)
        {
            var porCentro = centroRaiz.Contains(a.CentroId);
            if (porCentro || trabajadorRaiz.Contains(a.TrabajadorId) || (a.EmpresaId is { } e && empresaRaiz.Contains(e)))
                centros.Add(a.CentroId);
            if (porCentro)
                trabajadores.Add(a.TrabajadorId);
            if (a.EmpresaId is { } empresaId && (porCentro || trabajadorRaiz.Contains(a.TrabajadorId)))
                empresas.Add(empresaId);
        }

        // Eventos sin terminar: el Centro del evento exige a sus participantes aunque no tengan Asignación en él.
        var participaciones = await ParticipacionesEnEventosAsync(centroRaiz, trabajadorRaiz, hoy, cancellationToken);
        foreach (var p in participaciones)
        {
            if (trabajadorRaiz.Contains(p.TrabajadorId))
                centros.Add(p.CentroId);
            if (centroRaiz.Contains(p.CentroId))
                trabajadores.Add(p.TrabajadorId);
        }

        // Lo ya escrito de los titulares raíz: la acción puede haber roto la relación (sustituir el documento acreditado de un
        // Trabajador que ya no está asignado al Centro) y la fila vieja tiene que rehacerse.
        if (trabajadorRaiz.Count > 0 || empresaRaiz.Count > 0)
            centros.UnionWith(await escritor.CentrosConFilasDeTitularesAsync(trabajadorRaiz.Concat(empresaRaiz).ToList(), cancellationToken));

        return (centros.ToList(), trabajadores.ToList(), empresas.ToList());
    }

    private sealed record Participacion(Guid CentroId, Guid TrabajadorId, DateOnly? FechaFinPrevista);

    /// <summary>Participaciones activas en eventos sin terminar, de esos Centros o de esos Trabajadores.</summary>
    private async Task<List<Participacion>> ParticipacionesEnEventosAsync(
        IReadOnlyCollection<Guid> centroIds, IReadOnlyCollection<Guid> trabajadorIds, DateOnly hoy, CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0 && trabajadorIds.Count == 0)
            return [];

        return await (
            from proyecto in proyectosContext.Proyectos.Where(VigenciaEvento.SinTerminarEn(hoy))
            join participante in proyectosContext.ProyectosTecnicos on proyecto.Id equals participante.ProyectoId
            where participante.FechaBaja == null
                  && (centroIds.Contains(proyecto.CentroId) || trabajadorIds.Contains(participante.TrabajadorId))
            select new Participacion(proyecto.CentroId, participante.TrabajadorId, proyecto.FechaFinPrevista))
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    private sealed record DocumentoLeido(
        Guid Id, Guid? TrabajadorId, Guid? EmpresaId, Guid TipoDocumentoId, EstadoVigenciaDocumento EstadoVigencia,
        DateOnly? FechaVencimiento, DateOnly FechaEmision, DateTime CreadoEnUtc, bool EsRenuncia);

    private async Task<CalculoEstadoMaterializado> CalcularConjuntoAsync(
        IReadOnlyCollection<Guid> centroIdsPedidos, IReadOnlyCollection<Guid> trabajadorIdsPedidos,
        IReadOnlyCollection<Guid> empresaIdsPedidos, CancellationToken cancellationToken)
    {
        var hoy = DiaDeNegocio.Hoy();
        var centroIdsLista = centroIdsPedidos.Distinct().ToList();
        var trabajadorIdsLista = trabajadorIdsPedidos.Distinct().ToList();
        var empresaIdsLista = empresaIdsPedidos.Distinct().ToList();
        if (centroIdsLista.Count == 0 && trabajadorIdsLista.Count == 0 && empresaIdsLista.Count == 0)
            return Vacio(hoy);

        var parametros = await configuracionContext.ParametrosSistema
            .Select(p => new { p.UmbralAmbarDias, p.UmbralRojoDias })
            .SingleAsync(cancellationToken);
        DateOnly? Frontera(DocumentoLeido d) =>
            FronteraEstadoDocumento.ProximoCambio(d.EstadoVigencia, d.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias);

        // Solo las entidades que existen (el filtro global quita las borradas): las pedidas que no, se quedan sin fila.
        var centros = centroIdsLista.Count == 0
            ? []
            : await centrosContext.Centros
                .Where(c => centroIdsLista.Contains(c.Id))
                .Select(c => new { c.Id, c.EmpresaId })
                .ToListAsync(cancellationToken);
        var centroIds = centros.Select(c => c.Id).ToList();
        var trabajadorIds = trabajadorIdsLista.Count == 0
            ? []
            : await trabajadoresContext.Trabajadores.Where(t => trabajadorIdsLista.Contains(t.Id)).Select(t => t.Id).ToListAsync(cancellationToken);
        var empresaIds = empresaIdsLista.Count == 0
            ? []
            : await empresasContext.Empresas.Where(e => empresaIdsLista.Contains(e.Id)).Select(e => e.Id).ToListAsync(cancellationToken);

        // ---- Lo que hoy calculan las lecturas, sin otra copia de la regla ----
        var estadoDeCentros = await calculoEstadoCentro.CalcularAsync(centroIds, cancellationToken);
        var cumplimientoDeCentros = await calculoEstadoCentro.CalcularCumplimientoAsync(centroIds, cancellationToken);
        var paresDeTrabajador = (await calculoEstadoCentro.ObtenerParesExigidosAsync(centroIds, cancellationToken))
            .DistinctBy(p => (p.CentroId, p.TrabajadorId, p.TipoDocumentoId))
            .ToList();
        var paresDeEmpresa = (await calculoEstadoCentro.ObtenerParesDeEmpresaExigidosAsync(centroIds, cancellationToken))
            .DistinctBy(p => (p.CentroId, p.EmpresaId, p.TipoDocumentoId))
            .ToList();

        // ---- Actividad: Asignaciones activas y eventos sin terminar de los Centros y Trabajadores calculados ----
        var asignaciones = centroIds.Count == 0 && trabajadorIds.Count == 0 && empresaIds.Count == 0
            ? []
            : await (
                from asignacion in asignacionesContext.Asignaciones
                where asignacion.FechaBaja == null
                join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
                where centroIds.Contains(asignacion.CentroId)
                      || trabajadorIds.Contains(trabajador.Id)
                      || (trabajador.EmpresaId != null && empresaIds.Contains(trabajador.EmpresaId.Value))
                      || (trabajador.SubcontrataId != null && empresaIds.Contains(trabajador.SubcontrataId.Value))
                select new { asignacion.CentroId, asignacion.TrabajadorId, EmpresaId = trabajador.EmpresaId ?? trabajador.SubcontrataId })
                .Distinct()
                .ToListAsync(cancellationToken);
        // También los eventos de otros Centros de los Trabajadores asignados aquí: el fin de cualquiera de ellos puede dejar sin
        // contar a un voluntario (voluntario inactivo sin evento) y es frontera de sus pares y del Centro.
        var participaciones = await ParticipacionesEnEventosAsync(
            centroIds, trabajadorIds.Concat(asignaciones.Select(a => a.TrabajadorId)).Distinct().ToList(), hoy, cancellationToken);
        var trabajadoresDeCentro = asignaciones.Where(a => centroIds.Contains(a.CentroId))
            .Select(a => (a.CentroId, a.TrabajadorId))
            .Concat(participaciones.Where(p => centroIds.Contains(p.CentroId)).Select(p => (p.CentroId, p.TrabajadorId)))
            .ToLookup(a => a.CentroId, a => a.TrabajadorId);
        var empresasDeCentro = asignaciones.Where(a => centroIds.Contains(a.CentroId) && a.EmpresaId is not null)
            .ToLookup(a => a.CentroId, a => a.EmpresaId!.Value);

        // Centros donde están asignados los Trabajadores y operan las Empresas calculados: allí se evalúa su rojo.
        var centrosDeEvaluacion = asignaciones
            .Where(a => trabajadorIds.Contains(a.TrabajadorId) || (a.EmpresaId is { } e && empresaIds.Contains(e)))
            .Select(a => a.CentroId)
            .Distinct()
            .ToList();
        var incumplidos = centrosDeEvaluacion.Count == 0
            ? RequisitosObligatoriosIncumplidos.Ninguno
            : RequisitosObligatoriosIncumplidos.Desde(
                (await evaluacionDeAcceso.EvaluarCentrosAsync(centrosDeEvaluacion, cancellationToken)).Requisitos,
                new Dictionary<Guid, string>());

        // ---- Documentos de todos los titulares que intervienen ----
        var titularesTrabajador = trabajadorIds
            .Concat(paresDeTrabajador.Select(p => p.TrabajadorId))
            .Concat(trabajadoresDeCentro.SelectMany(g => g))
            .Distinct()
            .ToList();
        var titularesEmpresa = empresaIds
            .Concat(paresDeEmpresa.Select(p => p.EmpresaId))
            .Concat(centros.Select(c => c.EmpresaId))
            .Concat(empresasDeCentro.SelectMany(g => g))
            .Distinct()
            .ToList();
        var documentos = titularesTrabajador.Count == 0 && titularesEmpresa.Count == 0
            ? []
            : await documentosContext.Documentos.Operativos()
                .Where(d => (d.TrabajadorId != null && titularesTrabajador.Contains(d.TrabajadorId.Value))
                            || (d.EmpresaId != null && titularesEmpresa.Contains(d.EmpresaId.Value)))
                .Select(d => new DocumentoLeido(
                    d.Id, d.TrabajadorId, d.EmpresaId, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision,
                    d.CreadoEnUtc, d.EsRenuncia))
                .ToListAsync(cancellationToken);
        var documentosDeTrabajador = documentos.Where(d => d.TrabajadorId is not null).ToLookup(d => d.TrabajadorId!.Value);
        // Mismo universo que el peor estado de una Empresa (CalcularPeorEstadoAsync): todo documento con su EmpresaId.
        var documentosDeEmpresa = documentos.Where(d => d.EmpresaId is not null).ToLookup(d => d.EmpresaId!.Value);

        // ---- Requisitos de los Centros (inclusión, «bloquea el acceso», periodicidad y tolerancia) ----
        var centrosConRequisitos = centroIds.Concat(centrosDeEvaluacion).Distinct().ToList();
        var requisitos = await evaluacionDeAcceso.CondicionesDeRequisitosAsync(centrosConRequisitos, cancellationToken);
        var requisitosPorClave = requisitos.ToDictionary(r => (r.CentroId, r.TipoDocumentoId));

        // Frontera de un documento en un Centro: la de su estado en TALVEG y, si el Centro lo exige con condiciones, el día
        // en que deja de valer allí (periodicidad) y el día en que se acaba su tolerancia.
        DateOnly? FronteraEnCentro(DocumentoLeido d, CondicionDeRequisitoDelCentro? requisito)
        {
            var frontera = Frontera(d);
            if (requisito is null || d.EsRenuncia)
                return frontera;
            var acceso = new DocumentoParaAcceso(VigenciaDocumento.Rehidratar(d.EstadoVigencia, d.FechaVencimiento), d.FechaEmision);
            return FronteraEstadoDocumento.Minima(frontera, FronteraEstadoDocumento.PrimeraPosterior(
                hoy,
                FronteraEstadoDocumento.DiaSiguiente(ReglaBloqueoDeAcceso.VencimientoEfectivo(acceso, requisito.Condiciones.PeriodicidadEspecialMeses)),
                FronteraEstadoDocumento.DiaSiguiente(ReglaBloqueoDeAcceso.ValidoParaAccederHasta(acceso, requisito.Condiciones))));
        }

        DateOnly? FronteraEnCentros(DocumentoLeido d, IEnumerable<Guid> centrosDelTitular)
        {
            var frontera = Frontera(d);
            foreach (var centroId in centrosDelTitular)
                frontera = FronteraEstadoDocumento.Minima(frontera, FronteraEnCentro(d, requisitosPorClave.GetValueOrDefault((centroId, d.TipoDocumentoId))));
            return frontera;
        }

        // ---- Plataforma CAE ----
        var plataforma = await AcreditacionesAsync(centroIds, hoy, cancellationToken);
        var peorAcreditacionPorPar = plataforma
            .GroupBy(p => (p.CentroId, p.AmbitoTitular, p.TitularId, p.TipoDocumentoId))
            .ToDictionary(g => g.Key, g => g.OrderBy(p => GravedadAcreditacion(p, hoy)).First().Estado);
        var fronteraPlataformaPorCentro = plataforma
            .GroupBy(p => p.CentroId)
            .ToDictionary(g => g.Key, g => g.Aggregate((DateOnly?)null, (min, p) => FronteraEstadoDocumento.Minima(min, p.ProximoCambioEl)));

        var finDeEventoPorCentro = participaciones
            .Where(p => p.FechaFinPrevista is not null)
            .GroupBy(p => p.CentroId)
            .ToDictionary(g => g.Key, g => FronteraEstadoDocumento.DiaSiguiente(g.Min(p => p.FechaFinPrevista!.Value)));
        var finDeEventoPorTrabajador = participaciones
            .Where(p => p.FechaFinPrevista is not null)
            .GroupBy(p => p.TrabajadorId)
            .ToDictionary(g => g.Key, g => FronteraEstadoDocumento.DiaSiguiente(g.Min(p => p.FechaFinPrevista!.Value)));

        var entidades = new List<FilaEstadoEntidad>();

        // ---- Centros: estado en TALVEG y en la Plataforma CAE por separado ----
        foreach (var centro in centros)
        {
            var resultado = estadoDeCentros[centro.Id];
            var causasTalveg = resultado.Causas.Where(c => !c.Bloqueante && !c.PendienteEnPlataforma).ToList();
            var estadosTalveg = causasTalveg.Where(c => c.Estado is not null).Select(c => c.Estado!.Value).ToList();
            var estadoTalveg = resultado.Estado == EstadoCentro.SinGestionCae
                ? EstadoCentro.SinGestionCae
                : CalculadoraEstadoCentro.Calcular(estadosTalveg, tieneRequisitoBloqueanteSinCumplir: false);
            EstadoPlataformaCentro? estadoPlataforma =
                resultado.Causas.Any(c => c.Bloqueante) ? EstadoPlataformaCentro.Bloqueado
                : resultado.Causas.Any(c => c.PendienteEnPlataforma) ? EstadoPlataformaCentro.Pendiente
                : null;
            var cumplimiento = cumplimientoDeCentros.GetValueOrDefault(centro.Id, FraccionCumplimiento.SinRequisitos);

            DateOnly? frontera = null;
            if (resultado.Estado != EstadoCentro.SinGestionCae)
            {
                var trabajadoresDelCentro = trabajadoresDeCentro[centro.Id].ToHashSet();
                var empresasDelCentro = empresasDeCentro[centro.Id].Append(centro.EmpresaId).ToHashSet();
                foreach (var d in trabajadoresDelCentro.SelectMany(t => documentosDeTrabajador[t]))
                    frontera = FronteraEstadoDocumento.Minima(frontera, FronteraEnCentro(d, requisitosPorClave.GetValueOrDefault((centro.Id, d.TipoDocumentoId))));
                foreach (var d in empresasDelCentro.SelectMany(e => documentosDeEmpresa[e]))
                    frontera = FronteraEstadoDocumento.Minima(frontera, FronteraEnCentro(d, requisitosPorClave.GetValueOrDefault((centro.Id, d.TipoDocumentoId))));
                frontera = FronteraEstadoDocumento.Minima(frontera, fronteraPlataformaPorCentro.GetValueOrDefault(centro.Id));
                // El fin de un evento del Centro, o de uno de sus Trabajadores (un voluntario sin evento deja de contar).
                frontera = FronteraEstadoDocumento.Minima(frontera, finDeEventoPorCentro.GetValueOrDefault(centro.Id));
                foreach (var t in trabajadoresDelCentro)
                    frontera = FronteraEstadoDocumento.Minima(frontera, finDeEventoPorTrabajador.GetValueOrDefault(t));
            }

            entidades.Add(new FilaEstadoEntidad(
                AmbitoEstadoEntidad.Centro, centro.Id, (short)estadoTalveg, estadoPlataforma,
                Vencidos: estadosTalveg.Count(e => e == EstadoDocumento.Vencido),
                VencidosSinBloqueo: 0,
                Urgentes: estadosTalveg.Count(e => e == EstadoDocumento.Urgente),
                Proximos: estadosTalveg.Count(e => e == EstadoDocumento.Proximo),
                Faltan: estadosTalveg.Count(e => e == EstadoDocumento.Faltante),
                SinConfirmar: estadosTalveg.Count(e => e == EstadoDocumento.SinConfirmar),
                PendientesEnPlataforma: resultado.Causas.Count(c => c.PendienteEnPlataforma),
                cumplimiento.Requeridos, cumplimiento.AlDia, frontera));
        }

        // ---- Empresa y Trabajador: su contexto, con la regla del 2026-10-23 ----
        var centrosPorTrabajador = asignaciones.ToLookup(a => a.TrabajadorId, a => a.CentroId);
        var centrosPorEmpresa = asignaciones.Where(a => a.EmpresaId is not null).ToLookup(a => a.EmpresaId!.Value, a => a.CentroId);

        var peorDeTrabajador = await calculoEstadoDocumental.CalcularPeorEstadoAsync(AmbitoAplicacion.Trabajador, trabajadorIds, cancellationToken);
        foreach (var trabajadorId in trabajadorIds)
            entidades.Add(FilaDeTitular(
                AmbitoEstadoEntidad.Trabajador, trabajadorId, peorDeTrabajador, incumplidos.PorTrabajador,
                documentosDeTrabajador[trabajadorId].ToList(), centrosPorTrabajador[trabajadorId].Distinct().ToList()));

        var peorDeEmpresa = await calculoEstadoDocumental.CalcularPeorEstadoAsync(AmbitoAplicacion.Empresa, empresaIds, cancellationToken);
        foreach (var empresaId in empresaIds)
            entidades.Add(FilaDeTitular(
                AmbitoEstadoEntidad.Empresa, empresaId, peorDeEmpresa, incumplidos.PorEmpresa,
                documentosDeEmpresa[empresaId].ToList(), centrosPorEmpresa[empresaId].Distinct().ToList()));

        FilaEstadoEntidad FilaDeTitular(
            AmbitoEstadoEntidad ambito, Guid id, IReadOnlyDictionary<Guid, EstadoDocumento> peores,
            IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>> incumplidosPorTitular,
            List<DocumentoLeido> propios, List<Guid> centrosDelTitular)
        {
            var deEste = incumplidosPorTitular.GetValueOrDefault(id) ?? [];
            var vencido = RequisitosObligatoriosIncumplidos.HayVencido(deEste);
            var ausente = RequisitosObligatoriosIncumplidos.HayAusente(deEste);
            EstadoDocumento? peor = peores.TryGetValue(id, out var p) ? p : null;
            var estado = SeveridadEstadoDocumento.ConRequisitosObligatorios(peor, vencido, ausente);

            var estados = propios
                .Select(d => (d.TipoDocumentoId, Estado: CalculadoraEstadoDocumento.Calcular(
                    d.EstadoVigencia, d.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)))
                .ToList();
            var tiposVencidosQueBloquean = deEste
                .Where(i => i.Situacion == SituacionDeRequisitoBloqueante.Vencido)
                .Select(i => i.TipoDocumentoId)
                .ToHashSet();
            var fraccion = CumplimientoDocumental.Evaluar(estados.Select(e => e.Estado));

            DateOnly? frontera = null;
            foreach (var d in propios)
                frontera = FronteraEstadoDocumento.Minima(frontera, FronteraEnCentros(d, centrosDelTitular));

            return new FilaEstadoEntidad(
                ambito, id, estado is { } e ? (short)e : EstadoEntidad.SinEstado, EstadoPlataforma: null,
                Vencidos: estados.Count(x => x.Estado == EstadoDocumento.Vencido),
                VencidosSinBloqueo: estados.Count(x => x.Estado == EstadoDocumento.Vencido && !tiposVencidosQueBloquean.Contains(x.TipoDocumentoId)),
                Urgentes: estados.Count(x => x.Estado == EstadoDocumento.Urgente),
                Proximos: estados.Count(x => x.Estado == EstadoDocumento.Proximo),
                Faltan: deEste.Count(i => i.Situacion == SituacionDeRequisitoBloqueante.Ausente),
                SinConfirmar: estados.Count(x => x.Estado == EstadoDocumento.SinConfirmar),
                PendientesEnPlataforma: 0,
                fraccion.Requeridos, fraccion.AlDia, frontera);
        }

        // ---- Pares exigidos ----
        var clavesDeEvento = centroIds.Count == 0
            ? []
            : (await RequisitosDeEventos
                    .DeEventosQueLosAportan(
                        trabajadoresContext.Trabajadores, proyectosContext, requisitosEventoContext.ProyectosRequisitos,
                        tiposDocumentoContext.TiposDocumento, hoy)
                    .Where(r => centroIds.Contains(r.CentroId))
                    .Select(r => new { r.CentroId, r.TrabajadorId, r.TipoDocumentoId })
                    .Distinct()
                    .ToListAsync(cancellationToken))
                .Select(r => (r.CentroId, r.TrabajadorId, r.TipoDocumentoId))
                .ToHashSet();
        var finDeEventoPorPar = participaciones
            .Where(p => p.FechaFinPrevista is not null)
            .GroupBy(p => (p.CentroId, p.TrabajadorId))
            .ToDictionary(g => g.Key, g => FronteraEstadoDocumento.DiaSiguiente(g.Min(p => p.FechaFinPrevista!.Value)));

        var efectivosDeTrabajador = DocumentoEfectivo.UnoPorClave(
            documentos.Where(d => d.TrabajadorId is not null).ToList(),
            d => (d.TrabajadorId!.Value, d.TipoDocumentoId), d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision,
            d => d.CreadoEnUtc, d => d.Id, d => d.EsRenuncia, hoy);
        // Mismo universo que los pares de Empresa del Centro: documentos de la Empresa que no son de un Trabajador.
        var documentosPropiosDeEmpresa = documentos.Where(d => d.TrabajadorId is null && d.EmpresaId is not null).ToList();
        var efectivosDeEmpresa = DocumentoEfectivo.UnoPorClave(
            documentosPropiosDeEmpresa,
            d => (d.EmpresaId!.Value, d.TipoDocumentoId), d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision,
            d => d.CreadoEnUtc, d => d.Id, d => d.EsRenuncia, hoy);
        var documentosPorParDeTrabajador = documentos.Where(d => d.TrabajadorId is not null).ToLookup(d => (d.TrabajadorId!.Value, d.TipoDocumentoId));
        var documentosPorParDeEmpresa = documentosPropiosDeEmpresa.ToLookup(d => (d.EmpresaId!.Value, d.TipoDocumentoId));

        FilaEstadoRequisito Par(
            Guid centroId, AmbitoAplicacion ambito, Guid titularId, Guid tipoDocumentoId, EstadoDocumento estado,
            DocumentoLeido? efectivo, IEnumerable<DocumentoLeido> delPar, bool deEvento)
        {
            var requisito = requisitosPorClave.GetValueOrDefault((centroId, tipoDocumentoId));
            // El fin de un evento del Trabajador cambia el par aunque no sea de evento: el voluntario deja de contar y su par
            // desaparece (AsignacionesQueCuentan). Antes de tiempo solo cuesta un recálculo de más; tarde dejaría el par viejo.
            DateOnly? frontera = deEvento ? finDeEventoPorPar.GetValueOrDefault((centroId, titularId)) : null;
            if (ambito == AmbitoAplicacion.Trabajador)
                frontera = FronteraEstadoDocumento.Minima(frontera, finDeEventoPorTrabajador.GetValueOrDefault(titularId));
            foreach (var d in delPar)
                frontera = FronteraEstadoDocumento.Minima(frontera, FronteraEnCentro(d, requisito));
            return new FilaEstadoRequisito(
                centroId, ambito, titularId, tipoDocumentoId, efectivo?.Id, estado.ComoEstadoDeRequisito(),
                BloqueaAcceso: requisito is { Incluido: true, BloqueaAcceso: true },
                CuentaParaCumplimiento: requisito is { Incluido: true } || deEvento,
                peorAcreditacionPorPar.TryGetValue((centroId, ambito, titularId, tipoDocumentoId), out var acreditacion) ? acreditacion : null,
                efectivo is null
                    ? null
                    : ReglaBloqueoDeAcceso.VencimientoEfectivo(
                        new DocumentoParaAcceso(VigenciaDocumento.Rehidratar(efectivo.EstadoVigencia, efectivo.FechaVencimiento), efectivo.FechaEmision),
                        requisito?.Condiciones.PeriodicidadEspecialMeses),
                frontera);
        }

        var pares = new List<FilaEstadoRequisito>(paresDeTrabajador.Count + paresDeEmpresa.Count);
        foreach (var par in paresDeTrabajador)
            pares.Add(Par(
                par.CentroId, AmbitoAplicacion.Trabajador, par.TrabajadorId, par.TipoDocumentoId, par.Estado,
                efectivosDeTrabajador.GetValueOrDefault((par.TrabajadorId, par.TipoDocumentoId)),
                documentosPorParDeTrabajador[(par.TrabajadorId, par.TipoDocumentoId)],
                clavesDeEvento.Contains((par.CentroId, par.TrabajadorId, par.TipoDocumentoId))));
        foreach (var par in paresDeEmpresa)
            pares.Add(Par(
                par.CentroId, AmbitoAplicacion.Empresa, par.EmpresaId, par.TipoDocumentoId, par.Estado,
                efectivosDeEmpresa.GetValueOrDefault((par.EmpresaId, par.TipoDocumentoId)),
                documentosPorParDeEmpresa[(par.EmpresaId, par.TipoDocumentoId)],
                deEvento: false));

        // Lo que se sustituye son las entidades pedidas, existan o no: así una borrada pierde su fila.
        return new CalculoEstadoMaterializado(
            hoy, centroIdsLista, trabajadorIdsLista, empresaIdsLista, pares, entidades, plataforma);
    }

    /// <summary>
    /// Una fila por acreditación de los canales de Plataforma de esos Centros (de un documento operativo). Si un titular
    /// tiene dos documentos del mismo Tipo acreditados en el mismo canal, queda la acreditación más grave
    /// (<see cref="GravedadAcreditacion"/>), que es la que pesa en el estado del Centro.
    /// </summary>
    private async Task<List<FilaEstadoEnPlataforma>> AcreditacionesAsync(List<Guid> centroIds, DateOnly hoy, CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0)
            return [];

        var filas = await (
            from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
            join canal in centrosContext.CanalesGestionDocumental on acreditacion.CanalGestionDocumentalId equals canal.Id
            where centroIds.Contains(canal.CentroId)
            join documento in documentosContext.Documentos.Operativos() on acreditacion.DocumentoId equals documento.Id
            select new
            {
                canal.CentroId,
                acreditacion.CanalGestionDocumentalId,
                documento.TrabajadorId,
                documento.EmpresaId,
                documento.TipoDocumentoId,
                acreditacion.Estado,
                acreditacion.EstadoVigencia,
                acreditacion.FechaVencimientoEnPlataforma
            })
            .ToListAsync(cancellationToken);

        return filas
            .Where(f => f.TrabajadorId is not null || f.EmpresaId is not null)
            .Select(f => new FilaEstadoEnPlataforma(
                f.CentroId, f.CanalGestionDocumentalId,
                f.TrabajadorId is not null ? AmbitoAplicacion.Trabajador : AmbitoAplicacion.Empresa,
                f.TrabajadorId ?? f.EmpresaId!.Value,
                f.TipoDocumentoId, f.Estado, f.EstadoVigencia, f.FechaVencimientoEnPlataforma,
                // «Vencido en la Plataforma CAE» empieza el día siguiente a su fecha (CalculoEstadoCentroService: fecha < hoy).
                f.EstadoVigencia == EstadoVigenciaEnPlataforma.VenceEnFecha
                    ? FronteraEstadoDocumento.PrimeraPosterior(hoy, FronteraEstadoDocumento.DiaSiguiente(f.FechaVencimientoEnPlataforma))
                    : null))
            .GroupBy(f => (f.CentroId, f.CanalGestionDocumentalId, f.AmbitoTitular, f.TitularId, f.TipoDocumentoId))
            .Select(g => g.OrderBy(f => GravedadAcreditacion(f, hoy)).ThenBy(f => f.FechaVencimientoEnPlataforma).First())
            .ToList();
    }

    /// <summary>De más a menos grave: rechazada, vencida en la plataforma, sin subir, subida sin validar, aceptada, no requerida.</summary>
    private static int GravedadAcreditacion(FilaEstadoEnPlataforma fila, DateOnly hoy) =>
        fila.Estado == EstadoAcreditacion.Rechazada ? 0
        : fila.EstadoVigencia == EstadoVigenciaEnPlataforma.VenceEnFecha && fila.FechaVencimientoEnPlataforma < hoy ? 1
        : fila.Estado switch
        {
            EstadoAcreditacion.PendienteDeSubir => 2,
            EstadoAcreditacion.Subida => 3,
            EstadoAcreditacion.Aceptada => 4,
            _ => 5
        };
}

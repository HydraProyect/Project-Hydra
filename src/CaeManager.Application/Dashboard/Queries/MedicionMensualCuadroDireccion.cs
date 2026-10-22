using CaeManager.Application.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Documentos;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Comunicaciones;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>Medición por Tenant beneficiario, compartida por lectura en vivo y cierre programado.</summary>
public class MedicionMensualCuadroDireccion(
    ITenantActual tenantActual, ITrabajadoresQueryContext trabajadoresContext,
    ICentrosQueryContext centrosContext, IDocumentosQueryContext documentosContext,
    IReclamacionesQueryContext reclamacionesContext, IComunicacionesQueryContext comunicacionesContext,
    IMediator mediator,
    CaeManager.Application.Gestiones.IGestionesQueryContext? gestionesContext = null,
    CaeManager.Application.Proyectos.IProyectosQueryContext? proyectosContext = null,
    CaeManager.Application.Empresas.IEmpresasQueryContext? empresasContext = null,
    CaeManager.Application.Visitas.IVisitasQueryContext? visitasContext = null)
{
    /// <summary>Un mes con su instante de corte: la medición de <see cref="MedirAsync"/>, para cada mes de la serie.</summary>
    public virtual async Task<MedicionCuadroDireccion> MedirAsync(MesCuadroDireccion mes, DateTime corteUtc, CancellationToken cancellationToken, bool medirDocumental = true) =>
        (await MedirMesesAsync([new MesAMedir(mes, corteUtc)], cancellationToken, medirDocumental))[0];

    /// <summary>
    /// Mide varios meses del Tenant del ámbito en una sola pasada: una consulta por fuente para toda la ventana (del
    /// inicio del primer mes al final del último) y el reparto por mes en memoria, con las mismas expresiones de
    /// <see cref="DefinicionesCuadroDireccion"/> que traduce EF. El número de consultas no crece con los meses: la
    /// serie de 36 meses del Cuadro de dirección hacía antes 36 veces cada consulta por Tenant beneficiario. Devuelve
    /// una medición por mes, en el orden pedido.
    /// </summary>
    public virtual async Task<IReadOnlyList<MedicionCuadroDireccion>> MedirMesesAsync(
        IReadOnlyList<MesAMedir> meses, CancellationToken cancellationToken, bool medirDocumental = true)
    {
        if (meses.Count == 0)
            return [];

        // Falla cerrado: sin Tenant en el ámbito no hay con qué comparar las filas sin filtro.
        var tenantId = tenantActual.TenantId
            ?? throw new InvalidOperationException("El Cuadro de dirección se mide con un Tenant en el ámbito.");
        var desde = meses.Min(m => m.Mes.InicioUtc);
        var hasta = meses.Max(m => m.Mes.FinUtc);

        // Toda la vida de los Trabajadores y Centros del Tenant: los activos al corte dependen de altas anteriores a la
        // ventana. Trabajadores extranjeros: los activos en el corte con documento de extranjero (NIE, TIE u otro que no
        // sea DNI), con la regla única de ValidadorIdentificacion. Es un recuento de personas, no las visitas facturables.
        var trabajadores = (await VidasTrabajadores(tenantId).ToListAsync(cancellationToken))
            .Select(v => (Vida: v, Extranjero: ValidadorIdentificacion.EsDocumentoDeExtranjero(v.Documento)))
            .ToList();
        var centros = (await CentrosDelTenant(tenantId)
                .Select(c => new { c.CreadoEnUtc, c.EstaEliminado, c.EliminadoEnUtc, Cliente = c.ClienteId })
                .ToListAsync(cancellationToken))
            .Select(c => (Vida: new VidaDeRegistro { CreadoEnUtc = c.CreadoEnUtc, EstaEliminado = c.EstaEliminado, EliminadoEnUtc = c.EliminadoEnUtc }, c.Cliente))
            .ToList();

        // Actividad, no estado: una versión que después se sustituyó, o un Documento que después se eliminó,
        // también se registró en el mes. Por eso cuenta las filas eliminadas, como las bajas de Trabajadores, y
        // un mes estimado no baja porque alguien borre después lo que se gestionó.
        var documentosGestionados = await DocumentosDelTenant(tenantId)
            .Where(d => d.CreadoEnUtc >= desde && d.CreadoEnUtc < hasta)
            .Select(d => d.CreadoEnUtc)
            .ToListAsync(cancellationToken);

        var requerimientos = await reclamacionesContext.ReclamacionesDocumentales
            .Where(r => r.FechaEnvioUtc >= desde && r.FechaEnvioUtc < hasta)
            .Select(r => r.FechaEnvioUtc)
            .ToListAsync(cancellationToken);

        // Solo conversaciones vivas (la eliminada no es demanda); el primer entrante decide el mes.
        var conversacionesVivas = comunicacionesContext.Conversaciones.Select(c => c.Id);
        var solicitudes = await comunicacionesContext.Mensajes
            .Where(m => conversacionesVivas.Contains(m.ConversacionId))
            .PrimerosEntrantes()
            .Where(primero => primero >= desde && primero < hasta)
            .ToListAsync(cancellationToken);

        // El cumplimiento de hoy, con la consulta y las definiciones de Inicio, sin copiar la regla: es el mismo para
        // todos los meses, así que se pide una vez.
        var documental = medirDocumental
            ? await mediator.Send(new ObtenerKpisDashboardQuery(), cancellationToken)
            : new KpisDashboardDto(0, 0, 0, 0, 0, 0, 0, 0);
        var gestiones = gestionesContext is null ? null : await gestionesContext.Gestiones
            .Where(g => g.CompletadaEnUtc >= desde && g.CompletadaEnUtc < hasta)
            .Select(g => g.CompletadaEnUtc)
            .ToListAsync(cancellationToken);

        // Obras nuevas: Proyectos de tipo Obra dados de alta en el mes; los Eventos no se cobran. Como las altas de
        // Centros, cuenta también los que después se eliminaron: el alta ocurrió en el mes.
        var obras = ObrasDelTenant(tenantId) is not { } obrasDelTenant ? null : await obrasDelTenant
            .Where(p => p.CreadoEnUtc >= desde && p.CreadoEnUtc < hasta)
            .Select(p => p.CreadoEnUtc)
            .ToListAsync(cancellationToken);

        // Centros nuevos por Cliente empresarial: indicador de crecimiento, no se cobra. Con el filtro global: un
        // Cliente empresarial eliminado sale sin nombre, pero sus Centros nuevos cuentan.
        Dictionary<Guid, string>? nombres = null;
        if (empresasContext is not null)
        {
            var ids = centros.Where(c => c.Vida.CreadoEnUtc >= desde && c.Vida.CreadoEnUtc < hasta).Select(c => c.Cliente).Distinct().ToList();
            nombres = await empresasContext.Empresas
                .Where(e => ids.Contains(e.Id))
                .Select(e => new { e.Id, e.RazonSocial })
                .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);
        }

        var primerDiaVentana = DiaDeNegocio.De(desde);
        var visitas = visitasContext is null
            ? null
            : await LeerVisitasDeTrabajadorAsync(visitasContext, primerDiaVentana, DiaDeNegocio.De(hasta), meses.Max(m => m.CorteUtc), cancellationToken);

        return meses.Select(m => Repartir(m)).ToList();

        MedicionCuadroDireccion Repartir(MesAMedir aMedir)
        {
            var (mes, corteUtc) = (aMedir.Mes, aMedir.CorteUtc);
            var (inicio, fin) = (mes.InicioUtc, mes.FinUtc);
            var vivo = DefinicionesCuadroDireccion.VivoAlCorte(corteUtc).Compile();
            var alta = DefinicionesCuadroDireccion.AltaEnElMes(inicio, fin).Compile();
            var baja = DefinicionesCuadroDireccion.BajaEnElMes(inicio, fin).Compile();
            // Un único recorte al mes para toda la actividad leída de la ventana: [inicio, fin).
            bool EnElMes(DateTime? instante) => instante >= inicio && instante < fin;

            var activos = trabajadores.Where(t => vivo(t.Vida)).ToList();
            var altasDeCentros = centros.Where(c => alta(c.Vida)).ToList();

            IReadOnlyList<CentrosNuevosPorClienteDto>? porCliente = nombres is null
                ? null
                : ObtenerCuadroDireccionQueryHandler.SumarCentrosNuevosPorCliente(altasDeCentros
                    .GroupBy(c => c.Cliente)
                    .Select(g => new CentrosNuevosPorClienteDto(tenantId, g.Key, nombres.GetValueOrDefault(g.Key) ?? string.Empty, g.Count())));

            var primerDia = new DateOnly(mes.Anyo, mes.Mes, 1);
            return new MedicionCuadroDireccion(
                activos.Count,
                trabajadores.Count(t => alta(t.Vida)),
                trabajadores.Count(t => baja(t.Vida)),
                centros.Count(c => vivo(c.Vida)),
                altasDeCentros.Count,
                documentosGestionados.Count(d => EnElMes(d)),
                requerimientos.Count(r => EnElMes(r)),
                solicitudes.Where(s => EnElMes(s)).ToList(),
                documental,
                gestiones?.Count(EnElMes),
                ExtranjerosActivos: activos.Count(t => t.Extranjero),
                ObrasNuevas: obras?.Count(o => EnElMes(o)),
                CentrosNuevosPorCliente: porCliente,
                VisitasTrabajadorExtranjero: visitas?.Count(v => v.Extranjero && v.CreadoEnUtc <= corteUtc
                    && v.FechaInicio >= primerDia && v.FechaInicio < primerDia.AddMonths(1)));
        }
    }

    private IQueryable<VidaDeRegistro> VidasTrabajadores(Guid tenantId) =>
        trabajadoresContext.Trabajadores
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .Select(t => new VidaDeRegistro { CreadoEnUtc = t.CreadoEnUtc, EstaEliminado = t.EstaEliminado, EliminadoEnUtc = t.EliminadoEnUtc, Documento = t.Dni });

    private IQueryable<CaeManager.Domain.Documentos.Documento> DocumentosDelTenant(Guid tenantId) =>
        documentosContext.Documentos
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId);

    /// <summary>
    /// Las unidades que valora el Operador CAE externo (altas de Centros, Obras nuevas y visitas de Trabajador
    /// extranjero) contadas solo dentro de <paramref name="tramos"/>: los trozos del mes en que su Asignación de
    /// Operación estuvo en vigor (<see cref="ValoracionOperadorCae.TramosEnElMes"/>). Con las mismas definiciones que
    /// <see cref="MedirAsync"/>; las altas por instante y las Visitas por su día de inicio, que cuenta si el tramo
    /// empieza ese día o antes y termina después de él. Borde asimétrico: la Visita del día en que empieza la
    /// Asignación cuenta y la del día en que se cierra queda fuera. Se cuenta en vivo aunque el mes esté cerrado (la
    /// foto guarda el mes entero, no los trozos).
    /// </summary>
    public virtual async Task<UnidadesOperadorCae> MedirUnidadesOperadorAsync(
        IReadOnlyList<TramoDeVigencia> tramos, DateTime corteUtc, CancellationToken cancellationToken)
    {
        var tenantId = tenantActual.TenantId
            ?? throw new InvalidOperationException("El Cuadro de dirección se mide con un Tenant en el ámbito.");
        var altasCentros = 0;
        int? obrasNuevas = proyectosContext is null ? null : 0;
        int? visitasExtranjero = visitasContext is null ? null : 0;
        foreach (var tramo in tramos)
        {
            var (desde, hasta) = (tramo.DesdeUtc, tramo.HastaUtc);
            altasCentros += await CentrosDelTenant(tenantId).CountAsync(c => c.CreadoEnUtc >= desde && c.CreadoEnUtc < hasta, cancellationToken);
            if (ObrasDelTenant(tenantId) is { } obras)
                obrasNuevas += await obras.CountAsync(p => p.CreadoEnUtc >= desde && p.CreadoEnUtc < hasta, cancellationToken);
            if (visitasContext is not null)
                visitasExtranjero += await ContarVisitasTrabajadorExtranjeroAsync(
                    visitasContext, DiaDeNegocio.De(desde), DiaDeNegocio.De(hasta), corteUtc, cancellationToken);
        }

        return new UnidadesOperadorCae(altasCentros, obrasNuevas, visitasExtranjero);
    }

    // Una sola fuente para las filas sin filtro global (las eliminadas también se dieron de alta): el TenantId del
    // ámbito se compara a mano, como en el resto de la medición.
    private IQueryable<CaeManager.Domain.Centros.Centro> CentrosDelTenant(Guid tenantId) =>
        centrosContext.Centros.IgnoreQueryFilters().Where(c => c.TenantId == tenantId);

    private IQueryable<CaeManager.Domain.Proyectos.Proyecto>? ObrasDelTenant(Guid tenantId) =>
        proyectosContext?.Proyectos.IgnoreQueryFilters().Where(p => p.TenantId == tenantId && p.Tipo == CaeManager.Domain.Proyectos.TipoProyecto.Obra);

    /// <summary>
    /// Unidades de la visita de Trabajador extranjero (decisión de Chris, 2026-10-20): una por Trabajador en cada
    /// Visita no cancelada cuyo día de inicio cae en [<paramref name="desde"/>, <paramref name="hasta"/>), si su
    /// documento es de extranjero. Los límites son días civiles (el 1 del mes y el 1 del siguiente), no instantes
    /// UTC: la medianoche de Madrid en UTC cae el día anterior. Es el único punto que decide qué cuenta como
    /// extranjero aquí; hoy aplica el criterio vigente de la pantalla «Facturación»
    /// (<see cref="ValidadorIdentificacion.EsDocumentoDeExtranjero"/> sobre el documento actual del Trabajador). Cuando
    /// la Visita congele el documento del Trabajador, este método pasará a leer esa foto.
    /// </summary>
    private async Task<int> ContarVisitasTrabajadorExtranjeroAsync(
        CaeManager.Application.Visitas.IVisitasQueryContext visitas, DateOnly desde, DateOnly hasta, DateTime corteUtc, CancellationToken cancellationToken) =>
        (await LeerVisitasDeTrabajadorAsync(visitas, desde, hasta, corteUtc, cancellationToken)).Count(v => v.Extranjero);

    /// <summary>
    /// Una fila por Trabajador en cada Visita no cancelada creada hasta <paramref name="corteUtc"/> cuyo día de inicio
    /// cae en [<paramref name="desde"/>, <paramref name="hasta"/>): su día, su alta y si el documento es de extranjero.
    /// </summary>
    private async Task<IReadOnlyList<VisitaDeTrabajador>> LeerVisitasDeTrabajadorAsync(
        CaeManager.Application.Visitas.IVisitasQueryContext visitas, DateOnly desde, DateOnly hasta, DateTime corteUtc, CancellationToken cancellationToken)
    {
        var visitasDelPeriodo = visitas.Visitas
            .Where(v => !v.EstaCancelada && v.CreadoEnUtc <= corteUtc && v.FechaInicio >= desde && v.FechaInicio < hasta)
            .Select(v => new { v.Id, v.FechaInicio, v.CreadoEnUtc });
        var filas = await visitas.VisitasTrabajadores
            .Join(visitasDelPeriodo, vt => vt.VisitaId, v => v.Id, (vt, v) => new { vt.TrabajadorId, v.FechaInicio, v.CreadoEnUtc })
            .Join(trabajadoresContext.Trabajadores, x => x.TrabajadorId, t => t.Id, (x, t) => new { x.FechaInicio, x.CreadoEnUtc, t.Dni })
            .ToListAsync(cancellationToken);
        return filas
            .Select(f => new VisitaDeTrabajador(f.FechaInicio, f.CreadoEnUtc, ValidadorIdentificacion.EsDocumentoDeExtranjero(f.Dni)))
            .ToList();
    }

    private sealed record VisitaDeTrabajador(DateOnly FechaInicio, DateTime CreadoEnUtc, bool Extranjero);
}

/// <summary>Un mes del Cuadro de dirección y el instante hasta el que se miden sus cifras «a fecha».</summary>
public readonly record struct MesAMedir(MesCuadroDireccion Mes, DateTime CorteUtc);


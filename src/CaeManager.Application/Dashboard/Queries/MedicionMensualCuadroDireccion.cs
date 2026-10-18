using CaeManager.Application.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Documentos;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>Medición por Tenant beneficiario, compartida por lectura en vivo y cierre programado.</summary>
public class MedicionMensualCuadroDireccion(
    ITenantActual tenantActual, ITrabajadoresQueryContext trabajadoresContext,
    ICentrosQueryContext centrosContext, IDocumentosQueryContext documentosContext,
    IReclamacionesQueryContext reclamacionesContext, IComunicacionesQueryContext comunicacionesContext,
    IMediator mediator,
    CaeManager.Application.Gestiones.IGestionesQueryContext? gestionesContext = null)
{
    public virtual async Task<MedicionCuadroDireccion> MedirAsync(MesCuadroDireccion mes, DateTime corteUtc, CancellationToken cancellationToken, bool medirDocumental = true)
    {
        // Falla cerrado: sin Tenant en el ámbito no hay con qué comparar las filas sin filtro.
        var tenantId = tenantActual.TenantId
            ?? throw new InvalidOperationException("El Cuadro de dirección se mide con un Tenant en el ámbito.");
        var inicio = mes.InicioUtc;
        var fin = mes.FinUtc;

        var vidasTrabajadores = trabajadoresContext.Trabajadores
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId)
            .Select(t => new VidaDeRegistro { CreadoEnUtc = t.CreadoEnUtc, EstaEliminado = t.EstaEliminado, EliminadoEnUtc = t.EliminadoEnUtc });

        var vidasCentros = centrosContext.Centros
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId)
            .Select(c => new VidaDeRegistro { CreadoEnUtc = c.CreadoEnUtc, EstaEliminado = c.EstaEliminado, EliminadoEnUtc = c.EliminadoEnUtc });

        var trabajadoresActivos = await vidasTrabajadores.CountAsync(DefinicionesCuadroDireccion.VivoAlCorte(corteUtc), cancellationToken);
        var altasTrabajadores = await vidasTrabajadores.CountAsync(DefinicionesCuadroDireccion.AltaEnElMes(inicio, fin), cancellationToken);
        var bajasTrabajadores = await vidasTrabajadores.CountAsync(DefinicionesCuadroDireccion.BajaEnElMes(inicio, fin), cancellationToken);
        var centrosActivos = await vidasCentros.CountAsync(DefinicionesCuadroDireccion.VivoAlCorte(corteUtc), cancellationToken);
        var altasCentros = await vidasCentros.CountAsync(DefinicionesCuadroDireccion.AltaEnElMes(inicio, fin), cancellationToken);

        // Actividad, no estado: una versión que después se sustituyó, o un Documento que después se eliminó,
        // también se registró en el mes. Por eso cuenta las filas eliminadas, como las bajas de Trabajadores, y
        // un mes estimado no baja porque alguien borre después lo que se gestionó.
        var documentosGestionados = await documentosContext.Documentos
            .IgnoreQueryFilters()
            .Where(d => d.TenantId == tenantId)
            .CountAsync(d => d.CreadoEnUtc >= inicio && d.CreadoEnUtc < fin, cancellationToken);

        var requerimientos = await reclamacionesContext.ReclamacionesDocumentales
            .CountAsync(r => r.FechaEnvioUtc >= inicio && r.FechaEnvioUtc < fin, cancellationToken);

        // Solo conversaciones vivas (la eliminada no es demanda); el primer entrante decide el mes.
        var conversacionesVivas = comunicacionesContext.Conversaciones.Select(c => c.Id);
        var solicitudes = await comunicacionesContext.Mensajes
            .Where(m => conversacionesVivas.Contains(m.ConversacionId))
            .PrimerosEntrantes()
            .Where(primero => primero >= inicio && primero < fin)
            .ToListAsync(cancellationToken);

        // El cumplimiento de hoy, con la consulta y las definiciones de Inicio, sin copiar la regla.
        var documental = medirDocumental
            ? await mediator.Send(new ObtenerKpisDashboardQuery(), cancellationToken)
            : new KpisDashboardDto(0, 0, 0, 0, 0, 0, 0, 0);
        int? gestiones = gestionesContext is null ? null : await gestionesContext.Gestiones
            .CountAsync(g => g.CompletadaEnUtc >= inicio && g.CompletadaEnUtc < fin, cancellationToken);

        return new MedicionCuadroDireccion(
            trabajadoresActivos, altasTrabajadores, bajasTrabajadores,
            centrosActivos, altasCentros,
            documentosGestionados, requerimientos,
            solicitudes, documental, gestiones);
    }

}


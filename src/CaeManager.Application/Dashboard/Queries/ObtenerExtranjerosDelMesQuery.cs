using CaeManager.Application.Auditoria;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>Los Trabajadores extranjeros cobrables de un mes natural, de todos los Tenants beneficiarios que alcanza quien mira.</summary>
public record ObtenerExtranjerosDelMesQuery(int Anyo, int Mes) : IRequest<ExtranjerosDelMesDto>;

/// <summary>
/// Una fila de la exportación: un Trabajador extranjero cobrable en una Visita. Un Trabajador que va a varios Centros
/// (o varias veces al mismo) sale en varias filas.
/// </summary>
/// <param name="TenantNombre">El Tenant beneficiario de la Visita: con quién se factura.</param>
/// <param name="FechaSolicitud">Día (hora peninsular) en que se asignó el Trabajador a la Visita; ver <see cref="ObtenerExtranjerosDelMesQueryHandler"/>.</param>
/// <param name="ContactoVisita">El técnico o contacto de la Visita (<c>Visita.ContactoNombre</c>), si lo tiene.</param>
/// <param name="Empresa">La Empresa del Trabajador o, si es de una Subcontrata, la Empresa del Centro para la que entra.</param>
/// <param name="Subcontrata">La Subcontrata del Trabajador, si es de una.</param>
/// <param name="Cliente">El Cliente empresarial del Centro.</param>
/// <param name="GestorCae">Quien asignó el Trabajador a la Visita; vacío si no consta.</param>
public record FilaExtranjeroDelMesDto(
    Guid TenantId, string TenantNombre, DateOnly FechaSolicitud, string? ContactoVisita,
    string? Empresa, string? Subcontrata, string? Cliente, string Centro,
    DateOnly FechaIngreso, DateOnly FechaSalida, string NombreYApellidos, string? Documento, string? GestorCae);

/// <param name="TenantsSinAlcanceCompleto">Tenants beneficiarios autorizados que quedaron fuera porque quien mira no alcanza el Tenant entero.</param>
/// <param name="SinCifras">Null si se calculó; si no, por qué no (mes no válido o futuro), y sin filas.</param>
public record ExtranjerosDelMesDto(
    int Anyo, int Mes, IReadOnlyList<FilaExtranjeroDelMesDto> Filas, int TenantsSinAlcanceCompleto,
    CuadroDireccionSinCifras? SinCifras = null);

/// <summary>
/// Exportación mensual de Trabajadores extranjeros del Cuadro de dirección, con la que el Operador CAE externo cobra por
/// cada Trabajador extranjero gestionado (decisión de Chris, 2026-10-20).
///
/// <para>
/// <b>Unidad</b>: una fila por Trabajador extranjero cobrable (<see cref="CriterioExtranjeroCobrable.Efectivo"/>) por
/// Visita no cancelada cuya <c>FechaInicio</c> cae en el mes. La Visita eliminada no cuenta; el Trabajador, el Centro y
/// las Empresas eliminados después sí, porque la Visita se gestionó.
/// </para>
///
/// <para>
/// <b>Recorrido y alcance</b>: el del Cuadro de dirección (<see cref="ObtenerCuadroDireccionQueryHandler"/>), con su
/// misma autorización (<see cref="AutorizacionCuadroDireccion"/>). Por cada Tenant beneficiario autorizado
/// (<see cref="ObtenerClientesAutorizadosQuery"/>) las consultas corren con <see cref="AmbitoTenantExplicito"/> fijado a
/// ese Tenant (filtro global y RLS de ese Tenant) y solo si quien mira alcanza el Tenant entero
/// (<see cref="IAlcanceDatosService.AlcanzaElTenantEnteroAsync"/>); si no, el Tenant queda fuera y se cuenta. Los
/// <c>IgnoreQueryFilters()</c> de Trabajadores, Centros y Empresas solo dejan pasar filas eliminadas: cada uno va
/// seguido de la comparación del <c>TenantId</c> con el Tenant del ámbito, que falla cerrado sin él.
/// </para>
///
/// <para>
/// <b>Fecha de solicitud</b>: <c>VisitaTrabajador.FechaAsignacionUtc</c>; en las asignaciones anteriores a esa columna,
/// la fecha de su alta en la auditoría, y si no consta, el alta de la Visita.
/// <b>Gestor CAE</b>: <c>VisitaTrabajador.GestionadoPorUsuarioId</c> (el Actor real que asignó); en las anteriores, el
/// <c>ActorRealUsuarioId</c> de su alta en la auditoría; si no consta, vacío. Nunca el principal de cartera. El nombre
/// se resuelve con <see cref="IDirectorioUsuariosService.ObtenerNombresVisiblesAsync"/>, primero desde el Tenant de la
/// Visita y después desde el Tenant de origen de quien mira, como el principal del cuadro.
/// </para>
/// </summary>
public class ObtenerExtranjerosDelMesQueryHandler(
    IMediator mediator,
    IAlcanceDatosService alcanceDatos,
    ITenantActual tenantActual,
    IVisitasQueryContext visitasContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ICentrosQueryContext centrosContext,
    IEmpresasQueryContext empresasContext,
    IAuditoriaQueryContext auditoriaContext,
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    TimeProvider reloj)
    : IRequestHandler<ObtenerExtranjerosDelMesQuery, ExtranjerosDelMesDto>
{
    private sealed record Fila(
        Guid TenantId, string TenantNombre, DateTime FechaSolicitudUtc, string? ContactoVisita,
        string? Empresa, string? Subcontrata, string? Cliente, string Centro, DateOnly FechaIngreso, DateOnly FechaSalida,
        string NombreYApellidos, string? Documento, Guid? GestorCaeId);

    public async Task<ExtranjerosDelMesDto> Handle(ObtenerExtranjerosDelMesQuery request, CancellationToken cancellationToken)
    {
        await AutorizacionCuadroDireccion.ExigirLecturaAsync(currentUserService);
        if (!MesCuadroDireccion.EsValido(request.Anyo, request.Mes))
            return new ExtranjerosDelMesDto(request.Anyo, request.Mes, [], 0, CuadroDireccionSinCifras.MesNoValido);

        var mes = new MesCuadroDireccion(request.Anyo, request.Mes);
        if (mes.InicioUtc > reloj.GetUtcNow().UtcDateTime)
            return new ExtranjerosDelMesDto(request.Anyo, request.Mes, [], 0, CuadroDireccionSinCifras.MesFuturo);

        var clientes = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);
        var filas = new List<Fila>();
        var nombres = new Dictionary<Guid, string>();
        var sinAlcanceCompleto = 0;

        foreach (var cliente in clientes)
        {
            using (AmbitoTenantExplicito.Establecer(cliente.TenantId))
            {
                if (!await alcanceDatos.AlcanzaElTenantEnteroAsync(cancellationToken))
                {
                    sinAlcanceCompleto++;
                    continue;
                }

                var delTenant = await LeerDelTenantAsync(cliente, request.Anyo, request.Mes, cancellationToken);
                filas.AddRange(delTenant);
                await ResolverNombresAsync(delTenant, nombres, cancellationToken);
            }
        }

        await ResolverNombresDesdeElOrigenAsync(filas, nombres, cancellationToken);

        var resultado = filas
            .Select(f => new FilaExtranjeroDelMesDto(
                f.TenantId, f.TenantNombre, DiaDeNegocio.De(f.FechaSolicitudUtc), f.ContactoVisita,
                f.Empresa, f.Subcontrata, f.Cliente, f.Centro, f.FechaIngreso, f.FechaSalida,
                f.NombreYApellidos, f.Documento,
                f.GestorCaeId is { } gestor ? nombres.GetValueOrDefault(gestor) : null))
            .OrderBy(f => f.TenantNombre, StringComparer.CurrentCulture)
            .ThenBy(f => f.FechaIngreso)
            .ThenBy(f => f.Centro, StringComparer.CurrentCulture)
            .ThenBy(f => f.NombreYApellidos, StringComparer.CurrentCulture)
            .ToList();

        return new ExtranjerosDelMesDto(request.Anyo, request.Mes, resultado, sinAlcanceCompleto);
    }

    private async Task<List<Fila>> LeerDelTenantAsync(
        ClienteAutorizadoDto cliente, int anyo, int numeroMes, CancellationToken cancellationToken)
    {
        // Falla cerrado: sin Tenant en el ámbito no hay con qué comparar las filas sin filtro.
        var tenantId = tenantActual.TenantId
            ?? throw new InvalidOperationException("Los Trabajadores extranjeros del mes se leen con un Tenant en el ámbito.");
        var primerDia = new DateOnly(anyo, numeroMes, 1);
        var primerDiaSiguiente = primerDia.AddMonths(1);

        var trabajadores = trabajadoresContext.Trabajadores
            .IgnoreQueryFilters()
            .Where(t => t.TenantId == tenantId);
        var centros = centrosContext.Centros
            .IgnoreQueryFilters()
            .Where(c => c.TenantId == tenantId);

        var asignaciones = await (
                from vt in visitasContext.VisitasTrabajadores
                join v in visitasContext.Visitas on vt.VisitaId equals v.Id
                join t in trabajadores on vt.TrabajadorId equals t.Id
                join c in centros on v.CentroId equals c.Id
                where !v.EstaCancelada && v.FechaInicio >= primerDia && v.FechaInicio < primerDiaSiguiente
                select new
                {
                    vt.Id, vt.FechaAsignacionUtc, vt.GestionadoPorUsuarioId, VisitaCreadaEnUtc = v.CreadoEnUtc,
                    v.ContactoNombre, v.FechaInicio, v.FechaFin,
                    t.Nombre, t.Apellidos, t.Dni, t.MarcaExtranjeroCobrable, t.EmpresaId, t.SubcontrataId,
                    CentroNombre = c.Nombre, c.ClienteId, CentroEmpresaId = c.EmpresaId,
                })
            .ToListAsync(cancellationToken);

        // El criterio se evalúa en memoria: es código (ValidadorIdentificacion), no SQL.
        var extranjeras = asignaciones
            .Where(a => CriterioExtranjeroCobrable.Efectivo(a.MarcaExtranjeroCobrable, a.Dni))
            .ToList();
        if (extranjeras.Count == 0)
            return [];

        var empresaIds = extranjeras
            .SelectMany(a => new[] { a.EmpresaId, a.SubcontrataId, (Guid?)a.ClienteId, (Guid?)a.CentroEmpresaId })
            .OfType<Guid>().Distinct().ToList();
        var razonesSociales = await empresasContext.Empresas
            .IgnoreQueryFilters()
            .Where(e => e.TenantId == tenantId)
            .Where(e => empresaIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        // Las asignaciones anteriores a FechaAsignacionUtc/GestionadoPorUsuarioId: su alta en la auditoría del Tenant.
        var sinDatoPropio = extranjeras
            .Where(a => a.FechaAsignacionUtc is null || a.GestionadoPorUsuarioId is null)
            .Select(a => a.Id).ToList();
        var altasAuditadas = sinDatoPropio.Count == 0
            ? []
            : (await auditoriaContext.RegistrosAuditoria
                    .Where(r => r.EntidadTipo == nameof(VisitaTrabajador) && r.Accion == AccionAlta
                                && sinDatoPropio.Contains(r.EntidadId))
                    .Select(r => new { r.EntidadId, r.FechaUtc, r.ActorRealUsuarioId })
                    .ToListAsync(cancellationToken))
                .GroupBy(r => r.EntidadId)
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.FechaUtc).First());

        return extranjeras.Select(a =>
        {
            var alta = altasAuditadas.GetValueOrDefault(a.Id);
            return new Fila(
                tenantId, cliente.Nombre,
                a.FechaAsignacionUtc ?? alta?.FechaUtc ?? a.VisitaCreadaEnUtc,
                string.IsNullOrWhiteSpace(a.ContactoNombre) ? null : a.ContactoNombre,
                razonesSociales.GetValueOrDefault(a.EmpresaId ?? a.CentroEmpresaId),
                a.SubcontrataId is { } subcontrataId ? razonesSociales.GetValueOrDefault(subcontrataId) : null,
                razonesSociales.GetValueOrDefault(a.ClienteId),
                a.CentroNombre, a.FechaInicio, a.FechaFin,
                $"{a.Nombre} {a.Apellidos}".Trim(), a.Dni,
                a.GestionadoPorUsuarioId ?? alta?.ActorRealUsuarioId);
        }).ToList();
    }

    /// <summary>La acción con la que el interceptor de auditoría registra un alta.</summary>
    private const string AccionAlta = "Creado";

    private async Task ResolverNombresAsync(
        IReadOnlyCollection<Fila> filas, Dictionary<Guid, string> nombres, CancellationToken cancellationToken)
    {
        var pendientes = Pendientes(filas, nombres);
        if (pendientes.Count == 0)
            return;

        foreach (var (id, nombre) in await directorioUsuarios.ObtenerNombresVisiblesAsync(pendientes, cancellationToken))
            nombres[id] = nombre;
    }

    private async Task ResolverNombresDesdeElOrigenAsync(
        IReadOnlyCollection<Fila> filas, Dictionary<Guid, string> nombres, CancellationToken cancellationToken)
    {
        if (Pendientes(filas, nombres).Count == 0)
            return;

        if (await currentUserService.ObtenerTenantOrigenIdAsync() is not { } origen)
            return;

        using (AmbitoTenantExplicito.Establecer(origen))
            await ResolverNombresAsync(filas, nombres, cancellationToken);
    }

    private static List<Guid> Pendientes(IEnumerable<Fila> filas, Dictionary<Guid, string> nombres) =>
        filas.Select(f => f.GestorCaeId).OfType<Guid>().Where(id => !nombres.ContainsKey(id)).Distinct().ToList();
}

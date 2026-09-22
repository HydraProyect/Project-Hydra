using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;

namespace CaeManager.Web.Features.Bandeja;

public enum SeveridadMiTrabajo { Bloqueo, Actuacion, Proximo, Seguimiento }

public enum AgruparMiTrabajo { Organizacion, Severidad }

public enum OrdenMiTrabajo { Prioridad, Cliente }

/// <summary>
/// Una fila de la cola agregada. El Tenant propietario viaja aparte del ítem
/// porque <see cref="ItemBandejaDto"/> no lo lleva: nunca se deduce de
/// <c>EmpresaId</c>/<c>ClienteId</c> (contrato Gen2 § 7, regla dura).
/// </summary>
public sealed record FilaMiTrabajo(Guid TenantId, string TenantNombre, int OrdenTenant, SeveridadMiTrabajo Severidad, ItemBandejaDto Item);

public sealed record FiltroMiTrabajo(
    SeveridadMiTrabajo? Severidad = null,
    Guid? TenantId = null,
    string Busqueda = "",
    AgruparMiTrabajo Agrupar = AgruparMiTrabajo.Organizacion,
    OrdenMiTrabajo Orden = OrdenMiTrabajo.Prioridad,
    IReadOnlySet<string>? Abiertos = null,
    IReadOnlySet<Guid>? Cerrados = null);

/// <param name="Clave">TenantId en modo Organización; nombre de la severidad en modo Severidad.</param>
/// <param name="Plegado">Solo modo Organización: el Gestor CAE plegó la cabecera del Tenant.</param>
/// <param name="ResumenCalendario">Texto del pliegue «N vencimientos y envíos en calendario»; null si no hay nada plegado.</param>
/// <param name="ResumenCabecera">Recuento que se enseña en la cabecera cuando el grupo está plegado.</param>
public sealed record GrupoMiTrabajo(
    string Clave,
    string Etiqueta,
    Guid? TenantId,
    int Total,
    bool Plegado,
    IReadOnlyList<FilaMiTrabajo> Filas,
    string? ResumenCalendario,
    string? ResumenCabecera);

public sealed record FilaCarteraMiTrabajo(Guid TenantId, string Nombre, int Total, int Bloqueos);

/// <summary>
/// Lógica de presentación de Mi trabajo Gen2 (mockup «Cola operativa
/// multi-Tenant», <c>visible()</c>/<c>renderVals()</c>), sin Blazor, para
/// probarla directamente. Todo lo que filtra aquí ya llegó autorizado por
/// Tenant desde <see cref="ObtenerMiTrabajoAgregadoQuery"/>: este filtro es
/// de interfaz, nunca de seguridad.
/// </summary>
public sealed class MiTrabajoVista
{
    private readonly IReadOnlyList<FilaMiTrabajo> _cartera;

    public MiTrabajoVista(MiTrabajoAgregadoDto datos)
    {
        // Contrato § 10 (decisión del propietario): el Operador CAE no gestiona
        // su propio Tenant desde aquí. Que ObtenerClientesAutorizadosQuery lo
        // devuelva no basta para meterlo en la cartera.
        var gestionados = datos.Tenants.Where(t => !t.EsOrigen).ToList();
        Cartera = gestionados
            .Select(t => new FilaCarteraMiTrabajo(t.TenantId, t.TenantNombre, t.Resumen.TotalAcciones, t.Resumen.Bloqueos))
            .ToList();
        _cartera = gestionados.SelectMany((t, orden) => Aplanar(t, orden)).ToList();
    }

    public IReadOnlyList<FilaCarteraMiTrabajo> Cartera { get; }

    public int TotalCartera => _cartera.Count;

    private static IEnumerable<FilaMiTrabajo> Aplanar(MiTrabajoTenantDto tenant, int orden)
    {
        var bloqueoActuacion = tenant.BloqueoActuacion.Grupos.SelectMany(g => g.Items).Concat(tenant.BloqueoActuacion.SinGrupo);
        foreach (var item in bloqueoActuacion)
            yield return new(tenant.TenantId, tenant.TenantNombre, orden,
                ObtenerMiTrabajoAgregadoQueryHandler.EsBloqueo(item) ? SeveridadMiTrabajo.Bloqueo : SeveridadMiTrabajo.Actuacion, item);
        foreach (var item in tenant.Proximos)
            yield return new(tenant.TenantId, tenant.TenantNombre, orden, SeveridadMiTrabajo.Proximo, item);
        foreach (var item in tenant.Seguimiento)
            yield return new(tenant.TenantId, tenant.TenantNombre, orden, SeveridadMiTrabajo.Seguimiento, item);
    }

    /// <summary>Organización elegida + búsqueda, nunca la severidad: base de los recuentos de los chips.</summary>
    public IReadOnlyList<FilaMiTrabajo> Ambito(FiltroMiTrabajo filtro) =>
        _cartera.Where(f => (filtro.TenantId is null || f.TenantId == filtro.TenantId) && CoincideBusqueda(f, filtro.Busqueda)).ToList();

    public int Contar(FiltroMiTrabajo filtro, SeveridadMiTrabajo? severidad)
    {
        var ambito = Ambito(filtro);
        return severidad is null ? ambito.Count : ambito.Count(f => f.Severidad == severidad);
    }

    /// <summary>Filas que casan con todos los filtros, antes de aplicar ningún pliegue.</summary>
    public IReadOnlyList<FilaMiTrabajo> Alcance(FiltroMiTrabajo filtro) =>
        Ambito(filtro).Where(f => filtro.Severidad is null || f.Severidad == filtro.Severidad).ToList();

    /// <summary>
    /// Filas que se pintan, en orden de pantalla — la misma secuencia que
    /// recorren j/k. Con «Todas», Próximo y Seguimiento quedan plegados tras el
    /// resumen de calendario hasta que el Gestor CAE lo abre (mockup:
    /// <c>abiertos</c>). Una búsqueda activa anula todo pliegue: el buscador
    /// tiene que alcanzar cada gestión de la cartera (contrato § 5, que corrige
    /// en esto al mockup, donde el pliegue se aplicaba después del filtro).
    /// </summary>
    public IReadOnlyList<FilaMiTrabajo> Visibles(FiltroMiTrabajo filtro)
    {
        var abiertos = filtro.Abiertos ?? new HashSet<string>();
        var cerrados = filtro.Cerrados ?? new HashSet<Guid>();
        var pliega = PliegaCalendario(filtro);
        var buscando = BusquedaActiva(filtro);

        var lista = Alcance(filtro).AsEnumerable();
        lista = filtro.Agrupar == AgruparMiTrabajo.Organizacion
            ? lista.Where(f => (buscando || !cerrados.Contains(f.TenantId)) && (!pliega || EsUrgente(f) || abiertos.Contains(f.TenantId.ToString())))
            : lista.Where(f => !pliega || EsUrgente(f) || abiertos.Contains(f.Severidad.ToString()));

        var porCliente = filtro.Orden == OrdenMiTrabajo.Cliente;
        return (filtro.Agrupar == AgruparMiTrabajo.Organizacion
                ? (porCliente
                    ? lista.OrderBy(f => f.OrdenTenant).ThenBy(ClaveCliente).ThenBy(ClaveCentro).ThenBy(f => f.Severidad)
                    : lista.OrderBy(f => f.OrdenTenant).ThenBy(f => f.Severidad).ThenBy(ClaveCliente).ThenBy(ClaveCentro))
                : (porCliente
                    ? lista.OrderBy(f => f.Severidad).ThenBy(ClaveCliente).ThenBy(ClaveCentro).ThenBy(f => f.OrdenTenant)
                    : lista.OrderBy(f => f.Severidad).ThenBy(f => f.OrdenTenant).ThenBy(ClaveCliente).ThenBy(ClaveCentro)))
            .ToList();
    }

    public IReadOnlyList<GrupoMiTrabajo> Grupos(FiltroMiTrabajo filtro)
    {
        var abiertos = filtro.Abiertos ?? new HashSet<string>();
        var cerrados = filtro.Cerrados ?? new HashSet<Guid>();
        var alcance = Alcance(filtro);
        var visibles = Visibles(filtro);
        var grupos = new List<GrupoMiTrabajo>();

        if (filtro.Agrupar == AgruparMiTrabajo.Organizacion)
        {
            foreach (var tenant in Cartera)
            {
                var delTenant = alcance.Where(f => f.TenantId == tenant.TenantId).ToList();
                if (delTenant.Count == 0) continue;

                var plegado = !BusquedaActiva(filtro) && cerrados.Contains(tenant.TenantId);
                var bloqueos = delTenant.Count(f => f.Severidad == SeveridadMiTrabajo.Bloqueo);
                var actuaciones = delTenant.Count(f => f.Severidad == SeveridadMiTrabajo.Actuacion);
                var resto = delTenant.Count - bloqueos - actuaciones;
                var calendarioPlegado = !plegado && PliegaCalendario(filtro) && resto > 0 && !abiertos.Contains(tenant.TenantId.ToString());

                grupos.Add(new GrupoMiTrabajo(
                    tenant.TenantId.ToString(), tenant.Nombre, tenant.TenantId, delTenant.Count, plegado,
                    plegado ? [] : visibles.Where(f => f.TenantId == tenant.TenantId).ToList(),
                    calendarioPlegado ? (resto == 1 ? "1 vencimiento o envío en calendario" : $"{resto} vencimientos y envíos en calendario") : null,
                    plegado ? ResumenPlegado(bloqueos, actuaciones, resto) : null));
            }
        }
        else
        {
            foreach (var severidad in Enum.GetValues<SeveridadMiTrabajo>())
            {
                var filas = visibles.Where(f => f.Severidad == severidad).ToList();
                var total = alcance.Count(f => f.Severidad == severidad);
                var plegado = PliegaCalendario(filtro) && !EsUrgente(severidad) && !abiertos.Contains(severidad.ToString());
                if (plegado)
                {
                    if (total > 0)
                        grupos.Add(new GrupoMiTrabajo(severidad.ToString(), Etiqueta(severidad), null, total, false, [],
                            ResumenSeveridadPlegada(severidad, total), null));
                }
                else if (filas.Count > 0)
                {
                    grupos.Add(new GrupoMiTrabajo(severidad.ToString(), Etiqueta(severidad), null, total, false, filas, null, null));
                }
            }
        }

        return grupos;
    }

    public string Titular(FiltroMiTrabajo filtro)
    {
        var bloqueos = Contar(filtro, SeveridadMiTrabajo.Bloqueo);
        return bloqueos switch
        {
            0 => "Ningún bloqueo hoy",
            1 => "1 bloqueo que resolver hoy",
            _ => $"{bloqueos} bloqueos que resolver hoy"
        };
    }

    public string Subtitular(FiltroMiTrabajo filtro)
    {
        var actuaciones = Contar(filtro, SeveridadMiTrabajo.Actuacion);
        var calendario = Contar(filtro, SeveridadMiTrabajo.Proximo) + Contar(filtro, SeveridadMiTrabajo.Seguimiento);
        var ambito = filtro.TenantId is { } id ? Cartera.FirstOrDefault(t => t.TenantId == id)?.Nombre ?? "toda mi cartera" : "toda mi cartera";
        if (filtro.Severidad is { } severidad)
            ambito += " · " + Etiqueta(severidad).ToLowerInvariant();
        return $"{(actuaciones == 1 ? "1 acción pendiente" : $"{actuaciones} acciones pendientes")}, {calendario} en calendario · {ambito}";
    }

    public static bool EsUrgente(FilaMiTrabajo fila) => EsUrgente(fila.Severidad);

    private static bool EsUrgente(SeveridadMiTrabajo severidad) =>
        severidad is SeveridadMiTrabajo.Bloqueo or SeveridadMiTrabajo.Actuacion;

    public static string Etiqueta(SeveridadMiTrabajo severidad) => severidad switch
    {
        SeveridadMiTrabajo.Bloqueo => "Bloqueo",
        SeveridadMiTrabajo.Actuacion => "Requiere actuación",
        SeveridadMiTrabajo.Proximo => "Próximo",
        _ => "Seguimiento"
    };

    private static string ResumenSeveridadPlegada(SeveridadMiTrabajo severidad, int total) => severidad == SeveridadMiTrabajo.Proximo
        ? (total == 1 ? "1 vencimiento en los próximos 30 días" : $"{total} vencimientos en los próximos 30 días")
        : (total == 1 ? "1 documento a la espera de la plataforma" : $"{total} documentos a la espera de la plataforma");

    private static string ResumenPlegado(int bloqueos, int actuaciones, int resto) => string.Join(" · ", new[]
    {
        bloqueos switch { 0 => null, 1 => "1 bloqueo", _ => $"{bloqueos} bloqueos" },
        actuaciones == 0 ? null : $"{actuaciones} por actuar",
        resto == 0 ? null : $"{resto} en calendario"
    }.Where(p => p is not null));

    private static bool BusquedaActiva(FiltroMiTrabajo filtro) => !string.IsNullOrWhiteSpace(filtro.Busqueda);

    private static bool PliegaCalendario(FiltroMiTrabajo filtro) => filtro.Severidad is null && !BusquedaActiva(filtro);

    private static bool CoincideBusqueda(FilaMiTrabajo fila, string busqueda)
    {
        if (string.IsNullOrWhiteSpace(busqueda)) return true;
        var item = fila.Item;
        var texto = string.Join(' ', item.Titulo, item.Subtitulo, item.ClienteNombre, item.EmpresaNombre, item.TrabajadorNombre, item.ProveedorNombre, fila.TenantNombre);
        return texto.Contains(busqueda.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }

    // «zzz» del mockup: el trabajo sin Cliente empresarial resuelto va al final.
    private static string ClaveCliente(FilaMiTrabajo fila) => fila.Item.ClienteNombre ?? "￿";

    private static string ClaveCentro(FilaMiTrabajo fila) => fila.Item.Subtitulo ?? string.Empty;
}

using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Web.Features.Bandeja.Recursos;

namespace CaeManager.Web.Features.Bandeja;

public enum SeveridadMiTrabajo { Bloqueo, Actuacion, Proximo, Seguimiento }

public enum AgruparMiTrabajo { Tenant, Severidad }

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
    AgruparMiTrabajo Agrupar = AgruparMiTrabajo.Tenant,
    OrdenMiTrabajo Orden = OrdenMiTrabajo.Prioridad,
    IReadOnlySet<string>? Abiertos = null,
    IReadOnlySet<Guid>? Cerrados = null);

/// <param name="Clave">TenantId en modo Tenant; nombre de la severidad en modo Severidad.</param>
/// <param name="Plegado">Solo modo Tenant: el Gestor CAE plegó la cabecera del Tenant.</param>
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

/// <param name="AlcanceCero">Quien mira no alcanza nada en este Tenant (<see cref="MiTrabajoTenantDto.AlcanceCero"/>).</param>
public sealed record FilaCarteraMiTrabajo(Guid TenantId, string Nombre, int Total, int Bloqueos, bool AlcanceCero);

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
            .Select(t => new FilaCarteraMiTrabajo(t.TenantId, t.TenantNombre, t.Resumen.TotalAcciones, t.Resumen.Bloqueos, t.AlcanceCero))
            .ToList();
        _cartera = gestionados.SelectMany((t, orden) => Aplanar(t, orden)).ToList();
    }

    public IReadOnlyList<FilaCarteraMiTrabajo> Cartera { get; }

    /// <summary>
    /// Nada que vigilar en el ámbito elegido (P2.3 de la demo a Dirección): la
    /// Empresa filtrada tiene alcance cero o, sin filtro, ninguna Empresa de la
    /// cartera tiene alcance —incluido no tener ninguna—. Es lo que separa una
    /// cola vacía «sin Asignación de Cartera» de una cartera al día, que exige
    /// al menos una Empresa con alcance y ningún pendiente. Solo lee lo que la
    /// Query ya resolvió; no filtra nada.
    /// </summary>
    public bool SinAlcance(FiltroMiTrabajo filtro) => filtro.TenantId is { } id
        ? Cartera.FirstOrDefault(t => t.TenantId == id)?.AlcanceCero ?? true
        : Cartera.All(t => t.AlcanceCero);

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

    /// <summary>Tenant elegido + búsqueda, nunca la severidad: base de los recuentos de los chips.</summary>
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
        lista = filtro.Agrupar == AgruparMiTrabajo.Tenant
            ? lista.Where(f => (buscando || !cerrados.Contains(f.TenantId)) && (!pliega || EsUrgente(f) || abiertos.Contains(f.TenantId.ToString())))
            : lista.Where(f => !pliega || EsUrgente(f) || abiertos.Contains(f.Severidad.ToString()));

        var porCliente = filtro.Orden == OrdenMiTrabajo.Cliente;
        return (filtro.Agrupar == AgruparMiTrabajo.Tenant
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

        if (filtro.Agrupar == AgruparMiTrabajo.Tenant)
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
                    calendarioPlegado ? Plural(resto, "CalendarioPlegadoUno", "CalendarioPlegadoVarios") : null,
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
        // «Ningún bloqueo hoy» afirmaría algo sobre una cartera que no hay.
        if (bloqueos == 0 && SinAlcance(filtro) && Ambito(filtro with { Busqueda = string.Empty }).Count == 0)
            return TextosMiTrabajo.Texto("TitularSinCartera");
        return bloqueos == 0
            ? TextosMiTrabajo.Texto("TitularSinBloqueos")
            : Plural(bloqueos, "TitularBloqueosUno", "TitularBloqueosVarios");
    }

    public string Subtitular(FiltroMiTrabajo filtro)
    {
        var actuaciones = Contar(filtro, SeveridadMiTrabajo.Actuacion);
        var calendario = Contar(filtro, SeveridadMiTrabajo.Proximo) + Contar(filtro, SeveridadMiTrabajo.Seguimiento);
        var todaLaCartera = TextosMiTrabajo.Texto("AmbitoTodaLaCartera");
        var ambito = filtro.TenantId is { } id ? Cartera.FirstOrDefault(t => t.TenantId == id)?.Nombre ?? todaLaCartera : todaLaCartera;
        if (filtro.Severidad is { } severidad)
            ambito += " · " + Etiqueta(severidad).ToLowerInvariant();
        return TextosMiTrabajo.Formato("Subtitular", Plural(actuaciones, "AccionesUna", "AccionesVarias"), calendario, ambito);
    }

    public static bool EsUrgente(FilaMiTrabajo fila) => EsUrgente(fila.Severidad);

    private static bool EsUrgente(SeveridadMiTrabajo severidad) =>
        severidad is SeveridadMiTrabajo.Bloqueo or SeveridadMiTrabajo.Actuacion;

    public static string Etiqueta(SeveridadMiTrabajo severidad) => severidad switch
    {
        SeveridadMiTrabajo.Bloqueo => TextosMiTrabajo.Texto("SeveridadBloqueo"),
        SeveridadMiTrabajo.Actuacion => TextosMiTrabajo.Texto("SeveridadActuacion"),
        SeveridadMiTrabajo.Proximo => TextosMiTrabajo.Texto("SeveridadProximo"),
        _ => TextosMiTrabajo.Texto("SeveridadSeguimiento")
    };

    private static string ResumenSeveridadPlegada(SeveridadMiTrabajo severidad, int total) => severidad == SeveridadMiTrabajo.Proximo
        ? Plural(total, "ProximosPlegadosUno", "ProximosPlegadosVarios")
        : Plural(total, "SeguimientoPlegadoUno", "SeguimientoPlegadoVarios");

    private static string ResumenPlegado(int bloqueos, int actuaciones, int resto) => string.Join(" · ", new[]
    {
        bloqueos == 0 ? null : Plural(bloqueos, "BloqueosUno", "BloqueosVarios"),
        actuaciones == 0 ? null : TextosMiTrabajo.Formato("PorActuar", actuaciones),
        resto == 0 ? null : TextosMiTrabajo.Formato("EnCalendario", resto)
    }.Where(p => p is not null));

    /// <summary>
    /// Línea de sujeto de la fila y del detalle (contrato § 14). Si el sujeto es
    /// la Empresa propia del Tenant, «Documentación de empresa»: repetir su
    /// nombre dentro del grupo de esa misma Empresa no dice nada. Si es una
    /// Subcontrata, «Subcontrata · nombre», para no confundirla con el Tenant.
    /// Solo sustituye el nombre del principio: lo que va detrás (el motivo de
    /// un rechazo o de una revisión IA) se conserva. Sin
    /// <see cref="ItemBandejaDto.EmpresaEsPropia"/>, o si el subtítulo no empieza
    /// por el nombre de la Empresa, se pinta el subtítulo tal cual.
    /// </summary>
    public static string Sujeto(ItemBandejaDto item)
    {
        if (item.EmpresaEsPropia is not { } esPropia
            || item.EmpresaNombre is not { Length: > 0 } nombre
            || !item.Subtitulo.StartsWith(nombre, StringComparison.Ordinal))
            return item.Subtitulo;

        var rotulo = esPropia
            ? TextosMiTrabajo.Texto("SujetoEmpresaPropia")
            : TextosMiTrabajo.Formato("SujetoSubcontrata", nombre);
        return rotulo + item.Subtitulo[nombre.Length..];
    }

    /// <summary>Singular con su clave propia («1 bloqueo»), plural con el número como {0}.</summary>
    internal static string Plural(int n, string claveUno, string claveVarios) =>
        n == 1 ? TextosMiTrabajo.Texto(claveUno) : TextosMiTrabajo.Formato(claveVarios, n);

    private static bool BusquedaActiva(FiltroMiTrabajo filtro) => !string.IsNullOrWhiteSpace(filtro.Busqueda);

    private static bool PliegaCalendario(FiltroMiTrabajo filtro) => filtro.Severidad is null && !BusquedaActiva(filtro);

    private static bool CoincideBusqueda(FilaMiTrabajo fila, string busqueda)
    {
        if (string.IsNullOrWhiteSpace(busqueda)) return true;
        var item = fila.Item;
        var texto = string.Join(' ', item.Titulo, item.Subtitulo, Sujeto(item), item.ClienteNombre, item.EmpresaNombre, item.TrabajadorNombre, item.ProveedorNombre, fila.TenantNombre);
        return texto.Contains(busqueda.Trim(), StringComparison.CurrentCultureIgnoreCase);
    }

    // «zzz» del mockup: el trabajo sin Cliente empresarial resuelto va al final.
    private static string ClaveCliente(FilaMiTrabajo fila) => fila.Item.ClienteNombre ?? "￿";

    private static string ClaveCentro(FilaMiTrabajo fila) => fila.Item.Subtitulo ?? string.Empty;
}

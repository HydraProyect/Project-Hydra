using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Documentos;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Usuarios.Commands.AsumirPrincipalDeOperacion;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>El Cuadro de dirección de un mes natural (hora peninsular). El mes en curso sale parcial.</summary>
public record ObtenerCuadroDireccionQuery(int Anyo, int Mes) : IRequest<CuadroDireccionDto>;

/// <summary>Una cifra de un mes con su estado: parcial si el mes no ha terminado.</summary>
public record CifraMensual(int Valor, EstadoCifraMensual Estado);

/// <summary>Solicitudes que llegaron en una franja (día de la semana y hora, en hora peninsular).</summary>
public record FranjaSolicitudesDto(DayOfWeek Dia, int Hora, int Cantidad);

/// <summary>Las solicitudes del mes (primer mensaje entrante de cada conversación) y su reparto por franja.</summary>
public record SolicitudesDelMesDto(CifraMensual Total, IReadOnlyList<FranjaSolicitudesDto> PorFranja);

/// <summary>
/// El cumplimiento documental de hoy, no el del mes: es una foto en <paramref name="MedidoEnUtc"/> y no se
/// reconstruye para meses pasados. Los recuentos son los de <see cref="KpisDashboardDto"/> (Documentos de
/// Trabajador operativos, clasificados con la calculadora única) y el porcentaje sale de
/// <see cref="KpisDashboardDto.FraccionDe"/> y <see cref="KpisDashboardDto.SinDocumentos"/>, la misma
/// definición que Inicio y el Dashboard ejecutivo.
/// </summary>
public record CumplimientoActualDto(
    int Vigentes, int Proximos, int Urgentes, int Vencidos, int SinConfirmar, int SinCaducidad,
    int ParesExigidosSinDocumento, int Porcentaje, bool SinDatos, DateTime MedidoEnUtc);

/// <summary>
/// Las cifras del Cuadro de dirección de un Tenant beneficiario, o la suma de todos. Volumen de la operación,
/// nunca actividad de una persona: ni tiempo, ni conexiones, ni quién cerró qué.
/// </summary>
/// <param name="TrabajadoresActivos">Trabajadores dados de alta en TALVEG en la fecha de corte (<see cref="DefinicionesCuadroDireccion"/>).</param>
/// <param name="AltasTrabajadores">Trabajadores dados de alta dentro del mes.</param>
/// <param name="BajasTrabajadores">Trabajadores eliminados dentro del mes (a mano o por la detección de RNT/ITA).</param>
/// <param name="CentrosActivos">Centros de trabajo existentes en la fecha de corte.</param>
/// <param name="AltasCentros">Centros de trabajo dados de alta dentro del mes.</param>
/// <param name="DocumentosGestionados">Documentos registrados en el mes, de cualquier titular, incluidas las versiones que luego se sustituyeron y los que luego se eliminaron.</param>
/// <param name="Requerimientos">Reclamaciones de documentación enviadas en el mes (decisión 2, 2026-10-17).</param>
/// <param name="Gestiones">
/// Gestiones completadas dentro del mes (<see cref="MedicionCuadroDireccion.GestionesCompletadas"/>). Null si la
/// medición no la lleva: un cierre mensual anterior a este campo no la guardó y no se reconstruye ni se inventa.
/// </param>
/// <param name="Extranjeros">
/// Trabajadores activos en el corte cuyo documento de identidad es de extranjero (NIE, TIE u otro que no sea DNI;
/// <see cref="CaeManager.Domain.Common.ValidadorIdentificacion.EsDocumentoDeExtranjero"/>): personas, no visitas. Null si la
/// medición no lo lleva (un cierre anterior).
/// </param>
/// <param name="ObrasNuevas">
/// Proyectos de tipo Obra dados de alta en el mes (los Eventos no cuentan). Null si la medición no lo lleva (un
/// cierre anterior a este campo).
/// </param>
/// <param name="CentrosNuevosPorCliente">
/// Los Centros dados de alta en el mes repartidos por Cliente empresarial: indicador de crecimiento, no se cobra
/// (decisión de Chris, 2026-10-20). Null si la medición no lo lleva (un cierre anterior).
/// </param>
public record CifrasCuadroDireccionDto(
    CifraMensual TrabajadoresActivos,
    CifraMensual AltasTrabajadores,
    CifraMensual BajasTrabajadores,
    CifraMensual CentrosActivos,
    CifraMensual AltasCentros,
    CifraMensual DocumentosGestionados,
    CifraMensual Requerimientos,
    SolicitudesDelMesDto Solicitudes,
    CumplimientoActualDto Cumplimiento,
    CifraMensual? Gestiones = null,
    CifraMensual? Extranjeros = null,
    CifraMensual? ObrasNuevas = null,
    IReadOnlyList<CentrosNuevosPorClienteDto>? CentrosNuevosPorCliente = null);

/// <summary>Centros dados de alta en el mes bajo un Cliente empresarial de un Tenant beneficiario.</summary>
public record CentrosNuevosPorClienteDto(Guid TenantId, Guid ClienteEmpresarialId, string ClienteNombre, int Centros);

/// <summary>
/// Quien lleva la marca de principal en la Asignación de Cartera bajo la Asignación de Operación del Operador
/// CAE sobre ese Tenant beneficiario: el Gestor CAE principal o, en un relevo, el Coordinador CAE principal
/// (<paramref name="Rol"/>). Es quién responde del Tenant, no una medida de su actividad.
/// </summary>
public record PrincipalDeCarteraDto(Guid UsuarioId, string Nombre, string Rol);

public record CuadroDireccionTenantDto(
    Guid TenantId, string TenantNombre, CifrasCuadroDireccionDto Cifras, PrincipalDeCarteraDto? PrincipalDeCartera);

/// <summary>Por qué un cuadro sale sin cifras: el mes pedido no existe o todavía no ha empezado.</summary>
public enum CuadroDireccionSinCifras
{
    /// <summary>Año fuera de [<see cref="MesCuadroDireccion.AnyoMinimo"/>, <see cref="MesCuadroDireccion.AnyoMaximo"/>] o mes fuera de 1..12.</summary>
    MesNoValido,

    /// <summary>El mes empieza después de ahora: no hay nada que contar.</summary>
    MesFuturo,
}

/// <param name="Estado">Parcial si el mes no ha terminado; estimado si terminó y todavía no hay cierre mensual.</param>
/// <param name="CorteUtc">Fecha de corte de las cifras «a fecha»: ahora en el mes en curso, el final del mes si ya terminó.</param>
/// <param name="TenantsSinAlcanceCompleto">
/// Tenants beneficiarios autorizados en los que quien mira no alcanza el Tenant entero
/// (<see cref="IAlcanceDatosService.AlcanzaElTenantEnteroAsync"/>): no salen en el cuadro ni en el total.
/// </param>
/// <param name="SinCifras">
/// Null si el cuadro se calculó. Si no, por qué no: el resultado sale vacío (sin Tenants, total a cero) en vez
/// de lanzar, y con un mes no válido las fechas quedan en <see cref="DateTime.MinValue"/>.
/// </param>
public record CuadroDireccionDto(
    int Anyo, int Mes, EstadoCifraMensual Estado, DateTime InicioUtc, DateTime FinUtc, DateTime CorteUtc,
    IReadOnlyList<CuadroDireccionTenantDto> Tenants, CifrasCuadroDireccionDto Total, int TenantsSinAlcanceCompleto,
    CuadroDireccionSinCifras? SinCifras = null);

/// <summary>Lo medido en un Tenant beneficiario, antes de marcar el estado del mes y fusionar.</summary>
public record MedicionCuadroDireccion(
    int TrabajadoresActivos, int AltasTrabajadores, int BajasTrabajadores,
    int CentrosActivos, int AltasCentros,
    int DocumentosGestionados, int Requerimientos,
    IReadOnlyList<DateTime> SolicitudesUtc,
    KpisDashboardDto Documental,
    int? GestionesCompletadas = null,
    EstadoCifraMensual? EstadoPersistido = null,
    int? ExtranjerosActivos = null,
    int? ObrasNuevas = null,
    IReadOnlyList<CentrosNuevosPorClienteDto>? CentrosNuevosPorCliente = null);

/// <summary>
/// Cuadro de dirección (incremento 2; sustituirá al Dashboard ejecutivo). Mismo patrón que
/// <see cref="ObtenerDashboardEjecutivoQueryHandler"/>: por cada Tenant beneficiario autorizado
/// (<see cref="ObtenerClientesAutorizadosQuery"/>), las consultas corren con
/// <see cref="AmbitoTenantExplicito"/> fijado a ese Tenant —filtro global y RLS de ese Tenant, ninguna cruza
/// a otro— y el resultado se fusiona en memoria con <see cref="Fusionar"/>, que es pura. Secuencial por el
/// mismo motivo que allí: el <c>DbContext</c> scoped no admite consultas concurrentes.
///
/// <para>
/// <b>Alcance</b>: un Tenant solo entra si quien mira alcanza el Tenant entero
/// (<see cref="IAlcanceDatosService.AlcanzaElTenantEnteroAsync"/>). Las cifras son del Tenant entero y las
/// bajas cuentan filas eliminadas que ya no están en ninguna lista de alcance, así que no se pueden
/// recortar a una cartera parcial: quien no alcanza el Tenant entero no las ve, y se dice cuántos Tenants
/// quedaron fuera.
/// </para>
///
/// <para>
/// <b>Filas eliminadas</b>: activos, altas y bajas de Trabajadores y Centros, y los Documentos gestionados, necesitan las filas con borrado
/// lógico, que el filtro global oculta. El filtro combina Tenant y borrado en una condición, así que
/// <c>IgnoreQueryFilters()</c> obliga a comparar el <c>TenantId</c> a mano con el Tenant del ámbito —falla
/// cerrado sin Tenant— y la política RLS de la tabla sigue acotando al Tenant de la sesión.
/// </para>
///
/// <para>
/// <b>Gestor CAE principal</b>: se lee del catálogo del Operador CAE de origen de quien mira, igual que la
/// alerta «sin principal» (<c>ObtenerOperacionesSinPrincipalQuery</c>), y con su misma condición: solo lo
/// ven Coordinador CAE, Dirección CAE y Administrador del Operador CAE (<see cref="PerfilQueAsume"/>).
/// Nunca se mide la actividad de una persona.
/// </para>
/// </summary>
public class ObtenerCuadroDireccionQueryHandler(
    IMediator mediator,
    IAlcanceDatosService alcanceDatos,
    ITenantActual tenantActual,
    ITrabajadoresQueryContext trabajadoresContext,
    ICentrosQueryContext centrosContext,
    IDocumentosQueryContext documentosContext,
    IReclamacionesQueryContext reclamacionesContext,
    IComunicacionesQueryContext comunicacionesContext,
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    ICatalogoIncorporacionCartera catalogo,
    TimeProvider reloj,
    ICierresCuadroDireccion? cierresContext = null,
    CaeManager.Application.Gestiones.IGestionesQueryContext? gestionesContext = null,
    CaeManager.Application.Proyectos.IProyectosQueryContext? proyectosContext = null,
    CaeManager.Application.Empresas.IEmpresasQueryContext? empresasContext = null)
    : IRequestHandler<ObtenerCuadroDireccionQuery, CuadroDireccionDto>
{
    public async Task<CuadroDireccionDto> Handle(ObtenerCuadroDireccionQuery request, CancellationToken cancellationToken)
    {
        await AutorizacionCuadroDireccion.ExigirLecturaAsync(currentUserService);
        var ahoraUtc = reloj.GetUtcNow().UtcDateTime;
        if (!MesCuadroDireccion.EsValido(request.Anyo, request.Mes))
            return SinCifras(request.Anyo, request.Mes, null, ahoraUtc, CuadroDireccionSinCifras.MesNoValido);

        var mes = new MesCuadroDireccion(request.Anyo, request.Mes);
        if (mes.InicioUtc > ahoraUtc)
            return SinCifras(request.Anyo, request.Mes, mes, ahoraUtc, CuadroDireccionSinCifras.MesFuturo);

        var corteUtc = mes.CorteA(ahoraUtc);
        var clientes = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);
        var principales = await ObtenerPrincipalesAsync(cancellationToken);

        var porTenant = new List<(ClienteAutorizadoDto Cliente, MedicionCuadroDireccion Medicion)>();
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

                var cierre = mes.FinUtc <= ahoraUtc && cierresContext is not null
                    ? await cierresContext.CierresMensuales.SingleOrDefaultAsync(c => c.Anyo == mes.Anyo && c.Mes == mes.Mes && c.ReabiertoEnUtc == null, cancellationToken)
                    : null;
                var medicion = cierre is null
                    ? await new MedicionMensualCuadroDireccion(tenantActual, trabajadoresContext, centrosContext, documentosContext, reclamacionesContext, comunicacionesContext, mediator, gestionesContext, proyectosContext, empresasContext).MedirAsync(mes, corteUtc, cancellationToken)
                    : FotoCierreMensual.Leer(cierre.FotoJson).Medicion with
                    {
                        EstadoPersistido = cierre.Estimado ? EstadoCifraMensual.Estimado : EstadoCifraMensual.Existe,
                        Documental = await mediator.Send(new ObtenerKpisDashboardQuery(), cancellationToken)
                    };
                porTenant.Add((cliente, medicion));
            }
        }

        return Fusionar(mes, ahoraUtc, porTenant, principales, sinAlcanceCompleto);
    }

    private async Task<IReadOnlyDictionary<Guid, PrincipalDeCarteraDto>> ObtenerPrincipalesAsync(CancellationToken cancellationToken)
    {
        var ninguno = new Dictionary<Guid, PrincipalDeCarteraDto>();

        var actorId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var origen = await currentUserService.ObtenerTenantOrigenIdAsync();
        if (actorId is null || origen is null)
            return ninguno;

        using (AmbitoTenantExplicito.Establecer(origen.Value))
        {
            // Falla cerrado sin rol de negocio en la sesión: Soporte TALVEG nunca es Operador CAE.
            if (await currentUserService.ObtenerRolEfectivoAsync() is null)
                return ninguno;

            if (!await PerfilQueAsume.LoTieneAsync(actorId.Value, origen.Value, directorioUsuarios, cancellationToken))
                return ninguno;

            var principales = (await catalogo.ObtenerCarterasVivasAsync(origen.Value, null, cancellationToken))
                .Where(c => c.EsPrincipal)
                .GroupBy(c => c.PropietarioTenantId)
                .Select(g => g.First())
                .ToList();

            if (principales.Count == 0)
                return ninguno;

            var nombres = await directorioUsuarios.ObtenerNombresVisiblesAsync(
                principales.Select(p => p.UsuarioId).Distinct().ToList(), cancellationToken);

            return principales.ToDictionary(
                p => p.PropietarioTenantId,
                p => new PrincipalDeCarteraDto(p.UsuarioId, nombres.GetValueOrDefault(p.UsuarioId) ?? string.Empty, p.Rol));
        }
    }

    /// <summary>
    /// Fusión pura, sin infraestructura, para probarla con datos fabricados: los recuentos se suman; el
    /// cumplimiento del total se recalcula con <see cref="KpisDashboardDto.FraccionDe"/> sobre los recuentos
    /// sumados (nunca la media de porcentajes); las solicitudes se reparten por franja sobre todas juntas. Cada
    /// cifra mensual lleva el estado del mes: parcial si no ha terminado.
    /// </summary>
    public static CuadroDireccionDto Fusionar(
        MesCuadroDireccion mes,
        DateTime ahoraUtc,
        IReadOnlyList<(ClienteAutorizadoDto Cliente, MedicionCuadroDireccion Medicion)> porTenant,
        IReadOnlyDictionary<Guid, PrincipalDeCarteraDto> principales,
        int tenantsSinAlcanceCompleto)
    {
        var estado = mes.EstadoA(ahoraUtc);
        if (estado != EstadoCifraMensual.Parcial && porTenant.Count > 0 && porTenant.All(p => p.Medicion.EstadoPersistido == EstadoCifraMensual.Existe))
            estado = EstadoCifraMensual.Existe;

        var tenants = porTenant
            .Select(p => new CuadroDireccionTenantDto(
                p.Cliente.TenantId,
                p.Cliente.Nombre,
                Cifras([p.Medicion], p.Medicion.EstadoPersistido ?? mes.EstadoA(ahoraUtc), ahoraUtc),
                principales.GetValueOrDefault(p.Cliente.TenantId)))
            .OrderBy(t => t.TenantNombre, StringComparer.CurrentCulture)
            .ToList();

        return new CuadroDireccionDto(
            mes.Anyo, mes.Mes, estado, mes.InicioUtc, mes.FinUtc, mes.CorteA(ahoraUtc),
            tenants,
            Cifras(porTenant.Select(p => p.Medicion).ToList(), estado, ahoraUtc),
            tenantsSinAlcanceCompleto);
    }

    /// <summary>
    /// El resultado controlado de un mes que no se puede calcular: vacío, sin consultar nada, y con el motivo.
    /// Con un mes no válido no hay fechas que calcular y quedan en <see cref="DateTime.MinValue"/>.
    /// </summary>
    public static CuadroDireccionDto SinCifras(
        int anyo, int mes, MesCuadroDireccion? calculable, DateTime ahoraUtc, CuadroDireccionSinCifras motivo)
    {
        var inicio = calculable?.InicioUtc ?? DateTime.MinValue;
        return new CuadroDireccionDto(
            anyo, mes, EstadoCifraMensual.Parcial, inicio, calculable?.FinUtc ?? DateTime.MinValue, inicio,
            [], Cifras([], EstadoCifraMensual.Parcial, ahoraUtc), 0, motivo);
    }

    /// <summary>Las cifras de un conjunto de mediciones (un Tenant o la suma de varios); pura, la reutiliza la serie mensual.</summary>
    public static CifrasCuadroDireccionDto Cifras(
        IReadOnlyList<MedicionCuadroDireccion> mediciones, EstadoCifraMensual estado, DateTime ahoraUtc)
    {
        CifraMensual Suma(Func<MedicionCuadroDireccion, int> valor) => new(mediciones.Sum(valor), estado);

        var solicitudes = mediciones.SelectMany(m => m.SolicitudesUtc).ToList();

        var vigentes = mediciones.Sum(m => m.Documental.DocumentosVigentes);
        var proximos = mediciones.Sum(m => m.Documental.DocumentosProximos);
        var urgentes = mediciones.Sum(m => m.Documental.DocumentosUrgentes);
        var vencidos = mediciones.Sum(m => m.Documental.DocumentosVencidos);
        var sinConfirmar = mediciones.Sum(m => m.Documental.DocumentosSinConfirmar);
        var sinCaducidad = mediciones.Sum(m => m.Documental.DocumentosSinCaducidad);
        var paresExigidos = mediciones.Sum(m => m.Documental.ParesExigidosSinDocumento);
        var (porcentaje, sinDatos) = KpisDashboardDto.SinDocumentos(
            KpisDashboardDto.FraccionDe(vigentes, proximos, urgentes, vencidos, sinConfirmar, sinCaducidad),
            paresExigidos);

        return new CifrasCuadroDireccionDto(
            TrabajadoresActivos: Suma(m => m.TrabajadoresActivos),
            AltasTrabajadores: Suma(m => m.AltasTrabajadores),
            BajasTrabajadores: Suma(m => m.BajasTrabajadores),
            CentrosActivos: Suma(m => m.CentrosActivos),
            AltasCentros: Suma(m => m.AltasCentros),
            DocumentosGestionados: Suma(m => m.DocumentosGestionados),
            Requerimientos: Suma(m => m.Requerimientos),
            Solicitudes: new SolicitudesDelMesDto(
                new CifraMensual(solicitudes.Count, estado), DefinicionesCuadroDireccion.PorFranja(solicitudes)),
            Cumplimiento: new CumplimientoActualDto(
                vigentes, proximos, urgentes, vencidos, sinConfirmar, sinCaducidad, paresExigidos,
                porcentaje, sinDatos, ahoraUtc),
            Gestiones: mediciones.Count > 0 && mediciones.All(m => m.GestionesCompletadas is not null)
                ? new CifraMensual(mediciones.Sum(m => m.GestionesCompletadas!.Value), estado)
                : null,
            Extranjeros: mediciones.Count > 0 && mediciones.All(m => m.ExtranjerosActivos is not null)
                ? new CifraMensual(mediciones.Sum(m => m.ExtranjerosActivos!.Value), estado)
                : null,
            ObrasNuevas: mediciones.Count > 0 && mediciones.All(m => m.ObrasNuevas is not null)
                ? new CifraMensual(mediciones.Sum(m => m.ObrasNuevas!.Value), estado)
                : null,
            CentrosNuevosPorCliente: mediciones.Count > 0 && mediciones.All(m => m.CentrosNuevosPorCliente is not null)
                ? SumarCentrosNuevosPorCliente(mediciones.SelectMany(m => m.CentrosNuevosPorCliente!))
                : null);
    }

    /// <summary>Suma por Tenant y Cliente empresarial, de más Centros nuevos a menos.</summary>
    public static IReadOnlyList<CentrosNuevosPorClienteDto> SumarCentrosNuevosPorCliente(IEnumerable<CentrosNuevosPorClienteDto> filas) =>
        filas
            .GroupBy(f => (f.TenantId, f.ClienteEmpresarialId))
            .Select(g => new CentrosNuevosPorClienteDto(g.Key.TenantId, g.Key.ClienteEmpresarialId, g.First().ClienteNombre, g.Sum(f => f.Centros)))
            .Where(f => f.Centros > 0)
            .OrderByDescending(f => f.Centros)
            .ThenBy(f => f.ClienteNombre, StringComparer.CurrentCulture)
            .ToList();
}

using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.TarifasOperadorCae;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>
/// Las Empresas que el Operador CAE externo de quien mira gestiona en un mes, con los trozos del mes en que su Asignación
/// de Operación externa estuvo en vigor. Es la única fuente de esa vigencia para la Facturación del Operador CAE: su
/// consulta, el conteo de extranjeros, el detalle por Empresa, el Excel por Empresa, el Excel general de un Operador CAE y
/// el KPI «Extranjeros» de la Visión general la leen de aquí, para que cifra, detalle y Excel no diverjan.
/// </summary>
public record ObtenerEmpresasGestionadasDelMesQuery(int Anyo, int Mes) : IRequest<EmpresasGestionadasDelMesDto>;

/// <param name="EsOperadorCae">
/// Si el Tenant de origen de quien mira opera hoy o ha operado a algún Tenant ajeno mediante una Asignación de Operación
/// externa. Un Tenant que no es Operador CAE no tiene Empresas gestionadas: no se confunde con un Operador al que, en
/// ese mes, no le queda ninguna vigente (ahí <paramref name="Tramos"/> sale vacío y la cifra correcta es cero).
/// </param>
/// <param name="Tramos">Por Tenant beneficiario, los trozos del mes en vigor; los Tenants sin ninguno no figuran.</param>
public record EmpresasGestionadasDelMesDto(bool EsOperadorCae, IReadOnlyDictionary<Guid, IReadOnlyList<TramoDeVigencia>> Tramos)
{
    public static EmpresasGestionadasDelMesDto Ninguna { get; } =
        new(false, new Dictionary<Guid, IReadOnlyList<TramoDeVigencia>>());
}

/// <summary>
/// Autorización: <see cref="AutorizacionCuadroDireccion"/>, la de las cifras del Cuadro de dirección. El Operador CAE es el
/// Tenant de origen de quien mira, sea Administrador, Dirección CAE o Coordinador CAE; las vigencias son las de sus
/// Asignaciones de Operación externas (<see cref="AutorizacionTarifasOperadorCae.VigenciasPorTenantBeneficiarioAsync"/>,
/// la misma vigencia que usa la serie) y se leen con el ámbito en ese Tenant de origen, que es el propietario de las filas.
/// Una Sesión Privilegiada lee el Cuadro pero no tiene Empresas gestionadas: el Actor de Plataforma TALVEG no es Operador
/// CAE, y su Tenant de origen no es el que abrió (<see cref="AutorizacionCuadroDireccion.EntraPorRolAsync"/>).
/// </summary>
public class ObtenerEmpresasGestionadasDelMesQueryHandler(
    ICurrentUserService usuario, IOperacionesQueryContext operaciones, IPoliticaNotaInterna politicaAcceso)
    : IRequestHandler<ObtenerEmpresasGestionadasDelMesQuery, EmpresasGestionadasDelMesDto>
{
    public async Task<EmpresasGestionadasDelMesDto> Handle(ObtenerEmpresasGestionadasDelMesQuery request, CancellationToken cancellationToken)
    {
        await AutorizacionCuadroDireccion.ExigirLecturaAsync(usuario, politicaAcceso);
        return await LeerAsync(usuario, operaciones, request.Anyo, request.Mes, cancellationToken);
    }

    /// <summary>
    /// Quien llama ya pasó <see cref="AutorizacionCuadroDireccion"/>. Sin Tenant de origen, sin entrar por rol de negocio,
    /// con un mes no válido o sin Asignaciones externas, no hay Empresas gestionadas.
    /// </summary>
    public static async Task<EmpresasGestionadasDelMesDto> LeerAsync(
        ICurrentUserService usuario, IOperacionesQueryContext operaciones, int anyo, int numeroMes, CancellationToken cancellationToken)
    {
        if (!MesCuadroDireccion.EsValido(anyo, numeroMes)
            || !await AutorizacionCuadroDireccion.EntraPorRolAsync(usuario)
            || await usuario.ObtenerTenantOrigenIdAsync() is not { } operador)
            return EmpresasGestionadasDelMesDto.Ninguna;

        var mes = new MesCuadroDireccion(anyo, numeroMes);
        ILookup<Guid, VigenciaAsignacionOperacion> vigencias;
        using (AmbitoTenantExplicito.Establecer(operador))
            vigencias = await AutorizacionTarifasOperadorCae.VigenciasPorTenantBeneficiarioAsync(operaciones, operador, cancellationToken);

        var tramos = vigencias
            .Select(g => (TenantId: g.Key, Tramos: ValoracionOperadorCae.TramosEnElMes(g, mes.InicioUtc, mes.FinUtc)))
            .Where(e => e.Tramos.Count > 0)
            .ToDictionary(e => e.TenantId, e => e.Tramos);
        return new EmpresasGestionadasDelMesDto(vigencias.Count > 0, tramos);
    }
}

/// <summary>
/// Los Trabajadores extranjeros cobrables del mes de las Empresas gestionadas, acotados a la vigencia de la Asignación de
/// Operación externa: el detalle por Empresa de la Facturación y su Excel. Con <paramref name="TenantId"/>, solo esa
/// Empresa; si no es una Empresa gestionada en el mes (el propio Tenant del Operador, un Tenant sin Asignación externa en
/// vigor ese mes o uno ajeno), el resultado sale sin filas, nunca con datos de otro Tenant.
/// </summary>
/// <param name="GeneralSiNoEsOperadorCae">
/// Para el Excel «Exportar extranjeros del mes» sin Empresa concreta: quien mira no es de un Operador CAE (no hay Asignación
/// de Operación externa que acotar) y recibe el listado general de <see cref="ObtenerExtranjerosDelMesQuery"/>, como antes.
/// Un Operador CAE recibe siempre las filas acotadas a la vigencia de cada Empresa gestionada, que son las que cuenta su
/// cifra facturada; con <paramref name="TenantId"/> este indicador no se usa y quien no es Operador CAE sigue sin filas.
/// </param>
public record ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery(
    int Anyo, int Mes, Guid? TenantId = null, bool GeneralSiNoEsOperadorCae = false) : IRequest<ExtranjerosDelMesDto>;

/// <summary>
/// Misma autorización y mismo alcance que <see cref="ObtenerExtranjerosDelMesQuery"/> (de la que sale cada fila), y mismo
/// criterio de vigencia que <see cref="ContarExtranjerosCobrablesDelMesQuery"/> (<see cref="ContarExtranjerosCobrablesDelMesQueryHandler.EnAlgunTramo"/>),
/// así que el número de la pestaña, el detalle y el Excel por Empresa no pueden divergir.
/// <see cref="ExtranjerosDelMesDto.TenantsSinAlcanceCompleto"/> cuenta solo las Empresas gestionadas (la pedida, si se pidió una).
/// </summary>
public class ObtenerExtranjerosDeEmpresasGestionadasDelMesQueryHandler(IMediator mediator)
    : IRequestHandler<ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery, ExtranjerosDelMesDto>
{
    public async Task<ExtranjerosDelMesDto> Handle(
        ObtenerExtranjerosDeEmpresasGestionadasDelMesQuery request, CancellationToken cancellationToken)
    {
        var extranjeros = await mediator.Send(new ObtenerExtranjerosDelMesQuery(request.Anyo, request.Mes), cancellationToken);
        if (extranjeros.SinCifras is not null)
            return extranjeros;

        var gestionadas = await mediator.Send(new ObtenerEmpresasGestionadasDelMesQuery(request.Anyo, request.Mes), cancellationToken);
        // Quien no es de un Operador CAE no tiene vigencia que aplicar: solo el Excel general sin Empresa pedida conserva el listado entero.
        if (request.GeneralSiNoEsOperadorCae && request.TenantId is null && !gestionadas.EsOperadorCae)
            return extranjeros;

        var tramos = gestionadas.Tramos;
        bool Pedida(Guid tenantId) => request.TenantId is not { } pedida || pedida == tenantId;

        var filas = extranjeros.Filas
            .Where(f => Pedida(f.TenantId)
                        && tramos.TryGetValue(f.TenantId, out var delTenant)
                        && ContarExtranjerosCobrablesDelMesQueryHandler.EnAlgunTramo(f.FechaIngreso, delTenant))
            .ToList();
        var sinAlcance = (extranjeros.IdsSinAlcanceCompleto ?? []).Where(id => Pedida(id) && tramos.ContainsKey(id)).ToList();

        return extranjeros with { Filas = filas, TenantsSinAlcanceCompleto = sinAlcance.Count, IdsSinAlcanceCompleto = sinAlcance };
    }
}

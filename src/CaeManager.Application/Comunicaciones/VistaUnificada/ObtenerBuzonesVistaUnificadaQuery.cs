using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerActividadBuzones;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.VistaDemo;
using MediatR;

namespace CaeManager.Application.Comunicaciones.VistaUnificada;

/// <summary>
/// Los buzones de un Tenant beneficiario de la vista unificada de Comunicaciones (D-6 b), para la pantalla de Buzones
/// filtrada por «Empresa gestionada» (lienzo «Comunicaciones multi-buzón», tablero de Buzones, estado L-55). Solo lectura:
/// ninguna acción sobre los buzones de otro Tenant pasa por aquí.
///
/// <para>
/// Mismo recorrido que la lista y el selector (<see cref="ObtenerConversacionesUnificadasQuery"/>,
/// <see cref="ObtenerTenantsBeneficiariosVistaUnificadaQuery"/>): la entrada común
/// (<see cref="EntradaVistaUnificada.AbrirAsync"/>, con <see cref="TenantId"/> como coordenada que solo estrecha), el
/// <see cref="AmbitoTenantAutorizado"/> del Tenant, bajo su RLS, y la segunda línea de
/// <see cref="EntradaVistaUnificada.ConversacionesDelTenantAsync"/>. Si el Tenant no está en el conjunto o no pasa la segunda
/// línea, <c>null</c>: la pantalla no distingue un Tenant ajeno de uno inexistente.
/// </para>
///
/// <para>
/// <b>Qué buzones.</b> Los compartidos y el personal propio, con la regla de
/// <see cref="ObtenerConexionesIntegracionQuery"/> con <c>SoloPropiasYGenerales</c> (la misma lectura que la bandeja hace
/// de los buzones): fuera del Tenant activo no se lista el buzón personal de otro Gestor CAE, tampoco para el
/// Administrador. La actividad es la de <see cref="ObtenerActividadBuzonesQuery"/>, medida sobre las conversaciones que la
/// vista lee de ese Tenant.
/// </para>
/// </summary>
[AreaDelEncargo("Comunicaciones")]
public record ObtenerBuzonesVistaUnificadaQuery(Guid TenantId) : IRequest<BuzonesDeTenantBeneficiarioDto?>;

/// <summary>
/// Implementación en Outbound del puerto <see cref="IBuzonesDeTenantsBeneficiarios"/> del Núcleo: envía las peticiones de la
/// vista unificada por el mediador, para que pasen por la guarda y el ámbito de Outbound (como <c>ActividadBuzonesService</c>).
/// </summary>
public sealed class BuzonesDeTenantsBeneficiariosService(
    ISender mediador, ICoordenadaSesionPrivilegiada coordenadaSesionPrivilegiada) : IBuzonesDeTenantsBeneficiarios
{
    public async Task<IReadOnlyList<TenantBeneficiarioDeBuzonesDto>> ObtenerTenantsAsync(
        CancellationToken cancellationToken = default)
    {
        // Con Sesión Privilegiada la vista no existe y la entrada lo rechazaría con una excepción que LoggingBehavior
        // registra como error en cada carga de Buzones. No es una segunda regla de acceso: solo ahorra esa petición, y la
        // entrada sigue rechazando por su cuenta este plano y los demás.
        if (coordenadaSesionPrivilegiada.HaySeleccionada)
            return [];

        // Los del selector de la bandeja unificada que se pudieron leer; un Tenant no consultado no se ofrece. Coste
        // aceptado: el selector recorre el ámbito de cada Tenant del conjunto y cuenta sus Abiertas, que aquí se
        // descartan; a cambio, el conjunto y la segunda línea tienen una sola definición.
        var selector = await mediador.Send(new ObtenerTenantsBeneficiariosVistaUnificadaQuery(), cancellationToken);
        return selector.Tenants.Select(t => new TenantBeneficiarioDeBuzonesDto(t.TenantId, t.TenantNombre)).ToList();
    }

    public Task<BuzonesDeTenantBeneficiarioDto?> ObtenerBuzonesAsync(
        Guid tenantId, CancellationToken cancellationToken = default) =>
        mediador.Send(new ObtenerBuzonesVistaUnificadaQuery(tenantId), cancellationToken);
}

public class ObtenerBuzonesVistaUnificadaQueryHandler(
    IComunicacionesQueryContext comunicacionesContext, IIntegracionesQueryContext integracionesContext,
    IEmpresasQueryContext empresasContext, IOperacionesQueryContext operaciones, ITenantsQueryContext tenants,
    IAlcanceDatosService alcanceDatos, ICurrentUserService currentUserService, ITenantActual tenantActual,
    ICoordenadaSesionPrivilegiada coordenadaSesionPrivilegiada, IDirectorioUsuariosService directorioUsuarios,
    IVistaDemoActual? vistaDemo = null)
    : IRequestHandler<ObtenerBuzonesVistaUnificadaQuery, BuzonesDeTenantBeneficiarioDto?>
{
    public async Task<BuzonesDeTenantBeneficiarioDto?> Handle(
        ObtenerBuzonesVistaUnificadaQuery request, CancellationToken cancellationToken)
    {
        var (identidad, conjunto) = await EntradaVistaUnificada.AbrirAsync(
            [request.TenantId], currentUserService, operaciones, tenants, coordenadaSesionPrivilegiada, vistaDemo,
            cancellationToken);
        if (!conjunto.Contains(request.TenantId))
            return null;

        var esOrigen = request.TenantId == identidad.TenantOrigenId;
        var cota = OrdenConversacionesUnificadas.TruncarAMicrosegundos(DateTime.UtcNow);
        var (autorizado, buzones) = await AmbitoTenantAutorizado.EjecutarAsync(
            identidad, operaciones, tenants, request.TenantId,
            ambito => LeerAsync(ambito, esOrigen, cota, cancellationToken),
            cancellationToken);
        return autorizado ? buzones : null;
    }

    /// <summary>Los buzones del Tenant, dentro de su ámbito; <c>null</c> si no pasa la segunda línea.</summary>
    private async Task<BuzonesDeTenantBeneficiarioDto?> LeerAsync(
        AmbitoTenantAutorizado ambito, bool esOrigen, DateTime cota, CancellationToken cancellationToken)
    {
        var visibles = await EntradaVistaUnificada.ConversacionesDelTenantAsync(
            ambito, esOrigen, cota, null, comunicacionesContext, integracionesContext,
            alcanceDatos, currentUserService, tenantActual, cancellationToken);
        if (visibles is null)
            return null;

        // Con los errores y los avisos: la entrada ya rechazó la Sesión Privilegiada, que es el único caso en que
        // ObtenerConexionesIntegracionQuery los omite (cae_app_soporte no lee UltimoError).
        // La cartera de Clientes empresariales, por la extensión con el recorte de D6=a: en una petición acotada al Tenant
        // solo Inbound la vacía, y entonces solo salen los buzones sin Cliente empresarial (LecturasDeCartera…SoloInbound).
        ambito.ExigirAbierto();
        var clientesEmpresarialesVisibles = await alcanceDatos.ClientesEmpresarialesVisiblesAsync(cancellationToken);
        ambito.ExigirAbierto();
        var usuarioActualId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var conexiones = await ObtenerConexionesIntegracionQueryHandler.ListarAsync(
            integracionesContext, empresasContext, clientesEmpresarialesVisibles, soloPropiasYGenerales: true,
            usuarioActualId, conErroresYAvisos: true, cancellationToken);

        ambito.ExigirAbierto();
        var usuarioIds = conexiones.SelectMany(c => new[] { c.ResponsableUsuarioId, c.GestorPropietarioId })
            .OfType<Guid>().Distinct().ToList();
        var nombres = usuarioIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await directorioUsuarios.ObtenerNombresVisiblesAsync(usuarioIds, cancellationToken);

        ambito.ExigirAbierto();
        var actividad = await ObtenerActividadBuzonesQueryHandler.MedirAsync(
            visibles, comunicacionesContext.Mensajes, conexiones.Select(c => c.Id).ToList(), cancellationToken);

        return new BuzonesDeTenantBeneficiarioDto(conexiones, nombres, actividad);
    }
}

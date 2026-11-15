using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.Comercial.Commands.ContratarLineaInbound;

/// <summary>
/// B-04a: el Actor de Plataforma TALVEG contrata la línea Inbound (Producto Contratado de
/// <see cref="LineaProductoContratado.Inbound"/>) para un Tenant que ya existe. Es el segundo tiempo del alta del Tenant
/// TCE (decisión D-A, 2026-11-15): el Tenant se crea antes con <c>AdminPlataforma</c> global, por la vía vigente, y la
/// línea se contrata después sobre él. Un Tenant TCE es un Tenant con este Producto Contratado vigente, sin valor propio en
/// <c>TipoTenant</c> (D-E); el gate DN-7 de <c>app_inbound_tenant_contratista_del_contrato</c> ya lo lee así.
///
/// <para>
/// <b>Autorización</b>: solo dentro de una Sesión Privilegiada con la capacidad <see cref="CapacidadPrivilegio.Aprovisionamiento"/>
/// sobre el Tenant activo, que es el Tenant beneficiario. Nunca el rol efectivo: un Administrador del propio Tenant que
/// pudiera contratar la línea se abriría solo la lectura entre Tenants. <c>AutorizacionEscrituraBehavior</c> ya deniega
/// cualquier otra Sesión Privilegiada y la de Aprovisionamiento sobre otro Tenant, pero sin Sesión Privilegiada deja
/// pasar a quien tenga un rol con escritura, así que el handler repite la comprobación entera. Por ser
/// <see cref="IComandoDeAprovisionamiento"/>, el handler escribe como <c>cae_app_aprovisionamiento</c>, el único rol con
/// INSERT sobre <c>ProductosContratados</c> (D-C); el runtime sigue en SELECT.
/// </para>
///
/// <para>
/// <b>Lo contratado</b> (D-B): el Actor de Plataforma introduce a mano el Customer de Stripe del Pagador TALVEG, que ya
/// existe en Stripe, el tramo y la modalidad. El alta no llama a Stripe ni crea Customers, así que no comprueba que el
/// Customer exista: solo su formato. La vigencia empieza al contratar. Un solo Producto Contratado de la línea Inbound
/// vigente por Tenant: lo impone un índice único parcial de la base, porque el rol elevado no lee la fila entera (solo las
/// columnas del WHERE de la baja, opción B del 2026-11-15).
/// </para>
///
/// <para>
/// Fuera de alcance (D-D): el Administrador del Tenant y la Asignación de Operación Inbound, que van a B-04b.
/// Un Pagador, un tramo o una modalidad mal introducidos se corrigen con <c>CorregirLineaInboundCommand</c>, y la línea
/// se da de baja con <c>DarDeBajaLineaInboundCommand</c> (opción B del 2026-11-15).
/// </para>
/// </summary>
[AreaDelEncargo("Comercial")]
public record ContratarLineaInboundCommand(
    string PagadorStripeCustomerId, TramoTrabajadoresActivos Tramo, ModalidadSuscripcionAlta Modalidad)
    : ICommand<Guid>, IComandoDeAprovisionamiento;

public class ContratarLineaInboundCommandValidator : AbstractValidator<ContratarLineaInboundCommand>
{
    public ContratarLineaInboundCommandValidator()
    {
        RuleFor(c => c.PagadorStripeCustomerId)
            .Must(ProductoContratado.EsPagadorStripeCustomerIdValido)
            .WithMessage(ErroresContratarLineaInbound.PagadorNoValido.Mensaje);
        RuleFor(c => c.Tramo).IsInEnum();
        RuleFor(c => c.Modalidad).IsInEnum();
    }
}

public static class ErroresContratarLineaInbound
{
    public static readonly Error NoAutorizado = Error.Crear(
        "LineaInbound.NoAutorizado",
        "Solo una sesión de aprovisionamiento de la plataforma sobre esta organización puede contratar la línea Inbound.");

    public static readonly Error TenantNoEncontrado = Error.Crear(
        "LineaInbound.TenantNoEncontrado", "No encontramos la organización.");

    public static readonly Error TenantNoValido = Error.Crear(
        "LineaInbound.TenantNoValido", "La organización de la plataforma no contrata la línea Inbound.");

    public static readonly Error PagadorNoValido = Error.Crear(
        "LineaInbound.PagadorNoValido",
        $"Indica el Customer de Stripe del Pagador: solo letras sin acentos, dígitos y guion bajo, hasta {ProductoContratado.LongitudMaximaPagadorStripeCustomerId} caracteres.");

    public static readonly Error ContratoNoValido = Error.Crear(
        "LineaInbound.ContratoNoValido", "El tramo o la modalidad no son válidos.");

    public static readonly Error YaContratada = Error.Crear(
        "LineaInbound.YaContratada", "Esta organización ya tiene la línea Inbound contratada y vigente.");
}

public class ContratarLineaInboundCommandHandler(
    ITenantActual tenantActual,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ITenantsQueryContext tenants,
    IProductoContratadoRepository productosContratados,
    TimeProvider reloj)
    : IRequestHandler<ContratarLineaInboundCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(ContratarLineaInboundCommand request, CancellationToken cancellationToken)
    {
        // La autoridad va antes que cualquier lectura: quien no puede no debe poder distinguir, por el mensaje de error,
        // qué Tenants existen o cuáles tienen ya la línea. ValidationBehavior corre antes que el handler, así que un
        // Administrador sin Sesión Privilegiada puede recibir antes el fallo de formato del Pagador; no revela datos.
        //
        // ObtenerAsync y no RevalidarAsync, como AutoridadSobreElEncargo y AutorizacionEscrituraEfectiva: el handler
        // corre ya con la conexión elevada a cae_app_aprovisionamiento, que no lee las tablas de sesiones ni de
        // concesiones del plano de Plataforma (solo cae_app_runtime y cae_app_soporte), así que revalidar aquí fallaría
        // con 42501 (lo reprodujo CI en la ronda 1 de Codex).
        // La memo que lee la dejó en fresco ElevacionEscrituraAprovisionamientoBehavior justo antes de elevar, en este
        // mismo comando y después de la revalidación de AutorizacionEscrituraBehavior. Sin ese pipeline (sin sesión),
        // ObtenerAsync consulta como el rol que tenga la conexión, que no es el elevado.
        //
        // Ventana declarada (hallazgo 1 de Codex, ronda 2): la última comprobación de la Sesión Privilegiada y de su
        // concesión es la revalidación de ElevacionEscrituraAprovisionamientoBehavior, antes de elevar. Si entre ella y
        // el COMMIT del INSERT se cierra la sesión o se revoca la concesión, el INSERT se confirma igual, y la ventana se
        // alarga si el INSERT espera la plaza del índice único. La base no la cierra: cae_app_aprovisionamiento no lee
        // las tablas de sesiones ni de concesiones, el contexto firmado no lleva la sesión y la política solo mira el
        // Tenant. Es la misma ventana que aceptan hoy todos los comandos de aprovisionamiento (CrearEmpresa,
        // CrearTrabajador, el logo, las importaciones, los Encargos de administración); solo las capacidades acotadas de
        // cae_app_soporte revalidan dentro de su función SECURITY DEFINER, tras la espera. Cerrarla aquí es una decisión
        // pendiente, no un mecanismo que este comando invente.
        // Bajo impersonación no se contrata: el Actor real no actúa como el Usuario simulado.
        if (tenantActual.TenantId is not { } tenantId
            || await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not { } sesion
            || sesion.Capacidad != CapacidadPrivilegio.Aprovisionamiento
            || !sesion.TieneCaminoDeEscritura
            || sesion.TenantObjetivoId != tenantId
            || sesion.UsuarioSimuladoId is not null)
            return Result.Fallo<Guid>(ErroresContratarLineaInbound.NoAutorizado);

        // Se repite lo del validador: el handler no depende de que el pipeline lo haya ejecutado, y el dominio lanza
        // en vez de devolver un fallo.
        if (!ProductoContratado.EsPagadorStripeCustomerIdValido(request.PagadorStripeCustomerId))
            return Result.Fallo<Guid>(ErroresContratarLineaInbound.PagadorNoValido);
        if (!Enum.IsDefined(request.Tramo) || !Enum.IsDefined(request.Modalidad))
            return Result.Fallo<Guid>(ErroresContratarLineaInbound.ContratoNoValido);

        // La Sesión Privilegiada solo se abre sobre un Tenant que existe, pero el Tenant puede no estar ya cuando llega
        // el comando, y la base no tiene clave foránea de ProductosContratados a Tenants.
        // Solo lee: como el resto de Comercial, sobre el catálogo de Tenants y sin repositorio propio del agregado.
        var tenant = await tenants.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.EsPlataforma })
            .SingleOrDefaultAsync(cancellationToken);
        if (tenant is null)
            return Result.Fallo<Guid>(ErroresContratarLineaInbound.TenantNoEncontrado);

        // Defensa en profundidad: una Sesión Privilegiada no se abre sobre la organización del propio Actor de
        // Plataforma (ReglaTenantObjetivoAjeno), y TALVEG no es Cliente empresarial de nadie.
        if (tenant.EsPlataforma)
            return Result.Fallo<Guid>(ErroresContratarLineaInbound.TenantNoValido);

        var ahora = reloj.GetUtcNow().UtcDateTime;
        var producto = ProductoContratado.Contratar(
            LineaProductoContratado.Inbound, request.PagadorStripeCustomerId, request.Tramo, request.Modalidad,
            vigenteDesdeUtc: ahora, ahoraUtc: ahora);
        productosContratados.Agregar(producto);

        // Ya contratada, o dos altas a la vez: el índice único parcial deja pasar una.
        return await productosContratados.GuardarDetectandoInboundVigenteDuplicadoAsync(producto, cancellationToken)
            ? Result.Exito(producto.Id)
            : Result.Fallo<Guid>(ErroresContratarLineaInbound.YaContratada);
    }
}

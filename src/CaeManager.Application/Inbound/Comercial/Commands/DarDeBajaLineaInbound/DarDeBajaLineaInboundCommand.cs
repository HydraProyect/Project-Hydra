using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Inbound.Comercial.Commands.DarDeBajaLineaInbound;

/// <summary>
/// Baja de la línea Inbound (opción B del 2026-11-15, D1 (A)): el Actor de Plataforma TALVEG finaliza el Producto
/// Contratado de <see cref="LineaProductoContratado.Inbound"/> vigente del Tenant beneficiario, con fin de vigencia en el
/// instante en que ejecuta el comando. Con él se cierra el gate DN-7: el Tenant deja de responder como Tenant TCE.
///
/// <para>
/// <b>El cierre del periodo es manual</b> hasta que se modele la Subscription. La baja tiene efecto al cierre del periodo
/// en curso (DECISION_LOG 2026-11-05 (b); condiciones de facturación, baja y salida § 4.2 para el mensual y § 4.4 para el
/// anual, donde la baja impide la renovación), pero el periodo no está en el modelo: la línea Inbound va a presupuesto y
/// el alta no llama a Stripe. Así que la baja se registra cuando ya ha ocurrido, como exige
/// <see cref="ProductoContratado.Finalizar"/>: el Actor de Plataforma calcula ese cierre fuera de la aplicación y ejecuta
/// el comando ese día. Sin estado de baja programada ni job.
/// </para>
///
/// <para>
/// <b>Autorización</b>: <see cref="AutoridadSobreLaLineaInbound"/>, con su ventana de revocación declarada. Por ser
/// <see cref="IComandoDeAprovisionamiento"/>, escribe como <c>cae_app_aprovisionamiento</c>, que solo puede pasar una
/// fila de la línea Inbound vigente del Tenant del contexto firmado a Finalizado, con el fin en <c>now()</c> ± 5 minutos
/// (política <c>aprovisionamiento_solo_finaliza_inbound_vigente</c>).
/// </para>
/// </summary>
[AreaDelEncargo("Comercial")]
public record DarDeBajaLineaInboundCommand : ICommand<Guid>, IComandoDeAprovisionamiento;

public static class ErroresDarDeBajaLineaInbound
{
    public static readonly Error NoAutorizado = Error.Crear(
        "LineaInbound.NoAutorizado",
        "Solo una sesión de aprovisionamiento de la plataforma sobre esta organización puede dar de baja la línea Inbound.");

    public static readonly Error SinLineaVigente = Error.Crear(
        "LineaInbound.SinLineaVigente", "Esta organización no tiene la línea Inbound contratada y vigente.");

    public static readonly Error VigenciaAunNoIniciada = Error.Crear(
        "LineaInbound.VigenciaAunNoIniciada",
        "La vigencia de la línea Inbound todavía no ha empezado. Vuelve a intentarlo en unos minutos.");
}

public class DarDeBajaLineaInboundCommandHandler(
    ITenantActual tenantActual,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IProductoContratadoRepository productosContratados,
    ITransaccionDeComando transaccion)
    : IRequestHandler<DarDeBajaLineaInboundCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(DarDeBajaLineaInboundCommand request, CancellationToken cancellationToken)
    {
        // La autoridad va antes que cualquier lectura: quien no puede no distingue qué Tenants tienen la línea.
        if (await AutoridadSobreLaLineaInbound.TenantAutorizadoAsync(tenantActual, sesionPrivilegiadaActual, cancellationToken) is null)
            return Result.Fallo<Guid>(ErroresDarDeBajaLineaInbound.NoAutorizado);

        // En una transacción: la baja y su registro de auditoría se confirman o se deshacen juntos. El fin de vigencia es
        // el now() de esa transacción en la base, el reloj con el que comparan las políticas y el gate DN-7.
        var resultado = await transaccion.EjecutarAsync(async ct =>
        {
            var finalizacion = await productosContratados.FinalizarInboundVigenteAsync(ct);
            return (Result)(finalizacion.Resultado switch
            {
                ResultadoFinalizacionInbound.Finalizada => Result.Exito(finalizacion.ProductoContratadoId!.Value),
                ResultadoFinalizacionInbound.VigenciaAunNoIniciada => Result.Fallo<Guid>(ErroresDarDeBajaLineaInbound.VigenciaAunNoIniciada),
                _ => Result.Fallo<Guid>(ErroresDarDeBajaLineaInbound.SinLineaVigente),
            });
        }, cancellationToken);

        return (Result<Guid>)resultado;
    }
}

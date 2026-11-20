using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Comercial.Commands.ContratarLineaInbound;
using CaeManager.Application.Inbound.Comercial.Commands.DarDeBajaLineaInbound;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Inbound.Comercial.Commands.CorregirLineaInbound;

/// <summary>
/// Corrección de la línea Inbound (opción B del 2026-11-15, D2 (A)): el Actor de Plataforma TALVEG cambia el Pagador
/// TALVEG, el tramo, la modalidad o la declaración «solo Inbound» de lo contratado. Lo contratado no se sobrescribe (DECISION_LOG 2026-11-05 (a): el
/// histórico queda en filas; <see cref="ProductoContratado"/> fija que no cambia después de contratar): el comando
/// finaliza el Producto Contratado de <see cref="LineaProductoContratado.Inbound"/> vigente y contrata otro con los datos
/// corregidos, vigente desde el mismo instante. Devuelve el Id del nuevo.
///
/// <para>
/// <b>En una sola transacción</b>: primero la finalización y después el INSERT. Así el índice único parcial
/// <c>UX_ProductosContratados_InboundVigentePorTenant</c> no choca con la fila que se sustituye, y el gate DN-7 nunca ve
/// un instante sin línea Inbound vigente: o se confirman las dos escrituras o ninguna.
/// </para>
///
/// <para>
/// <b>El cierre del periodo es manual</b> hasta que se modele la Subscription: un cambio de Pagador TALVEG se aplica al
/// cierre del periodo en curso (condiciones de facturación, baja y salida § 2.4), y el Actor de Plataforma ejecuta el
/// comando ese día, como la baja (<c>DarDeBajaLineaInboundCommand</c>). Un error al contratar se corrige en cuanto se
/// detecta; la fila errónea queda como histórico.
/// </para>
///
/// <para>
/// <b>Autorización</b>: <see cref="AutoridadSobreLaLineaInbound"/>, con su ventana de revocación declarada. Escribe como
/// <c>cae_app_aprovisionamiento</c>, con las políticas de la baja y del alta de la línea Inbound.
/// </para>
///
/// <para>
/// <b>Declaración «solo Inbound»</b> (opción A', DECISION_LOG e496f33): va en la fila de la línea Inbound, y la corrección
/// contrata una fila nueva, así que el Actor de Plataforma TALVEG la declara otra vez en <see cref="DeclaraSoloInbound"/>.
/// No se copia de la fila sustituida: el rol elevado solo lee las columnas del WHERE de la baja
/// (<c>FinalizaLineaInboundPorAprovisionamiento</c>), y leerla exigiría ampliar sus privilegios. Sin marcar, el Tenant
/// conserva Outbound, como en el alta.
/// </para>
/// </summary>
/// <param name="DeclaraSoloInbound">
/// La declaración «solo Inbound» de la fila corregida. Opcional: <c>false</c> deja al Tenant con Outbound.
/// </param>
[AreaDelEncargo("Comercial")]
public record CorregirLineaInboundCommand(
    string PagadorStripeCustomerId, TramoTrabajadoresActivos Tramo, ModalidadSuscripcionAlta Modalidad,
    bool DeclaraSoloInbound)
    : ICommand<Guid>, IComandoDeAprovisionamiento;

public class CorregirLineaInboundCommandValidator : AbstractValidator<CorregirLineaInboundCommand>
{
    public CorregirLineaInboundCommandValidator()
    {
        RuleFor(c => c.PagadorStripeCustomerId)
            .Must(ProductoContratado.EsPagadorStripeCustomerIdValido)
            .WithMessage(ErroresContratarLineaInbound.PagadorNoValido.Mensaje);
        RuleFor(c => c.Tramo).IsInEnum();
        RuleFor(c => c.Modalidad).IsInEnum();
    }
}

public static class ErroresCorregirLineaInbound
{
    public static readonly Error NoAutorizado = Error.Crear(
        "LineaInbound.NoAutorizado",
        "Solo una sesión de aprovisionamiento de la plataforma sobre esta organización puede corregir la línea Inbound.");

    /// <summary>Los mismos que el alta: lo corregido tiene las reglas de lo contratado.</summary>
    public static Error PagadorNoValido => ErroresContratarLineaInbound.PagadorNoValido;

    public static Error ContratoNoValido => ErroresContratarLineaInbound.ContratoNoValido;

    public static Error SinLineaVigente => ErroresDarDeBajaLineaInbound.SinLineaVigente;

    public static Error VigenciaAunNoIniciada => ErroresDarDeBajaLineaInbound.VigenciaAunNoIniciada;

    /// <summary>
    /// Otra contratación de la línea Inbound se confirmó a la vez sobre el mismo Tenant: no puede pasar si la corrección
    /// finalizó la vigente, pero se traduce en vez de propagarse.
    /// </summary>
    public static Error YaContratada => ErroresContratarLineaInbound.YaContratada;
}

public class CorregirLineaInboundCommandHandler(
    ITenantActual tenantActual,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IProductoContratadoRepository productosContratados,
    ITransaccionDeComando transaccion)
    : IRequestHandler<CorregirLineaInboundCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CorregirLineaInboundCommand request, CancellationToken cancellationToken)
    {
        // La autoridad va antes que cualquier lectura, como en el alta y en la baja.
        if (await AutoridadSobreLaLineaInbound.TenantAutorizadoAsync(tenantActual, sesionPrivilegiadaActual, cancellationToken) is null)
            return Result.Fallo<Guid>(ErroresCorregirLineaInbound.NoAutorizado);

        // Se repite lo del validador: el handler no depende de que el pipeline lo haya ejecutado, y el dominio lanza
        // en vez de devolver un fallo.
        if (!ProductoContratado.EsPagadorStripeCustomerIdValido(request.PagadorStripeCustomerId))
            return Result.Fallo<Guid>(ErroresCorregirLineaInbound.PagadorNoValido);
        if (!Enum.IsDefined(request.Tramo) || !Enum.IsDefined(request.Modalidad))
            return Result.Fallo<Guid>(ErroresCorregirLineaInbound.ContratoNoValido);

        var resultado = await transaccion.EjecutarAsync(async ct =>
        {
            // Primero la finalización: el índice único parcial solo admite la nueva cuando la sustituida ya no es vigente.
            var finalizacion = await productosContratados.FinalizarInboundVigenteAsync(ct);
            if (finalizacion.Resultado == ResultadoFinalizacionInbound.SinInboundVigente)
                return Result.Fallo<Guid>(ErroresCorregirLineaInbound.SinLineaVigente);
            if (finalizacion.Resultado == ResultadoFinalizacionInbound.VigenciaAunNoIniciada)
                return Result.Fallo<Guid>(ErroresCorregirLineaInbound.VigenciaAunNoIniciada);

            // Un solo instante para el fin de la sustituida y el inicio de la nueva, sin hueco ni solape de vigencia: el
            // now() de esta transacción en la base, que devuelve la finalización. No el reloj de la aplicación: con él
            // adelantado, la nueva empezaría después del instante con el que el gate DN-7 compara, y tras el COMMIT no
            // habría línea Inbound que el gate viera vigente (hallazgo 1 de Codex, ronda 1). El gate compara con el
            // clock_timestamp() del momento en que se evalúa (GateInboundConRelojDeEvaluacion): now() de esta transacción
            // < COMMIT < instantánea lectora ≤ clock_timestamp(), así que toda lectura cuya instantánea ve este COMMIT ve
            // la nueva vigente, aunque su transacción o su sentencia exterior empezaran antes (rondas 3 y 4).
            var ahora = finalizacion.VigenteHastaUtc
                        ?? throw new InvalidOperationException("La finalización no devolvió su fin de vigencia.");

            var corregido = ProductoContratado.Contratar(
                LineaProductoContratado.Inbound, request.PagadorStripeCustomerId, request.Tramo, request.Modalidad,
                vigenteDesdeUtc: ahora, ahoraUtc: ahora, declaraSoloInbound: request.DeclaraSoloInbound);
            productosContratados.Agregar(corregido);

            return await productosContratados.GuardarDetectandoInboundVigenteDuplicadoAsync(corregido, ct)
                ? Result.Exito(corregido.Id)
                : Result.Fallo<Guid>(ErroresCorregirLineaInbound.YaContratada);
        }, cancellationToken);

        return (Result<Guid>)resultado;
    }
}

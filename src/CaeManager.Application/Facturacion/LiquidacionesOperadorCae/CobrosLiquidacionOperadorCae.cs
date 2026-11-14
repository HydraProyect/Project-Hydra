using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Facturacion;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Facturacion.LiquidacionesOperadorCae;

// Módulo Facturación (D-M1, 2026-11-14). Depende solo del Núcleo (D-F2): quién emite, qué meses están terminados y a qué
// Tenants beneficiarios gestionó el emisor los lee por el puerto IFuenteLiquidacionOperadorCae, que hoy implementa Outbound.
// Lo comprueba FacturacionSoloDependeDelNucleoTests.

public interface ICobrosLiquidacionOperadorCaeContext
{
    /// <summary>Acotado al Tenant del ámbito por el filtro global y por la política RLS <c>aislamiento_tenant</c>.</summary>
    IQueryable<CobroLiquidacionOperadorCae> CobrosLiquidacionOperadorCae { get; }
    void Agregar(CobroLiquidacionOperadorCae cobro);
}

/// <summary>
/// Quién lee y quién marca el cobro de la liquidación mensual del Operador CAE externo a cada Tenant beneficiario (D-F9,
/// 2026-11-14), por <see cref="IFuenteLiquidacionOperadorCae"/>: marcan y exportan el Administrador y la Dirección CAE del
/// Operador, con el rol efectivo en su Tenant de origen; el Coordinador CAE del Operador solo lee. Una Sesión Privilegiada
/// no tiene rol de negocio y no entra (Soporte TALVEG no es Operador CAE); el Tenant beneficiario tampoco, porque las
/// filas son del Operador y la RLS lo deja fuera.
/// </summary>
public static class AutorizacionCobrosLiquidacionOperadorCae
{
    public static readonly Error SinPermiso = Error.Crear(
        "CobroLiquidacionOperadorCae.SinPermiso", "Solo el Administrador o la Dirección CAE del Operador CAE marcan el cobro de sus liquidaciones.");

    public static readonly Error EmpresaNoGestionada = Error.Crear(
        "CobroLiquidacionOperadorCae.EmpresaNoGestionada", "Tu organización no gestionó a esta empresa ese mes, así que no tiene liquidación que marcar.");

    public static readonly Error MesNoTerminado = Error.Crear(
        "CobroLiquidacionOperadorCae.MesNoTerminado", "Solo se marca el cobro de la liquidación de un mes terminado.");

    public static readonly Error Conflicto = Error.Crear(
        "CobroLiquidacionOperadorCae.Conflicto", "Otra persona ha marcado este cobro a la vez. Vuelve a cargar el mes para ver su marca y aplica la tuya de nuevo.");
}

/// <param name="ReferenciaFacturaEmisor">El número de la factura real del emisor, si lo anotó.</param>
public record CobroLiquidacionOperadorCaeDto(
    Guid TenantBeneficiarioId, EstadoCobroLiquidacion Estado, string? ReferenciaFacturaEmisor, DateTime MarcadaEnUtc);

/// <param name="PuedeMarcar">Si quien mira puede cambiar las marcas (Administrador o Dirección CAE del Operador).</param>
/// <param name="Cobros">Solo las liquidaciones marcadas; la que no sale está <see cref="EstadoCobroLiquidacion.Pendiente"/>.</param>
public record CobrosLiquidacionOperadorCaeDto(bool PuedeMarcar, IReadOnlyList<CobroLiquidacionOperadorCaeDto> Cobros)
{
    public static CobrosLiquidacionOperadorCaeDto Ninguno { get; } = new(false, []);

    public CobroLiquidacionOperadorCaeDto? De(Guid tenantBeneficiarioId) =>
        Cobros.FirstOrDefault(c => c.TenantBeneficiarioId == tenantBeneficiarioId);
}

/// <summary>Las marcas de cobro de las liquidaciones de un mes del Operador CAE de quien pide; ninguna si no puede leerlas.</summary>
public record ObtenerCobrosLiquidacionOperadorCaeQuery(int Anyo, int Mes) : IRequest<CobrosLiquidacionOperadorCaeDto>;

public class ObtenerCobrosLiquidacionOperadorCaeQueryHandler(IFuenteLiquidacionOperadorCae fuente, ICobrosLiquidacionOperadorCaeContext contexto)
    : IRequestHandler<ObtenerCobrosLiquidacionOperadorCaeQuery, CobrosLiquidacionOperadorCaeDto>
{
    public async Task<CobrosLiquidacionOperadorCaeDto> Handle(ObtenerCobrosLiquidacionOperadorCaeQuery request, CancellationToken cancellationToken)
    {
        if (fuente.FinDelMesUtc(request.Anyo, request.Mes) is null || await fuente.EmisorQueLeeAsync() is not { } emisor)
            return CobrosLiquidacionOperadorCaeDto.Ninguno;

        var puedeMarcar = await fuente.EmisorQueEmiteAsync() == emisor;
        using (AmbitoTenantExplicito.Establecer(emisor))
        {
            var cobros = await contexto.CobrosLiquidacionOperadorCae
                .Where(c => c.TenantId == emisor && c.Anyo == request.Anyo && c.Mes == request.Mes)
                .Select(c => new CobroLiquidacionOperadorCaeDto(c.PropietarioTenantId, c.Estado, c.ReferenciaFacturaEmisor, c.MarcadaEnUtc))
                .ToListAsync(cancellationToken);
            return new CobrosLiquidacionOperadorCaeDto(puedeMarcar, cobros);
        }
    }
}

/// <summary>
/// Marca a mano el cobro de la liquidación de un mes terminado a un Tenant beneficiario (D-F5): pendiente, cobrada o
/// impagada, con el número de la factura real del emisor si lo quiere anotar. Es solo una anotación: ningún estado
/// suspende la gestión, corta el acceso ni avisa a nadie.
/// </summary>
public record MarcarCobroLiquidacionOperadorCaeCommand(
    Guid TenantBeneficiarioId, int Anyo, int Mes, EstadoCobroLiquidacion Estado, string? ReferenciaFacturaEmisor) : ICommand;

public class MarcarCobroLiquidacionOperadorCaeCommandValidator : AbstractValidator<MarcarCobroLiquidacionOperadorCaeCommand>
{
    public MarcarCobroLiquidacionOperadorCaeCommandValidator()
    {
        RuleFor(c => c.TenantBeneficiarioId).NotEmpty();
        RuleFor(c => c.Estado).IsInEnum();
        RuleFor(c => c.Mes).InclusiveBetween(1, 12);
        RuleFor(c => c.ReferenciaFacturaEmisor).MaximumLength(CobroLiquidacionOperadorCae.LongitudMaximaReferencia);
    }
}

public class MarcarCobroLiquidacionOperadorCaeCommandHandler(
    ICurrentUserService usuario, ICobrosLiquidacionOperadorCaeContext contexto, IFuenteLiquidacionOperadorCae fuente, IUnitOfWork unidad,
    TimeProvider reloj, IDescarteCambiosPendientes descarte)
    : IRequestHandler<MarcarCobroLiquidacionOperadorCaeCommand, Result>
{
    /// <summary>SQLSTATE estándar de violación de unicidad (unique_violation).</summary>
    private const string ViolacionDeUnicidad = "23505";

    /// <summary>
    /// La Empresa tiene liquidación ese mes si el emisor la gestionó en algún tramo del mes: la misma vigencia con que la
    /// pantalla «Facturación» la lista (<see cref="IFuenteLiquidacionOperadorCae.TenantsBeneficiariosDelMesAsync"/>). Así no
    /// se marca una liquidación que el emisor no tiene, ni la de un Tenant que no gestiona.
    /// </summary>
    public async Task<Result> Handle(MarcarCobroLiquidacionOperadorCaeCommand request, CancellationToken cancellationToken)
    {
        if (await fuente.EmisorQueEmiteAsync() is not { } emisor)
            return Result.Fallo(AutorizacionCobrosLiquidacionOperadorCae.SinPermiso);
        if (await usuario.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return Result.Fallo(AutorizacionCobrosLiquidacionOperadorCae.SinPermiso);

        var ahora = reloj.GetUtcNow().UtcDateTime;
        if (fuente.FinDelMesUtc(request.Anyo, request.Mes) is not { } fin || fin > ahora)
            return Result.Fallo(AutorizacionCobrosLiquidacionOperadorCae.MesNoTerminado);

        var beneficiarios = await fuente.TenantsBeneficiariosDelMesAsync(request.Anyo, request.Mes, cancellationToken);
        if (request.TenantBeneficiarioId == emisor || !beneficiarios.Contains(request.TenantBeneficiarioId))
            return Result.Fallo(AutorizacionCobrosLiquidacionOperadorCae.EmpresaNoGestionada);

        using var ambito = AmbitoTenantExplicito.Establecer(emisor);
        var existente = await contexto.CobrosLiquidacionOperadorCae.FirstOrDefaultAsync(c =>
            c.TenantId == emisor && c.PropietarioTenantId == request.TenantBeneficiarioId
            && c.Anyo == request.Anyo && c.Mes == request.Mes, cancellationToken);

        if (existente is null)
            contexto.Agregar(CobroLiquidacionOperadorCae.Registrar(
                request.TenantBeneficiarioId, request.Anyo, request.Mes, request.Estado, request.ReferenciaFacturaEmisor, usuarioId, ahora));
        else
            existente.Marcar(request.Estado, request.ReferenciaFacturaEmisor, usuarioId, ahora);

        // Dos marcas a la vez: la primera alta choca con el índice único (Tenant emisor, Tenant beneficiario, mes) y una
        // edición sobre una marca que otra persona acaba de cambiar, con el token de concurrencia (IVersionable). En los
        // dos casos gana la primera entera, nunca una mezcla de estado, referencia y autoría, y lo que este intento dejó
        // rastreado se suelta para que el siguiente Command del circuito no lo guarde.
        try
        {
            await unidad.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            descarte.DescartarCambiosPendientes();
            return Result.Fallo(AutorizacionCobrosLiquidacionOperadorCae.Conflicto);
        }
        catch (DbUpdateException excepcion) when (excepcion.InnerException is System.Data.Common.DbException { SqlState: ViolacionDeUnicidad })
        {
            descarte.DescartarCambiosPendientes();
            return Result.Fallo(AutorizacionCobrosLiquidacionOperadorCae.Conflicto);
        }
        return Result.Exito();
    }
}

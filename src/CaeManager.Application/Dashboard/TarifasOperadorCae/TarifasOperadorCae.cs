using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Dashboard;
using CaeManager.Domain.Operaciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.TarifasOperadorCae;

public interface ITarifasOperadorCaeContext
{
    /// <summary>Acotado al Tenant del ámbito por el filtro global y por la política RLS <c>aislamiento_tenant</c>.</summary>
    IQueryable<TarifaOperadorCae> TarifasOperadorCae { get; }
    void Agregar(TarifaOperadorCae tarifa);
}

/// <summary>
/// Quién lee y fija lo que el Operador CAE externo cobra a sus Tenants beneficiarios (decisión de Chris, 2026-10-20):
/// el Administrador o la Dirección CAE del propio Operador CAE, con el rol efectivo en su Tenant de origen. No es
/// <c>AutorizacionTarifasCliente</c>: aquel guarda lo que un Tenant cobra a sus Clientes empresariales. Un Gestor CAE
/// o un Coordinador CAE no pasan; una Sesión Privilegiada de plano 3 no tiene rol de negocio y tampoco (Soporte
/// TALVEG no es Operador CAE). Las filas son del Tenant de origen del Operador, así que la RLS deja fuera al
/// Tenant beneficiario aunque su Administrador lea el Cuadro de dirección.
/// </summary>
public static class AutorizacionTarifasOperadorCae
{
    private static readonly string[] RolesConAcceso = ["Administrador", "DireccionCae"];

    public static readonly Error SinPermiso = Error.Crear(
        "TarifaOperadorCae.SinPermiso", "Solo el Administrador o la Dirección CAE del Operador CAE gestionan sus tarifas.");

    public static readonly Error EmpresaNoGestionada = Error.Crear(
        "TarifaOperadorCae.EmpresaNoGestionada", "Tu organización no gestiona hoy a esta empresa, así que no puedes fijarle una tarifa.");

    /// <summary>El Tenant de origen del Operador CAE si quien pide puede gestionar sus tarifas; null si no.</summary>
    public static async Task<Guid?> OperadorQueGestionaAsync(ICurrentUserService usuario)
    {
        var origen = await usuario.ObtenerTenantOrigenIdAsync();
        if (origen is null)
            return null;

        using (AmbitoTenantExplicito.Establecer(origen.Value))
            return await usuario.ObtenerRolEfectivoAsync() is { } rol && RolesConAcceso.Contains(rol) ? origen : null;
    }

    /// <summary>
    /// Si <paramref name="operadorTenantId"/> opera hoy a <paramref name="propietarioTenantId"/> como Operador CAE
    /// externo: una Asignación de Operación vigente, no raíz y con propietario distinto del operador.
    /// </summary>
    public static Task<bool> GestionaAsync(
        IOperacionesQueryContext operaciones, Guid operadorTenantId, Guid propietarioTenantId, DateTime ahoraUtc, CancellationToken cancellationToken) =>
        operaciones.AsignacionesOperacion.AnyAsync(o =>
            !o.EsRaiz
            && o.OperadorTenantId == operadorTenantId
            && o.PropietarioTenantId == propietarioTenantId
            && o.PropietarioTenantId != operadorTenantId
            && o.Estado == EstadoAsignacion.Vigente
            && o.VigenciaDesde <= ahoraUtc
            && (o.VigenciaHasta == null || ahoraUtc < o.VigenciaHasta), cancellationToken);

    /// <summary>
    /// Las vigencias de las Asignaciones de Operación externas de <paramref name="operadorTenantId"/>, por Tenant
    /// beneficiario. Se llama con el ámbito en el Tenant de origen del Operador. Una Programada no ha empezado y no
    /// cuenta; una Cerrada cuenta hasta su cierre. Una Suspendida cuenta entera: la suspensión no guarda desde
    /// cuándo, y descontarla cambiaría meses ya cerrados. Toda Asignación externa cuenta como vigencia sobre el
    /// Tenant beneficiario entero, aunque su ámbito sea parcial: la tarifa es por Tenant, no por Cliente empresarial.
    /// </summary>
    public static async Task<ILookup<Guid, VigenciaAsignacionOperacion>> VigenciasPorTenantBeneficiarioAsync(
        IOperacionesQueryContext operaciones, Guid operadorTenantId, CancellationToken cancellationToken)
    {
        var filas = await operaciones.AsignacionesOperacion
            .Where(o => !o.EsRaiz
                && o.OperadorTenantId == operadorTenantId
                && o.PropietarioTenantId != operadorTenantId
                && o.Estado != EstadoAsignacion.Programada)
            .Select(o => new { o.PropietarioTenantId, o.VigenciaDesde, o.VigenciaHasta })
            .ToListAsync(cancellationToken);
        return filas.ToLookup(f => f.PropietarioTenantId, f => new VigenciaAsignacionOperacion(f.VigenciaDesde, f.VigenciaHasta));
    }
}

/// <summary>Todas las versiones de las tarifas del Operador CAE de quien pide; null si no puede verlas.</summary>
public record ObtenerTarifasOperadorCaeQuery : IRequest<IReadOnlyList<TarifaOperadorCaeDto>?>;

public class ObtenerTarifasOperadorCaeQueryHandler(ICurrentUserService usuario, ITarifasOperadorCaeContext contexto)
    : IRequestHandler<ObtenerTarifasOperadorCaeQuery, IReadOnlyList<TarifaOperadorCaeDto>?>
{
    public async Task<IReadOnlyList<TarifaOperadorCaeDto>?> Handle(ObtenerTarifasOperadorCaeQuery request, CancellationToken cancellationToken)
    {
        if (await AutorizacionTarifasOperadorCae.OperadorQueGestionaAsync(usuario) is not { } operador)
            return null;

        using (AmbitoTenantExplicito.Establecer(operador))
            return await LeerAsync(contexto, operador, cancellationToken);
    }

    /// <summary>Todas las versiones del Operador; quien llama ya pasó la guarda y fijó el ámbito en su Tenant de origen.</summary>
    public static async Task<IReadOnlyList<TarifaOperadorCaeDto>> LeerAsync(
        ITarifasOperadorCaeContext contexto, Guid operador, CancellationToken cancellationToken) =>
        await contexto.TarifasOperadorCae
            .Where(t => t.TenantId == operador)
            .OrderBy(t => t.VigenteDesdeUtc)
            .Select(t => new TarifaOperadorCaeDto(t.PropietarioTenantId, t.Concepto, t.PrecioUnitario, t.MonedaIso, t.Activa, t.VigenteDesdeUtc))
            .ToListAsync(cancellationToken);
}

/// <summary>
/// Fija la tarifa del Operador CAE de quien pide para un Tenant beneficiario y un concepto: añade una versión
/// vigente desde ahora (ver <see cref="TarifaOperadorCae"/>). Desactivar el cobro es una versión con
/// <paramref name="Activa"/> en falso; las unidades se siguen mostrando.
/// </summary>
public record FijarTarifaOperadorCaeCommand(
    Guid PropietarioTenantId, ConceptoTarifaOperadorCae Concepto, decimal PrecioUnitario, string MonedaIso, bool Activa) : ICommand;

public class FijarTarifaOperadorCaeCommandValidator : AbstractValidator<FijarTarifaOperadorCaeCommand>
{
    public FijarTarifaOperadorCaeCommandValidator()
    {
        RuleFor(c => c.PropietarioTenantId).NotEmpty();
        RuleFor(c => c.Concepto).IsInEnum();
        RuleFor(c => c.PrecioUnitario).InclusiveBetween(0m, 999_999.99m)
            .Must(p => decimal.Round(p, 2) == p).WithMessage("El precio lleva como mucho dos decimales.");
        RuleFor(c => c.MonedaIso).NotEmpty().Must(m => m.Trim().Length == TarifaOperadorCae.LongitudMonedaIso)
            .WithMessage("La moneda debe ser un código ISO de 3 caracteres (p. ej. EUR).");
    }
}

public class FijarTarifaOperadorCaeCommandHandler(
    ICurrentUserService usuario, ITarifasOperadorCaeContext contexto, IOperacionesQueryContext operaciones,
    IUnitOfWork unidad, TimeProvider reloj) : IRequestHandler<FijarTarifaOperadorCaeCommand, Result>
{
    public async Task<Result> Handle(FijarTarifaOperadorCaeCommand request, CancellationToken cancellationToken)
    {
        if (await AutorizacionTarifasOperadorCae.OperadorQueGestionaAsync(usuario) is not { } operador)
            return Result.Fallo(AutorizacionTarifasOperadorCae.SinPermiso);
        if (await usuario.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return Result.Fallo(AutorizacionTarifasOperadorCae.SinPermiso);

        var ahora = reloj.GetUtcNow().UtcDateTime;
        using var ambito = AmbitoTenantExplicito.Establecer(operador);
        if (!await AutorizacionTarifasOperadorCae.GestionaAsync(operaciones, operador, request.PropietarioTenantId, ahora, cancellationToken))
            return Result.Fallo(AutorizacionTarifasOperadorCae.EmpresaNoGestionada);

        contexto.Agregar(TarifaOperadorCae.Registrar(
            request.PropietarioTenantId, request.Concepto, request.PrecioUnitario, request.MonedaIso, request.Activa, usuarioId, ahora));
        await unidad.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}

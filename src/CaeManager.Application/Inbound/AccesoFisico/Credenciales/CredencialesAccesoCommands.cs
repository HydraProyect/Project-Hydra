using System.Data.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Inbound.ContratosAcceso;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.AccesoFisico;
using CaeManager.Domain.Inbound.ContratosAcceso;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.AccesoFisico.Credenciales;

// Credencial de acceso físico (plan de control de acceso físico, AF-02). Tres comandos del Tenant TCE, todos con
// GestionarInbound y la puerta IAutorizacionInbound como primera orden. No firman ni generan QR (AF-03), no registran
// entradas (AF-06) ni tienen pantalla: solo mueven el estado de la credencial.
//
// SUPUESTO (H5 del plan): todavía no existe la asignación del Trabajador a un contrato de acceso, así que el sujeto
// «acreditable» sale de las evidencias: un Trabajador con alguna evidencia en un contrato de acceso Activo y vinculado al
// Tenant TEC, visible para el actor con su Asignación de Cartera (ContratoAccesoAutorizacion). Cuando la asignación
// exista pasa a ser el disparador y esta búsqueda se sustituye por ella, no se suma. Quien no tiene evidencias no puede
// tener credencial. Una evidencia Rechazada no acredita. La credencial es una por Trabajador y Tenant TCE (M8) y no por Centro:
// quien tiene cartera sobre el contrato que acredita al Trabajador la emite, regenera o revoca para todos los Centros donde
// vale, también los de Clientes empresariales fuera de su cartera (consecuencia asumida de M8 con H5, a revisar con la
// asignación). Lo que no se encuentra o no se ve responde «no encontrado», sin distinguir cuál de las dos cosas falla.

[AreaDelEncargo("ContratosAcceso")]
public record EmitirCredencialCommand(Guid TenantOrigenId, Guid TrabajadorOrigenId) : ICommand<Guid>, IComandoInbound;

/// <summary>Invalida la serie anterior de la credencial Emitida: sube la serie y refresca la instantánea del sujeto.</summary>
[AreaDelEncargo("ContratosAcceso")]
public record RegenerarCredencialCommand(Guid CredencialId) : ICommand<int>, IComandoInbound;

/// <param name="Motivo">Enum cerrado: la credencial no guarda texto libre.</param>
[AreaDelEncargo("ContratosAcceso")]
public record RevocarCredencialCommand(Guid CredencialId, MotivoRevocacionCredencial Motivo) : ICommand, IComandoInbound;

public class EmitirCredencialCommandValidator : AbstractValidator<EmitirCredencialCommand>
{
    public EmitirCredencialCommandValidator()
    {
        RuleFor(c => c.TenantOrigenId).NotEmpty();
        RuleFor(c => c.TrabajadorOrigenId).NotEmpty();
    }
}

public class RevocarCredencialCommandValidator : AbstractValidator<RevocarCredencialCommand>
{
    public RevocarCredencialCommandValidator()
    {
        RuleFor(c => c.Motivo).IsInEnum();
    }
}

public class EmitirCredencialCommandHandler(
    ICredencialAccesoRepository repositorio,
    IContratosAccesoQueryContext contratosContext,
    IEvidenciasInboundQueryContext evidenciasContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUser,
    IUnitOfWork unitOfWork,
    IDescarteCambiosPendientes descarteCambios,
    TimeProvider reloj,
    IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<EmitirCredencialCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(EmitirCredencialCommand request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.GestionarInbound, cancellationToken))
            return Result.Fallo<Guid>(AutorizacionInboundErrores.SinCapacidad);

        if (await currentUser.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return Result.Fallo<Guid>(CredencialAccesoErrores.NoEncontrada);

        var sujeto = await SujetoAcreditable.LocalizarAsync(
            request.TenantOrigenId, request.TrabajadorOrigenId, contratosContext, evidenciasContext, alcanceDatos, cancellationToken);
        if (sujeto is null)
            return Result.Fallo<Guid>(CredencialAccesoErrores.NoEncontrada);

        if (await repositorio.ObtenerEmitidaDelTrabajadorAsync(request.TenantOrigenId, request.TrabajadorOrigenId, cancellationToken) is not null)
            return Result.Fallo<Guid>(CredencialAccesoErrores.YaEmitida);

        var credencial = CredencialAcceso.Emitir(sujeto, usuarioId, reloj.GetUtcNow().UtcDateTime);
        repositorio.Agregar(credencial);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException excepcion) when (SujetoAcreditable.EsConflicto(excepcion))
        {
            // Dos emisiones a la vez: el índice único parcial deja pasar una sola.
            descarteCambios.DescartarCambiosPendientes();
            return Result.Fallo<Guid>(CredencialAccesoErrores.YaEmitida);
        }

        return Result.Exito(credencial.Id);
    }
}

public class RegenerarCredencialCommandHandler(
    ICredencialAccesoRepository repositorio,
    IContratosAccesoQueryContext contratosContext,
    IEvidenciasInboundQueryContext evidenciasContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUser,
    IUnitOfWork unitOfWork,
    IDescarteCambiosPendientes descarteCambios,
    TimeProvider reloj,
    IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<RegenerarCredencialCommand, Result<int>>
{
    public async Task<Result<int>> Handle(RegenerarCredencialCommand request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.GestionarInbound, cancellationToken))
            return Result.Fallo<int>(AutorizacionInboundErrores.SinCapacidad);

        if (await currentUser.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return Result.Fallo<int>(CredencialAccesoErrores.NoEncontrada);

        var credencial = await repositorio.ObtenerPorIdAsync(request.CredencialId, cancellationToken);
        if (credencial is null)
            return Result.Fallo<int>(CredencialAccesoErrores.NoEncontrada);

        // La misma condición que la emisión: regenerar un carné cuyo Trabajador ya no se acredita en un contrato vivo y visible no
        // tiene objeto. De paso trae la instantánea al día (un nombre o un documento corregido en el Tenant TEC).
        var sujeto = await SujetoAcreditable.LocalizarAsync(
            credencial.TenantOrigenId, credencial.TrabajadorOrigenId, contratosContext, evidenciasContext, alcanceDatos, cancellationToken);
        if (sujeto is null)
            return Result.Fallo<int>(CredencialAccesoErrores.NoEncontrada);
        if (credencial.Estado != EstadoCredencialAcceso.Emitida)
            return Result.Fallo<int>(CredencialAccesoErrores.EstadoNoAdmite);

        credencial.Regenerar(sujeto.Nombre, sujeto.DocumentoIdentidad, usuarioId, reloj.GetUtcNow().UtcDateTime);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException excepcion) when (SujetoAcreditable.EsConflicto(excepcion))
        {
            // Otro cambio sobre la misma credencial (token de versión): se vuelve a pedir, no se pisa.
            descarteCambios.DescartarCambiosPendientes();
            return Result.Fallo<int>(CredencialAccesoErrores.Conflicto);
        }

        return Result.Exito(credencial.Serie);
    }
}

public class RevocarCredencialCommandHandler(
    ICredencialAccesoRepository repositorio,
    IContratosAccesoQueryContext contratosContext,
    IEvidenciasInboundQueryContext evidenciasContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUser,
    IUnitOfWork unitOfWork,
    IDescarteCambiosPendientes descarteCambios,
    TimeProvider reloj,
    IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<RevocarCredencialCommand, Result>
{
    public async Task<Result> Handle(RevocarCredencialCommand request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.GestionarInbound, cancellationToken))
            return Result.Fallo(AutorizacionInboundErrores.SinCapacidad);

        if (await currentUser.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return Result.Fallo(CredencialAccesoErrores.NoEncontrada);

        var credencial = await repositorio.ObtenerPorIdAsync(request.CredencialId, cancellationToken);
        if (credencial is null)
            return Result.Fallo(CredencialAccesoErrores.NoEncontrada);

        // Revocar tiene que poder hacerse también cuando el contrato ya acabó (el carné sigue en el bolsillo del Trabajador):
        // basta con que el actor vea el Trabajador por alguna evidencia, sea cual sea el estado de su contrato.
        if (!await SujetoAcreditable.EsVisibleAsync(
                credencial.TenantOrigenId, credencial.TrabajadorOrigenId, contratosContext, evidenciasContext, alcanceDatos, cancellationToken))
            return Result.Fallo(CredencialAccesoErrores.NoEncontrada);
        if (credencial.Estado != EstadoCredencialAcceso.Emitida)
            return Result.Fallo(CredencialAccesoErrores.EstadoNoAdmite);

        credencial.Revocar(request.Motivo, usuarioId, reloj.GetUtcNow().UtcDateTime);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException excepcion) when (SujetoAcreditable.EsConflicto(excepcion))
        {
            descarteCambios.DescartarCambiosPendientes();
            return Result.Fallo(CredencialAccesoErrores.Conflicto);
        }

        return Result.Exito();
    }
}

/// <summary>
/// El sujeto acreditable por evidencias (supuesto H5, ver arriba). Solo lee de los contextos de consulta del Tenant TCE
/// actual, con su RLS y su filtro de Tenant: nada del otro Tenant, cuyos ids llegan como procedencia.
/// </summary>
internal static class SujetoAcreditable
{
    private const string ViolacionDeUnicidad = "23505";

    /// <summary>
    /// La instantánea del Trabajador desde su evidencia más reciente en un contrato de acceso Activo y vinculado a ese Tenant TEC
    /// que el actor ve; null si no hay ninguna.
    /// </summary>
    public static async Task<CredencialAcceso.Sujeto?> LocalizarAsync(
        Guid tenantOrigenId, Guid trabajadorOrigenId, IContratosAccesoQueryContext contratosContext,
        IEvidenciasInboundQueryContext evidenciasContext, IAlcanceDatosService alcanceDatos, CancellationToken cancellationToken)
    {
        var contratoIds = await ContratosVisiblesAsync(tenantOrigenId, soloActivos: true, contratosContext, alcanceDatos, cancellationToken);
        if (contratoIds.Count == 0)
            return null;

        var evidencia = await evidenciasContext.EvidenciasInbound
            // Una evidencia Rechazada ni acredita ni da el nombre y el documento del carné: la portería los compara con el
            // documento de identidad del visitante, y los de una evidencia rechazada por datos erróneos son los que fallan.
            .Where(e => e.TenantOrigenId == tenantOrigenId && e.TrabajadorOrigenId == trabajadorOrigenId
                        && e.Estado != EstadoAcreditacion.Rechazada && contratoIds.Contains(e.ContratoAccesoId))
            .OrderByDescending(e => e.ValidadaEnUtc).ThenBy(e => e.Id)
            .Select(e => new { e.SujetoNombre, e.SujetoDocumentoIdentidad })
            .FirstOrDefaultAsync(cancellationToken);

        return evidencia is null
            ? null
            : new CredencialAcceso.Sujeto(tenantOrigenId, trabajadorOrigenId, evidencia.SujetoNombre, evidencia.SujetoDocumentoIdentidad);
    }

    /// <summary>Hay evidencia del Trabajador en algún contrato (de cualquier estado) que el actor ve.</summary>
    public static async Task<bool> EsVisibleAsync(
        Guid tenantOrigenId, Guid trabajadorOrigenId, IContratosAccesoQueryContext contratosContext,
        IEvidenciasInboundQueryContext evidenciasContext, IAlcanceDatosService alcanceDatos, CancellationToken cancellationToken)
    {
        var contratoIds = await ContratosVisiblesAsync(tenantOrigenId, soloActivos: false, contratosContext, alcanceDatos, cancellationToken);
        return contratoIds.Count > 0
               && await evidenciasContext.EvidenciasInbound.AnyAsync(
                   e => e.TenantOrigenId == tenantOrigenId && e.TrabajadorOrigenId == trabajadorOrigenId
                        && contratoIds.Contains(e.ContratoAccesoId),
                   cancellationToken);
    }

    private static async Task<List<Guid>> ContratosVisiblesAsync(
        Guid tenantOrigenId, bool soloActivos, IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos,
        CancellationToken cancellationToken)
    {
        var consulta = contratosContext.ContratosAcceso.Where(c => c.TenantContratistaId == tenantOrigenId);
        if (soloActivos)
            // Abierto de verdad: un contrato con cierre real cuenta como Finalizado aunque su columna de estado aún diga Activo
            // (ContratoAcceso.EstadoEfectivo, durante el relevo de la versión anterior).
            consulta = consulta.Where(c => c.Estado == EstadoContratoAcceso.Activo && c.FechaCierreReal == null);

        var candidatos = await consulta.Select(c => new { c.Id, c.ClienteEmpresarialId }).ToListAsync(cancellationToken);
        var visibles = new List<Guid>();
        foreach (var contrato in candidatos)
        {
            if (await ContratoAccesoAutorizacion.VisibleAsync(contrato.ClienteEmpresarialId, alcanceDatos, cancellationToken))
                visibles.Add(contrato.Id);
        }

        return visibles;
    }

    // DbException y no PostgresException: Application no depende de Npgsql (FronterasDeCapaTests).
    public static bool EsConflicto(DbUpdateException excepcion) =>
        excepcion is DbUpdateConcurrencyException || excepcion.InnerException is DbException { SqlState: ViolacionDeUnicidad };
}

public static class CredencialAccesoErrores
{
    /// <summary>Una sola respuesta para «no existe» y «no lo ves»: no dice cuál de las dos falla.</summary>
    public static readonly Error NoEncontrada = Error.Crear(
        "CredencialAcceso.NoEncontrada", "No encontramos al Trabajador o su credencial de acceso.");

    public static readonly Error YaEmitida = Error.Crear(
        "CredencialAcceso.YaEmitida", "Este Trabajador ya tiene una credencial de acceso emitida. Regenérala o revócala.");

    public static readonly Error EstadoNoAdmite = Error.Crear(
        "CredencialAcceso.EstadoNoAdmite", "La credencial no está en un estado que admita este cambio.");

    public static readonly Error Conflicto = Error.Crear(
        "CredencialAcceso.Conflicto", "La credencial cambió mientras la modificabas. Vuelve a abrirla e inténtalo de nuevo.");
}

using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using MediatR;

namespace CaeManager.Application.Tenants.Commands.MarcarParticipanteTec;

/// <summary>
/// D3 (decisiones de Chris del 2026-11-19): el Actor de Plataforma TALVEG marca el Tenant activo como Tenant TEC
/// participante (<c>Tenants.EsParticipanteTec</c>), y desde entonces Outbound queda cerrado para él mientras no tenga una
/// línea Outbound vigente (<see cref="Comercial.LineaOutbound.IOutboundCerradoTenantTec"/>). Es el único escritor del
/// marcador: <c>UnicoEscritorDeEsParticipanteTecTests</c> fija que <see cref="Tenant.MarcarComoParticipanteTec"/> solo se
/// llama desde aquí, y el trigger <c>proteger_es_participante_tec</c> impide que el runtime cambie la columna.
///
/// <para>
/// <b>Autorización</b>, como <c>ContratarLineaInboundCommand</c>: solo dentro de una Sesión Privilegiada con la capacidad
/// <see cref="CapacidadPrivilegio.Aprovisionamiento"/> sobre el Tenant activo, con camino de escritura y sin impersonación.
/// Nunca el rol efectivo: un Administrador del propio Tenant no se pone el marcador. Por ser
/// <see cref="IComandoDeAprovisionamiento"/>, el handler escribe como <c>cae_app_aprovisionamiento</c>, que solo puede
/// actualizar esa columna de <c>Tenants</c> (GRANT por columna de la migración de D3).
/// </para>
///
/// <para>
/// Sin inverso, sin endpoint y sin pantalla en este incremento: se invoca desde el aprovisionamiento. Idempotente: marcar
/// un Tenant ya marcado no escribe nada. No comprueba que el Tenant no sea el de plataforma: la Sesión Privilegiada no se
/// abre sobre él (ReglaTenantObjetivoAjeno).
/// </para>
/// </summary>
[AreaDelEncargo("Tenants")]
public record MarcarTenantComoParticipanteTecCommand : ICommand, IComandoDeAprovisionamiento;

public static class ErroresMarcarParticipanteTec
{
    public static readonly Error NoAutorizado = Error.Crear(
        "ParticipanteTec.NoAutorizado",
        "Solo una sesión de aprovisionamiento de la plataforma sobre esta organización puede marcarla como empresa contratista participante.");
}

public class MarcarTenantComoParticipanteTecCommandHandler(
    ITenantActual tenantActual,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ITenantRepository tenantRepository,
    IUnitOfWork unitOfWork)
    : IRequestHandler<MarcarTenantComoParticipanteTecCommand, Result>
{
    public async Task<Result> Handle(MarcarTenantComoParticipanteTecCommand request, CancellationToken cancellationToken)
    {
        // La autoridad va antes que cualquier lectura. ObtenerAsync y no RevalidarAsync, por lo mismo que
        // ContratarLineaInboundCommand: el handler corre ya bajo cae_app_aprovisionamiento, que no lee las tablas de
        // sesiones; la revalidación la hizo ElevacionEscrituraAprovisionamientoBehavior antes de elevar (misma ventana
        // declarada que el resto de comandos de aprovisionamiento).
        if (tenantActual.TenantId is not { } tenantId
            || await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not { } sesion
            || sesion.Capacidad != CapacidadPrivilegio.Aprovisionamiento
            || !sesion.TieneCaminoDeEscritura
            || sesion.TenantObjetivoId != tenantId
            || sesion.UsuarioSimuladoId is not null)
            return Result.Fallo(ErroresMarcarParticipanteTec.NoAutorizado);

        // El mismo error si el Tenant ya no está: quien llega aquí ya tiene la sesión sobre él, y no hay nada que marcar.
        // La fila que se escribe tiene que ser la del Tenant de la elevación de Aprovisionamiento abierta: Tenants no
        // tiene RLS y el GRANT UPDATE ("EsParticipanteTec") a cae_app_aprovisionamiento no limita por fila, así que la
        // base no impide marcar otro Tenant. Lo impide esta comparación, que falla cerrado; el Tenant activo es una
        // coordenada de contexto, no autoridad.
        var tenant = await tenantRepository.ObtenerPorIdAsync(tenantId, cancellationToken);
        if (tenant is null || tenant.Id != sesion.TenantObjetivoId)
            return Result.Fallo(ErroresMarcarParticipanteTec.NoAutorizado);

        if (tenant.EsParticipanteTec)
            return Result.Exito();

        tenant.MarcarComoParticipanteTec();
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}

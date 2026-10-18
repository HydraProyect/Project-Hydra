using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Usuarios.Commands.AsignarCarteraGestorCae;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Operaciones.Traspasos;

public record DestinoTraspaso(Guid AsignacionOperacionId, Guid DestinatarioUsuarioId);
public record TraspasarCarteraCommand(Guid GestorUsuarioId, IReadOnlyList<DestinoTraspaso> Destinos,
    bool MantenerComoApoyo = false, Guid? AusenciaId = null) : ICommand;
public record RegistrarAusenciaGestorCaeCommand(Guid GestorUsuarioId, TipoAusenciaGestorCae Tipo,
    DateOnly Desde, DateOnly? Hasta) : ICommand;
public record CerrarAusenciaGestorCaeCommand(Guid GestorUsuarioId, Guid AusenciaId) : ICommand;

public class TraspasosCarteraCommandsHandler(
    ICurrentUserService currentUser, IDirectorioUsuariosService usuarios, IDirectorioDestinosCartera directorio,
    ICatalogoIncorporacionCartera catalogo, ITraspasosCarteraWriter writer, IAusenciasGestorCae ausencias,
    ITransaccionDeComando transaccion, IBloqueoCarteraUsuario bloqueo, IUnitOfWork unitOfWork)
    : IRequestHandler<TraspasarCarteraCommand, Result>, IRequestHandler<RegistrarAusenciaGestorCaeCommand, Result>,
      IRequestHandler<CerrarAusenciaGestorCaeCommand, Result>
{
    public static readonly Error Cambio = Error.Crear("Traspaso.Cambio", "La cartera ha cambiado. Revisa las Empresas y vuelve a intentarlo.");
    public static readonly Error DestinoInvalido = Error.Crear("Traspaso.Destino", "Elige un Gestor CAE disponible de tu organización para cada Empresa.");
    public static readonly Error AusenciaInvalida = Error.Crear("Traspaso.Ausencia", "Revisa el tipo y las fechas de la ausencia; no puede haber otra ausencia abierta.");

    public Task<Result> Handle(TraspasarCarteraCommand request, CancellationToken ct) =>
        EjecutarAsync(request.GestorUsuarioId, request.Destinos.Select(d => d.DestinatarioUsuarioId), async (ctx, token) =>
        {
            if (request.Destinos.Count == 0 || request.Destinos.Select(d => d.AsignacionOperacionId).Distinct().Count() != request.Destinos.Count)
                return Result.Fallo(DestinoInvalido);
            var ausencia = request.AusenciaId is { } id ? await ausencias.ObtenerAsync(id, token) : null;
            if (request.AusenciaId is not null && (ausencia is null || ausencia.GestorUsuarioId != ctx.GestorUsuarioId || ausencia.CerradaEnUtc is not null))
                return Result.Fallo(AusenciaInvalida);
            var abiertas = await ausencias.ListarAbiertasAsync(token);
            foreach (var destino in request.Destinos)
            {
                if (destino.DestinatarioUsuarioId == ctx.GestorUsuarioId || abiertas.Any(a => a.GestorUsuarioId == destino.DestinatarioUsuarioId)
                    || !await usuarios.EsCuentaActivaConRolAsync(destino.DestinatarioUsuarioId, ctx.OperadorTenantId, "GestorCae", token))
                    return Result.Fallo(DestinoInvalido);
                // La misma autoridad sobre origen y destino: un Coordinador CAE no asigna fuera de su equipo.
                var autoridadDestino = await AutoridadSobreCarteraDeGestorCae.ResolverAsync(destino.DestinatarioUsuarioId, currentUser, usuarios, directorio, token);
                if (autoridadDestino.EsFallido) return Result.Fallo(autoridadDestino.Error);
            }
            var vivas = await catalogo.ObtenerCarterasVivasAsync(ctx.OperadorTenantId, null, token);
            var asignables = await catalogo.ObtenerAsignablesAsync(ctx.OperadorTenantId, token);
            foreach (var destino in request.Destinos)
            {
                var origen = vivas.SingleOrDefault(c => c.UsuarioId == ctx.GestorUsuarioId && c.AsignacionOperacionId == destino.AsignacionOperacionId);
                if (origen is null || asignables.All(a => a.AsignacionOperacionId != origen.AsignacionOperacionId)
                    || ausencia?.Coberturas.Any(c => c.AsignacionOperacionId == origen.AsignacionOperacionId) == true)
                    return Result.Fallo(Cambio);
                var temporal = ausencia is not null && ausencia.Tipo != TipoAusenciaGestorCae.BajaDefinitiva;
                using (AmbitoTenantExplicito.Establecer(origen.PropietarioTenantId))
                {
                    if (!await writer.TraspasarAsync(origen.AsignacionOperacionId, origen.PropietarioTenantId, ctx.OperadorTenantId,
                            ctx.GestorUsuarioId, destino.DestinatarioUsuarioId, ctx.ActorUsuarioId,
                            ausencia is null && request.MantenerComoApoyo, temporal ? ausencia!.Id : null, token)
                        || !await catalogo.GuardarDetectandoCarreraAsync(token))
                        return Result.Fallo(Cambio);
                }
            }
            // Se modifica la ausencia solo al volver al Tenant del Operador CAE, tras todos los guardados en los Tenants propietarios.
            foreach (var destino in request.Destinos)
            {
                var origen = vivas.Single(c => c.UsuarioId == ctx.GestorUsuarioId && c.AsignacionOperacionId == destino.AsignacionOperacionId);
                ausencia?.Cubrir(origen.AsignacionOperacionId, origen.PropietarioTenantId, destino.DestinatarioUsuarioId);
            }
            await unitOfWork.SaveChangesAsync(token);
            return Result.Exito();
        }, ct);

    public Task<Result> Handle(RegistrarAusenciaGestorCaeCommand request, CancellationToken ct) =>
        EjecutarAsync(request.GestorUsuarioId, [], async (ctx, token) =>
        {
            if (!ctx.GestorActivo || !Enum.IsDefined(request.Tipo)
                || (request.Tipo != TipoAusenciaGestorCae.BajaDefinitiva && (request.Hasta is null || request.Hasta < request.Desde))
                || (await ausencias.ListarAbiertasAsync(token)).Any(a => a.GestorUsuarioId == ctx.GestorUsuarioId))
                return Result.Fallo(AusenciaInvalida);
            ausencias.Agregar(new(ctx.GestorUsuarioId, request.Tipo, request.Desde, request.Hasta, ctx.ActorUsuarioId, DateTime.UtcNow));
            await unitOfWork.SaveChangesAsync(token);
            return Result.Exito();
        }, ct);

    public Task<Result> Handle(CerrarAusenciaGestorCaeCommand request, CancellationToken ct) =>
        EjecutarAsync(request.GestorUsuarioId, [], async (ctx, token) =>
        {
            var ausencia = await ausencias.ObtenerAsync(request.AusenciaId, token);
            if (ausencia is null || ausencia.GestorUsuarioId != ctx.GestorUsuarioId || ausencia.Tipo == TipoAusenciaGestorCae.BajaDefinitiva)
                return Result.Fallo(AusenciaInvalida);
            if (ausencia.CerradaEnUtc is not null) return Result.Exito();
            foreach (var cobertura in ausencia.Coberturas)
            {
                using (AmbitoTenantExplicito.Establecer(cobertura.PropietarioTenantId))
                {
                    await writer.RetirarCoberturaAsync(ausencia.Id, cobertura.AsignacionOperacionId,
                        cobertura.PropietarioTenantId, ctx.OperadorTenantId, ctx.ActorUsuarioId, token);
                    if (!await catalogo.GuardarDetectandoCarreraAsync(token)) return Result.Fallo(Cambio);
                }
            }
            ausencia.Cerrar(DateTime.UtcNow);
            await unitOfWork.SaveChangesAsync(token);
            return Result.Exito();
        }, ct);

    private async Task<Result> EjecutarAsync(Guid gestorId, IEnumerable<Guid> destinos,
        Func<ContextoCarteraDeGestorCae, CancellationToken, Task<Result>> ejecutar, CancellationToken ct)
    {
        var inicial = await AutoridadSobreCarteraDeGestorCae.ResolverAsync(gestorId, currentUser, usuarios, directorio, ct);
        if (inicial.EsFallido) return Result.Fallo(inicial.Error);
        try
        {
            return await transaccion.EjecutarAsync(async token =>
            {
                foreach (var id in destinos.Append(gestorId).Distinct().Order()) await bloqueo.BloquearExclusivoAsync(id, token);
                var actual = await AutoridadSobreCarteraDeGestorCae.ResolverAsync(gestorId, currentUser, usuarios, directorio, token);
                if (actual.EsFallido) return Result.Fallo(actual.Error);
                if (actual.Valor.OperadorTenantId != inicial.Valor.OperadorTenantId || actual.Valor.ActorUsuarioId != inicial.Valor.ActorUsuarioId)
                    return Result.Fallo(AutoridadSobreCarteraDeGestorCae.SinAutoridad);
                using (AmbitoTenantExplicito.Establecer(actual.Valor.OperadorTenantId)) return await ejecutar(actual.Valor, token);
            }, ct);
        }
        catch (DbUpdateException) { return Result.Fallo(Cambio); }
    }
}

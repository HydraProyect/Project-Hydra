using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Usuarios.Commands.AsignarCarteraGestorCae;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using MediatR;

namespace CaeManager.Application.Operaciones.Traspasos;

public record GestorDeCartera(Guid UsuarioId, string Nombre, bool Disponible);
public record AusenciaDeGestor(Guid Id, Guid GestorUsuarioId, TipoAusenciaGestorCae Tipo,
    DateOnly Desde, DateOnly? Hasta, IReadOnlyList<CoberturaEmpresaAusencia> Coberturas);
public record CarterasGestoresDto(IReadOnlyList<GestorDeCartera> Gestores,
    IReadOnlyList<CarteraVivaDeOperacion> Carteras, IReadOnlyList<AusenciaDeGestor> Ausencias);
public record ObtenerCarterasGestoresQuery : IRequest<Result<CarterasGestoresDto>>;

public class ObtenerCarterasGestoresQueryHandler(ICurrentUserService currentUser,
    IDirectorioUsuariosService usuarios, IDirectorioDestinosCartera directorio,
    ICatalogoIncorporacionCartera catalogo, IAusenciasGestorCae ausencias)
    : IRequestHandler<ObtenerCarterasGestoresQuery, Result<CarterasGestoresDto>>
{
    public async Task<Result<CarterasGestoresDto>> Handle(ObtenerCarterasGestoresQuery request, CancellationToken ct)
    {
        var actor = await AutoridadSobreCarteraDeGestorCae.ResolverActorAsync(currentUser, usuarios, ct);
        if (actor.EsFallido) return Result.Fallo<CarterasGestoresDto>(actor.Error);
        using (AmbitoTenantExplicito.Establecer(actor.Valor.OperadorTenantId))
        {
            var abiertas = await ausencias.ListarAbiertasAsync(ct);
            var carteras = await catalogo.ObtenerCarterasVivasAsync(actor.Valor.OperadorTenantId, null, ct);
            var activos = await usuarios.ObtenerCuentasActivasConRolAsync(actor.Valor.OperadorTenantId, "GestorCae", ct);
            var permitidos = new List<Guid>();
            foreach (var id in activos.Concat(carteras.Select(c => c.UsuarioId)).Concat(abiertas.Select(a => a.GestorUsuarioId)).Distinct())
                if ((await AutoridadSobreCarteraDeGestorCae.ResolverAsync(id, currentUser, usuarios, directorio, ct)).EsExitoso)
                    permitidos.Add(id);
            var nombres = await usuarios.ObtenerNombresVisiblesAsync(permitidos, ct);
            return Result.Exito(new CarterasGestoresDto(
                permitidos.Where(nombres.ContainsKey).Select(id => new GestorDeCartera(id, nombres[id],
                    activos.Contains(id) && abiertas.All(a => a.GestorUsuarioId != id))).OrderBy(g => g.Nombre).ToList(),
                carteras.Where(c => permitidos.Contains(c.UsuarioId)).ToList(),
                abiertas.Where(a => permitidos.Contains(a.GestorUsuarioId)).Select(a => new AusenciaDeGestor(a.Id,
                    a.GestorUsuarioId, a.Tipo, a.Desde, a.Hasta, a.Coberturas.ToList())).ToList()));
        }
    }
}

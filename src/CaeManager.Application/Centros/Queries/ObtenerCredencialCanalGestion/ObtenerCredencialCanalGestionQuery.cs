using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerCredencialCanalGestion;

/// <summary>
/// El usuario y la contraseña con los que se entra al portal de la Plataforma
/// CAE de un canal de gestión de un Centro. Existe para que el Gestor CAE los
/// copie al portapapeles desde Centro 360 y los pegue en el portal, que abre en
/// otra pestaña.
///
/// <para>
/// Es la única vía que decodifica <c>CanalGestionDocumental.Contrasena</c> hacia
/// la interfaz (DEC-53/DEC-62): <see cref="ObtenerCanalesGestionDeCentroQuery"/>
/// sigue sin proyectarla. Solo se invoca tras el clic explícito de copiar, nunca
/// como efecto de abrir la pantalla, y el resultado no se pinta: va al
/// portapapeles.
/// </para>
///
/// <para>
/// Decisión del propietario (2026-09-21): copiar usuario y contraseña con un
/// clic explícito, siguiendo el precedente de
/// <c>ObtenerCredencialAccesoEmpresaQuery</c>, <b>sin registro de auditoría de
/// la lectura</b>. Ese mecanismo no existe todavía; cuando exista, esta consulta
/// entra en él. Consecuencia conocida: quien tenga alcance de gestión sobre el
/// Centro —incluidos los roles de organización completa, que el precedente
/// también admite— puede leer la contraseña sin que quede rastro.
/// </para>
/// </summary>
public record ObtenerCredencialCanalGestionQuery(Guid CentroId, Guid CanalId)
    : IRequest<CredencialCanalGestionDto?>, IConsultaDeSecretosDeTenant;

public record CredencialCanalGestionDto(string? Usuario, string? Contrasena);

public class ObtenerCredencialCanalGestionQueryHandler(
    ICentrosQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCredencialCanalGestionQuery, CredencialCanalGestionDto?>
{
    public async Task<CredencialCanalGestionDto?> Handle(
        ObtenerCredencialCanalGestionQuery request, CancellationToken cancellationToken)
    {
        // Alcance de GESTIÓN, no de lectura (REC-153): la cartera de lectura de un
        // usuario de portal (rol Cliente) incluye los Centros de su propio Cliente,
        // pero las credenciales de la Plataforma CAE son un artefacto interno de la
        // gestión. Fuera de la cartera se responde como si no existiera: confirmar
        // que la fila existe ya sería decir algo sobre datos que no corresponden.
        if (!await alcanceDatos.CentroParaGestionVisibleAsync(request.CentroId, cancellationToken))
            return null;

        // El canal se busca por su Id Y por el Centro autorizado: un Id de canal de
        // otro Centro no se resuelve aunque el Centro pedido sí esté en la cartera.
        return await dbContext.CanalesGestionDocumental
            .Where(c => c.Id == request.CanalId
                        && c.CentroId == request.CentroId
                        && c.Tipo == TipoCanalGestion.Plataforma)
            .Select(c => new CredencialCanalGestionDto(c.Usuario, c.Contrasena))
            .FirstOrDefaultAsync(cancellationToken);
    }
}

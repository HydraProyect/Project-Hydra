using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Plataforma.OrdenMenu;
using CaeManager.Application.Tenants.Queries.EsAdministradorPlataforma;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Tenants.Queries.UsaRotulosPrimeraPersona;
using CaeManager.Application.Usuarios.Queries.ObtenerAvatarPropio;
using CaeManager.Domain.Tenants;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Marco.Queries.ObtenerContextoMarco;

/// <summary>
/// Lo que el marco de la aplicación (menú lateral, menú de usuario, selector de Tenant beneficiario y migas) necesita
/// para pintarse, en un solo despacho y una sola pasada por la puerta de acceso a datos (pieza 1.3 del plan de
/// rendimiento del 2026-11-09). Antes cada componente despachaba sus propias Queries, y una navegación repetía la
/// lista de Tenants beneficiarios autorizados hasta cuatro veces entre el prerenderizado y el circuito.
///
/// <para>
/// <b>Lo que no entra, a propósito.</b> Las lecturas que fallan cerradas: el guard de la cuenta propia de
/// <c>MainLayout</c> (contraseña forzosa, rol pendiente, 2FA obligatoria) y las variantes <c>LecturaVigente</c> de
/// <see cref="ObtenerClientesAutorizadosQuery"/> y <see cref="EsAdministradorPlataformaQuery"/>. Esas siguen leyendo
/// el dato vigente por su camino. Tampoco la aceptación de términos, que es un bloqueo con su propio estado
/// persistido, ni las preferencias de tema, que se leen por un servicio de Infrastructure.
/// </para>
///
/// <para>
/// <b>Autorización.</b> No cambia nada: el handler compone las mismas Queries que antes despachaba cada componente,
/// con su pipeline, sus memos por ámbito (<c>MemoPorContexto</c>) y la lente de demo aplicada por cada una. Las
/// anidadas entran en la puerta que ya tiene esta (<c>PuertaAccesoDatos</c> es reentrante dentro del mismo flujo).
/// Lo que devuelve decide qué se ofrece, no qué se permite: los endpoints, los Commands y RLS siguen decidiendo con
/// el dato vigente (OWASP ASVS 4.0.3 V4.1.1, V4.1.3).
/// </para>
/// </summary>
public record ObtenerContextoMarcoQuery : IRequest<ContextoMarcoDto>;

/// <param name="PerfilVocabulario">Perfil de vocabulario del Tenant efectivo (vista previa del Administrador aplicada).</param>
/// <param name="RotulosPrimeraPersona">«Mi empresa», «Mis trabajadores»: ver <see cref="UsaRotulosPrimeraPersonaQuery"/>.</param>
/// <param name="EsAdministradorPlataforma">Señal memoizada, no la vigente: decide si se ofrece el grupo Plataforma.</param>
/// <param name="ClientesAutorizados">Tenants beneficiarios que el usuario puede activar, con la lente de demo aplicada.</param>
/// <param name="OrdenGrupos">Orden global de los grupos del menú, o <c>null</c> si nunca se cambió.</param>
/// <param name="OrdenEnlaces">Orden global de los enlaces del menú, o <c>null</c> si nunca se cambió.</param>
/// <param name="ParticipaEnIncorporacionCartera">Gestor CAE o Coordinador CAE en su Operador CAE de origen; <c>false</c> también si no se pudo leer (no se ofrece).</param>
/// <param name="Avatar">Clave del avatar propio, o <c>null</c> (también si no se pudo leer: es decoración).</param>
public sealed record ContextoMarcoDto(
    PerfilVocabularioTenant PerfilVocabulario,
    bool RotulosPrimeraPersona,
    bool EsAdministradorPlataforma,
    IReadOnlyList<ClienteAutorizadoDto> ClientesAutorizados,
    IReadOnlyList<string>? OrdenGrupos,
    IReadOnlyList<string>? OrdenEnlaces,
    bool ParticipaEnIncorporacionCartera,
    string? Avatar);

public class ObtenerContextoMarcoQueryHandler(ISender mediador, ILogger<ObtenerContextoMarcoQueryHandler>? logger = null)
    : IRequestHandler<ObtenerContextoMarcoQuery, ContextoMarcoDto>
{
    public async Task<ContextoMarcoDto> Handle(ObtenerContextoMarcoQuery request, CancellationToken cancellationToken)
    {
        // En serie: todas comparten el DbContext del ámbito, y la puerta ya está tomada por este flujo.
        var perfil = await mediador.Send(new ObtenerPerfilVocabularioActualQuery(), cancellationToken);
        var rotulosPrimeraPersona = await mediador.Send(new UsaRotulosPrimeraPersonaQuery(), cancellationToken);
        var esAdministradorPlataforma = await mediador.Send(new EsAdministradorPlataformaQuery(), cancellationToken);
        var clientes = await mediador.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);
        var orden = await mediador.Send(new ObtenerOrdenMenuLateralQuery(), cancellationToken);

        // Las dos lecturas opcionales van al final y se toleran: «+ Solicitar incorporación» es una comodidad (sin la
        // señal no se ofrece, y el Command vuelve a decidir) y el avatar es decoración (la cabecera pinta las
        // iniciales). Mismo criterio que tenían el selector y MenuUsuario: un fallo suyo no tumba el marco de todas
        // las páginas. Tras un fallo no se lee nada más, por si el DbContext del ámbito quedó en mal estado.
        var participa = false;
        string? avatar = null;
        try
        {
            participa = await mediador.Send(new ParticipaEnIncorporacionCarteraQuery(), cancellationToken);
            avatar = await mediador.Send(new ObtenerAvatarPropioQuery(), cancellationToken);
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            logger?.LogWarning(excepcion, "No se pudo leer una parte opcional del marco (incorporación a cartera o avatar)");
        }

        return new ContextoMarcoDto(
            perfil, rotulosPrimeraPersona, esAdministradorPlataforma, [.. clientes],
            orden is null ? null : [.. orden.Grupos], orden is null ? null : [.. orden.Enlaces],
            participa, avatar);
    }
}

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

/// <param name="PerfilVocabulario">Perfil de vocabulario del Tenant efectivo (vista previa del Administrador aplicada); el perfil por defecto si no se pudo leer.</param>
/// <param name="RotulosPrimeraPersona">«Mi empresa», «Mis trabajadores»: ver <see cref="UsaRotulosPrimeraPersonaQuery"/>; <c>false</c> si no se pudo leer.</param>
/// <param name="EsAdministradorPlataforma">Señal memoizada, no la vigente: decide si se ofrece el grupo Plataforma; <c>false</c> si no se pudo leer.</param>
/// <param name="ClientesAutorizados">Tenants beneficiarios que el usuario puede activar, con la lente de demo aplicada. Nunca tolerada: si falla, falla la lectura.</param>
/// <param name="OrdenGrupos">Orden global de los grupos del menú, o <c>null</c> (orden canónico) si nunca se cambió o no se pudo leer.</param>
/// <param name="OrdenEnlaces">Orden global de los enlaces del menú, o <c>null</c> (orden canónico) si nunca se cambió o no se pudo leer.</param>
/// <param name="ParticipaEnIncorporacionCartera">Gestor CAE o Coordinador CAE en su Operador CAE de origen; <c>false</c> también si no se pudo leer (no se ofrece).</param>
/// <param name="Avatar">Clave del avatar propio, o <c>null</c> (también si no se pudo leer: es decoración).</param>
/// <param name="Degradado">Alguna parte de presentación no se pudo leer y lleva su valor sin oferta: no se reutiliza ni viaja al circuito.</param>
public sealed record ContextoMarcoDto(
    PerfilVocabularioTenant PerfilVocabulario,
    bool RotulosPrimeraPersona,
    bool EsAdministradorPlataforma,
    IReadOnlyList<ClienteAutorizadoDto> ClientesAutorizados,
    IReadOnlyList<string>? OrdenGrupos,
    IReadOnlyList<string>? OrdenEnlaces,
    bool ParticipaEnIncorporacionCartera,
    string? Avatar,
    bool Degradado = false);

public class ObtenerContextoMarcoQueryHandler(ISender mediador, ILogger<ObtenerContextoMarcoQueryHandler>? logger = null)
    : IRequestHandler<ObtenerContextoMarcoQuery, ContextoMarcoDto>
{
    public async Task<ContextoMarcoDto> Handle(ObtenerContextoMarcoQuery request, CancellationToken cancellationToken)
    {
        // En serie: todas comparten el DbContext del ámbito, y la puerta ya está tomada por este flujo.
        //
        // Primero la lista de Tenants beneficiarios autorizados, la única parte que no se tolera: es el dato de
        // autorización (como oferta) del que viven el selector, las migas y el menú. Si falla, falla la lectura entera
        // (fail-closed) y cada componente se comporta como ante un fallo de su lista; sin valores inventados.
        var clientes = await mediador.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);

        // El resto solo es presentación, y cada parte tolera su propio fallo con un valor que no ofrece nada: un fallo
        // del vocabulario o del orden del menú no tumba el selector ni las migas, que antes solo leían lo suyo. Tras el
        // primer fallo no se lee nada más (por si el DbContext del ámbito quedó en mal estado): las partes que quedan
        // toman también su valor sin oferta. Un contexto así va marcado como degradado y no se reutiliza.
        var degradado = false;
        var perfil = await TolerarAsync(new ObtenerPerfilVocabularioActualQuery(), PerfilVocabularioTenant.ClienteDirecto, "el perfil de vocabulario");
        var rotulosPrimeraPersona = await TolerarAsync(new UsaRotulosPrimeraPersonaQuery(), false, "los rótulos en primera persona");
        var esAdministradorPlataforma = await TolerarAsync(new EsAdministradorPlataformaQuery(), false, "la señal de AdminPlataforma");
        var orden = await TolerarAsync(new ObtenerOrdenMenuLateralQuery(), null, "el orden del menú");
        // «+ Solicitar incorporación» es una comodidad (sin la señal no se ofrece, y el Command vuelve a decidir) y el
        // avatar es decoración (la cabecera pinta las iniciales).
        var participa = await TolerarAsync(new ParticipaEnIncorporacionCarteraQuery(), false, "la participación en la incorporación a cartera");
        var avatar = await TolerarAsync(new ObtenerAvatarPropioQuery(), null, "el avatar");

        return new ContextoMarcoDto(
            perfil, rotulosPrimeraPersona, esAdministradorPlataforma, [.. clientes],
            orden is null ? null : [.. orden.Grupos], orden is null ? null : [.. orden.Enlaces],
            participa, avatar, Degradado: degradado);

        async Task<T> TolerarAsync<T>(IRequest<T> query, T sinOferta, string parte)
        {
            if (degradado) return sinOferta;
            try
            {
                return await mediador.Send(query, cancellationToken);
            }
            catch (Exception excepcion) when (excepcion is not OperationCanceledException)
            {
                degradado = true;
                logger?.LogWarning(excepcion, "No se pudo leer {Parte} del marco; se pinta sin ofrecerlo", parte);
                return sinOferta;
            }
        }
    }
}
